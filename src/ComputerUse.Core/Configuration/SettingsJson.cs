using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComputerUse.Core.Configuration;

public static class SettingsJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static ApplicationSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<ApplicationSettings>(json, Options)
        ?? throw new JsonException("The application settings are empty.");
}
