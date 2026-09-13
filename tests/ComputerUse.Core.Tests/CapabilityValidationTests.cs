using ComputerUse.Core.Contracts;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Validation;

namespace ComputerUse.Core.Tests;

public sealed class CapabilityValidationTests
{
    public static TheoryData<ActionKind, SemanticAction> ValidActions => new()
    {
        { ActionKind.Navigate, new SemanticAction { Kind = ActionKind.Navigate, Destination = new Uri("https://example.test/next") } },
        { ActionKind.Click, new SemanticAction { Kind = ActionKind.Click, Target = Target(TargetStrategyKind.AccessibleRoleAndName) } },
        { ActionKind.Type, new SemanticAction { Kind = ActionKind.Type, Target = Target(TargetStrategyKind.Label), ValueTemplate = "${value}" } },
        { ActionKind.Select, new SemanticAction { Kind = ActionKind.Select, Target = Target(TargetStrategyKind.StableId), ValueTemplate = "${option}" } },
        { ActionKind.Read, new SemanticAction { Kind = ActionKind.Read, Target = Target(TargetStrategyKind.Text), OutputName = "title" } },
        { ActionKind.Wait, new SemanticAction { Kind = ActionKind.Wait, Duration = TimeSpan.FromMilliseconds(100) } },
        { ActionKind.Complete, new SemanticAction { Kind = ActionKind.Complete } },
        { ActionKind.RequestIntervention, new SemanticAction { Kind = ActionKind.RequestIntervention } }
    };

