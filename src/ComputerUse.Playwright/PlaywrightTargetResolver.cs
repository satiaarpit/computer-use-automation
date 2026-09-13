using ComputerUse.Core.Contracts;
using Microsoft.Playwright;

namespace ComputerUse.Playwright;

public sealed class PlaywrightTargetResolver
{
    private readonly IPage page;

    public PlaywrightTargetResolver(IPage page)
    {
        this.page = page;
    }

    public async Task<ResolvedTarget> ResolveAsync(TargetDescriptor descriptor, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        foreach (var strategy in descriptor.Strategies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var locator = CreateLocator(strategy);
            var count = await locator.CountAsync().ConfigureAwait(false);

            if (count == 0)
            {
                continue;
            }

            if (descriptor.RequireUniqueMatch && count != 1)
            {
                throw new TargetResolutionException(
                    "ambiguous-target",
                    $"Target strategy '{strategy.Kind}' matched {count} elements; exactly one was required.");
            }

            return new ResolvedTarget(locator.First, strategy.Kind, count);
        }

        throw new TargetResolutionException("target-not-found", "No target strategy matched an element.");
    }

    private ILocator CreateLocator(TargetStrategy strategy)
    {
        var locator = strategy.Kind switch
        {
            TargetStrategyKind.AccessibleRoleAndName => CreateRoleLocator(strategy.Value),
            TargetStrategyKind.Label => page.GetByLabel(strategy.Value, new() { Exact = true }),
            TargetStrategyKind.StableId => page.Locator($"[id={QuoteCssValue(strategy.Value)}]"),
            TargetStrategyKind.Text => page.GetByText(strategy.Value, new() { Exact = true }),
            TargetStrategyKind.StructuralRelation => CreateStructuralLocator(strategy),
            TargetStrategyKind.Css => page.Locator(strategy.Value),
            TargetStrategyKind.XPath => page.Locator($"xpath={strategy.Value}"),
            TargetStrategyKind.VisualAnchor => throw new TargetResolutionException(
                "unsupported-target-strategy",
                "Visual-anchor targeting is not supported by the web adapter."),
            _ => throw new TargetResolutionException("unsupported-target-strategy", "The target strategy is not supported.")
        };

        return string.IsNullOrWhiteSpace(strategy.Scope) || strategy.Kind == TargetStrategyKind.StructuralRelation
            ? locator
            : page.Locator(strategy.Scope).Locator(locator);
    }

    private ILocator CreateRoleLocator(string value)
    {
        var separator = value.IndexOf(':', StringComparison.Ordinal);
        if (separator <= 0 || separator == value.Length - 1)
        {
            throw new TargetResolutionException(
                "invalid-target-strategy",
                "Accessible role targets must use the format 'role:accessible name'.");
        }

        var roleText = value[..separator].Replace("-", string.Empty, StringComparison.Ordinal);
        var name = value[(separator + 1)..];
        if (!Enum.TryParse<AriaRole>(roleText, true, out var role))
        {
            throw new TargetResolutionException("invalid-target-strategy", $"Unknown accessibility role '{value[..separator]}'.");
        }

        return page.GetByRole(role, new() { Name = name, Exact = true });
    }

    private ILocator CreateStructuralLocator(TargetStrategy strategy)
    {
        if (string.IsNullOrWhiteSpace(strategy.Scope))
        {
            throw new TargetResolutionException(
                "invalid-target-strategy",
                "Structural relation targets require a CSS scope.");
        }

        return page.Locator(strategy.Scope).Locator(strategy.Value);
    }

    private static string QuoteCssValue(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}

public sealed record ResolvedTarget(ILocator Locator, TargetStrategyKind Strategy, int MatchCount);
