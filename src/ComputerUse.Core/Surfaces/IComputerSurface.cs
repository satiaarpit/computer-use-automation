using ComputerUse.Core.Contracts;
using ComputerUse.Core.Policy;

namespace ComputerUse.Core.Surfaces;

public interface IComputerSurface : IAsyncDisposable
{
    Task SetNavigationPolicyAsync(AutomationPolicy policy, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    Task<SurfaceObservation> ObserveAsync(CancellationToken cancellationToken);

    Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken);

    Task<SurfaceInspectionResult> InspectAsync(
        SurfaceInspection inspection,
        CancellationToken cancellationToken) =>
        Task.FromResult(new SurfaceInspectionResult
        {
            Succeeded = false,
            ErrorCode = "inspection-not-supported",
            SafeMessage = "The surface does not support deterministic inspection."
        });

    Task<IReadOnlyList<Uri>> GetActiveLocationsAsync(CancellationToken cancellationToken);

    Task<string?> GetSessionContinuityCommitmentAsync(CancellationToken cancellationToken) =>
        Task.FromResult<string?>(null);

    Task<EvidenceReference> CaptureEvidenceAsync(EvidenceKind kind, CancellationToken cancellationToken);

    Task<IHumanControlSession> TransferControlToHumanAsync(
        HumanControlAuthorization authorization,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException("The surface does not support human control transfer.");
}

public sealed class HumanControlAuthorization
{
    internal HumanControlAuthorization(string runId, string stepId)
    {
        RunId = runId;
        StepId = stepId;
    }

    public string RunId { get; }

    public string StepId { get; }
}

public interface IHumanControlSession : IAsyncDisposable
{
    Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken);

    Task ResumeAutomationAsync(CancellationToken cancellationToken);

    Task CancelAsync(CancellationToken cancellationToken);
}

public sealed record SurfaceObservation
{
    public required Uri Url { get; init; }

    public required string Title { get; init; }

    public required string VisibleText { get; init; }

    public required IReadOnlyList<InteractiveElement> InteractiveElements { get; init; }

    public required string Fingerprint { get; init; }

    public bool IsTruncated { get; init; }
}

public sealed record InteractiveElement
{
    public required string ElementType { get; init; }

    public string? Role { get; init; }

    public string? AccessibleName { get; init; }

    public string? StableId { get; init; }

    public string? CssSelector { get; init; }

    public string? Value { get; init; }
}

public sealed record SurfaceActionResult
{
    public required bool Succeeded { get; init; }

    public string? Value { get; init; }

    public string? MatchedStrategy { get; init; }

    public string? ErrorCode { get; init; }

    public string? SafeMessage { get; init; }
}

public sealed record SurfaceInspection
{
    public required SurfaceInspectionKind Kind { get; init; }

    public TargetDescriptor? Target { get; init; }

    public string? AttributeName { get; init; }
}

public sealed record SurfaceInspectionResult
{
    public required bool Succeeded { get; init; }

    public string? Value { get; init; }

    public string? MatchedStrategy { get; init; }

    public string? ErrorCode { get; init; }

    public string? SafeMessage { get; init; }
}

public enum SurfaceInspectionKind
{
    Visible,
    Text,
    Value,
    Attribute,
    Url,
    State
}

public enum EvidenceKind
{
    Screenshot,
    Snapshot
}
