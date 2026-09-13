using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ComputerUse.Core;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Evidence;
using ComputerUse.Core.Serialization;

namespace ComputerUse.Replay;

public static class DiscoveryEvidenceWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(CapabilityJson.Options)
    {
        WriteIndented = true
    };

    public static async Task<string> WriteAsync(
        GoalRequest request,
        LoopResult result,
        CapabilityArtifact artifact,
        string artifactPath,
        string provider,
        string model,
        string evidenceRoot,
        CancellationToken cancellationToken = default)
    {
        if (!result.Completed || !string.Equals(result.RunId, artifact.Provenance.DiscoveryRunId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Discovery evidence requires a completed run that matches artifact provenance.");
        }

        var approvedRoot = EvidencePathPolicy.ResolveRoot(evidenceRoot);
        Directory.CreateDirectory(approvedRoot);
        var path = EvidencePathPolicy.ResolveFile(approvedRoot, $"{result.RunId}.discovery.json");
        var receipt = new DiscoveryReceipt
        {
            SchemaVersion = 1,
            RunId = result.RunId,
            Provider = provider,
            Model = model,
            GoalCommitment = Commitment(JsonSerializer.SerializeToElement(request.Goal)),
            InputCommitments = request.Inputs.OrderBy(input => input.Key, StringComparer.Ordinal)
                .ToDictionary(
                    input => input.Key,
                    input => Commitment(JsonSerializer.SerializeToElement(input.Value)),
                    StringComparer.Ordinal),
            ModelCallCount = result.ModelCalls,
            Status = result.Status,
            CapabilityId = artifact.CapabilityId,
            ArtifactSha256 = await EvidenceDigest.Sha256FileAsync(artifactPath, cancellationToken).ConfigureAwait(false)
        };

        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        await JsonSerializer.SerializeAsync(stream, receipt, JsonOptions, cancellationToken).ConfigureAwait(false);
        return path;
    }

    public static string Commitment(JsonElement value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value.GetRawText()))).ToLowerInvariant();

    private sealed record DiscoveryReceipt
    {
        public required int SchemaVersion { get; init; }
        public required string RunId { get; init; }
        public required string Provider { get; init; }
        public required string Model { get; init; }
        public required string GoalCommitment { get; init; }
        public required IReadOnlyDictionary<string, string> InputCommitments { get; init; }
        public required int ModelCallCount { get; init; }
        public required DiscoveryRunStatus Status { get; init; }
        public required string CapabilityId { get; init; }
        public required string ArtifactSha256 { get; init; }
    }
}