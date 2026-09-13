using ComputerUse.Core.Contracts;
using ComputerUse.Core.Validation;

namespace ComputerUse.Core.Configuration;

public sealed record ApplicationSettings
{
    public required GeminiSettings Gemini { get; init; }

    public required ComputerUseSettings ComputerUse { get; init; }
}

public sealed record GeminiSettings
{
    public string ApiKeyEnvironmentVariable { get; init; } = "GEMINI_API_KEY";

    public string? Model { get; init; }
}

public sealed record ComputerUseSettings
{
    public string ArtifactRoot { get; init; } = "artifacts";

    public string EvidenceRoot { get; init; } = "evidence/runtime";

    public int DefaultMaxSteps { get; init; } = 20;

    public int DefaultTimeoutSeconds { get; init; } = 180;
}

public static class StartupValidator
{
    public static ValidationResult Validate(
        ApplicationSettings settings,
        RuntimeMode mode,
        Func<string, string?>? readEnvironmentVariable = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        readEnvironmentVariable ??= Environment.GetEnvironmentVariable;
        var issues = new List<ValidationIssue>();

        if (settings.Gemini is null)
        {
            issues.Add(new("gemini", "required", "Gemini settings are required."));
        }

        if (settings.ComputerUse is null)
        {
            issues.Add(new("computerUse", "required", "Computer-use settings are required."));
            return new(issues);
        }

        ValidateRelativeRoot(settings.ComputerUse.ArtifactRoot, "computerUse.artifactRoot", issues);
        ValidateRelativeRoot(settings.ComputerUse.EvidenceRoot, "computerUse.evidenceRoot", issues);

        if (settings.ComputerUse.DefaultMaxSteps < 1)
        {
            issues.Add(new("computerUse.defaultMaxSteps", "positive-value-required", "Default maximum steps must be at least one."));
        }

        if (settings.ComputerUse.DefaultTimeoutSeconds < 1)
        {
            issues.Add(new("computerUse.defaultTimeoutSeconds", "positive-value-required", "Default timeout must be at least one second."));
        }

        if (mode == RuntimeMode.Discovery && settings.Gemini is not null)
        {
            if (string.IsNullOrWhiteSpace(settings.Gemini.Model) || settings.Gemini.Model.StartsWith('<'))
            {
                issues.Add(new("gemini.model", "model-required", "Discovery mode requires a configured Gemini model."));
            }

            if (string.IsNullOrWhiteSpace(settings.Gemini.ApiKeyEnvironmentVariable))
            {
                issues.Add(new("gemini.apiKeyEnvironmentVariable", "required", "Discovery mode requires an API-key environment variable name."));
            }
            else if (string.IsNullOrWhiteSpace(readEnvironmentVariable(settings.Gemini.ApiKeyEnvironmentVariable)))
            {
                issues.Add(new(
                    "gemini.apiKeyEnvironmentVariable",
                    "gemini-api-key-required",
                    $"Discovery mode requires the {settings.Gemini.ApiKeyEnvironmentVariable} environment variable."));
            }
        }

        return new(issues);
    }

    private static void ValidateRelativeRoot(string? value, string path, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(path, "required", "A relative storage root is required."));
            return;
        }

        var normalizedSegments = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var hasDriveRoot = value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '/' or '\\';
        var hasNetworkRoot = value.StartsWith("//", StringComparison.Ordinal) || value.StartsWith("\\\\", StringComparison.Ordinal);

        if (Path.IsPathRooted(value) || hasDriveRoot || hasNetworkRoot || normalizedSegments.Contains("..", StringComparer.Ordinal))
        {
            issues.Add(new(path, "confined-relative-path-required", "Storage roots must be relative and cannot traverse parent directories."));
        }
    }
}
