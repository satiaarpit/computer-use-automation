using System.Net;
using System.Text;
using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Surfaces;
using ComputerUse.Replay;

namespace ComputerUse.Core.Tests;

public sealed class AutomationTests
{
    [Fact]
    public async Task Successful_discovery_compiles_and_replays_with_new_input_without_model_calls()
    {
        var request = Request("Aurora");
        var model = new FakeDecisionModel(Decisions("Aurora"));
        await using var discoverySurface = new SearchSurface();

        var discovered = await new AutomationLoop().RunAsync(request, model, discoverySurface);
        var artifact = TraceCompiler.Compile(request, discovered);
        var callsAfterDiscovery = model.Calls;
        var replaySurface = new SearchSurface();
        var replay = await new ReplayEngine().RunAsync(
            artifact,
            Inputs("Borealis"),
            _ => Task.FromResult<IComputerSurface>(replaySurface));

        Assert.True(discovered.Completed);
        Assert.Equal(4, callsAfterDiscovery);
        Assert.Equal(callsAfterDiscovery, model.Calls);
        Assert.Equal(ReplayRunStatus.Success, replay.Status);
        Assert.Equal("${query}", artifact.Steps[0].Action.ValueTemplate);
        Assert.Equal(RiskLevel.ReversibleWrite, artifact.Steps[0].Risk);
        Assert.Equal(RiskLevel.ReversibleWrite, artifact.Risk);
        Assert.Equal(4, artifact.Steps.Single(step => step.Action.Kind == ActionKind.Read).Retry.MaxAttempts);
        Assert.Contains("target-not-found", artifact.Steps.Single(step => step.Action.Kind == ActionKind.Read).Retry.RecoverableConditionCodes);
        Assert.Contains(artifact.Outcomes, outcome => outcome.Code == "target-not-found" && outcome.Kind == OutcomeKind.RecoverableCondition);
        Assert.Equal("Borealis", replaySurface.Actions[0].ValueTemplate);
        Assert.Equal("Borealis Atlas", replay.Steps.Single(step => step.Value is not null).Value);
        Assert.Equal("Borealis Atlas", replay.Outputs["result"].GetString());
    }

    [Fact]
    public async Task Completion_claim_without_visible_evidence_is_rejected()
    {
        var model = new FakeDecisionModel([
            new ModelDecision { Kind = DecisionKind.Complete, Risk = RiskLevel.Safe, CompletionText = "Not on the page" }
        ]);
        await using var surface = new SearchSurface();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AutomationLoop().RunAsync(Request("Aurora"), model, surface));

