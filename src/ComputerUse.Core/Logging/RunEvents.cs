using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Evidence;
using ComputerUse.Core.Intervention;
using ComputerUse.Core.Serialization;

namespace ComputerUse.Core.Logging;

public sealed record RunEvent
{
    public required string RunId { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required RuntimeMode Mode { get; init; }

    public required RunEventKind Kind { get; init; }

    public required RunEventOutcome Outcome { get; init; }

    public int? StepIndex { get; init; }

    public string? StepId { get; init; }

    public ActionKind? Action { get; init; }

    public RiskLevel? Risk { get; init; }

    public int? Attempts { get; init; }

    public ControlOwner? ControlOwner { get; init; }

    public long? ControlVersion { get; init; }

    public OperatorDecision? OperatorDecision { get; init; }

    public string? SessionContinuityCommitment { get; init; }
}

public enum RunEventKind
{
    RunStarted,
    ObservationCompleted,
    DecisionSelected,
    PolicyEvaluated,
    ActionCompleted,
    InterventionRequested,
    ControlTransferred,
    HumanApprovalRecorded,
    HumanActionCompleted,
    SessionContinuityVerified,
    RunCompleted
}

public enum RunEventOutcome
{
    Started,
    Observed,
    Selected,
    Allowed,
    Denied,
    Succeeded,
    Failed,
    Completed,
    InterventionRequired,
    BusinessOutcome,
    RecoverableCondition,
    InvalidRequest,
    RepeatedState,
    Cancelled
}

public interface IRunEventSink
{
    Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken);
}

public sealed class NullRunEventSink : IRunEventSink
{
    public static NullRunEventSink Instance { get; } = new();

    private NullRunEventSink()
    {
    }

    public Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}

public sealed class JsonLinesRunEventSink : IRunEventSink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonLineOptions = new(CapabilityJson.Options)
    {
        WriteIndented = false
    };

    private readonly string path;
    private readonly string runId;
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly StreamWriter writer;
    private bool disposed;

    public JsonLinesRunEventSink(string evidenceRoot, string runId)
    {
        ValidateRunId(runId);
        var approvedRoot = EvidencePathPolicy.ResolveRoot(evidenceRoot);
        Directory.CreateDirectory(approvedRoot);
        this.runId = runId;
        path = EvidencePathPolicy.ResolveFile(approvedRoot, $"{runId}.events.jsonl");
        writer = new StreamWriter(new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            AutoFlush = true
        };
    }

    public string Path => path;

    public async Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runEvent);
        if (!string.Equals(runEvent.RunId, runId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The event run ID does not match this sink.", nameof(runEvent));
        }
        if (runEvent.StepId is not null && (runEvent.StepId.Length is < 1 or > 100 || runEvent.StepId.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_')))
        {
            throw new ArgumentException("Event step IDs must be opaque identifiers.", nameof(runEvent));
        }

        var json = JsonSerializer.Serialize(runEvent, JsonLineOptions);
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(json).ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            await writer.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static void ValidateRunId(string runId)
    {
        if (string.IsNullOrWhiteSpace(runId) || runId.Length > 64 || runId.Any(character =>
            !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException(
                "Run IDs must be 1-64 ASCII letters, digits, hyphens, or underscores.",
                nameof(runId));
        }
    }
}

public sealed class BestEffortJsonLinesRunEventSink : IRunEventSink, IAsyncDisposable
{
    private readonly JsonLinesRunEventSink? inner;

    private BestEffortJsonLinesRunEventSink(JsonLinesRunEventSink? inner)
    {
        this.inner = inner;
    }

    public static BestEffortJsonLinesRunEventSink Create(string evidenceRoot, string runId)
    {
        try
        {
            return new BestEffortJsonLinesRunEventSink(new JsonLinesRunEventSink(evidenceRoot, runId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException)
        {
            return new BestEffortJsonLinesRunEventSink(null);
        }
    }

    public async Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken)
    {
        if (inner is null)
        {
            return;
        }

        try
        {
            await inner.WriteAsync(runEvent, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Evidence persistence is optional and must not alter automation behavior.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (inner is not null)
        {
            try
            {
                await inner.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Evidence persistence is optional and must not alter automation behavior.
            }
        }
    }
}
