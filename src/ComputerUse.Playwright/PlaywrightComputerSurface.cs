using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Evidence;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Surfaces;
using Microsoft.Playwright;

namespace ComputerUse.Playwright;

public sealed class PlaywrightComputerSurface : IComputerSurface
{
    private readonly Microsoft.Playwright.IPlaywright playwright;
    private readonly IBrowser browser;
    private readonly IBrowserContext context;
    private readonly IPage page;
    private readonly PlaywrightSurfaceOptions options;
    private readonly EvidenceRedactor redactor;
    private readonly PlaywrightTargetResolver resolver;
    private readonly ConcurrentDictionary<string, byte> observedPageUrls = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<PolicyDecision> interceptedNavigationDecisions = new();
    private readonly SemaphoreSlim controlGate = new(1, 1);
    private AutomationPolicy? navigationPolicy;
    private bool navigationRouteInstalled;
    private HumanControlSession? activeHumanSession;
    private int humanControlRequested;

    private PlaywrightComputerSurface(
        Microsoft.Playwright.IPlaywright playwright,
        IBrowser browser,
        IBrowserContext context,
        IPage page,
        PlaywrightSurfaceOptions options)
    {
        this.playwright = playwright;
        this.browser = browser;
        this.context = context;
        this.page = page;
        this.options = options;
        redactor = new EvidenceRedactor(options.SensitiveValues);
        resolver = new PlaywrightTargetResolver(page);
        TrackAbsoluteLocation(page.Url);
        context.Page += (_, openedPage) => TrackOpenedPage(openedPage);
        page.Popup += (_, openedPage) => TrackOpenedPage(openedPage);
    }

    private void TrackOpenedPage(IPage openedPage)
    {
        TrackAbsoluteLocation(openedPage.Url);
        openedPage.FrameNavigated += (_, frame) =>
        {
            if (frame == openedPage.MainFrame)
            {
                TrackAbsoluteLocation(frame.Url);
            }
        };
    }

    public static async Task<PlaywrightComputerSurface> CreateAsync(
        PlaywrightSurfaceOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = options ?? new PlaywrightSurfaceOptions();
        effectiveOptions.Validate();
        var playwright = await Microsoft.Playwright.Playwright.CreateAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        IBrowser? browser = null;
        IBrowserContext? context = null;

        try
        {
            browser = await playwright.Chromium.LaunchAsync(new()
            {
                Headless = effectiveOptions.Headless
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
            context = await browser.NewContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            var page = await context.NewPageAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return new PlaywrightComputerSurface(playwright, browser, context, page, effectiveOptions);
        }
        catch
        {
            if (context is not null)
            {
                await context.CloseAsync().ConfigureAwait(false);
            }

            if (browser is not null)
            {
                await browser.CloseAsync().ConfigureAwait(false);
            }

            playwright.Dispose();
            throw;
        }
    }

    public async Task SetNavigationPolicyAsync(
        AutomationPolicy policy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);
        cancellationToken.ThrowIfCancellationRequested();
        var validation = policy.Validate();
        if (!validation.Allowed)
        {
            throw new ArgumentException(validation.SafeMessage, nameof(policy));
        }

        navigationPolicy = policy;
        if (navigationRouteInstalled)
        {
            return;
        }

        await context.RouteAsync("**/*", async route =>
        {
            var activePolicy = navigationPolicy;
            PolicyDecision? decision = null;
            if (activePolicy is not null &&
                route.Request.IsNavigationRequest &&
                Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var destination) &&
                !(decision = activePolicy.EvaluateLocation(destination)).Allowed)
            {
                TrackAbsoluteLocation(destination.AbsoluteUri);
                interceptedNavigationDecisions.Enqueue(decision);
                await route.AbortAsync("blockedbyclient").ConfigureAwait(false);
                return;
            }

            await route.ContinueAsync().ConfigureAwait(false);
        }).WaitAsync(cancellationToken).ConfigureAwait(false);
        navigationRouteInstalled = true;
    }

