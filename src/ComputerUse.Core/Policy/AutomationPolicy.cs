using ComputerUse.Core.Contracts;
using ComputerUse.Core.Surfaces;
using System.Text.RegularExpressions;

namespace ComputerUse.Core.Policy;

public sealed record AutomationPolicy
{
    public required IReadOnlySet<string> AllowedSchemes { get; init; }

    public required IReadOnlySet<string> AllowedHosts { get; init; }

    public required IReadOnlySet<int> AllowedPorts { get; init; }

    public required IReadOnlyList<string> AllowedPathPrefixes { get; init; }

    public required IReadOnlySet<ActionKind> AllowedActions { get; init; }

    public int MaximumActions { get; init; } = 100;

    public static AutomationPolicy SameOrigin(Uri entryPoint, int maximumActions = 100)
    {
        ArgumentNullException.ThrowIfNull(entryPoint);
        if (!entryPoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The policy entry point must be an absolute URI.", nameof(entryPoint));
        }

        return new AutomationPolicy
        {
            AllowedSchemes = new HashSet<string>([entryPoint.Scheme], StringComparer.OrdinalIgnoreCase),
            AllowedHosts = new HashSet<string>([entryPoint.IdnHost], StringComparer.OrdinalIgnoreCase),
            AllowedPorts = new HashSet<int>([entryPoint.Port]),
            AllowedPathPrefixes = ["/"],
            AllowedActions = new HashSet<ActionKind>(
            [
                ActionKind.Navigate,
                ActionKind.Click,
                ActionKind.Type,
                ActionKind.Select,
                ActionKind.Read,
                ActionKind.Wait
            ]),
            MaximumActions = maximumActions
        };
    }

    public PolicyDecision Validate()
    {
        if (AllowedSchemes is null || AllowedSchemes.Count == 0)
        {
            return PolicyDecision.Deny("policy-invalid", "At least one allowed URI scheme is required.");
        }

        if (AllowedHosts is null || AllowedHosts.Count == 0)
        {
            return PolicyDecision.Deny("policy-invalid", "At least one allowed host is required.");
        }

        if (AllowedPorts is null || AllowedPorts.Count == 0 || AllowedPorts.Any(port => port is < 1 or > 65535))
        {
            return PolicyDecision.Deny("policy-invalid", "Allowed ports must contain values from 1 through 65535.");
        }

        if (AllowedPathPrefixes is null || AllowedPathPrefixes.Count == 0 ||
            AllowedPathPrefixes.Any(prefix => string.IsNullOrWhiteSpace(prefix) || !prefix.StartsWith('/')))
        {
            return PolicyDecision.Deny("policy-invalid", "Allowed path prefixes must be non-empty absolute paths.");
        }

        if (AllowedActions is null || AllowedActions.Count == 0)
        {
            return PolicyDecision.Deny("policy-invalid", "At least one allowed action is required.");
        }

        return MaximumActions < 1
            ? PolicyDecision.Deny("policy-invalid", "The maximum action count must be at least one.")
            : PolicyDecision.Allow;
    }

    public PolicyDecision EvaluateLocation(Uri location)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (!location.IsAbsoluteUri ||
            !AllowedSchemes.Contains(location.Scheme) ||
            !AllowedHosts.Contains(location.IdnHost) ||
            !AllowedPorts.Contains(location.Port) ||
            !AllowedPathPrefixes.Any(prefix => PathMatches(location.AbsolutePath, prefix)))
        {
            return PolicyDecision.Deny(
                "navigation-not-allowed",
                "The surface navigated outside the configured URI allowlist.");
        }

