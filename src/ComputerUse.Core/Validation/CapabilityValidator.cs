using ComputerUse.Core.Contracts;
using ComputerUse.Core.Policy;
using System.Text.RegularExpressions;

namespace ComputerUse.Core.Validation;

public static class CapabilityValidator
{
    public static ValidationResult Validate(CapabilityArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var issues = new List<ValidationIssue>();

        if (artifact.SchemaVersion != CapabilityArtifact.CurrentSchemaVersion)
        {
            issues.Add(new("schemaVersion", "unsupported-schema-version",
                $"Schema version {artifact.SchemaVersion} is unsupported. Expected {CapabilityArtifact.CurrentSchemaVersion}."));
        }

        Required(artifact.CapabilityId, "capabilityId", issues);
        Required(artifact.Name, "name", issues);
        Required(artifact.Description, "description", issues);
        if (!Enum.IsDefined(artifact.Risk))
        {
            issues.Add(new("risk", "invalid-risk-level", "The capability risk classification is not recognized."));
        }

        if (artifact.Provenance is null)
        {
            issues.Add(new("provenance", "required", "A provenance definition is required."));
        }
        else
        {
            Required(artifact.Provenance.DiscoveryRunId, "provenance.discoveryRunId", issues);
            Required(artifact.Provenance.GeneratorVersion, "provenance.generatorVersion", issues);
        }

        if (artifact.Target is null)
        {
            issues.Add(new("target", "required", "A target definition is required."));
        }
        else if (artifact.Target.EntryPointTemplate is null || !artifact.Target.EntryPointTemplate.IsAbsoluteUri)
        {
            issues.Add(new("target.entryPointTemplate", "absolute-uri-required", "The target entry point must be an absolute URI."));
        }

        if (artifact.Steps is null || artifact.Steps.Count == 0)
        {
            issues.Add(new("steps", "steps-required", "A capability must contain at least one step."));
        }

        CollectionRequired(artifact.Inputs, "inputs", issues);
        CollectionRequired(artifact.Outputs, "outputs", issues);
        CollectionRequired(artifact.Outcomes, "outcomes", issues);

        var inputs = artifact.Inputs ?? [];
        var outputs = artifact.Outputs ?? [];
        var steps = artifact.Steps ?? [];
        var outcomes = artifact.Outcomes ?? [];
        if (steps.Count > 0 && steps.All(step => step is not null) &&
            Enum.IsDefined(artifact.Risk) &&
            steps.Where(step => step is not null).Max(step => step.Risk) > artifact.Risk)
        {
            issues.Add(new(
                "risk",
                "risk-understated",
                "The capability risk must be at least as restrictive as its riskiest step."));
        }
        var recoverableOutcomeCodes = outcomes
            .Where(outcome => outcome is not null && outcome.Kind == OutcomeKind.RecoverableCondition)
            .Select(outcome => outcome.Code)
            .ToHashSet(StringComparer.Ordinal);

        UniqueNames(inputs.Where(input => input is not null).Select(input => input.Name), "inputs", issues);
        UniqueNames(outputs.Where(output => output is not null).Select(output => output.Name), "outputs", issues);
        UniqueNames(steps.Where(step => step is not null).Select(step => step.Id), "steps", issues);
        UniqueNames(outcomes.Where(outcome => outcome is not null).Select(outcome => outcome.Code), "outcomes", issues);

        for (var index = 0; index < inputs.Count; index++)
        {
            var input = inputs[index];
            if (input is null)
            {
                issues.Add(new($"inputs[{index}]", "required", "An input definition is required."));
                continue;
            }

            Required(input.Name, $"inputs[{index}].name", issues);
            if (input.Type == ValueTypeKind.Enum && (input.AllowedValues is null || input.AllowedValues.Count == 0))
            {
                issues.Add(new($"inputs[{index}].allowedValues", "enum-values-required", "Enum inputs require allowed values."));
            }

            if (input.AllowedValues is not null)
            {
                UniqueNames(input.AllowedValues, $"inputs[{index}].allowedValues", issues);
            }

            if (input.Pattern is not null)
            {
                try
                {
                    _ = new Regex(input.Pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException)
                {
                    issues.Add(new($"inputs[{index}].pattern", "invalid-pattern", "The input pattern must be a valid regular expression."));
                }
            }
        }

        for (var index = 0; index < outputs.Count; index++)
        {
            var output = outputs[index];
            if (output is null)
            {
                issues.Add(new($"outputs[{index}]", "required", "An output definition is required."));
                continue;
            }

            Required(output.Name, $"outputs[{index}].name", issues);
            if (output.Extraction is null)
            {
                issues.Add(new($"outputs[{index}].extraction", "required", "An output extraction definition is required."));
                continue;
            }

            ValidateTarget(output.Extraction.Target, $"outputs[{index}].extraction.target", issues, required: true);
            if (output.Extraction.Kind == ExtractionKind.Attribute)
            {
                Required(output.Extraction.AttributeName, $"outputs[{index}].extraction.attributeName", issues);
            }
        }

        for (var index = 0; index < steps.Count; index++)
        {
            var step = steps[index];
            if (step is null)
            {
                issues.Add(new($"steps[{index}]", "required", "A capability step is required."));
                continue;
            }

            Required(step.Id, $"steps[{index}].id", issues);
            if (!string.IsNullOrWhiteSpace(step.Id) && !Regex.IsMatch(
                step.Id,
                "^[A-Za-z0-9_-]{1,100}$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)))
            {
                issues.Add(new(
                    $"steps[{index}].id",
                    "opaque-identifier-required",
                    "Step IDs may contain only ASCII letters, digits, hyphens, and underscores."));
            }
            if (!Enum.IsDefined(step.Risk))
            {
                issues.Add(new($"steps[{index}].risk", "invalid-risk-level", "The step risk classification is not recognized."));
            }
            if (step.Timeout <= TimeSpan.Zero)
            {
                issues.Add(new($"steps[{index}].timeout", "positive-timeout-required", "Step timeout must be positive."));
            }

            if (step.Retry is null)
            {
                issues.Add(new($"steps[{index}].retry", "required", "A retry policy is required."));
            }
            else
            {
                if (step.Retry.MaxAttempts < 1)
                {
                    issues.Add(new($"steps[{index}].retry.maxAttempts", "positive-attempts-required", "Retry attempts must be at least one."));
                }

                if (step.Retry.Delay < TimeSpan.Zero)
                {
                    issues.Add(new($"steps[{index}].retry.delay", "nonnegative-delay-required", "Retry delay cannot be negative."));
                }

                CollectionRequired(
                    step.Retry.RecoverableConditionCodes,
                    $"steps[{index}].retry.recoverableConditionCodes",
                    issues);
                var recoverableConditionCodes = step.Retry.RecoverableConditionCodes ?? [];
                for (var codeIndex = 0; codeIndex < recoverableConditionCodes.Count; codeIndex++)
                {
                    var code = recoverableConditionCodes[codeIndex];
                    if (!recoverableOutcomeCodes.Contains(code))
                    {
                        issues.Add(new(
                            $"steps[{index}].retry.recoverableConditionCodes[{codeIndex}]",
                            "recoverable-outcome-required",
                            $"Retry condition '{code}' must reference a declared recoverable outcome."));
                    }
                }
            }

            if (step.Action is null)
            {
                issues.Add(new($"steps[{index}].action", "required", "A semantic action is required."));
            }
            else
            {
                ValidateAction(step.Action, $"steps[{index}].action", issues);
                if (Enum.IsDefined(step.Risk) && ActionRiskClassifier.MinimumRisk(step.Action) > step.Risk)
                {
                    issues.Add(new(
                        $"steps[{index}].risk",
                        "risk-understated",
                        "The step risk is lower than the minimum classification inferred from its action."));
                }
            }

            CollectionRequired(step.Preconditions, $"steps[{index}].preconditions", issues);
            var preconditions = step.Preconditions ?? [];
            for (var conditionIndex = 0; conditionIndex < preconditions.Count; conditionIndex++)
            {
                var condition = preconditions[conditionIndex];
                if (condition is null)
                {
                    issues.Add(new($"steps[{index}].preconditions[{conditionIndex}]", "required", "A precondition is required."));
                }
                else
                {
                    ValidateCondition(condition, $"steps[{index}].preconditions[{conditionIndex}]", issues);
                }
            }

            if (step.Postcondition is not null)
            {
                ValidateCondition(step.Postcondition, $"steps[{index}].postcondition", issues);
            }
        }

        for (var index = 0; index < outcomes.Count; index++)
        {
            var outcome = outcomes[index];
            if (outcome is null)
            {
                issues.Add(new($"outcomes[{index}]", "required", "A known outcome definition is required."));
                continue;
            }

            Required(outcome.Code, $"outcomes[{index}].code", issues);
            Required(outcome.Description, $"outcomes[{index}].description", issues);
            if (outcome.Detection is null)
            {
                issues.Add(new($"outcomes[{index}].detection", "required", "An outcome detection condition is required."));
            }
            else
            {
                ValidateCondition(outcome.Detection, $"outcomes[{index}].detection", issues);
            }
        }

        if (artifact.Checkpoint is null)
        {
            issues.Add(new("checkpoint", "required", "A completion checkpoint is required."));
        }
        else
        {
            ValidateCondition(artifact.Checkpoint, "checkpoint", issues);
        }

        return new(issues);
    }

    private static void ValidateAction(SemanticAction action, string path, ICollection<ValidationIssue> issues)
    {
        var targetRequired = action.Kind is ActionKind.Click or ActionKind.Type or ActionKind.Select or ActionKind.Read;
        ValidateTarget(action.Target, $"{path}.target", issues, targetRequired);

        if (action.Kind == ActionKind.Navigate && (action.Destination is null || !action.Destination.IsAbsoluteUri))
        {
            issues.Add(new($"{path}.destination", "absolute-uri-required", "Navigation requires an absolute destination URI."));
        }

        if (action.Kind is ActionKind.Type or ActionKind.Select)
        {
            Required(action.ValueTemplate, $"{path}.valueTemplate", issues);
        }

        if (action.Kind == ActionKind.Read)
        {
            Required(action.OutputName, $"{path}.outputName", issues);
        }

        if (action.Kind == ActionKind.Wait && (action.Duration is null || action.Duration <= TimeSpan.Zero))
        {
            issues.Add(new($"{path}.duration", "positive-duration-required", "Wait requires a positive duration."));
        }
    }

    private static void ValidateCondition(Condition condition, string path, ICollection<ValidationIssue> issues)
    {
        ValidateTarget(condition.Target, $"{path}.target", issues, condition.Kind != ConditionKind.UrlMatches);
        if (condition.Kind is ConditionKind.TextEquals or ConditionKind.TextContains or ConditionKind.ValueEquals or
            ConditionKind.UrlMatches or ConditionKind.StateEquals)
        {
            Required(condition.ExpectedTemplate, $"{path}.expectedTemplate", issues);
        }
    }

    private static void ValidateTarget(
        TargetDescriptor? target,
        string path,
        ICollection<ValidationIssue> issues,
        bool required)
    {
        if (target is null)
        {
            if (required)
            {
                issues.Add(new(path, "required", "A target descriptor is required."));
            }

            return;
        }

        if (target.Strategies is null || target.Strategies.Count == 0)
        {
            issues.Add(new($"{path}.strategies", "target-strategy-required", "A target descriptor must contain at least one strategy."));
            return;
        }

        for (var index = 0; index < target.Strategies.Count; index++)
        {
            var strategy = target.Strategies[index];
            if (strategy is null)
            {
                issues.Add(new($"{path}.strategies[{index}]", "required", "A target strategy is required."));
                continue;
            }

            Required(strategy.Value, $"{path}.strategies[{index}].value", issues);
        }
    }

    private static void Required(string? value, string path, ICollection<ValidationIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            issues.Add(new(path, "required", "A non-empty value is required."));
        }
    }

    private static void CollectionRequired<T>(
        IReadOnlyList<T>? value,
        string path,
        ICollection<ValidationIssue> issues)
    {
        if (value is null)
        {
            issues.Add(new(path, "required", "A collection value is required; use an empty array when no items apply."));
        }
    }

    private static void UniqueNames(IEnumerable<string> values, string path, ICollection<ValidationIssue> issues)
    {
        var duplicates = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .GroupBy(value => value, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key);

        foreach (var duplicate in duplicates)
        {
            issues.Add(new(path, "duplicate-name", $"'{duplicate}' must be unique."));
        }
    }
}
