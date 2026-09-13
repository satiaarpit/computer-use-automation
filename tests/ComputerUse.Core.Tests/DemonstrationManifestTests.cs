using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComputerUse.Replay;

namespace ComputerUse.Core.Tests;

public sealed class DemonstrationManifestTests
{
    [Fact]
    public async Task Manifest_text_hashing_is_line_ending_invariant()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.JSON");
        try
        {
            await File.WriteAllTextAsync(path, "{\r\n  \"value\": true\r\n}\r\n");
            var windowsHash = await EvidenceDigest.Sha256FileAsync(path);
            await File.WriteAllTextAsync(
                path,
                "{\n  \"value\": true\n}\n",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            Assert.Equal(windowsHash, await EvidenceDigest.Sha256FileAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Manifest_text_hashing_rejects_malformed_utf8()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.jsonl");
        try
        {
            await File.WriteAllBytesAsync(path, [0xC0, 0xAF]);

            await Assert.ThrowsAsync<System.Text.DecoderFallbackException>(
                () => EvidenceDigest.Sha256FileAsync(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Manifest_cryptographically_binds_consistent_curated_evidence()
    {
        var repositoryRoot = FindRepositoryRoot();
        var manifestPath = Path.Combine(repositoryRoot, "evidence", "demonstration-manifest.json");
        var manifest = JsonSerializer.Deserialize<DemonstrationManifest>(
            await File.ReadAllTextAsync(manifestPath),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
            });

        Assert.NotNull(manifest);
        Assert.Equal(1, manifest!.SchemaVersion);
        Assert.Equal("phase-6-demonstration.v1", manifest.ManifestId);
        Assert.NotEmpty(manifest.Scenarios);
        Assert.Equal(manifest.Scenarios.Count, manifest.Scenarios.Select(scenario => scenario.Id).Distinct(StringComparer.Ordinal).Count());
        AssertRuntimeAllowlistExactlyMatchesManifest(repositoryRoot, manifest);

        foreach (var scenario in manifest.Scenarios)
        {
            Assert.Matches("^[a-f0-9]{32}$", scenario.RunId);
            Assert.NotEmpty(scenario.Invocation);
            Assert.NotEmpty(scenario.ExpectedEvents);
            Assert.Equal(scenario.Files.Count, scenario.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count());

            foreach (var file in scenario.Files)
            {
                Assert.False(Path.IsPathRooted(file.Path));
                var fullPath = ManifestPath(repositoryRoot, file.Path);
                Assert.True(File.Exists(fullPath), $"Manifest file does not exist: {file.Path}");
                Assert.Matches("^[a-f0-9]{64}$", file.Sha256);
                Assert.Equal(file.Sha256, await Sha256Async(fullPath));
            }

            var capabilityPath = RequiredFile(scenario, "capability", repositoryRoot);
            using (var capability = JsonDocument.Parse(await File.ReadAllTextAsync(capabilityPath)))
            {
                Assert.Equal(scenario.CapabilityId, capability.RootElement.GetProperty("capabilityId").GetString());
                if (scenario.DiscoveryRunId is not null)
                {
                    Assert.Equal(
                        scenario.DiscoveryRunId,
                        capability.RootElement.GetProperty("provenance").GetProperty("discoveryRunId").GetString());
                    var receiptPath = RequiredFile(scenario, "discoveryReceipt", repositoryRoot);
                    using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(receiptPath));
                    Assert.Equal(scenario.DiscoveryRunId, receipt.RootElement.GetProperty("runId").GetString());
                    Assert.Equal("gemini", receipt.RootElement.GetProperty("provider").GetString());
                    Assert.Equal("completed", receipt.RootElement.GetProperty("status").GetString());
                    Assert.True(receipt.RootElement.GetProperty("modelCallCount").GetInt32() > 0);
                    Assert.Equal(scenario.CapabilityId, receipt.RootElement.GetProperty("capabilityId").GetString());
                    Assert.Equal(await Sha256Async(capabilityPath), receipt.RootElement.GetProperty("artifactSha256").GetString());
                    var discoveryEventsPath = RequiredFile(scenario, "discoveryEventLog", repositoryRoot);
                    var discoveryEvents = (await File.ReadAllLinesAsync(discoveryEventsPath))
                        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
                        .ToArray();
                    Assert.All(discoveryEvents, runEvent =>
                    {
                        Assert.Equal(scenario.DiscoveryRunId, runEvent.GetProperty("runId").GetString());
                        Assert.Equal("discovery", runEvent.GetProperty("mode").GetString());
                    });
                    Assert.Equal(
                        receipt.RootElement.GetProperty("modelCallCount").GetInt32(),
                        discoveryEvents.Count(runEvent => runEvent.GetProperty("kind").GetString() == "decisionSelected"));
                    Assert.Equal("runStarted", discoveryEvents[0].GetProperty("kind").GetString());
                    Assert.Equal("runCompleted", discoveryEvents[^1].GetProperty("kind").GetString());
                    Assert.Equal("completed", discoveryEvents[^1].GetProperty("outcome").GetString());
                }
            }

            var resultPath = RequiredFile(scenario, "result", repositoryRoot);
            using (var result = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath)))
            {
                Assert.Equal(scenario.RunId, result.RootElement.GetProperty("runId").GetString());
                Assert.Equal(scenario.ExpectedStatus, result.RootElement.GetProperty("status").GetString());
                var hasCommitments = result.RootElement.TryGetProperty("outputCommitments", out var commitments);
                Assert.True(hasCommitments || scenario.ExpectedOutputTemplates.Count == 0);
                Assert.Equal(
                    scenario.ExpectedOutputTemplates.Count,
                    hasCommitments ? commitments.EnumerateObject().Count() : 0);
                foreach (var expectedOutput in scenario.ExpectedOutputTemplates)
                {
                    var expectedValue = BindExpectedOutput(expectedOutput.Value, scenario.Invocation);
                    var expectedCommitment = Commitment(JsonSerializer.SerializeToElement(expectedValue));
                    Assert.Equal(
                        expectedCommitment,
                        commitments.GetProperty(expectedOutput.Key).GetString());
                }
                foreach (var evidence in result.RootElement.GetProperty("evidence").EnumerateArray())
                {
                    var relativePath = evidence.GetProperty("relativePath").GetString();
                    var expectedPath = $"evidence/runtime/{relativePath}";
                    Assert.Contains(scenario.Files, file =>
                        string.Equals(file.Path, expectedPath, StringComparison.Ordinal));
                }
            }

            var eventLogPath = RequiredFile(scenario, "eventLog", repositoryRoot);
            var eventKinds = new List<string>();
            var runEvents = new List<JsonElement>();
            DateTimeOffset? previousTimestamp = null;
            foreach (var line in await File.ReadAllLinesAsync(eventLogPath))
            {
                using var runEvent = JsonDocument.Parse(line);
                Assert.Equal(scenario.RunId, runEvent.RootElement.GetProperty("runId").GetString());
                var timestamp = runEvent.RootElement.GetProperty("timestamp").GetDateTimeOffset();
                Assert.True(previousTimestamp is null || timestamp >= previousTimestamp);
                previousTimestamp = timestamp;
                eventKinds.Add(runEvent.RootElement.GetProperty("kind").GetString()!);
                runEvents.Add(runEvent.RootElement.Clone());
            }

            Assert.Equal(scenario.ExpectedEvents, eventKinds);

            if (scenario.OperatorApproval is not null)
            {
                Assert.Equal("APPROVE", scenario.OperatorApproval);
                var approval = Assert.Single(runEvents, runEvent =>
                    runEvent.GetProperty("kind").GetString() == "humanApprovalRecorded");
                Assert.Equal("human", approval.GetProperty("controlOwner").GetString());
                var action = Assert.Single(runEvents, runEvent =>
                    runEvent.GetProperty("kind").GetString() == "humanActionCompleted");
                Assert.Equal("succeeded", action.GetProperty("outcome").GetString());
                var continuity = Assert.Single(runEvents, runEvent =>
                    runEvent.GetProperty("kind").GetString() == "sessionContinuityVerified");
                Assert.Matches(
                    "^[a-f0-9]{64}$",
                    continuity.GetProperty("sessionContinuityCommitment").GetString());
                var finalTransfer = runEvents.Last(runEvent =>
                    runEvent.GetProperty("kind").GetString() == "controlTransferred");
                Assert.Equal("automation", finalTransfer.GetProperty("controlOwner").GetString());
                Assert.Equal("succeeded", finalTransfer.GetProperty("outcome").GetString());

                using var handoffResult = JsonDocument.Parse(await File.ReadAllTextAsync(resultPath));
                var summary = handoffResult.RootElement.GetProperty("interventionSummary");
                Assert.True(summary.GetProperty("approvalRecorded").GetBoolean());
                Assert.True(summary.GetProperty("humanActionSucceeded").GetBoolean());
                Assert.True(summary.GetProperty("resumeValidationSucceeded").GetBoolean());
                Assert.Equal("automation", summary.GetProperty("finalOwner").GetString());
                Assert.Equal(
                    continuity.GetProperty("sessionContinuityCommitment").GetString(),
                    summary.GetProperty("sessionContinuityCommitment").GetString());
            }

            if (scenario.Id == "bounded-recoverable-condition")
            {
                var recoveryActions = runEvents.Where(runEvent =>
                    runEvent.GetProperty("kind").GetString() == "actionCompleted").ToArray();
                Assert.Equal(["failed", "failed", "succeeded"], recoveryActions
                    .Select(runEvent => runEvent.GetProperty("outcome").GetString()!).ToArray());
                Assert.Equal([1, 2, 3], recoveryActions
                    .Select(runEvent => runEvent.GetProperty("attempts").GetInt32()).ToArray());
            }

            foreach (var suppliedValue in SuppliedInputValues(scenario.Invocation))
            {
                Assert.DoesNotContain(suppliedValue, await File.ReadAllTextAsync(eventLogPath), StringComparison.Ordinal);
            }
        }
    }

    private static void AssertRuntimeAllowlistExactlyMatchesManifest(
        string repositoryRoot,
        DemonstrationManifest manifest)
    {
        var allowlisted = File.ReadAllLines(Path.Combine(repositoryRoot, ".gitignore"))
            .Where(line => line.StartsWith("!evidence/runtime/", StringComparison.Ordinal))
            .Select(line => line[1..].Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        var manifested = manifest.Scenarios
            .SelectMany(scenario => scenario.Files)
            .Where(file => file.Path.StartsWith("evidence/runtime/", StringComparison.Ordinal))
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(allowlisted.Order(StringComparer.Ordinal), manifested.Order(StringComparer.Ordinal));
    }

    private static string RequiredFile(DemonstrationScenario scenario, string role, string repositoryRoot)
    {
        var binding = Assert.Single(scenario.Files, file => string.Equals(file.Role, role, StringComparison.Ordinal));
        return ManifestPath(repositoryRoot, binding.Path);
    }

    private static string ManifestPath(string repositoryRoot, string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var relative = Path.GetRelativePath(normalizedRoot, fullPath);
        Assert.False(Path.IsPathRooted(relative));
        Assert.DoesNotContain(relative.Split(Path.DirectorySeparatorChar), segment => segment == "..");
        Assert.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, fullPath, StringComparison.OrdinalIgnoreCase);
        return fullPath;
    }

    private static IEnumerable<string> SuppliedInputValues(IReadOnlyList<string> invocation)
    {
        for (var index = 3; index < invocation.Count; index += 2)
        {
            yield return invocation[index];
        }
    }

    private static string BindExpectedOutput(string template, IReadOnlyList<string> invocation)
    {
        var bound = template;
        for (var index = 2; index + 1 < invocation.Count; index += 2)
        {
            bound = bound.Replace($"${{{invocation[index]}}}", invocation[index + 1], StringComparison.Ordinal);
        }

        Assert.DoesNotContain("${", bound, StringComparison.Ordinal);
        return bound;
    }

    private static string Commitment(JsonElement value) => Convert.ToHexString(
        SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value.GetRawText()))).ToLowerInvariant();

