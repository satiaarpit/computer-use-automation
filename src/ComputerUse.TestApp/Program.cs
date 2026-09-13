using System.Diagnostics;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Use(async (context, next) =>
{
    const string sessionCookie = "fixture-session";
    if (!context.Request.Cookies.ContainsKey(sessionCookie))
    {
        context.Response.Cookies.Append(sessionCookie, Guid.NewGuid().ToString("N"), new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = context.Request.IsHttps
        });
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

var records = new[]
{
    new CatalogRecord("REC-100", "Aurora Field Guide", 12.50m, true, new DateOnly(2026, 9, 1)),
    new CatalogRecord("REC-200", "Borealis Atlas", 24.00m, false, new DateOnly(2026, 9, 2)),
    new CatalogRecord("REC-300", "Cirrus Handbook", 18.75m, true, new DateOnly(2026, 9, 3))
};

app.MapGet("/api/search", async (string? query, string? state, CancellationToken cancellationToken) =>
{
    var normalizedState = NormalizeState(state);
    var stopwatch = Stopwatch.StartNew();

    if (normalizedState == FixtureState.SlowLoad)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(350), cancellationToken);
    }

    if (normalizedState == FixtureState.Permission)
    {
        return Results.Json(
            new SearchResponse(normalizedState, query ?? string.Empty, [], stopwatch.ElapsedMilliseconds, "permission-required"),
            statusCode: StatusCodes.Status403Forbidden);
    }

    var matches = normalizedState == FixtureState.NoResults
        ? []
        : records
            .Where(record => string.IsNullOrWhiteSpace(query)
                || record.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || record.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    return Results.Ok(new SearchResponse(normalizedState, query ?? string.Empty, matches, stopwatch.ElapsedMilliseconds));
});

app.MapGet("/api/state/{state}", (string state) =>
{
    var normalizedState = NormalizeState(state);
    return Results.Ok(new FixtureStateResponse(
        normalizedState,
        normalizedState == FixtureState.Dialog,
        normalizedState == FixtureState.RiskySubmit,
        normalizedState == FixtureState.Permission));
    });

app.MapGet("/api/session", (HttpRequest request) =>
    Results.Ok(new { sessionId = request.Cookies["fixture-session"] ?? "pending" }));

app.MapPost("/api/submit", (SubmitRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Reference) || string.IsNullOrWhiteSpace(request.Note))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [nameof(request.Reference)] = ["Reference and note are required."]
        });
    }

    return Results.Json(
        new SubmitResponse("intervention-required", "Final submission is intentionally blocked for human review."),
        statusCode: StatusCodes.Status409Conflict);
});

app.MapFallbackToFile("index.html");

app.Run();

static string NormalizeState(string? state) => state?.Trim().ToLowerInvariant() switch
{
    FixtureState.NoResults => FixtureState.NoResults,
    FixtureState.SlowLoad => FixtureState.SlowLoad,
    FixtureState.Permission => FixtureState.Permission,
    FixtureState.Dialog => FixtureState.Dialog,
    FixtureState.RiskySubmit => FixtureState.RiskySubmit,
    _ => FixtureState.Success
};

internal static class FixtureState
{
    public const string Success = "success";
    public const string NoResults = "no-results";
    public const string SlowLoad = "slow-load";
    public const string Permission = "permission";
    public const string Dialog = "dialog";
    public const string RiskySubmit = "risky-submit";
}

internal sealed record CatalogRecord(string Id, string Title, decimal Price, bool Available, DateOnly PublishedOn);

internal sealed record SearchResponse(
    string State,
    string Query,
    IReadOnlyList<CatalogRecord> Records,
    long ElapsedMilliseconds,
    string? Outcome = null);

internal sealed record FixtureStateResponse(
    string State,
    bool RequiresDialog,
    bool RequiresHumanReview,
    bool RequiresPermission);

internal sealed record SubmitRequest(string Reference, string Note);

internal sealed record SubmitResponse(string Status, string Message);

public partial class Program
{
}
