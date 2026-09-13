namespace ComputerUse.Core.Contracts;

public sealed record CapabilityStep
{
    public required string Id { get; init; }

    public required SemanticAction Action { get; init; }

    public IReadOnlyList<Condition> Preconditions { get; init; } = [];

    public Condition? Postcondition { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public RetryPolicy Retry { get; init; } = RetryPolicy.None;

    public RiskLevel Risk { get; init; } = RiskLevel.Safe;
}

public sealed record SemanticAction
{
    public required ActionKind Kind { get; init; }

    public TargetDescriptor? Target { get; init; }

    public string? ValueTemplate { get; init; }

    public Uri? Destination { get; init; }

    public string? OutputName { get; init; }

    public TimeSpan? Duration { get; init; }
}

public sealed record TargetDescriptor
{
    public required IReadOnlyList<TargetStrategy> Strategies { get; init; }

    public bool RequireUniqueMatch { get; init; } = true;
}

public sealed record TargetStrategy
{
    public required TargetStrategyKind Kind { get; init; }

    public required string Value { get; init; }

    public string? Scope { get; init; }

    public string? ControlType { get; init; }
}

public sealed record Condition
{
    public required ConditionKind Kind { get; init; }

    public TargetDescriptor? Target { get; init; }

    public string? ExpectedTemplate { get; init; }

    public bool Negate { get; init; }
}

public sealed record RetryPolicy
{
    public static RetryPolicy None { get; } = new();

    public int MaxAttempts { get; init; } = 1;

    public TimeSpan Delay { get; init; } = TimeSpan.Zero;

    public IReadOnlyList<string> RecoverableConditionCodes { get; init; } = [];
}

public enum ActionKind
{
    Navigate,
    Click,
    Type,
    Select,
    Read,
    Wait,
    Complete,
    RequestIntervention
}

public enum TargetStrategyKind
{
    AccessibleRoleAndName,
    Label,
    StableId,
    Text,
    StructuralRelation,
    Css,
    XPath,
    VisualAnchor
}

public enum ConditionKind
{
    Visible,
    Hidden,
    TextEquals,
    TextContains,
    ValueEquals,
    UrlMatches,
    StateEquals
}

public enum RiskLevel
{
    Safe,
    ReversibleWrite,
    Irreversible
}
