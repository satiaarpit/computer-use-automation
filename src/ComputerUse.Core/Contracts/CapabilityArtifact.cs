namespace ComputerUse.Core.Contracts;

public sealed record CapabilityArtifact
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string CapabilityId { get; init; }

    public required string Name { get; init; }

    public required string Description { get; init; }

    public required CapabilityTarget Target { get; init; }

    public RiskLevel Risk { get; init; } = RiskLevel.Safe;

    public IReadOnlyList<InputDefinition> Inputs { get; init; } = [];

    public IReadOnlyList<OutputDefinition> Outputs { get; init; } = [];

    public required IReadOnlyList<CapabilityStep> Steps { get; init; }

    public IReadOnlyList<KnownOutcome> Outcomes { get; init; } = [];

    public required Condition Checkpoint { get; init; }

    public required CapabilityProvenance Provenance { get; init; }
}

public sealed record CapabilityTarget
{
    public required SurfaceKind Surface { get; init; }

    public string? ApplicationFamily { get; init; }

    public string? CompatibleVersionRange { get; init; }

    public required Uri EntryPointTemplate { get; init; }
}

public sealed record KnownOutcome
{
    public required string Code { get; init; }

    public required string Description { get; init; }

    public required OutcomeKind Kind { get; init; }

    public required Condition Detection { get; init; }
}

public sealed record CapabilityProvenance
{
    public required string DiscoveryRunId { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string GeneratorVersion { get; init; }

    public string? Model { get; init; }
}

public enum OutcomeKind
{
    BusinessOutcome,
    RecoverableCondition,
    InterventionRequired,
    HardFailure
}
