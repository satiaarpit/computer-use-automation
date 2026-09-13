using ComputerUse.Core.Configuration;
using ComputerUse.Core.Contracts;

namespace ComputerUse.Core.Tests;

public sealed class StartupValidatorTests
{
    [Fact]
    public void ReplayMode_DoesNotRequireGeminiCredentials()
    {
        var result = StartupValidator.Validate(Settings(), RuntimeMode.Replay, _ => null);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void DiscoveryMode_ReportsMissingModelAndApiKeyClearly()
    {
        var result = StartupValidator.Validate(Settings(), RuntimeMode.Discovery, _ => null);

        Assert.Contains(result.Issues, issue => issue.Code == "model-required");
        Assert.Contains(result.Issues, issue => issue.Code == "gemini-api-key-required");
    }

    [Fact]
    public void DiscoveryMode_AcceptsConfiguredModelAndEnvironmentKey()
    {
        var settings = Settings() with
        {
            Gemini = new GeminiSettings { Model = "configured-model", ApiKeyEnvironmentVariable = "GEMINI_API_KEY" }
        };

        var result = StartupValidator.Validate(settings, RuntimeMode.Discovery, name => name == "GEMINI_API_KEY" ? "local-secret" : null);

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    public void StorageRoots_MustRemainConfined(string root)
    {
        var settings = Settings() with
        {
            ComputerUse = Settings().ComputerUse with { EvidenceRoot = root }
        };

        var result = StartupValidator.Validate(settings, RuntimeMode.Replay, _ => null);

        Assert.Contains(result.Issues, issue => issue.Code == "confined-relative-path-required");
    }

    [Fact]
    public void SettingsJson_RejectsUnknownProperties()
    {
        const string json = """
            {
              "Gemini": { "ApiKeyEnvironmentVariable": "GEMINI_API_KEY", "Model": "configured-model" },
              "ComputerUse": {
                "ArtifactRoot": "artifacts",
                "EvidenceRoot": "evidence/runtime",
                "DefaultMaxSteps": 20,
                "DefaultTimeoutSeconds": 180,
                "Unexpected": true
              }
            }
            """;

        Assert.Throws<System.Text.Json.JsonException>(() => SettingsJson.Deserialize(json));
    }

    private static ApplicationSettings Settings() => new()
    {
        Gemini = new GeminiSettings { Model = "<supported-model-name>" },
        ComputerUse = new ComputerUseSettings()
    };
}
