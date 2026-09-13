using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Serialization;

namespace ComputerUse.Core.Tests;

public sealed class RunEventLoggingTests
{
    [Fact]
    public async Task Json_lines_sink_persists_only_structured_allowlisted_fields()
    {
        const string runId = "run_logging_001";
        const string canarySecret = "canary-secret-that-must-not-persist";
        var root = Path.Combine(Path.GetTempPath(), $"run-events-{Guid.NewGuid():N}");
        try
        {
            string path;
            await using (var sink = new JsonLinesRunEventSink(root, runId))
            {
                await sink.WriteAsync(new RunEvent
                {
                    RunId = runId,
                    Timestamp = DateTimeOffset.UnixEpoch,
                    Mode = RuntimeMode.Discovery,
                    Kind = RunEventKind.ActionCompleted,
                    Outcome = RunEventOutcome.Succeeded,
                    StepIndex = 1,
                    Action = ActionKind.Type,
                    Risk = RiskLevel.ReversibleWrite
                }, CancellationToken.None);
                path = sink.Path;
            }

            var persisted = await File.ReadAllTextAsync(path);
            var runEvent = JsonSerializer.Deserialize<RunEvent>(persisted, CapabilityJson.Options);

            Assert.NotNull(runEvent);
            Assert.Single(await File.ReadAllLinesAsync(path));
            Assert.Equal(runId, runEvent.RunId);
            Assert.Equal(RunEventKind.ActionCompleted, runEvent.Kind);
            Assert.DoesNotContain(canarySecret, persisted, StringComparison.Ordinal);
            Assert.DoesNotContain("valueTemplate", persisted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("target", persisted, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("run/escape")]
    [InlineData("run.escape")]
    public void Json_lines_sink_rejects_unsafe_run_ids(string runId)
    {
        var root = Path.Combine(Path.GetTempPath(), $"run-events-{Guid.NewGuid():N}");

        Assert.Throws<ArgumentException>(() => new JsonLinesRunEventSink(root, runId));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task Json_lines_sink_rejects_nonopaque_step_ids()
    {
        const string runId = "run_logging_002";
        var root = Path.Combine(Path.GetTempPath(), $"run-events-{Guid.NewGuid():N}");
        try
        {
            await using var sink = new JsonLinesRunEventSink(root, runId);
            var runEvent = new RunEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UnixEpoch,
                Mode = RuntimeMode.Replay,
                Kind = RunEventKind.ActionCompleted,
                Outcome = RunEventOutcome.Failed,
                StepId = "failed value=Borealis"
            };

            await Assert.ThrowsAsync<ArgumentException>(() =>
                sink.WriteAsync(runEvent, CancellationToken.None));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Best_effort_sink_does_not_fail_when_evidence_root_is_unavailable()
    {
        var conflictingFile = Path.Combine(Path.GetTempPath(), $"run-events-file-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(conflictingFile, "not a directory");
        try
        {
            await using var sink = BestEffortJsonLinesRunEventSink.Create(conflictingFile, "run_logging_003");

            await sink.WriteAsync(new RunEvent
            {
                RunId = "run_logging_003",
                Timestamp = DateTimeOffset.UnixEpoch,
                Mode = RuntimeMode.Replay,
                Kind = RunEventKind.RunStarted,
                Outcome = RunEventOutcome.Started
            }, CancellationToken.None);
        }
        finally
        {
            File.Delete(conflictingFile);
        }
    }
}
