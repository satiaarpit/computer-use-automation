using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ComputerUse.TestApp.Tests;

public sealed class FixtureApplicationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient client;

    public FixtureApplicationTests(WebApplicationFactory<Program> application)
    {
        client = application.CreateClient();
    }

    [Fact]
    public async Task Root_exposes_semantically_targetable_search_surface()
    {
        var html = await client.GetStringAsync("/");

        Assert.Contains("role=\"search\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"query\"", html, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Search results\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_route_exposes_risk_gated_form_state()
    {
        var html = await client.GetStringAsync("/review?state=risky-submit");

        Assert.Contains("Review proposed change", html, StringComparison.Ordinal);
        Assert.Contains("Submit final change", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Success_state_returns_typed_deterministic_record()
    {
        var response = await client.GetFromJsonAsync<SearchPayload>("/api/search?query=Aurora&state=success");

        var record = Assert.Single(Assert.IsType<List<RecordPayload>>(response!.Records));
        Assert.Equal("REC-100", record.Id);
        Assert.Equal(12.50m, record.Price);
        Assert.True(record.Available);
        Assert.Equal(new DateOnly(2026, 9, 1), record.PublishedOn);
    }

    [Fact]
    public async Task No_results_state_is_repeatable_regardless_of_query()
    {
        var response = await client.GetFromJsonAsync<SearchPayload>("/api/search?query=Aurora&state=no-results");

        Assert.Empty(response!.Records);
        Assert.Equal("no-results", response.State);
    }

    [Fact]
    public async Task Permission_state_returns_declared_business_outcome()
    {
        var response = await client.GetAsync("/api/search?query=Aurora&state=permission");
        var payload = await response.Content.ReadFromJsonAsync<SearchPayload>();

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("permission-required", payload!.Outcome);
        Assert.Empty(payload.Records);
    }

    [Fact]
    public async Task Slow_load_state_applies_predictable_delay_then_succeeds()
    {
        var stopwatch = Stopwatch.StartNew();
        var response = await client.GetFromJsonAsync<SearchPayload>("/api/search?query=Aurora&state=slow-load");
        stopwatch.Stop();

        Assert.Single(response!.Records);
        Assert.Equal("slow-load", response.State);
        Assert.True(stopwatch.ElapsedMilliseconds >= 300, $"Expected at least 300 ms but observed {stopwatch.ElapsedMilliseconds} ms.");
    }

    [Theory]
    [InlineData("dialog", true, false)]
    [InlineData("risky-submit", false, true)]
    public async Task Exceptional_ui_states_are_explicit(string state, bool requiresDialog, bool requiresHumanReview)
    {
        var response = await client.GetFromJsonAsync<StatePayload>($"/api/state/{state}");

        Assert.Equal(state, response!.State);
        Assert.Equal(requiresDialog, response.RequiresDialog);
        Assert.Equal(requiresHumanReview, response.RequiresHumanReview);
    }

    [Fact]
    public async Task Final_submit_is_blocked_before_commit()
    {
        var response = await client.PostAsJsonAsync("/api/submit", new { reference = "REC-100", note = "Synthetic review" });
        var payload = await response.Content.ReadFromJsonAsync<SubmitPayload>();

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("intervention-required", payload!.Status);
    }

    private sealed record SearchPayload(string State, IReadOnlyList<RecordPayload> Records, string? Outcome);

    private sealed record RecordPayload(string Id, string Title, decimal Price, bool Available, DateOnly PublishedOn);

    private sealed record StatePayload(string State, bool RequiresDialog, bool RequiresHumanReview, bool RequiresPermission);

    private sealed record SubmitPayload(string Status, string Message);
}
