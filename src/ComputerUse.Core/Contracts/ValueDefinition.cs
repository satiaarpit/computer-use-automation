using System.Text.Json;
using System.Text.Json.Serialization;

namespace ComputerUse.Core.Contracts;

public sealed record InputDefinition
{
    public required string Name { get; init; }

    public required ValueTypeKind Type { get; init; }

    public bool Required { get; init; } = true;

    public DataClassification Classification { get; init; } = DataClassification.Internal;

    [JsonIgnore]
    public bool IsSensitive => Classification is DataClassification.Confidential or DataClassification.Secret;

    public string? Pattern { get; init; }

    public IReadOnlyList<string>? AllowedValues { get; init; }
}

public sealed record OutputDefinition
{
    public required string Name { get; init; }

    public required ValueTypeKind Type { get; init; }

    public required ValueExtraction Extraction { get; init; }

    public bool Required { get; init; } = true;

    public DataClassification Classification { get; init; } = DataClassification.Internal;

    [JsonIgnore]
    public bool IsSensitive => Classification is DataClassification.Confidential or DataClassification.Secret;
}

public sealed record ValueExtraction
{
    public required TargetDescriptor Target { get; init; }

    public ExtractionKind Kind { get; init; } = ExtractionKind.Text;

    public string? AttributeName { get; init; }
}

public enum ValueTypeKind
{
    String,
    Integer,
    Decimal,
    Boolean,
    Date,
    Enum
}

public enum ExtractionKind
{
    Text,
    Value,
    Attribute,
    Url,
    State
}

public enum DataClassification
{
    Public,
    Internal,
    Confidential,
    Secret
}

public sealed record BoundInputs(IReadOnlyDictionary<string, JsonElement> Values);
