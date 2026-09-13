using ComputerUse.Core.Contracts;
using ComputerUse.Core.Serialization;
using ComputerUse.Core.Validation;

namespace ComputerUse.Core.Tests;

public sealed class CapabilityContractTests
{
    [Fact]
    public void JsonRoundTrip_PreservesCapabilityContract()
    {
        var expected = CapabilityFixture.Create();

        var json = CapabilityJson.Serialize(expected);
        var actual = CapabilityJson.Deserialize(json);

        Assert.Equal(expected.CapabilityId, actual.CapabilityId);
        Assert.Equal(json, CapabilityJson.Serialize(actual));
        Assert.True(CapabilityValidator.Validate(actual).IsValid);
    }

    [Fact]
    public void Validate_RejectsUnsupportedSchemaVersion()
    {
        var artifact = CapabilityFixture.Create() with { SchemaVersion = 99 };

        var result = CapabilityValidator.Validate(artifact);

        var issue = Assert.Single(result.Issues, item => item.Code == "unsupported-schema-version");
        Assert.Equal("schemaVersion", issue.Path);
    }

    [Fact]
    public void Validate_RejectsMissingStepsAndDuplicateInputs()
    {
        var input = new InputDefinition { Name = "query", Type = ValueTypeKind.String };
        var artifact = CapabilityFixture.Create() with
        {
            Inputs = [input, input],
            Steps = []
        };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Contains(result.Issues, item => item.Code == "steps-required");
        Assert.Contains(result.Issues, item => item.Code == "duplicate-name" && item.Path == "inputs");
    }

    [Fact]
    public void Validate_AcceptsCurrentSchemaVersion()
    {
        var result = CapabilityValidator.Validate(CapabilityFixture.Create());

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
    }

    [Fact]
    public void Validate_ReportsNullRequiredNestedMembersWithoutThrowing()
    {
        var artifact = CapabilityFixture.Create() with
        {
            Target = null!,
            Steps = [null!],
            Checkpoint = null!,
            Provenance = null!
        };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Contains(result.Issues, item => item.Path == "target" && item.Code == "required");
        Assert.Contains(result.Issues, item => item.Path == "steps[0]" && item.Code == "required");
        Assert.Contains(result.Issues, item => item.Path == "checkpoint" && item.Code == "required");
        Assert.Contains(result.Issues, item => item.Path == "provenance" && item.Code == "required");
    }

    [Fact]
    public void Deserialize_RejectsMissingRequiredMembers()
    {
        const string json = """{"schemaVersion":1,"capabilityId":"incomplete"}""";

        Assert.Throws<System.Text.Json.JsonException>(() => CapabilityJson.Deserialize(json));
    }

    [Fact]
    public void Deserialize_RejectsUnknownMembers()
    {
        var json = CapabilityJson.Serialize(CapabilityFixture.Create())
            .Replace("\"schemaVersion\": 1,", "\"schemaVersion\": 1,\n  \"unknownMember\": true,", StringComparison.Ordinal);

        Assert.Throws<System.Text.Json.JsonException>(() => CapabilityJson.Deserialize(json));
    }
}
