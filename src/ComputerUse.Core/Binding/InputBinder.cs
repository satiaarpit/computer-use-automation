using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Validation;

namespace ComputerUse.Core.Binding;

public static partial class InputBinder
{
    public static (BoundInputs? Bound, ValidationResult Validation) Bind(
        IReadOnlyList<InputDefinition> definitions,
        IReadOnlyDictionary<string, JsonElement> supplied)
    {
        var issues = new List<ValidationIssue>();
        var definitionMap = definitions.ToDictionary(item => item.Name, StringComparer.Ordinal);

        foreach (var unknown in supplied.Keys.Where(key => !definitionMap.ContainsKey(key)))
        {
            issues.Add(new($"inputs.{unknown}", "unknown-input", "The input is not declared by this capability."));
        }

        foreach (var definition in definitions)
        {
            if (!supplied.TryGetValue(definition.Name, out var value))
            {
                if (definition.Required)
                {
                    issues.Add(new($"inputs.{definition.Name}", "required", "A required input was not supplied."));
                }

                continue;
            }

            ValidateValue(definition, value, issues);
        }

        return issues.Count == 0
            ? (new BoundInputs(new Dictionary<string, JsonElement>(supplied, StringComparer.Ordinal)), ValidationResult.Success)
            : (null, new ValidationResult(issues));
    }

    private static void ValidateValue(InputDefinition definition, JsonElement value, ICollection<ValidationIssue> issues)
    {
        var path = $"inputs.{definition.Name}";
        var isValidType = definition.Type switch
        {
            ValueTypeKind.String => value.ValueKind == JsonValueKind.String,
            ValueTypeKind.Integer => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            ValueTypeKind.Decimal => value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out _),
            ValueTypeKind.Boolean => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            ValueTypeKind.Date => value.ValueKind == JsonValueKind.String &&
                                  DateOnly.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
            ValueTypeKind.Enum => value.ValueKind == JsonValueKind.String,
            _ => false
        };

        if (!isValidType)
        {
            issues.Add(new(path, "type-mismatch", $"The value must be a {definition.Type.ToString().ToLowerInvariant()}."));
            return;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var text = value.GetString() ?? string.Empty;
        if (definition.Pattern is not null && !Regex.IsMatch(text, definition.Pattern, RegexOptions.CultureInvariant))
        {
            issues.Add(new(path, "pattern-mismatch", "The value does not match the declared pattern."));
        }

        if (definition.Type == ValueTypeKind.Enum &&
            definition.AllowedValues is not null &&
            !definition.AllowedValues.Contains(text, StringComparer.Ordinal))
        {
            issues.Add(new(path, "value-not-allowed", "The value is not in the declared allowed set."));
        }
    }
}
