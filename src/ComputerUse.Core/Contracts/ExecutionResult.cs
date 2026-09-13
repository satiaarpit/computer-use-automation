using System.Text.Json;

namespace ComputerUse.Core.Contracts;

public sealed record ExecutionResult
{
    public required string RunId { get; init; }

    public required string CapabilityId { get; init; }

    public required ExecutionStatus Status { get; init; }

    public IReadOnlyDictionary<string, JsonElement> Outputs { get; init; }
        = new Dictionary<string, JsonElement>();

    public string? OutcomeCode { get; init; }

    public FailureDetail? Failure { get; init; }

    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];
}

public sealed record FailureDetail
{
    public required string Category { get; init; }

    public required string Message { get; init; }

    public string? StepId { get; init; }

    public string? Expected { get; init; }

    public string? Observed { get; init; }
}

public sealed record EvidenceReference
{
    public required string Kind { get; init; }

    public required string RelativePath { get; init; }

    public DataClassification Classification { get; init; } = DataClassification.Internal;

    public EvidenceProtection Protection { get; init; } = EvidenceProtection.Redacted;
}

public enum EvidenceProtection
{
    Redacted,
    Omitted,
    Synthetic
}

public enum ExecutionStatus
{
    Success,
    BusinessOutcome,
    InterventionRequired,
    Cancelled,
    Failure
}
