using ComputerUse.Core;
using ComputerUse.Core.Contracts;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ComputerUse.Replay;

public static class TraceCompiler
{
    public static CapabilityArtifact Compile(
        GoalRequest request,
        LoopResult result,
        string capabilityId = "discovered-capability.v1",
        string? model = null)
    {
        if (!result.Completed || result.Steps.Count == 0 || result.Steps[^1].Decision.Kind != DecisionKind.Complete)
            throw new InvalidOperationException("Only an independently verified successful trace can be compiled.");

        ValidateInputNames(request.Inputs.Keys);
        ValidateInputValues(request.Inputs);
        var actions = new List<CapabilityStep>();
        var outputs = new List<OutputDefinition>();
        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in result.Steps.Where(step => step.Decision.Kind == DecisionKind.Action))
        {
            var action = Parameterize(step.Decision.Action!, request.Inputs);
            if (action.Kind == ActionKind.Read && step.ActionResult is { Succeeded: true, Value: null })
            {
                throw new InvalidOperationException("A successful read must provide a value before it can be compiled.");
            }

            if (action.Kind == ActionKind.Read && action.Target is not null && step.ActionResult is { Succeeded: true, Value: not null })
            {
                var baseName = SafeOutputName(action.OutputName, request.Inputs) ??
                    SafeTargetOutputName(step.Decision.Action!.Target, request.Inputs) ??
                    $"result-{outputs.Count + 1}";
                var name = UniqueName(baseName, outputNames);
                action = action with { OutputName = name };
                outputs.Add(new OutputDefinition
                {
                    Name = name,
                    Type = InferPrimitiveType(step.ActionResult.Value),
                    Required = true,
                    Classification = DataClassification.Internal,
                    Extraction = new ValueExtraction { Target = action.Target, Kind = ExtractionKind.Text }
                });
            }

            actions.Add(new CapabilityStep
            {
                Id = $"step-{actions.Count + 1}",
                Action = action,
                Timeout = TimeSpan.FromSeconds(15),
                Retry = action.Kind == ActionKind.Read
                    ? new RetryPolicy
                    {
                        MaxAttempts = 4,
                        Delay = TimeSpan.FromMilliseconds(250),
                        RecoverableConditionCodes = ["target-not-found"]
                    }
                    : RetryPolicy.None,
                Risk = step.Decision.Risk!.Value
            });
        }
        var readAction = actions.LastOrDefault(step => step.Action.Kind == ActionKind.Read)?.Action;
        var completion = result.Steps[^1].Decision;
        var checkpointTarget = completion.CompletionTarget ?? new TargetDescriptor
        {
            Strategies = [new TargetStrategy { Kind = TargetStrategyKind.Text, Value = completion.CompletionText! }]
        };
        checkpointTarget = ParameterizeTarget(checkpointTarget, request.Inputs)!;

