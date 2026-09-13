using ComputerUse.Core.Contracts;
using ComputerUse.Core.Policy;

namespace ComputerUse.Core.Tests;

public sealed class AutomationPolicyTests
{
    [Fact]
    public void Same_origin_policy_allows_matching_origin_and_routes()
    {
        var policy = AutomationPolicy.SameOrigin(new Uri("https://example.test/app/start"));

        Assert.True(policy.EvaluateLocation(new Uri("https://example.test/other")).Allowed);
        Assert.False(policy.EvaluateLocation(new Uri("http://example.test/other")).Allowed);
        Assert.False(policy.EvaluateLocation(new Uri("https://other.test/other")).Allowed);
    }

    [Theory]
    [InlineData("https://example.test/app", true)]
    [InlineData("https://example.test/app/records", true)]
    [InlineData("https://example.test/app?next=/other", true)]
    [InlineData("https://example.test/app#section", true)]
    [InlineData("https://example.test/other#/app", false)]
    [InlineData("https://example.test/application", false)]
    [InlineData("https://example.test/other", false)]
    public void Route_prefixes_respect_path_boundaries(string url, bool expectedAllowed)
    {
        var policy = CreatePolicy() with { AllowedPathPrefixes = ["/app"] };

        Assert.Equal(expectedAllowed, policy.EvaluateLocation(new Uri(url)).Allowed);
    }

    [Theory]
    [InlineData("https://example.test/app", true)]
    [InlineData("https://example.test:443/app", true)]
    [InlineData("https://example.test:8443/app", false)]
    public void Port_matching_uses_the_effective_uri_port(string url, bool expectedAllowed)
    {
        var policy = CreatePolicy();

        Assert.Equal(expectedAllowed, policy.EvaluateLocation(new Uri(url)).Allowed);
    }

    [Fact]
    public void Action_policy_denies_unlisted_actions_and_enforces_limit()
    {
        var policy = CreatePolicy() with
        {
            AllowedActions = new HashSet<ActionKind>([ActionKind.Read]),
            MaximumActions = 1
        };
        var enforcer = new PolicyEnforcer(policy);

        Assert.Equal("action-not-allowed", enforcer.BeforeAction(new SemanticAction { Kind = ActionKind.Click }).Code);
        Assert.True(enforcer.BeforeAction(new SemanticAction { Kind = ActionKind.Read, Target = Target() }).Allowed);
        Assert.Equal("action-limit-exceeded", enforcer.BeforeAction(new SemanticAction { Kind = ActionKind.Read, Target = Target() }).Code);
        Assert.Equal(1, enforcer.ActionCount);
    }

    [Fact]
    public void Direct_navigation_is_checked_before_execution()
    {
        var policy = CreatePolicy();

        var decision = policy.EvaluateAction(
            new SemanticAction { Kind = ActionKind.Navigate, Destination = new Uri("https://outside.test/") },
            priorActionCount: 0);

        Assert.False(decision.Allowed);
        Assert.Equal("navigation-not-allowed", decision.Code);
    }

    [Theory]
    [InlineData(RiskLevel.Safe, true)]
    [InlineData(RiskLevel.ReversibleWrite, true)]
    [InlineData(RiskLevel.Irreversible, false)]
    public void Risk_policy_requires_intervention_only_for_irreversible_actions(
        RiskLevel risk,
        bool expectedAllowed)
    {
        var decision = CreatePolicy().EvaluateRisk(
            new SemanticAction { Kind = ActionKind.Read, Target = Target() },
            risk);

        Assert.Equal(expectedAllowed, decision.Allowed);
        Assert.Equal(expectedAllowed ? null : "intervention-required", decision.Code);
    }

    [Theory]
    [InlineData("button:Search", RiskLevel.Safe, true)]
    [InlineData("button:Confirm purchase", RiskLevel.Safe, false)]
    [InlineData("button:Transfer funds", RiskLevel.ReversibleWrite, false)]
    [InlineData("#checkout-button", RiskLevel.Safe, false)]
    [InlineData("opaque-control-42", RiskLevel.Safe, false)]
    public void Consequential_clicks_have_an_independent_minimum_risk(
        string targetName,
        RiskLevel declaredRisk,
        bool expectedAllowed)
    {
        var action = new SemanticAction
        {
            Kind = ActionKind.Click,
            Target = new TargetDescriptor
            {
                Strategies = [new TargetStrategy { Kind = TargetStrategyKind.AccessibleRoleAndName, Value = targetName }]
            }
        };

        Assert.Equal(expectedAllowed, CreatePolicy().EvaluateRisk(action, declaredRisk).Allowed);
    }

    private static AutomationPolicy CreatePolicy() => new()
    {
        AllowedSchemes = new HashSet<string>(["https"], StringComparer.OrdinalIgnoreCase),
        AllowedHosts = new HashSet<string>(["example.test"], StringComparer.OrdinalIgnoreCase),
        AllowedPorts = new HashSet<int>([443]),
        AllowedPathPrefixes = ["/"],
        AllowedActions = new HashSet<ActionKind>(Enum.GetValues<ActionKind>()),
        MaximumActions = 10
    };

    private static TargetDescriptor Target() => new()
    {
        Strategies = [new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "value" }]
    };
}