    [Theory]
    [MemberData(nameof(ValidActions))]
    public void Validate_AcceptsEverySemanticActionCase(ActionKind expectedKind, SemanticAction action)
    {
        var risk = ActionRiskClassifier.MinimumRisk(action);
        var artifact = CapabilityFixture.Create() with
        {
            Risk = risk,
            Steps = [new CapabilityStep { Id = "case", Action = action, Risk = risk }]
        };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Equal(expectedKind, action.Kind);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues));
    }

    [Theory]
    [InlineData(ActionKind.Navigate, "absolute-uri-required")]
    [InlineData(ActionKind.Click, "required")]
    [InlineData(ActionKind.Type, "required")]
    [InlineData(ActionKind.Select, "required")]
    [InlineData(ActionKind.Read, "required")]
    [InlineData(ActionKind.Wait, "positive-duration-required")]
    public void Validate_RejectsIncompleteActionCases(ActionKind kind, string expectedCode)
    {
        var artifact = CapabilityFixture.Create() with
        {
            Steps = [new CapabilityStep { Id = "invalid", Action = new SemanticAction { Kind = kind } }]
        };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Contains(result.Issues, issue => issue.Code == expectedCode);
    }

    [Theory]
    [InlineData(TargetStrategyKind.AccessibleRoleAndName)]
    [InlineData(TargetStrategyKind.Label)]
    [InlineData(TargetStrategyKind.StableId)]
    [InlineData(TargetStrategyKind.Text)]
    [InlineData(TargetStrategyKind.StructuralRelation)]
    [InlineData(TargetStrategyKind.Css)]
    [InlineData(TargetStrategyKind.XPath)]
    [InlineData(TargetStrategyKind.VisualAnchor)]
    public void Validate_AcceptsEveryTargetStrategyCase(TargetStrategyKind kind)
    {
        var artifact = CapabilityFixture.Create() with
        {
            Checkpoint = new Condition { Kind = ConditionKind.Visible, Target = Target(kind) }
        };

        Assert.True(CapabilityValidator.Validate(artifact).IsValid);
    }

    [Theory]
    [InlineData(OutcomeKind.BusinessOutcome)]
    [InlineData(OutcomeKind.RecoverableCondition)]
    [InlineData(OutcomeKind.InterventionRequired)]
    [InlineData(OutcomeKind.HardFailure)]
    public void Validate_AcceptsEveryOutcomeCase(OutcomeKind kind)
    {
        var original = Assert.Single(CapabilityFixture.Create().Outcomes);
        var artifact = CapabilityFixture.Create() with { Outcomes = [original with { Kind = kind }] };

        Assert.True(CapabilityValidator.Validate(artifact).IsValid);
    }

    [Theory]
    [InlineData(ConditionKind.Visible, false)]
    [InlineData(ConditionKind.Hidden, false)]
    [InlineData(ConditionKind.TextEquals, true)]
    [InlineData(ConditionKind.TextContains, true)]
    [InlineData(ConditionKind.ValueEquals, true)]
    [InlineData(ConditionKind.UrlMatches, true)]
    [InlineData(ConditionKind.StateEquals, true)]
    public void Validate_AcceptsEveryConditionCase(ConditionKind kind, bool requiresExpectedValue)
    {
        var condition = new Condition
        {
            Kind = kind,
            Target = kind == ConditionKind.UrlMatches ? null : Target(TargetStrategyKind.AccessibleRoleAndName),
            ExpectedTemplate = requiresExpectedValue ? "expected" : null
        };
        var artifact = CapabilityFixture.Create() with { Checkpoint = condition };

        Assert.True(CapabilityValidator.Validate(artifact).IsValid);
    }

    [Theory]
    [InlineData(ExtractionKind.Text)]
    [InlineData(ExtractionKind.Value)]
    [InlineData(ExtractionKind.Attribute)]
    [InlineData(ExtractionKind.Url)]
    [InlineData(ExtractionKind.State)]
    public void Validate_AcceptsEveryExtractionCase(ExtractionKind kind)
    {
        var artifact = CapabilityFixture.Create() with
        {
            Outputs =
            [
                new OutputDefinition
                {
                    Name = "value",
                    Type = ValueTypeKind.String,
                    Extraction = new ValueExtraction
                    {
                        Kind = kind,
                        Target = Target(TargetStrategyKind.StableId),
                        AttributeName = kind == ExtractionKind.Attribute ? "data-value" : null
                    }
                }
            ]
        };

        Assert.True(CapabilityValidator.Validate(artifact).IsValid);
    }

    [Fact]
    public void Validate_RejectsInvalidPatternRetryAndAttributeExtraction()
    {
        var artifact = CapabilityFixture.Create() with
        {
            Inputs = [new InputDefinition { Name = "value", Type = ValueTypeKind.String, Pattern = "[" }],
            Outputs =
            [
                new OutputDefinition
                {
                    Name = "attribute",
                    Type = ValueTypeKind.String,
                    Extraction = new ValueExtraction { Kind = ExtractionKind.Attribute, Target = Target(TargetStrategyKind.StableId) }
                }
            ],
            Steps =
            [
                new CapabilityStep
                {
                    Id = "bad-retry",
                    Action = new SemanticAction { Kind = ActionKind.Complete },
                    Retry = new RetryPolicy { MaxAttempts = 0, Delay = TimeSpan.FromSeconds(-1) }
                }
            ]
        };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Contains(result.Issues, issue => issue.Code == "invalid-pattern");
        Assert.Contains(result.Issues, issue => issue.Code == "positive-attempts-required");
        Assert.Contains(result.Issues, issue => issue.Code == "nonnegative-delay-required");
        Assert.Contains(result.Issues, issue => issue.Path.EndsWith("attributeName", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Requires_retry_codes_to_reference_declared_recoverable_outcomes()
    {
        var artifact = CapabilityFixture.Create();
        var retryingStep = artifact.Steps[0] with
        {
            Retry = new RetryPolicy
            {
                MaxAttempts = 2,
                RecoverableConditionCodes = ["slow-load", "no-results"]
            }
        };
        var recoverableOutcome = artifact.Outcomes[0] with
        {
            Code = "slow-load",
            Kind = OutcomeKind.RecoverableCondition
        };

        var result = CapabilityValidator.Validate(artifact with
        {
            Steps = [retryingStep],
            Outcomes = [recoverableOutcome, artifact.Outcomes[0]]
        });

        Assert.Contains(result.Issues, issue =>
            issue.Code == "recoverable-outcome-required" && issue.Message.Contains("no-results", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Issues, issue =>
            issue.Code == "recoverable-outcome-required" && issue.Message.Contains("slow-load", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_Rejects_malformed_step_conditions()
    {
        var original = CapabilityFixture.Create();
        var artifact = original with
        {
            Steps =
            [
                original.Steps[0] with
                {
                    Preconditions = [new Condition { Kind = ConditionKind.TextEquals, Target = null }],
                    Postcondition = new Condition { Kind = ConditionKind.ValueEquals, Target = Target(TargetStrategyKind.StableId) }
                }
            ]
        };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Contains(result.Issues, issue => issue.Path == "steps[0].preconditions[0].target" && issue.Code == "required");
        Assert.Contains(result.Issues, issue => issue.Path == "steps[0].preconditions[0].expectedTemplate" && issue.Code == "required");
        Assert.Contains(result.Issues, issue => issue.Path == "steps[0].postcondition.expectedTemplate" && issue.Code == "required");
    }

    [Fact]
    public void Validate_Rejects_explicit_null_collections_from_persisted_artifacts()
    {
        var artifact = CapabilityFixture.Create() with { Inputs = null! };

        var result = CapabilityValidator.Validate(artifact);

        Assert.Contains(result.Issues, issue => issue.Path == "inputs" && issue.Code == "required");
    }

    [Fact]
    public void Validate_rejects_unknown_and_understated_risk_classifications()
    {
        var original = CapabilityFixture.Create();
        var invalid = CapabilityValidator.Validate(original with { Risk = (RiskLevel)999 });
        var understated = CapabilityValidator.Validate(original with
        {
            Risk = RiskLevel.Safe,
            Steps = [original.Steps[0] with { Risk = RiskLevel.Irreversible }]
        });

        Assert.Contains(invalid.Issues, issue => issue.Path == "risk" && issue.Code == "invalid-risk-level");
        Assert.Contains(understated.Issues, issue => issue.Path == "risk" && issue.Code == "risk-understated");
    }

    [Fact]
    public void Validate_rejects_nonopaque_step_identifier()
    {
        var original = CapabilityFixture.Create();
        var result = CapabilityValidator.Validate(original with
        {
            Steps = [original.Steps[0] with { Id = "query=Borealis" }]
        });

        Assert.Contains(result.Issues, issue =>
            issue.Path == "steps[0].id" && issue.Code == "opaque-identifier-required");
    }

    private static TargetDescriptor Target(TargetStrategyKind kind) => new()
    {
        Strategies = [new TargetStrategy { Kind = kind, Value = "stable-target" }]
    };
}
