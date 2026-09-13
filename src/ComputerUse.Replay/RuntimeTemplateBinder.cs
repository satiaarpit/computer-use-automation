using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ComputerUse.Core.Contracts;

namespace ComputerUse.Replay;

internal static partial class RuntimeTemplateBinder
{
    public static SemanticAction Bind(SemanticAction action, BoundInputs inputs) => action with
    {
        ValueTemplate = Expand(action.ValueTemplate, inputs),
        Destination = BindUri(action.Destination, inputs),
        Target = Bind(action.Target, inputs)
    };

    public static Condition Bind(Condition condition, BoundInputs inputs) => condition with
    {
        Target = Bind(condition.Target, inputs),
        ExpectedTemplate = Expand(condition.ExpectedTemplate, inputs)
    };

    public static ValueExtraction Bind(ValueExtraction extraction, BoundInputs inputs) => extraction with
    {
        Target = Bind(extraction.Target, inputs)!
    };

    public static Uri Bind(Uri uri, BoundInputs inputs) => BindUri(uri, inputs)!;

    private static TargetDescriptor? Bind(TargetDescriptor? target, BoundInputs inputs) => target is null
        ? null
        : target with
        {
            Strategies = target.Strategies.Select(strategy => strategy with
            {
                Value = Expand(strategy.Value, inputs)!,
                Scope = Expand(strategy.Scope, inputs)
            }).ToArray()
        };

    private static Uri? BindUri(Uri? uri, BoundInputs inputs)
    {
        if (uri is null)
        {
            return null;
        }

        var expanded = Expand(uri.OriginalString, inputs)!;
        return new Uri(expanded, UriKind.Absolute);
    }

    private static string? Expand(string? template, BoundInputs inputs)
    {
        if (template is null)
        {
            return null;
        }

        var expanded = Placeholder().Replace(template, match =>
        {
            var name = match.Groups[1].Value;
            if (!inputs.Values.TryGetValue(name, out var value))
            {
                throw new InvalidOperationException($"The template references unbound input '{name}'.");
            }

            return ToInvariantString(value);
        });
        if (expanded.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The template contains an invalid or unresolved input placeholder.");
        }

        return expanded;
    }

    private static string ToInvariantString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
        JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer.ToString(CultureInfo.InvariantCulture),
        JsonValueKind.Number when value.TryGetDecimal(out var number) => number.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Only validated primitive input values can be bound to templates.")
    };

    [GeneratedRegex(@"\$\{([A-Za-z][A-Za-z0-9_.-]*)\}", RegexOptions.CultureInvariant)]
    private static partial Regex Placeholder();
}
