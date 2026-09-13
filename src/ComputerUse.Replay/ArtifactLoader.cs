using System.Text.Json;
using ComputerUse.Core.Binding;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Serialization;
using ComputerUse.Core.Validation;

namespace ComputerUse.Replay;

public static class ArtifactLoader
{
    public static async Task<ArtifactLoadResult> LoadFileAndBindAsync(
        string path,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return LoadAndBind(json, suppliedInputs);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return ArtifactLoadResult.Invalid(new ValidationIssue(
                "artifact",
                "artifact-read-failed",
                $"The capability artifact could not be read: {exception.Message}"));
        }
    }

    public static ArtifactLoadResult LoadAndBind(
        string json,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs)
    {
        ArgumentNullException.ThrowIfNull(json);
        ArgumentNullException.ThrowIfNull(suppliedInputs);

        CapabilityArtifact artifact;
        try
        {
            artifact = CapabilityJson.Deserialize(json);
        }
        catch (JsonException exception)
        {
            return ArtifactLoadResult.Invalid(new ValidationIssue(
                "artifact",
                "invalid-json",
                $"The capability artifact could not be loaded: {exception.Message}"));
        }

        return Bind(artifact, suppliedInputs);
    }

    public static ArtifactLoadResult Bind(
        CapabilityArtifact artifact,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentNullException.ThrowIfNull(suppliedInputs);

        var artifactValidation = CapabilityValidator.Validate(artifact);
        if (!artifactValidation.IsValid)
        {
            return new ArtifactLoadResult(null, null, artifactValidation);
        }

        var (bound, inputValidation) = InputBinder.Bind(artifact.Inputs ?? [], suppliedInputs);
        return inputValidation.IsValid
            ? new ArtifactLoadResult(artifact, bound, ValidationResult.Success)
            : new ArtifactLoadResult(null, null, inputValidation);
    }
}

public sealed record ArtifactLoadResult(
    CapabilityArtifact? Artifact,
    BoundInputs? Inputs,
    ValidationResult Validation)
{
    public bool IsValid => Validation.IsValid && Artifact is not null && Inputs is not null;

    internal static ArtifactLoadResult Invalid(ValidationIssue issue) =>
        new(null, null, new ValidationResult([issue]));
}