        Assert.Contains("claimed completion", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_stops_at_the_configured_step_bound()
    {
        var action = new ModelDecision
        {
            Kind = DecisionKind.Action,
            Risk = RiskLevel.Safe,
            Action = new SemanticAction { Kind = ActionKind.Wait, Duration = TimeSpan.Zero }
        };
        var model = new FakeDecisionModel([action, action]);
        await using var surface = new SearchSurface();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new AutomationLoop().RunAsync(Request("Aurora"), model, surface, maxSteps: 2));

        Assert.Contains("Maximum steps exceeded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_stops_before_another_model_call_at_repeated_state_bound()
    {
        var action = new ModelDecision
        {
            Kind = DecisionKind.Action,
            Risk = RiskLevel.Safe,
            Action = new SemanticAction { Kind = ActionKind.Wait, Duration = TimeSpan.Zero }
        };
        var model = new FakeDecisionModel([action, action, action]);
        await using var surface = new SearchSurface();
        var sink = new RecordingRunEventSink();

        var result = await new AutomationLoop().RunAsync(
            Request("Aurora"),
            model,
            surface,
            maxSteps: 8,
            eventSink: sink,
            repeatedStateLimit: 2);

        Assert.Equal(DiscoveryRunStatus.RepeatedState, result.Status);
        Assert.False(result.Completed);
        Assert.Equal(2, result.ModelCalls);
        Assert.Equal(2, model.Calls);
        Assert.Equal(2, surface.Actions.Count);
        Assert.Contains(sink.Events, item =>
            item.Kind == RunEventKind.RunCompleted && item.Outcome == RunEventOutcome.RepeatedState);
    }

    [Fact]
    public async Task Discovery_timeout_cancels_a_blocked_surface_operation()
    {
        var model = new FakeDecisionModel(Decisions("Aurora"));
        await using var surface = new DelayedObservationSurface();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AutomationLoop().RunAsync(
            Request("Aurora"),
            model,
            surface,
            timeout: TimeSpan.FromMilliseconds(20)));
    }

    [Fact]
    public async Task Discovery_allows_distinct_reads_on_an_unchanged_surface_before_completion()
    {
        var model = new FakeDecisionModel([
            new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Read, Target = Target("first") }
            },
            new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Read, Target = Target("second") }
            },
            new ModelDecision
            {
                Kind = DecisionKind.Complete,
                Risk = RiskLevel.Safe,
                CompletionText = "Search records"
            }
        ]);
        await using var surface = new SearchSurface();

        var result = await new AutomationLoop().RunAsync(Request("Aurora"), model, surface);

        Assert.True(result.Completed);
        Assert.Equal(DiscoveryRunStatus.Completed, result.Status);
        Assert.Equal(3, model.Calls);
    }

    [Fact]
    public async Task Discovery_blocks_model_action_not_present_in_allowlist()
    {
        var model = new FakeDecisionModel(Decisions("Aurora"));
        await using var surface = new SearchSurface();
        var policy = AutomationPolicy.SameOrigin(Request("Aurora").Url) with
        {
            AllowedActions = new HashSet<ActionKind>([ActionKind.Read])
        };

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AutomationLoop().RunAsync(Request("Aurora"), model, surface, policy: policy));

        Assert.Contains("action-not-allowed", exception.Message, StringComparison.Ordinal);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Discovery_blocks_consequential_click_even_when_model_declares_safe()
    {
        var model = new FakeDecisionModel(
        [
            new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Click, Target = Target("confirm-purchase") }
            }
        ]);
        await using var surface = new SearchSurface();

        var result = await new AutomationLoop().RunAsync(Request("Aurora"), model, surface);

        Assert.Equal(DiscoveryRunStatus.InterventionRequired, result.Status);
        Assert.False(result.Completed);
        Assert.Equal(RiskLevel.Safe, result.PendingIntervention!.Risk);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Discovery_events_share_run_id_and_include_policy_action_and_completion()
    {
        const string runId = "discovery_run_001";
        var sink = new RecordingRunEventSink();
        var model = new FakeDecisionModel(Decisions("Aurora"));
        await using var surface = new SearchSurface();

        var result = await new AutomationLoop().RunAsync(
            Request("Aurora"),
            model,
            surface,
            eventSink: sink,
            runId: runId);

        Assert.Equal(runId, result.RunId);
        Assert.All(sink.Events, runEvent => Assert.Equal(runId, runEvent.RunId));
        Assert.Contains(sink.Events, runEvent => runEvent.Kind == RunEventKind.RunStarted);
        Assert.Contains(sink.Events, runEvent => runEvent.Kind == RunEventKind.ObservationCompleted);
        Assert.Contains(sink.Events, runEvent => runEvent.Kind == RunEventKind.DecisionSelected);
        Assert.Contains(sink.Events, runEvent => runEvent.Kind == RunEventKind.PolicyEvaluated);
        Assert.Contains(sink.Events, runEvent => runEvent.Kind == RunEventKind.ActionCompleted);
        Assert.Equal(RunEventOutcome.Completed, sink.Events[^1].Outcome);
        Assert.Equal(runId, TraceCompiler.Compile(Request("Aurora"), result).Provenance.DiscoveryRunId);
    }

    [Fact]
    public async Task Discovery_intervention_event_precedes_any_consequential_action()
    {
        const string runId = "discovery_intervention_001";
        var sink = new RecordingRunEventSink();
        var model = new FakeDecisionModel(
        [
            new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Click, Target = Target("confirm-purchase") }
            }
        ]);
        await using var surface = new SearchSurface();

        var result = await new AutomationLoop().RunAsync(
            Request("Aurora"),
            model,
            surface,
            eventSink: sink,
            runId: runId);

        Assert.Equal(DiscoveryRunStatus.InterventionRequired, result.Status);
        Assert.Contains(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.PolicyEvaluated &&
            runEvent.Outcome == RunEventOutcome.Denied &&
            runEvent.Risk == RiskLevel.Irreversible);
        Assert.Contains(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.InterventionRequested &&
            runEvent.Risk == RiskLevel.Irreversible);
        Assert.Equal(RunEventOutcome.InterventionRequired, sink.Events[^1].Outcome);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Discovery_success_is_not_changed_by_logging_failure()
    {
        var model = new FakeDecisionModel(Decisions("Aurora"));
        await using var surface = new SearchSurface();

        var result = await new AutomationLoop().RunAsync(
            Request("Aurora"),
            model,
            surface,
            eventSink: new ThrowingRunEventSink());

        Assert.True(result.Completed);
        Assert.Equal(DiscoveryRunStatus.Completed, result.Status);
    }

    [Fact]
    public async Task Completion_without_explicit_safe_risk_is_rejected()
    {
        var model = new FakeDecisionModel(
        [
            new ModelDecision { Kind = DecisionKind.Complete, CompletionText = "Search records" }
        ]);
        await using var surface = new SearchSurface();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new AutomationLoop().RunAsync(Request("Aurora"), model, surface));

        Assert.Contains("safe risk classification", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_uses_function_call_decision_and_api_key_header()
    {
        const string response = """
            {"candidates":[{"content":{"parts":[{"functionCall":{"name":"submit_decision","args":{"kind":"action","risk":"safe","action":{"kind":"click","target":{"strategies":[{"kind":"stableId","value":"search"}]}}}}}]}}]}
            """;
        var handler = new RecordingHandler(response);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var model = new GeminiDecisionModel(client, "test-model", apiKey: "test-key");

        var decision = await model.DecideAsync(await Observation(), Request("Aurora"), CancellationToken.None);

        Assert.Equal(DecisionKind.Action, decision.Kind);
        Assert.Equal(ActionKind.Click, decision.Action!.Kind);
        Assert.Equal(RiskLevel.Safe, decision.Risk);
        Assert.Equal("search", decision.Action.Target!.Strategies[0].Value);
        Assert.Equal("test-key", handler.ApiKey);
        Assert.Equal("/v1beta/models/test-model:generateContent", handler.RequestUri!.AbsolutePath);
        Assert.Contains("functionDeclarations", handler.RequestBody, StringComparison.Ordinal);
        Assert.DoesNotContain("\"target\":null", handler.RequestBody, StringComparison.Ordinal);
        Assert.Contains("Interactive elements", handler.RequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gemini_failure_does_not_expose_api_key()
    {
        var handler = new RecordingHandler("{}", HttpStatusCode.BadRequest);
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://example.test/") };
        var model = new GeminiDecisionModel(client, "test-model", apiKey: "secret-test-key");
        var observation = await Observation();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => model.DecideAsync(observation, Request("Aurora"), CancellationToken.None));

        Assert.DoesNotContain("secret-test-key", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("key=", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Trace_compiler_parameterizes_embedded_values_and_infers_named_typed_outputs()
    {
        var observation = await Observation();
        var request = new GoalRequest(
            new Uri("https://fixture.test/?key=synthetic-secret"),
            "Read typed values using synthetic-secret.",
            new Dictionary<string, string>
            {
                ["query"] = "Aurora",
                ["page"] = "42",
                ["accessToken"] = "synthetic-secret"
            });
        var target = new TargetDescriptor
        {
            Strategies = [new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "Aurora" }]
        };
        var result = new LoopResult(
        [
            new DiscoveryStep(0, observation, new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Read, Target = target, OutputName = "count" }
            }, new SurfaceActionResult { Succeeded = true, Value = "42" }),
            new DiscoveryStep(1, observation, new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Read, Target = Target("price"), OutputName = "synthetic-secret" }
            }, new SurfaceActionResult { Succeeded = true, Value = "12.50" }),
            new DiscoveryStep(2, observation, new ModelDecision
            {
                Kind = DecisionKind.Action,
                Risk = RiskLevel.Safe,
                Action = new SemanticAction { Kind = ActionKind.Read, Target = Target("ready"), OutputName = "count" }
            }, new SurfaceActionResult { Succeeded = true, Value = "true" }),
            new DiscoveryStep(3, observation with { VisibleText = "done" }, new ModelDecision
            {
                Kind = DecisionKind.Complete,
                Risk = RiskLevel.Safe,
                CompletionText = "done synthetic-secret",
                CompletionTarget = new TargetDescriptor
                {
                    Strategies = [new TargetStrategy { Kind = TargetStrategyKind.Text, Value = "synthetic-secret" }]
                }
            }, null)
        ], true, 4, observation with { VisibleText = "done synthetic-secret" }, RunId: "compiler_test_001");

        var artifact = TraceCompiler.Compile(request, result);

        Assert.Equal("${query}", artifact.Steps[0].Action.Target!.Strategies[0].Value);
        Assert.Equal(["count", "price", "count-2"], artifact.Outputs.Select(output => output.Name));
        Assert.Equal(artifact.Outputs.Select(output => output.Name), artifact.Steps.Select(step => step.Action.OutputName));
        Assert.Equal(ValueTypeKind.Integer, artifact.Outputs[0].Type);
        Assert.Equal(ValueTypeKind.Decimal, artifact.Outputs[1].Type);
        Assert.Equal(ValueTypeKind.Boolean, artifact.Outputs[2].Type);
        Assert.Equal(ValueTypeKind.Integer, artifact.Inputs.Single(input => input.Name == "page").Type);
        Assert.Null(artifact.Inputs.Single(input => input.Name == "page").Pattern);
        Assert.Equal(DataClassification.Secret, artifact.Inputs.Single(input => input.Name == "accessToken").Classification);
        Assert.DoesNotContain("synthetic-secret", ComputerUse.Core.Serialization.CapabilityJson.Serialize(artifact), StringComparison.Ordinal);
    }

    private static GoalRequest Request(string query) => new(
        new Uri("https://fixture.test/?state=success"),
        "Search for the requested record and read its title.",
        new Dictionary<string, string> { ["query"] = query });

    private static IEnumerable<ModelDecision> Decisions(string query) =>
    [
        new ModelDecision
        {
            Kind = DecisionKind.Action,
            Risk = RiskLevel.ReversibleWrite,
            Action = new SemanticAction { Kind = ActionKind.Type, Target = Target("query"), ValueTemplate = query }
        },
        new ModelDecision
        {
            Kind = DecisionKind.Action,
            Risk = RiskLevel.Safe,
            Action = new SemanticAction { Kind = ActionKind.Click, Target = Target("search") }
        },
        new ModelDecision
        {
            Kind = DecisionKind.Action,
            Risk = RiskLevel.Safe,
            Action = new SemanticAction { Kind = ActionKind.Read, Target = Target("result") }
        },
        new ModelDecision
        {
            Kind = DecisionKind.Complete,
            Risk = RiskLevel.Safe,
            CompletionText = "1 result",
            CompletionTarget = Target("status")
        }
    ];

    private static TargetDescriptor Target(string id) => new()
    {
        Strategies = [new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = id }]
    };

    private static IReadOnlyDictionary<string, JsonElement> Inputs(string query)
    {
        var element = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["query"] = query });
        return element.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
    }

    private static Task<SurfaceObservation> Observation() => Task.FromResult(new SurfaceObservation
    {
        Url = new Uri("https://fixture.test/"),
        Title = "Fixture",
        VisibleText = "Search records",
        InteractiveElements = [new InteractiveElement { ElementType = "text", StableId = "query", AccessibleName = "Query" }],
        Fingerprint = "fixture"
    });

    private sealed class RecordingHandler(string responseBody, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? ApiKey { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ApiKey = request.Headers.GetValues("x-goog-api-key").Single();
            RequestUri = request.RequestUri;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(statusCode)
            {
                ReasonPhrase = statusCode == HttpStatusCode.OK ? "OK" : "Bad Request",
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class SearchSurface : IComputerSurface
    {
        private string query = "Aurora";
        private bool searched;
        public List<SemanticAction> Actions { get; } = [];

        public Task<SurfaceObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            var title = query.Equals("Borealis", StringComparison.Ordinal) ? "Borealis Atlas" : "Aurora Field Guide";
            var text = searched ? $"1 result\n{title}" : "Search records";
            return Task.FromResult(new SurfaceObservation
            {
                Url = new Uri("https://fixture.test/?state=success"),
                Title = "Deterministic UI Fixture",
                VisibleText = text,
                InteractiveElements = [],
                Fingerprint = text
            });
        }

        public Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken)
        {
            Actions.Add(action);
            if (action.Kind == ActionKind.Type) query = action.ValueTemplate!;
            if (action.Kind == ActionKind.Click) searched = true;
            var value = action.Kind == ActionKind.Read
                ? query.Equals("Borealis", StringComparison.Ordinal) ? "Borealis Atlas" : "Aurora Field Guide"
                : null;
            return Task.FromResult(new SurfaceActionResult { Succeeded = true, Value = value });
        }

        public Task<SurfaceInspectionResult> InspectAsync(
            SurfaceInspection inspection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = inspection.Target?.Strategies.FirstOrDefault()?.Value;
            var value = inspection.Kind switch
            {
                SurfaceInspectionKind.Visible => (searched && target is "status" or "result").ToString().ToLowerInvariant(),
                SurfaceInspectionKind.Text when target == "result" =>
                    query.Equals("Borealis", StringComparison.Ordinal) ? "Borealis Atlas" : "Aurora Field Guide",
                SurfaceInspectionKind.Text when target == "status" && searched => "1 result",
                _ => null
            };
            return Task.FromResult(new SurfaceInspectionResult { Succeeded = true, Value = value });
        }

        public Task<IReadOnlyList<Uri>> GetActiveLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Uri>>([new Uri("https://fixture.test/?state=success")]);

        public Task<EvidenceReference> CaptureEvidenceAsync(EvidenceKind kind, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PauseAutomationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ResumeAutomationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DelayedObservationSurface : IComputerSurface
    {
        public async Task<SurfaceObservation> ObserveAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }

        public Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken) =>
            Task.FromResult(new SurfaceActionResult { Succeeded = true });

        public Task<IReadOnlyList<Uri>> GetActiveLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Uri>>([new Uri("https://fixture.test/")]);

        public Task<EvidenceReference> CaptureEvidenceAsync(EvidenceKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingRunEventSink : IRunEventSink
    {
        public List<RunEvent> Events { get; } = [];

        public Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Events.Add(runEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingRunEventSink : IRunEventSink
    {
        public Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken) =>
            throw new IOException("Synthetic logging failure.");
    }
}
