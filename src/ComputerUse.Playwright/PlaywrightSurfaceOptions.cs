namespace ComputerUse.Playwright;

public sealed record PlaywrightSurfaceOptions
{
    public bool Headless { get; init; } = true;

    public int MaximumObservationCharacters { get; init; } = 12_000;

    public int MaximumInteractiveElements { get; init; } = 100;

    public string EvidenceDirectory { get; init; } = "evidence/runtime";

    public IReadOnlyCollection<string> SensitiveValues { get; init; } = [];

    internal void Validate()
    {
        if (MaximumObservationCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumObservationCharacters),
                "The observation character limit must be greater than zero.");
        }

        if (MaximumInteractiveElements <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumInteractiveElements),
                "The interactive-element limit must be greater than zero.");
        }

        _ = ComputerUse.Core.Evidence.EvidencePathPolicy.ResolveRoot(EvidenceDirectory);
    }
}