    private static async Task<string> Sha256Async(string path)
        => await EvidenceDigest.Sha256FileAsync(path);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ComputerUse.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate the repository root.");
    }

    private sealed record DemonstrationManifest
    {
        public required int SchemaVersion { get; init; }
        public required string ManifestId { get; init; }
        public required DateTimeOffset GeneratedAt { get; init; }
        public required IReadOnlyList<DemonstrationScenario> Scenarios { get; init; }
    }

    private sealed record DemonstrationScenario
    {
        public required string Id { get; init; }
        public required string CapabilityId { get; init; }
        public string? DiscoveryRunId { get; init; }
        public required string ExpectedStatus { get; init; }
        public required string RunId { get; init; }
        public required IReadOnlyList<string> Invocation { get; init; }
        public required IReadOnlyList<string> ExpectedEvents { get; init; }
        public string? OperatorApproval { get; init; }
        public IReadOnlyDictionary<string, string> ExpectedOutputTemplates { get; init; }
            = new Dictionary<string, string>(StringComparer.Ordinal);
        public required IReadOnlyList<ManifestFile> Files { get; init; }
    }

    private sealed record ManifestFile
    {
        public required string Role { get; init; }
        public required string Path { get; init; }
        public required string Sha256 { get; init; }
    }
}
