namespace ComputerUse.Core.Contracts;

public sealed record TaskRequest
{
    public required string Goal { get; init; }

    public required TargetDefinition Target { get; init; }

    public IReadOnlyDictionary<string, object?> Inputs { get; init; }
        = new Dictionary<string, object?>();

    public RunLimits Limits { get; init; } = new();

    public required string PolicyProfile { get; init; }
}

public sealed record TargetDefinition
{
    public required Uri EntryPoint { get; init; }

    public SurfaceKind Surface { get; init; } = SurfaceKind.Web;

    public string? ApplicationFamily { get; init; }
}

public sealed record RunLimits
{
    public int MaxSteps { get; init; } = 20;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    public int RepeatedStateLimit { get; init; } = 3;
}

public sealed record RunContext
{
    public required string RunId { get; init; }

    public required RuntimeMode Mode { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string PolicyProfile { get; init; }

    public required TargetDefinition Target { get; init; }

    public RunLimits Limits { get; init; } = new();
}

public enum RuntimeMode
{
    Discovery,
    Replay
}

public enum SurfaceKind
{
    Web,
    Desktop
}
