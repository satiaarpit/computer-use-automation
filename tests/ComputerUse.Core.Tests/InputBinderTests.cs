using System.Text.Json;
using ComputerUse.Core.Binding;
using ComputerUse.Core.Contracts;

namespace ComputerUse.Core.Tests;

public sealed class InputBinderTests
{
    [Fact]
    public void Bind_AcceptsDeclaredTypedInputs()
    {
        var definitions = new[]
        {
            new InputDefinition { Name = "query", Type = ValueTypeKind.String, Pattern = "^[a-z]+$" },
            new InputDefinition { Name = "page", Type = ValueTypeKind.Integer, Required = false }
        };
        var supplied = ParseInputs("""{"query":"maps","page":2}""");

        var result = InputBinder.Bind(definitions, supplied);

        Assert.True(result.Validation.IsValid);
        Assert.NotNull(result.Bound);
        Assert.Equal("maps", result.Bound.Values["query"].GetString());
    }

    [Fact]
    public void Bind_RejectsMissingUnknownAndIncorrectInputs()
    {
        var definitions = new[]
        {
            new InputDefinition { Name = "query", Type = ValueTypeKind.String },
            new InputDefinition { Name = "page", Type = ValueTypeKind.Integer }
        };
        var supplied = ParseInputs("""{"page":"two","unexpected":true}""");

        var result = InputBinder.Bind(definitions, supplied);

        Assert.Null(result.Bound);
        Assert.Contains(result.Validation.Issues, item => item.Path == "inputs.query" && item.Code == "required");
        Assert.Contains(result.Validation.Issues, item => item.Path == "inputs.page" && item.Code == "type-mismatch");
        Assert.Contains(result.Validation.Issues, item => item.Path == "inputs.unexpected" && item.Code == "unknown-input");
    }

    [Fact]
    public void Bind_RejectsEnumValueOutsideAllowedSet()
    {
        var definitions = new[]
        {
            new InputDefinition
            {
                Name = "quality",
                Type = ValueTypeKind.Enum,
                AllowedValues = ["standard", "high"]
            }
        };
        var supplied = ParseInputs("""{"quality":"ultra"}""");

        var result = InputBinder.Bind(definitions, supplied);

        Assert.Contains(result.Validation.Issues, item => item.Code == "value-not-allowed");
    }

    [Theory]
    [InlineData(ValueTypeKind.String, "\"text\"")]
    [InlineData(ValueTypeKind.Integer, "42")]
    [InlineData(ValueTypeKind.Decimal, "42.5")]
    [InlineData(ValueTypeKind.Boolean, "true")]
    [InlineData(ValueTypeKind.Date, "\"2026-09-10\"")]
    [InlineData(ValueTypeKind.Enum, "\"standard\"")]
    public void Bind_AcceptsEveryPrimitiveType(ValueTypeKind type, string jsonValue)
    {
        var definition = new InputDefinition
        {
            Name = "value",
            Type = type,
            AllowedValues = type == ValueTypeKind.Enum ? ["standard"] : null
        };
        var supplied = ParseInputs($$"""{"value":{{jsonValue}}}""");

        var result = InputBinder.Bind([definition], supplied);

        Assert.True(result.Validation.IsValid);
        Assert.NotNull(result.Bound);
    }

    private static IReadOnlyDictionary<string, JsonElement> ParseInputs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}
