using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Replay;

namespace ComputerUse.Replay.Tests;

public sealed class ReplayResultEvidenceWriterTests : IDisposable
{
    private const string ContinuityCommitment = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private readonly string evidenceRoot = Path.Combine(Path.GetTempPath(), $"replay-result-{Guid.NewGuid():N}");

    [Fact]
    public async Task Writer_preserves_run_correlation_and_redacts_persisted_values()
    {
        const string runId = "0123456789abcdef0123456789abcdef";
        const string sensitiveValue = "private synthetic note";
        const string opaqueValue = "fedcba9876543210fedcba9876543210";
        var result = new ReplayRunResult
        {
            RunId = runId,
            Status = ReplayRunStatus.Failed,
            Steps =
            [
                new ReplayStepExecution("step-1", false, 1, sensitiveValue, null, "failed", $"session {opaqueValue}")
            ],
            Failure = new ReplayFailure("step-1", "failed", sensitiveValue),
            Outputs = new Dictionary<string, JsonElement>
            {
                ["runId"] = JsonSerializer.SerializeToElement(sensitiveValue),
                ["relativePath"] = JsonSerializer.SerializeToElement(sensitiveValue)
            },
            Evidence =
            [
                new EvidenceReference
                {
                    Kind = "snapshot",
                    RelativePath = "surface-safe.txt"
                }
            ],
            InterventionSummary = new ReplayInterventionSummary(
                true,
                true,
                ContinuityCommitment,
                ComputerUse.Core.Intervention.ControlOwner.Automation,
                4,
                true)
        };

        var path = await ReplayResultEvidenceWriter.WriteBestEffortAsync(
            result,
            CreateArtifact(DataClassification.Secret, DataClassification.Secret),
            new Dictionary<string, JsonElement>
            {
                ["value"] = JsonSerializer.SerializeToElement(sensitiveValue)
            },
            evidenceRoot);

        Assert.NotNull(path);
        var persisted = await File.ReadAllTextAsync(path!);
        Assert.Contains(runId, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(sensitiveValue, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(opaqueValue, persisted, StringComparison.Ordinal);
        Assert.Contains("surface-safe.txt", persisted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(persisted);
        Assert.Equal(runId, document.RootElement.GetProperty("runId").GetString());
        Assert.Equal("failed", document.RootElement.GetProperty("status").GetString());
        Assert.Matches("^[a-f0-9]{64}$", document.RootElement.GetProperty("outputCommitments").GetProperty("runId").GetString());
        Assert.Equal(
            ContinuityCommitment,
            document.RootElement.GetProperty("interventionSummary").GetProperty("sessionContinuityCommitment").GetString());
    }

    [Fact]
    public async Task Writer_does_not_overwrite_an_existing_result()
    {
        var result = ReplayRunResult.Completed([], new Dictionary<string, JsonElement>()) with
        {
            RunId = "fixed-run"
        };

        var artifact = CreateArtifact(DataClassification.Public, DataClassification.Public);
        var first = await ReplayResultEvidenceWriter.WriteBestEffortAsync(result, artifact, new Dictionary<string, JsonElement>(), evidenceRoot);
        var second = await ReplayResultEvidenceWriter.WriteBestEffortAsync(result, artifact, new Dictionary<string, JsonElement>(), evidenceRoot);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task Writer_preserves_public_values_and_redacts_confidential_outputs()
    {
        const string publicValue = "public synthetic value";
        const string confidentialValue = "ordinary confidential synthetic value";
        var result = ReplayRunResult.Completed([], new Dictionary<string, JsonElement>
        {
            ["publicResult"] = JsonSerializer.SerializeToElement(publicValue),
            ["confidentialResult"] = JsonSerializer.SerializeToElement(confidentialValue)
        }) with { RunId = "classification-aware" };
        var artifact = CreateArtifact(DataClassification.Public, DataClassification.Confidential);

        var path = await ReplayResultEvidenceWriter.WriteBestEffortAsync(
            result,
            artifact,
            new Dictionary<string, JsonElement> { ["value"] = JsonSerializer.SerializeToElement(publicValue) },
            evidenceRoot);

        Assert.NotNull(path);
        var persisted = await File.ReadAllTextAsync(path!);
        Assert.Contains(publicValue, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(confidentialValue, persisted, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(persisted);
        Assert.Equal("[REDACTED]", document.RootElement.GetProperty("outputs").GetProperty("confidentialResult").GetString());
        Assert.Matches("^[a-f0-9]{64}$", document.RootElement.GetProperty("outputCommitments").GetProperty("confidentialResult").GetString());
    }

    private static CapabilityArtifact CreateArtifact(
        DataClassification inputClassification,
        DataClassification outputClassification) => new()
    {
        CapabilityId = "evidence-writer-test.v1",
        Name = "Evidence writer test",
        Description = "Synthetic classification test.",
        Target = new CapabilityTarget { Surface = SurfaceKind.Web, EntryPointTemplate = new Uri("https://example.invalid/") },
        Inputs =
        [
            new InputDefinition { Name = "value", Type = ValueTypeKind.String, Classification = inputClassification }
        ],
        Outputs =
        [
            new OutputDefinition
            {
                Name = "runId",
                Type = ValueTypeKind.String,
                Classification = outputClassification,
                Extraction = Extraction("run-id")
            },
            new OutputDefinition
            {
                Name = "relativePath",
                Type = ValueTypeKind.String,
                Classification = outputClassification,
                Extraction = Extraction("relative-path")
            },
            new OutputDefinition
            {
                Name = "publicResult",
                Type = ValueTypeKind.String,
                Classification = DataClassification.Public,
                Extraction = Extraction("public-result")
            },
            new OutputDefinition
            {
                Name = "confidentialResult",
                Type = ValueTypeKind.String,
                Classification = outputClassification,
                Extraction = Extraction("confidential-result")
            }
        ],
        Steps = [],
        Checkpoint = new Condition { Kind = ConditionKind.Visible, Target = Target("checkpoint") },
        Provenance = new CapabilityProvenance
        {
            DiscoveryRunId = "test",
            CreatedAt = DateTimeOffset.UnixEpoch,
            GeneratorVersion = "test"
        }
    };

    private static ValueExtraction Extraction(string id) => new()
    {
        Target = Target(id),
        Kind = ExtractionKind.Text
    };

    private static TargetDescriptor Target(string id) => new()
    {
        Strategies = [new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = id }]
    };

    public void Dispose()
    {
        if (Directory.Exists(evidenceRoot))
        {
            Directory.Delete(evidenceRoot, recursive: true);
        }
    }
}