        return new CapabilityArtifact
        {
            CapabilityId = capabilityId,
            Name = "Discovered UI capability",
            Description = ParameterizeText(request.Goal, request.Inputs, allowEmbedded: true)!,
            Target = new CapabilityTarget { Surface = SurfaceKind.Web, EntryPointTemplate = ParameterizeUri(request.Url, request.Inputs)! },
            Risk = actions.Count == 0 ? RiskLevel.Safe : actions.Max(step => step.Risk),
            Inputs = request.Inputs.Select(input => new InputDefinition
            {
                Name = input.Key,
                Type = InferPrimitiveType(input.Value),
                Required = true,
                Classification = IsSensitiveName(input.Key)
                    ? DataClassification.Secret
                    : DataClassification.Internal,
                Pattern = InferPrimitiveType(input.Value) == ValueTypeKind.String ? "^.{1,200}$" : null
            }).ToArray(),
            Outputs = outputs.ToArray(),
            Steps = actions,
            Outcomes = readAction?.Target is null
                ? []
                : [new KnownOutcome
                {
                    Code = "target-not-found",
                    Description = "The asynchronous result target is not visible yet.",
                    Kind = OutcomeKind.RecoverableCondition,
                    Detection = new Condition { Kind = ConditionKind.Hidden, Target = readAction.Target }
                }],
            Checkpoint = new Condition
            {
                Kind = ConditionKind.TextContains,
                Target = checkpointTarget,
                ExpectedTemplate = ParameterizeText(completion.CompletionText, request.Inputs, allowEmbedded: true)
            },
            Provenance = new CapabilityProvenance
            {
                DiscoveryRunId = string.IsNullOrWhiteSpace(result.RunId)
                    ? Guid.NewGuid().ToString("N")
                    : result.RunId,
                CreatedAt = DateTimeOffset.UtcNow,
                GeneratorVersion = "0.2.0",
                Model = model
            }
        };
    }

    private static SemanticAction Parameterize(SemanticAction action, IReadOnlyDictionary<string, string> inputs)
    {
        return action with
        {
            ValueTemplate = ParameterizeText(action.ValueTemplate, inputs, allowEmbedded: true),
            Destination = ParameterizeUri(action.Destination, inputs),
            Target = ParameterizeTarget(action.Target, inputs),
            OutputName = action.Kind == ActionKind.Read
                ? SafeOutputName(action.OutputName, inputs) ?? SafeTargetOutputName(action.Target, inputs) ?? "result"
                : null
        };
    }

    private static TargetDescriptor? ParameterizeTarget(
        TargetDescriptor? target,
        IReadOnlyDictionary<string, string> inputs) => target is null
        ? null
        : target with
        {
            Strategies = target.Strategies.Select(strategy => strategy with
            {
                Value = ParameterizeText(strategy.Value, inputs, allowEmbedded: false)!,
                Scope = ParameterizeText(strategy.Scope, inputs, allowEmbedded: false)
            }).ToArray()
        };

    private static Uri? ParameterizeUri(Uri? uri, IReadOnlyDictionary<string, string> inputs)
    {
        if (uri is null)
        {
            return null;
        }

        var template = uri.OriginalString;
        foreach (var input in inputs.Where(input => !string.IsNullOrEmpty(input.Value)))
        {
            var placeholder = $"${{{input.Key}}}";
            var encoded = Uri.EscapeDataString(input.Value);
            template = template.Replace(encoded, placeholder, StringComparison.OrdinalIgnoreCase);
            template = template.Replace(input.Value, placeholder, StringComparison.Ordinal);
        }

        return new Uri(template, UriKind.Absolute);
    }

    private static string? ParameterizeText(
        string? text,
        IReadOnlyDictionary<string, string> inputs,
        bool allowEmbedded)
    {
        if (text is null)
        {
            return null;
        }

        var candidates = inputs
            .Where(input => !string.IsNullOrEmpty(input.Value))
            .OrderByDescending(input => input.Value.Length)
            .ToArray();
        var exact = candidates.FirstOrDefault(input => string.Equals(text, input.Value, StringComparison.Ordinal));
        if (!string.IsNullOrEmpty(exact.Value))
        {
            return $"${{{exact.Key}}}";
        }

        if (candidates.Length == 0)
        {
            return text;
        }

        var byValue = candidates.ToDictionary(input => input.Value, input => input.Key, StringComparer.Ordinal);
        var pattern = string.Join("|", candidates.Select(input => Regex.Escape(input.Value)));
        return Regex.Replace(text, pattern, match =>
        {
            if (!allowEmbedded || match.Value.Length < 3)
            {
                throw new InvalidOperationException(
                    $"Input '{byValue[match.Value]}' appears ambiguously inside a persisted field and cannot be safely parameterized.");
            }

            return $"${{{byValue[match.Value]}}}";
        }, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private static ValueTypeKind InferPrimitiveType(string value)
    {
        if (bool.TryParse(value, out _))
        {
            return ValueTypeKind.Boolean;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) &&
            string.Equals(integer.ToString(CultureInfo.InvariantCulture), value, StringComparison.Ordinal))
        {
            return ValueTypeKind.Integer;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) &&
            value.Contains('.', StringComparison.Ordinal) &&
            string.Equals(
                number.ToString(CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.'),
                value.TrimEnd('0').TrimEnd('.'),
                StringComparison.Ordinal))
        {
            return ValueTypeKind.Decimal;
        }

        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
        {
            return ValueTypeKind.Date;
        }

        return ValueTypeKind.String;
    }

    private static string? NormalizeIdentifier(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var normalized = Regex.Replace(candidate.Trim().ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        return normalized.Length is > 0 and <= 64 ? normalized : null;
    }

    private static string? SafeOutputName(string? candidate, IReadOnlyDictionary<string, string> inputs)
    {
        if (candidate is not null && inputs.Values.Any(value =>
                !string.IsNullOrEmpty(value) && candidate.Contains(value, StringComparison.Ordinal)))
        {
            return null;
        }

        return NormalizeIdentifier(candidate);
    }

    private static string? SafeTargetOutputName(
        TargetDescriptor? target,
        IReadOnlyDictionary<string, string> inputs) => SafeOutputName(
            target?.Strategies
                .FirstOrDefault(strategy => strategy.Kind is TargetStrategyKind.StableId or TargetStrategyKind.Label)
                ?.Value,
            inputs);

    private static string UniqueName(string baseName, ISet<string> usedNames)
    {
        var name = baseName;
        for (var suffix = 2; !usedNames.Add(name); suffix++)
        {
            name = $"{baseName}-{suffix}";
        }

        return name;
    }

    private static void ValidateInputNames(IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                throw new InvalidOperationException($"Input name '{name}' cannot be represented by the capability template grammar.");
            }
        }
    }

    private static void ValidateInputValues(IReadOnlyDictionary<string, string> inputs)
    {
        var duplicate = inputs
            .Where(input => !string.IsNullOrEmpty(input.Value))
            .GroupBy(input => input.Value, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Inputs {string.Join(", ", duplicate.Select(input => $"'{input.Key}'"))} have the same discovery value, so parameter inference is ambiguous.");
        }
    }

    private static bool IsSensitiveName(string name) => Regex.IsMatch(
        Regex.Replace(name, "([a-z0-9])([A-Z])", "$1-$2"),
        "(^|[-_.])(api[-_.]?key|credential|password|passcode|secret|token|private[-_.]?key)($|[-_.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}
