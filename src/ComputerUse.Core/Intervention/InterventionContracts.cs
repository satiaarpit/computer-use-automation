using ComputerUse.Core.Contracts;
using ComputerUse.Core.Surfaces;

namespace ComputerUse.Core.Intervention;

public sealed record InterventionRequest
{
    public required string RunId { get; init; }

    public required string CapabilityId { get; init; }

    public required string Goal { get; init; }

    public required Uri Target { get; init; }

    public required string StepId { get; init; }

    public required SemanticAction ProposedAction { get; init; }

    public required string Reason { get; init; }

    public required RiskLevel Risk { get; init; }

    public required string RedactedStateSummary { get; init; }

    public Condition? ResumeCondition { get; init; }

    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];

    public IReadOnlyList<OperatorDecision> AllowedDecisions { get; init; } =
        [OperatorDecision.Resume, OperatorDecision.Complete, OperatorDecision.Decline, OperatorDecision.Cancel, OperatorDecision.Unresolved];
}

public sealed record ControlState
{
    public required ControlPhase Phase { get; init; }

    public required ControlOwner Owner { get; init; }

    public required long Version { get; init; }

    public OperatorDecision? Outcome { get; init; }
}

public sealed record ControlTransition(
    DateTimeOffset Timestamp,
    ControlPhase From,
    ControlPhase To,
    ControlOwner Owner,
    long Version,
    OperatorDecision? Decision = null);

public sealed record HumanActionRecord(
    DateTimeOffset Timestamp,
    long ControlVersion,
    ActionKind Action,
    bool Succeeded,
    string? ErrorCode);

public sealed record InterventionResolution(
    ControlState State,
    SurfaceObservation? FreshObservation = null,
    string? SafeMessage = null);

public enum ControlPhase
{
    Automation,
    Pausing,
    Human,
    Resuming,
    Completed,
    Cancelled
}

public enum ControlOwner
{
    Automation,
    Human,
    None
}

public enum OperatorDecision
{
    Resume,
    Complete,
    Decline,
    Cancel,
    Unresolved
}