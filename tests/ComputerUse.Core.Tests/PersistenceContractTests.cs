using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Serialization;

namespace ComputerUse.Core.Tests;

public sealed class PersistenceContractTests
{
    [Theory]
    [InlineData(DataClassification.Public)]
    [InlineData(DataClassification.Internal)]
    [InlineData(DataClassification.Confidential)]
    [InlineData(DataClassification.Secret)]
    public void CapabilityJson_RoundTripsEveryDataClassification(DataClassification classification)
    {
        var input = Assert.Single(CapabilityFixture.Create().Inputs);
        var artifact = CapabilityFixture.Create() with { Inputs = [input with { Classification = classification }] };

        var actual = CapabilityJson.Deserialize(CapabilityJson.Serialize(artifact));

        Assert.Equal(classification, Assert.Single(actual.Inputs).Classification);
    }

    [Fact]
    public void PersistableContracts_DoNotContainBoundInputValues()
    {
        const string canarySecret = "canary-value-that-must-not-persist";
        using var document = JsonDocument.Parse($$"""{"password":"{{canarySecret}}"}""");
        var boundInputs = new BoundInputs(new Dictionary<string, JsonElement>
        {
            ["password"] = document.RootElement.GetProperty("password").Clone()
        });
        var input = new InputDefinition
        {
            Name = "password",
            Type = ValueTypeKind.String,
            Classification = DataClassification.Secret
        };
        var artifact = CapabilityFixture.Create() with { Inputs = [input] };
        var context = new RunContext
        {
            RunId = "run-safe",
            Mode = RuntimeMode.Replay,
            StartedAt = DateTimeOffset.UnixEpoch,
            PolicyProfile = "default",
            Target = new TargetDefinition { EntryPoint = new Uri("https://example.test"), Surface = SurfaceKind.Web }
        };
        var result = new ExecutionResult
        {
            RunId = context.RunId,
            CapabilityId = artifact.CapabilityId,
            Status = ExecutionStatus.Success,
            Evidence =
            [
                new EvidenceReference
                {
                    Kind = "observation",
                    RelativePath = "evidence/runtime/run-safe/observation.json",
                    Classification = DataClassification.Confidential,
                    Protection = EvidenceProtection.Redacted
                }
            ]
        };

        var persisted = string.Join('\n',
            CapabilityJson.Serialize(artifact),
            JsonSerializer.Serialize(context, CapabilityJson.Options),
            JsonSerializer.Serialize(result, CapabilityJson.Options));

        Assert.Equal(canarySecret, boundInputs.Values["password"].GetString());
        Assert.DoesNotContain(canarySecret, persisted, StringComparison.Ordinal);
        Assert.Contains("\"classification\": \"secret\"", persisted, StringComparison.Ordinal);
        Assert.Contains("\"protection\": \"redacted\"", persisted, StringComparison.Ordinal);
    }
}