        return PolicyDecision.Allow;
    }

    public PolicyDecision EvaluateAction(SemanticAction action, int priorActionCount)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!AllowedActions.Contains(action.Kind))
        {
            return PolicyDecision.Deny("action-not-allowed", $"Action '{action.Kind}' is not allowed by policy.");
        }

        if (priorActionCount >= MaximumActions)
        {
            return PolicyDecision.Deny("action-limit-exceeded", "The configured per-run action limit was reached.");
        }

        return action.Kind == ActionKind.Navigate && action.Destination is not null
            ? EvaluateLocation(action.Destination)
            : PolicyDecision.Allow;
    }

    public PolicyDecision EvaluateRisk(SemanticAction action, RiskLevel declaredRisk)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Enum.IsDefined(declaredRisk))
        {
            return PolicyDecision.Deny("risk-classification-invalid", "The action has an unrecognized risk classification.");
        }

        var effectiveRisk = ActionRiskClassifier.EffectiveRisk(action, declaredRisk);
        return effectiveRisk == RiskLevel.Irreversible
            ? PolicyDecision.Deny(
                "intervention-required",
                "An irreversible action requires human approval before execution.")
            : PolicyDecision.Allow;
    }

    private static bool PathMatches(string path, string prefix)
    {
        if (prefix == "/")
        {
            return true;
        }

        var normalized = prefix.EndsWith('/') ? prefix.TrimEnd('/') : prefix;
        return string.Equals(path, normalized, StringComparison.Ordinal) ||
            (path.StartsWith(normalized, StringComparison.Ordinal) &&
             path.Length > normalized.Length &&
             path[normalized.Length] == '/');
    }
}

public static class ActionRiskClassifier
{
    private static readonly string[] IrreversibleTerms =
    [
        "accept", "allow", "approve", "authorize", "buy", "checkout", "commit", "confirm",
        "consent", "delete", "grant", "order", "pay", "permission", "place order", "publish",
        "purchase", "remove", "save", "send", "submit", "transfer"
    ];

    private static readonly string[] SafeClickTerms =
    [
        "back", "close", "collapse", "details", "expand", "filter", "forward", "hide", "menu",
        "next", "open", "previous", "refresh", "search", "show", "sort", "tab", "view"
    ];

    public static RiskLevel MinimumRisk(SemanticAction action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (action.Kind != ActionKind.Click)
        {
            return RiskLevel.Safe;
        }

        var targetText = string.Join(
            ' ',
            action.Target?.Strategies.Select(strategy => strategy.Value) ?? []);
        if (IrreversibleTerms.Any(term => ContainsTerm(targetText, term)))
        {
            return RiskLevel.Irreversible;
        }

        return SafeClickTerms.Any(term => ContainsTerm(targetText, term))
            ? RiskLevel.Safe
            : RiskLevel.Irreversible;
    }

    public static RiskLevel EffectiveRisk(SemanticAction action, RiskLevel declaredRisk)
    {
        ArgumentNullException.ThrowIfNull(action);
        return (RiskLevel)Math.Max((int)declaredRisk, (int)MinimumRisk(action));
    }

    private static bool ContainsTerm(string text, string term) => Regex.IsMatch(
        text,
        $@"\b{Regex.Escape(term)}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(100));
}

public sealed record PolicyDecision(bool Allowed, string? Code, string? SafeMessage)
{
    public static PolicyDecision Allow { get; } = new(true, null, null);

    public static PolicyDecision Deny(string code, string safeMessage) => new(false, code, safeMessage);
}

public sealed class PolicyEnforcer
{
    private readonly AutomationPolicy policy;
    private int actionCount;

    public PolicyEnforcer(AutomationPolicy policy)
    {
        this.policy = policy ?? throw new ArgumentNullException(nameof(policy));
        var validation = policy.Validate();
        if (!validation.Allowed)
        {
            throw new ArgumentException(validation.SafeMessage, nameof(policy));
        }
    }

    public int ActionCount => actionCount;

    public PolicyDecision BeforeAction(SemanticAction action)
    {
        var decision = policy.EvaluateAction(action, actionCount);
        if (decision.Allowed)
        {
            actionCount++;
        }

        return decision;
    }

    public async Task<PolicyDecision> ValidateSurfaceAsync(
        IComputerSurface surface,
        CancellationToken cancellationToken)
    {
        foreach (var location in await surface.GetActiveLocationsAsync(cancellationToken).ConfigureAwait(false))
        {
            var decision = policy.EvaluateLocation(location);
            if (!decision.Allowed)
            {
                return decision;
            }
        }

        return PolicyDecision.Allow;
    }
}
