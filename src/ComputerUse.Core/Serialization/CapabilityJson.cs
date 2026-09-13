using System.Text.Json;
using System.Text.Json.Serialization;
using ComputerUse.Core.Contracts;

namespace ComputerUse.Core.Serialization;

public static class CapabilityJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize(CapabilityArtifact artifact) =>
        JsonSerializer.Serialize(artifact, Options);

    public static CapabilityArtifact Deserialize(string json) =>
        JsonSerializer.Deserialize<CapabilityArtifact>(json, Options)
        ?? throw new JsonException("The capability artifact is empty.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