    public async Task<SurfaceObservation> ObserveAsync(CancellationToken cancellationToken)
    {
        var title = await page.TitleAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var rawText = await page.Locator("body").InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var isTruncated = rawText.Length > options.MaximumObservationCharacters;
        var visibleText = redactor.Redact(isTruncated ? rawText[..options.MaximumObservationCharacters] : rawText);
        var interactive = page.Locator("a, button, input, select, textarea, [role], h1, h2, h3, h4, h5, h6, [data-testid], [data-record-id], [data-field]");
        var totalElements = await interactive.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var count = Math.Min(totalElements, options.MaximumInteractiveElements);
        var elements = new List<InteractiveElement>(count);

        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var element = interactive.Nth(index);
            if (!await element.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var type = await element.GetAttributeAsync("type").WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? await element.GetAttributeAsync("role").WaitAsync(cancellationToken).ConfigureAwait(false)
                ?? await element.EvaluateAsync<string>("element => element.tagName.toLowerCase()").WaitAsync(cancellationToken).ConfigureAwait(false);
            var role = await element.GetAttributeAsync("role").WaitAsync(cancellationToken).ConfigureAwait(false);
            var accessibleName = await GetSafeElementNameAsync(element, cancellationToken).ConfigureAwait(false);
            var id = await element.GetAttributeAsync("id").WaitAsync(cancellationToken).ConfigureAwait(false);
            var cssSelector = await GetSafeCssSelectorAsync(element, cancellationToken).ConfigureAwait(false);
            var value = await GetSafeValueAsync(element, cancellationToken).ConfigureAwait(false);

            elements.Add(new InteractiveElement
            {
                ElementType = type,
                Role = role,
                AccessibleName = accessibleName is null ? null : redactor.Redact(accessibleName),
                StableId = id,
                CssSelector = cssSelector,
                Value = value is null ? null : redactor.Redact(value)
            });
        }

        var fingerprintSource = $"{page.Url}\n{title}\n{visibleText}\n{string.Join('|', elements)}";
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintSource))).ToLowerInvariant();

        return new SurfaceObservation
        {
            Url = new Uri(page.Url, UriKind.Absolute),
            Title = title,
            VisibleText = visibleText,
            InteractiveElements = elements,
            Fingerprint = fingerprint,
            IsTruncated = isTruncated || totalElements > options.MaximumInteractiveElements
        };
    }

    public async Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Volatile.Read(ref humanControlRequested) == 1)
        {
            return Failure("automation-paused", "Automation cannot act while human control is pending or active.");
        }

        await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref humanControlRequested) == 1 || activeHumanSession is not null)
            {
                return Failure("automation-paused", "Automation cannot act while a human control session is active.");
            }

            return await ExecuteCoreAsync(action, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            controlGate.Release();
        }
    }

    public async Task<IHumanControlSession> TransferControlToHumanAsync(
        HumanControlAuthorization authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        if (Interlocked.CompareExchange(ref humanControlRequested, 1, 0) != 0)
        {
            throw new InvalidOperationException("Human control is already pending or active.");
        }

        try
        {
            await controlGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Volatile.Write(ref humanControlRequested, 0);
            throw;
        }
        if (activeHumanSession is not null)
        {
            Volatile.Write(ref humanControlRequested, 0);
            controlGate.Release();
            throw new InvalidOperationException("Human control is already active.");
        }

        activeHumanSession = new HumanControlSession(this);
        return activeHumanSession;
    }

    private async Task<SurfaceActionResult> ExecuteCoreAsync(SemanticAction action, CancellationToken cancellationToken)
    {

        try
        {
            if (action.Kind == ActionKind.Navigate)
            {
                if (action.Destination is null)
                {
                    return Failure("invalid-action", "Navigate requires a destination.");
                }

                await page.GotoAsync(action.Destination.AbsoluteUri).WaitAsync(cancellationToken).ConfigureAwait(false);
                return Success();
            }

            if (action.Kind == ActionKind.Wait)
            {
                await Task.Delay(action.Duration ?? TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
                return Success();
            }

            if (action.Kind == ActionKind.Complete)
            {
                return Success();
            }

            if (action.Kind == ActionKind.RequestIntervention)
            {
                return Failure("intervention-required", "The action requires human intervention.");
            }

            if (action.Target is null)
            {
                return Failure("invalid-action", $"{action.Kind} requires a target.");
            }

            var target = await resolver.ResolveAsync(action.Target, cancellationToken).ConfigureAwait(false);
            var result = action.Kind switch
            {
                ActionKind.Click => await ClickAsync(target, cancellationToken).ConfigureAwait(false),
                ActionKind.Type => await TypeAsync(target, action.ValueTemplate, cancellationToken).ConfigureAwait(false),
                ActionKind.Select => await SelectAsync(target, action.ValueTemplate, cancellationToken).ConfigureAwait(false),
                ActionKind.Read => await ReadAsync(target, cancellationToken).ConfigureAwait(false),
                _ => Failure("unsupported-action", $"Action '{action.Kind}' is not supported by the web adapter.")
            };

            if (interceptedNavigationDecisions.TryDequeue(out var navigationDecision))
            {
                return Failure(navigationDecision.Code!, navigationDecision.SafeMessage!);
            }

            return result with { MatchedStrategy = target.Strategy.ToString() };
        }
        catch (TargetResolutionException exception)
        {
            return Failure(exception.Code, exception.Message);
        }
        catch (PlaywrightException)
        {
            if (interceptedNavigationDecisions.TryDequeue(out var navigationDecision))
            {
                return Failure(navigationDecision.Code!, navigationDecision.SafeMessage!);
            }

            return Failure("surface-action-failed", "The browser could not complete the requested action.");
        }
    }

    public async Task<SurfaceInspectionResult> InspectAsync(
        SurfaceInspection inspection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inspection);

        try
        {
            if (inspection.Kind == SurfaceInspectionKind.Url)
            {
                return InspectionSuccess(page.Url);
            }

            if (inspection.Target is null)
            {
                return InspectionFailure("invalid-inspection", $"{inspection.Kind} inspection requires a target.");
            }

            var target = await resolver.ResolveAsync(inspection.Target, cancellationToken).ConfigureAwait(false);
            var value = inspection.Kind switch
            {
                SurfaceInspectionKind.Visible =>
                    (await target.Locator.IsVisibleAsync().WaitAsync(cancellationToken).ConfigureAwait(false)).ToString().ToLowerInvariant(),
                SurfaceInspectionKind.Text =>
                    await target.Locator.InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false),
                SurfaceInspectionKind.Value =>
                    await target.Locator.InputValueAsync().WaitAsync(cancellationToken).ConfigureAwait(false),
                SurfaceInspectionKind.Attribute when !string.IsNullOrWhiteSpace(inspection.AttributeName) =>
                    await target.Locator.GetAttributeAsync(inspection.AttributeName).WaitAsync(cancellationToken).ConfigureAwait(false),
                SurfaceInspectionKind.Attribute =>
                    throw new InvalidOperationException("Attribute inspection requires an attribute name."),
                SurfaceInspectionKind.State =>
                    await ReadStateAsync(target.Locator, cancellationToken).ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Inspection '{inspection.Kind}' is not supported.")
            };

            return InspectionSuccess(value) with { MatchedStrategy = target.Strategy.ToString() };
        }
        catch (TargetResolutionException exception) when (
            inspection.Kind == SurfaceInspectionKind.Visible &&
            string.Equals(exception.Code, "target-not-found", StringComparison.Ordinal))
        {
            return InspectionSuccess(bool.FalseString.ToLowerInvariant());
        }
        catch (TargetResolutionException exception)
        {
            return InspectionFailure(exception.Code, exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return InspectionFailure("invalid-inspection", exception.Message);
        }
        catch (PlaywrightException)
        {
            return InspectionFailure("surface-inspection-failed", "The browser could not inspect the requested value.");
        }
    }

    public Task<IReadOnlyList<Uri>> GetActiveLocationsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var activePage in context.Pages)
        {
            TrackAbsoluteLocation(activePage.Url);
        }

        IReadOnlyList<Uri> locations = observedPageUrls.Keys
            .Select(url => new Uri(url, UriKind.Absolute))
            .ToArray();
        return Task.FromResult(locations);
    }

    public async Task<string?> GetSessionContinuityCommitmentAsync(CancellationToken cancellationToken)
    {
        var cookies = await context.CookiesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (cookies.Count == 0)
        {
            return null;
        }

        var canonicalSession = string.Join('\n', cookies
            .OrderBy(cookie => cookie.Domain, StringComparer.Ordinal)
            .ThenBy(cookie => cookie.Path, StringComparer.Ordinal)
            .ThenBy(cookie => cookie.Name, StringComparer.Ordinal)
            .Select(cookie => $"{cookie.Domain}\t{cookie.Path}\t{cookie.Name}\t{cookie.Value}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalSession))).ToLowerInvariant();
    }

    private void TrackAbsoluteLocation(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var location) &&
            location.Scheme is "http" or "https")
        {
            observedPageUrls.TryAdd(location.AbsoluteUri, 0);
        }
    }

    public async Task<EvidenceReference> CaptureEvidenceAsync(EvidenceKind kind, CancellationToken cancellationToken)
    {
        var approvedRoot = EvidencePathPolicy.ResolveRoot(options.EvidenceDirectory);
        Directory.CreateDirectory(approvedRoot);
        var extension = kind == EvidenceKind.Screenshot ? "png" : "txt";
        var fileName = $"surface-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.{extension}";
        var path = EvidencePathPolicy.ResolveFile(approvedRoot, fileName);

        if (kind == EvidenceKind.Screenshot)
        {
            var masks = new List<ILocator>
            {
                page.Locator("input, textarea, [contenteditable='true'], #session-id")
            };
            masks.AddRange(options.SensitiveValues
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => page.GetByText(value, new() { Exact = false })));
            await page.ScreenshotAsync(new()
            {
                Path = path,
                FullPage = true,
                Mask = masks
            }).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var observation = await ObserveAsync(cancellationToken).ConfigureAwait(false);
            var allowlistedSnapshot = string.Join(
                Environment.NewLine,
                $"url-origin: {observation.Url.GetLeftPart(UriPartial.Authority)}",
                $"title: {redactor.Redact(observation.Title)}",
                $"fingerprint: {observation.Fingerprint}",
                $"truncated: {observation.IsTruncated}",
                "interactive-elements:",
                string.Join(Environment.NewLine, observation.InteractiveElements.Select(element =>
                    $"- type={element.ElementType}; id={element.StableId}; name={element.AccessibleName}; value=[OMITTED]")),
                "visible-text:",
                redactor.Redact(observation.VisibleText));
            await File.WriteAllTextAsync(path, allowlistedSnapshot, cancellationToken).ConfigureAwait(false);
        }

        return new EvidenceReference
        {
            Kind = kind.ToString().ToLowerInvariant(),
            RelativePath = Path.GetFileName(path),
            Classification = DataClassification.Internal,
            Protection = EvidenceProtection.Redacted
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (activeHumanSession is not null)
        {
            await activeHumanSession.CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }
        await context.DisposeAsync().ConfigureAwait(false);
        await browser.DisposeAsync().ConfigureAwait(false);
        controlGate.Dispose();
        playwright.Dispose();
    }

    private async Task<SurfaceActionResult> ExecuteHumanActionAsync(
        HumanControlSession session,
        SemanticAction action,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);
        EnsureActiveSession(session);
        return await ExecuteCoreAsync(action, cancellationToken).ConfigureAwait(false);
    }

    private Task EndHumanControlAsync(HumanControlSession session, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureActiveSession(session);
        activeHumanSession = null;
        Volatile.Write(ref humanControlRequested, 0);
        controlGate.Release();
        return Task.CompletedTask;
    }

    private void EnsureActiveSession(HumanControlSession session)
    {
        if (!ReferenceEquals(activeHumanSession, session))
        {
            throw new InvalidOperationException("The human control session is inactive or stale.");
        }
    }

    private sealed class HumanControlSession(PlaywrightComputerSurface owner) : IHumanControlSession
    {
        private bool ended;

        public Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(ended, this);
            return owner.ExecuteHumanActionAsync(this, action, cancellationToken);
        }

        public async Task ResumeAutomationAsync(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(ended, this);
            await owner.EndHumanControlAsync(this, cancellationToken).ConfigureAwait(false);
            ended = true;
        }

        public async Task CancelAsync(CancellationToken cancellationToken)
        {
            if (ended)
            {
                return;
            }

            await owner.EndHumanControlAsync(this, cancellationToken).ConfigureAwait(false);
            ended = true;
        }

        public async ValueTask DisposeAsync()
        {
            await CancelAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<SurfaceActionResult> ClickAsync(ResolvedTarget target, CancellationToken cancellationToken)
    {
        await target.Locator.ClickAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return Success();
    }

    private static async Task<SurfaceActionResult> TypeAsync(
        ResolvedTarget target,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return Failure("invalid-action", "Type requires a value.");
        }

        await target.Locator.FillAsync(value).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Success();
    }

    private static async Task<SurfaceActionResult> SelectAsync(
        ResolvedTarget target,
        string? value,
        CancellationToken cancellationToken)
    {
        if (value is null)
        {
            return Failure("invalid-action", "Select requires a value.");
        }

        await target.Locator.SelectOptionAsync(value).WaitAsync(cancellationToken).ConfigureAwait(false);
        return Success();
    }

    private static async Task<SurfaceActionResult> ReadAsync(ResolvedTarget target, CancellationToken cancellationToken)
    {
        var value = await target.Locator.InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return Success(value);
    }

    private static async Task<string> ReadStateAsync(ILocator locator, CancellationToken cancellationToken)
    {
        var checkedState = await locator.GetAttributeAsync("aria-checked").WaitAsync(cancellationToken).ConfigureAwait(false)
            ?? await locator.GetAttributeAsync("aria-selected").WaitAsync(cancellationToken).ConfigureAwait(false)
            ?? await locator.GetAttributeAsync("data-state").WaitAsync(cancellationToken).ConfigureAwait(false);
        return checkedState ?? (await locator.IsEnabledAsync().WaitAsync(cancellationToken).ConfigureAwait(false)
            ? "enabled"
            : "disabled");
    }

    private async Task<string?> GetSafeElementNameAsync(ILocator element, CancellationToken cancellationToken)
    {
        var label = await element.GetAttributeAsync("aria-label").WaitAsync(cancellationToken).ConfigureAwait(false)
            ?? await element.GetAttributeAsync("placeholder").WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(label))
        {
            return label;
        }

        var id = await element.GetAttributeAsync("id").WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(id))
        {
            var associatedLabel = page.Locator($"label[for={QuoteCssValue(id)}]");
            if (await associatedLabel.CountAsync().WaitAsync(cancellationToken).ConfigureAwait(false) == 1)
            {
                var labelText = await associatedLabel.InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(labelText))
                {
                    return labelText.Trim()[..Math.Min(labelText.Trim().Length, 200)];
                }
            }
        }

        var text = await element.InnerTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, 200)];
    }

    private static async Task<string?> GetSafeValueAsync(ILocator element, CancellationToken cancellationToken)
    {
        try
        {
            var value = await element.InputValueAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            return value[..Math.Min(value.Length, 200)];
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

        private static async Task<string?> GetSafeCssSelectorAsync(ILocator element, CancellationToken cancellationToken)
        {
                const string script = """
                        element => {
                            const escape = value => CSS.escape(value);
                            if (element.id) return `#${escape(element.id)}`;
                            const parts = [];
                            let current = element;
                            while (current && current.nodeType === 1 && parts.length < 6) {
                                if (current.id) {
                                    parts.unshift(`#${escape(current.id)}`);
                                    break;
                                }
                                let part = current.tagName.toLowerCase();
                                const siblings = current.parentElement
                                    ? Array.from(current.parentElement.children).filter(sibling => sibling.tagName === current.tagName)
                                    : [];
                                if (siblings.length > 1) part += `:nth-of-type(${siblings.indexOf(current) + 1})`;
                                parts.unshift(part);
                                current = current.parentElement;
                            }
                            return parts.join(' > ');
                        }
                        """;
                var selector = await element.EvaluateAsync<string>(script).WaitAsync(cancellationToken).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(selector) || selector.Length > 300 ? null : selector;
        }

    private static SurfaceActionResult Success(string? value = null) => new()
    {
        Succeeded = true,
        Value = value
    };

    private static SurfaceActionResult Failure(string code, string message) => new()
    {
        Succeeded = false,
        ErrorCode = code,
        SafeMessage = message
    };

    private static SurfaceInspectionResult InspectionSuccess(string? value) => new()
    {
        Succeeded = true,
        Value = value
    };

    private static SurfaceInspectionResult InspectionFailure(string code, string message) => new()
    {
        Succeeded = false,
        ErrorCode = code,
        SafeMessage = message
    };

    private static string QuoteCssValue(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
}
