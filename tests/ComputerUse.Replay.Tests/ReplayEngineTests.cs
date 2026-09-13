using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Intervention;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Serialization;
using ComputerUse.Core.Surfaces;
using ComputerUse.Replay;

namespace ComputerUse.Replay.Tests;

public sealed class ReplayEngineTests
{
    [Fact]
    public void Artifact_loader_rejects_malformed_json()
    {
        var result = ArtifactLoader.LoadAndBind("{not-json", EmptyInputs());

        Assert.False(result.IsValid);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "invalid-json");
    }

    [Fact]
    public void Artifact_loader_deserializes_validates_and_binds_inputs()
    {
        var artifact = CreateArtifact();
        var inputs = ParseInputs("""{"query":"Aurora"}""");

        var result = ArtifactLoader.LoadAndBind(CapabilityJson.Serialize(artifact), inputs);

        Assert.True(result.IsValid);
        Assert.Equal("Aurora", result.Inputs!.Values["query"].GetString());
    }

    [Fact]
    public async Task Artifact_loader_reads_a_persisted_artifact_before_binding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"capability-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, CapabilityJson.Serialize(CreateArtifact()));

            var result = await ArtifactLoader.LoadFileAndBindAsync(
                path,
                ParseInputs("""{"query":"Aurora"}"""));

            Assert.True(result.IsValid);
            Assert.Equal("generic-search.v1", result.Artifact!.CapabilityId);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Invalid_inputs_fail_before_surface_creation()
    {
        var factoryCalls = 0;
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            CreateArtifact(),
            EmptyInputs(),
            _ =>
            {
                factoryCalls++;
                return Task.FromResult<IComputerSurface>(new FakeSurface());
            });

        Assert.Equal(ReplayRunStatus.InvalidRequest, result.Status);
        Assert.Equal(0, factoryCalls);
        Assert.Contains(result.Validation.Issues, issue => issue.Path == "inputs.query" && issue.Code == "required");
    }

    [Fact]
    public async Task Missing_optional_template_input_fails_before_surface_creation()
    {
        var factoryCalls = 0;
        var engine = new ReplayEngine();
        var artifact = CreateArtifact() with
        {
            Inputs =
            [
                new InputDefinition { Name = "query", Type = ValueTypeKind.String, Required = false }
            ]
        };

        var result = await engine.RunAsync(
            artifact,
            EmptyInputs(),
            _ =>
            {
                factoryCalls++;
                return Task.FromResult<IComputerSurface>(new FakeSurface());
            });

        Assert.Equal(ReplayRunStatus.InvalidRequest, result.Status);
        Assert.Equal(0, factoryCalls);
        Assert.Contains(result.Validation.Issues, issue => issue.Code == "input-binding-failed");
    }

    [Fact]
    public async Task Replay_executes_bound_actions_once_in_artifact_order_without_a_model()
    {
        var surface = new FakeSurface();
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Borealis"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(["enter-query", "submit-search", "settle"], result.Steps.Select(step => step.StepId));
        Assert.Equal([ActionKind.Type, ActionKind.Click, ActionKind.Wait], surface.Actions.Select(action => action.Kind));
        Assert.Equal("Borealis", surface.Actions[0].ValueTemplate);
        Assert.Equal("Borealis Atlas", result.Outputs["result"].GetString());
        Assert.All(result.Steps, step => Assert.Equal(1, step.Attempts));
    }

    [Fact]
    public async Task Declared_transient_failure_recovers_once_within_bound()
    {
        var surface = new FakeSurface(new Queue<SurfaceActionResult>(
        [
            Failure("slow-load"),
            Success(),
            Success(),
            Success()
        ]));
        var engine = new ReplayEngine();
        var original = CreateArtifact();
        var artifact = original with
        {
            Steps = original.Steps.Select((step, index) => index == 0
                ? step with
                {
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 2,
                        RecoverableConditionCodes = ["slow-load"]
                    }
                }
                : step).ToArray(),
            Outcomes = [RecoverableOutcome("slow-load")]
        };

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(2, result.Steps[0].Attempts);
        Assert.Equal(4, surface.Actions.Count);
    }

    [Fact]
    public async Task Replay_rejects_false_success_when_checkpoint_is_not_satisfied()
    {
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(visibleTargetIds: [])));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("checkpoint-not-satisfied", result.Failure!.Code);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task Replay_stops_before_action_when_step_precondition_is_not_satisfied()
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Steps =
            [
                original.Steps[0] with
                {
                    Preconditions = [new Condition { Kind = ConditionKind.Visible, Target = Target("ready") }]
                }
            ]
        };
        var surface = new FakeSurface(visibleTargetIds: ["result"]);

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("precondition-not-satisfied", result.Failure!.Code);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Replay_fails_after_action_when_step_postcondition_is_not_satisfied()
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Steps =
            [
                original.Steps[0] with
                {
                    Postcondition = new Condition { Kind = ConditionKind.Visible, Target = Target("updated") }
                }
            ]
        };
        var surface = new FakeSurface(visibleTargetIds: ["result"]);

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("postcondition-not-satisfied", result.Failure!.Code);
        Assert.Single(surface.Actions);
    }

    [Fact]
    public async Task Replay_converts_extracted_output_to_declared_type()
    {
        var artifact = CreateArtifact() with
        {
            Outputs =
            [
                CreateArtifact().Outputs[0] with { Type = ValueTypeKind.Integer }
            ]
        };
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(textValue: "42")));

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(42, result.Outputs["result"].GetInt64());
    }

    [Theory]
    [InlineData(ValueTypeKind.Decimal, "12.50", JsonValueKind.Number)]
    [InlineData(ValueTypeKind.Boolean, "TRUE", JsonValueKind.True)]
    [InlineData(ValueTypeKind.Date, "2026-09-10", JsonValueKind.String)]
    [InlineData(ValueTypeKind.Enum, "available", JsonValueKind.String)]
    public async Task Replay_converts_each_supported_output_type(
        ValueTypeKind type,
        string rawValue,
        JsonValueKind expectedKind)
    {
        var artifact = CreateArtifact() with
        {
            Outputs =
            [
                CreateArtifact().Outputs[0] with { Type = type }
            ]
        };
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(textValue: rawValue)));

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(expectedKind, result.Outputs["result"].ValueKind);
    }

    [Fact]
    public async Task Replay_omits_optional_output_when_inspection_returns_no_value()
    {
        var artifact = CreateArtifact() with
        {
            Outputs =
            [
                CreateArtifact().Outputs[0] with { Required = false }
            ]
        };
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(textValue: null)));

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Empty(result.Outputs);
    }

    [Fact]
    public async Task Replay_fails_when_extracted_output_does_not_match_declared_type()
    {
        var artifact = CreateArtifact() with
        {
            Outputs =
            [
                CreateArtifact().Outputs[0] with { Type = ValueTypeKind.Boolean }
            ]
        };
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(textValue: "not-a-boolean")));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("output-type-mismatch", result.Failure!.Code);
    }

    [Theory]
    [InlineData(OutcomeKind.BusinessOutcome, ReplayRunStatus.BusinessOutcome)]
    [InlineData(OutcomeKind.RecoverableCondition, ReplayRunStatus.RecoverableCondition)]
    [InlineData(OutcomeKind.InterventionRequired, ReplayRunStatus.InterventionRequired)]
    [InlineData(OutcomeKind.HardFailure, ReplayRunStatus.Failed)]
    public async Task Replay_classifies_detected_known_outcome(
        OutcomeKind outcomeKind,
        ReplayRunStatus expectedStatus)
    {
        var artifact = CreateArtifact() with
        {
            Outcomes =
            [
                new KnownOutcome
                {
                    Code = "known-state",
                    Description = "A known terminal state was reached.",
                    Kind = outcomeKind,
                    Detection = new Condition { Kind = ConditionKind.Visible, Target = Target("known-state") }
                }
            ]
        };
        var engine = new ReplayEngine();

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(visibleTargetIds: ["known-state"])));

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal("known-state", result.OutcomeCode);
    }

    [Fact]
    public async Task Replay_classifies_known_outcome_when_action_cannot_continue()
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Steps = [original.Steps[0]],
            Outcomes =
            [
                new KnownOutcome
                {
                    Code = "no-results",
                    Description = "The search completed without a match.",
                    Kind = OutcomeKind.BusinessOutcome,
                    Detection = new Condition { Kind = ConditionKind.Visible, Target = Target("no-results") }
                }
            ]
        };
        var surface = new FakeSurface(
            new Queue<SurfaceActionResult>([Failure("target-not-found")]),
            visibleTargetIds: ["no-results"]);

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.BusinessOutcome, result.Status);
        Assert.Equal("no-results", result.OutcomeCode);
    }

    [Fact]
    public async Task Known_intervention_outcome_logs_request_without_false_policy_denial()
    {
        var artifact = CreateArtifact() with
        {
            Outcomes =
            [
                new KnownOutcome
                {
                    Code = "permission-decision",
                    Description = "A human permission decision is required.",
                    Kind = OutcomeKind.InterventionRequired,
                    Detection = new Condition { Kind = ConditionKind.Visible, Target = Target("permission") }
                }
            ]
        };
        var sink = new RecordingRunEventSink();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(new FakeSurface(visibleTargetIds: ["permission"])),
            eventSink: sink,
            runId: "known_intervention_001");

        Assert.Equal(ReplayRunStatus.InterventionRequired, result.Status);
        Assert.Contains(sink.Events, runEvent => runEvent.Kind == RunEventKind.InterventionRequested);
        Assert.DoesNotContain(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.PolicyEvaluated && runEvent.Outcome == RunEventOutcome.Denied);
    }

    [Fact]
    public async Task Nonrecoverable_failure_is_not_retried()
    {
        var surface = new FakeSurface(new Queue<SurfaceActionResult>([Failure("ambiguous-target")]));
        var engine = new ReplayEngine();
        var original = CreateArtifact();
        var artifact = original with
        {
            Steps = original.Steps.Select((step, index) => index == 0
                ? step with
                {
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 3,
                        RecoverableConditionCodes = ["slow-load"]
                    }
                }
                : step).ToArray(),
            Outcomes = [RecoverableOutcome("slow-load")]
        };

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("ambiguous-target", result.Failure!.Code);
        Assert.Single(surface.Actions);
        Assert.Equal(1, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task Recoverable_failure_stops_at_configured_attempt_bound()
    {
        var surface = new FakeSurface(new Queue<SurfaceActionResult>(
        [
            Failure("slow-load"),
            Failure("slow-load")
        ]));
        var engine = new ReplayEngine();
        var original = CreateArtifact();
        var artifact = original with
        {
            Steps =
            [
                original.Steps[0] with
                {
                    Retry = new RetryPolicy
                    {
                        MaxAttempts = 2,
                        RecoverableConditionCodes = ["slow-load"]
                    }
                }
            ],
            Outcomes = [RecoverableOutcome("slow-load")]
        };

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("slow-load", result.Failure!.Code);
        Assert.Equal(2, result.Steps[0].Attempts);
        Assert.Equal(2, surface.Actions.Count);
    }

    [Fact]
    public async Task Step_timeout_returns_bounded_failure()
    {
        var surface = new FakeSurface(delay: TimeSpan.FromSeconds(2));
        var engine = new ReplayEngine();
        var artifact = CreateArtifact() with
        {
            Steps =
            [
                CreateArtifact().Steps[0] with
                {
                    Timeout = TimeSpan.FromMilliseconds(20),
                    Retry = RetryPolicy.None
                }
            ]
        };

        var result = await engine.RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("step-timeout", result.Failure!.Code);
        Assert.Equal(1, result.Steps[0].Attempts);
    }

    [Fact]
    public async Task Disallowed_action_is_blocked_before_surface_execution()
    {
        var policy = Policy(maximumActions: 10) with
        {
            AllowedActions = new HashSet<ActionKind>([ActionKind.Read])
        };
        var surface = new FakeSurface();

        var result = await new ReplayEngine(policy).RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("action-not-allowed", result.Failure!.Code);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Action_limit_counts_attempts_and_blocks_excess_execution()
    {
        var surface = new FakeSurface();

        var result = await new ReplayEngine(Policy(maximumActions: 1)).RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("action-limit-exceeded", result.Failure!.Code);
        Assert.Single(surface.Actions);
    }

    [Theory]
    [InlineData(RiskLevel.Safe)]
    [InlineData(RiskLevel.ReversibleWrite)]
    public async Task Replay_executes_non_irreversible_risk_classes(RiskLevel risk)
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Risk = risk,
            Steps = original.Steps.Select(step => step with { Risk = risk }).ToArray()
        };
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(3, surface.Actions.Count);
    }

    [Fact]
    public async Task Replay_requests_intervention_before_irreversible_action()
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Risk = RiskLevel.Irreversible,
            Steps = [original.Steps[0] with { Risk = RiskLevel.Irreversible }]
        };
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.InterventionRequired, result.Status);
        Assert.Equal("enter-query", result.Failure!.StepId);
        Assert.Equal("intervention-required", result.Failure.Code);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Replay_events_share_run_id_and_record_each_executed_step()
    {
        const string runId = "replay_run_001";
        var sink = new RecordingRunEventSink();
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface),
            eventSink: sink,
            runId: runId);

        Assert.Equal(runId, result.RunId);
        Assert.All(sink.Events, runEvent => Assert.Equal(runId, runEvent.RunId));
        Assert.Equal(RunEventKind.RunStarted, sink.Events[0].Kind);
        Assert.Equal(3, sink.Events.Count(runEvent => runEvent.Kind == RunEventKind.ActionCompleted));
        Assert.Equal(3, sink.Events.Count(runEvent =>
            runEvent.Kind == RunEventKind.PolicyEvaluated && runEvent.Outcome == RunEventOutcome.Allowed));
        Assert.All(
            sink.Events.Where(runEvent => runEvent.Kind is RunEventKind.PolicyEvaluated or RunEventKind.ActionCompleted),
            runEvent => Assert.NotNull(runEvent.Risk));
        Assert.Equal(RunEventOutcome.Completed, sink.Events[^1].Outcome);
    }

    [Fact]
    public async Task Replay_intervention_events_are_correlated_and_no_action_event_is_emitted()
    {
        const string runId = "replay_intervention_001";
        var original = CreateArtifact();
        var artifact = original with
        {
            Risk = RiskLevel.Irreversible,
            Steps = [original.Steps[0] with { Risk = RiskLevel.Irreversible }]
        };
        var sink = new RecordingRunEventSink();
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface),
            eventSink: sink,
            runId: runId);

        Assert.Equal(ReplayRunStatus.InterventionRequired, result.Status);
        Assert.Contains(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.PolicyEvaluated &&
            runEvent.Outcome == RunEventOutcome.Denied &&
            runEvent.Risk == RiskLevel.Irreversible);
        Assert.Contains(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.InterventionRequested &&
            runEvent.Risk == RiskLevel.Irreversible);
        Assert.DoesNotContain(sink.Events, runEvent => runEvent.Kind == RunEventKind.ActionCompleted);
        Assert.Equal(RunEventOutcome.InterventionRequired, sink.Events[^1].Outcome);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Replay_handoff_executes_proposed_action_only_under_human_control_and_resumes()
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Risk = RiskLevel.Irreversible,
            Steps = [original.Steps[1] with { Risk = RiskLevel.Irreversible }]
        };
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface),
            interventionHandler: async (operatorSurface, cancellationToken) =>
            {
                await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version, cancellationToken);
                var actionResult = await operatorSurface.ExecuteHumanActionAsync(
                    operatorSurface.Request.ProposedAction,
                    operatorSurface.State.Version,
                    cancellationToken);
                Assert.True(actionResult.Succeeded);
                return OperatorDecision.Resume;
            });

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(OperatorDecision.Resume, result.OperatorDecision);
        Assert.NotNull(result.InterventionSummary);
        Assert.True(result.InterventionSummary.ApprovalRecorded);
        Assert.True(result.InterventionSummary.HumanActionSucceeded);
        Assert.True(result.InterventionSummary.ResumeValidationSucceeded);
        Assert.Equal(ControlOwner.Automation, result.InterventionSummary.FinalOwner);
        Assert.NotEmpty(result.Evidence);
        Assert.Empty(surface.Actions);
        Assert.Single(surface.HumanActions);
    }

    [Theory]
    [InlineData(OperatorDecision.Complete, ReplayRunStatus.Success, RunEventOutcome.Completed)]
    [InlineData(OperatorDecision.Decline, ReplayRunStatus.Cancelled, RunEventOutcome.Cancelled)]
    [InlineData(OperatorDecision.Cancel, ReplayRunStatus.Cancelled, RunEventOutcome.Cancelled)]
    [InlineData(OperatorDecision.Unresolved, ReplayRunStatus.InterventionRequired, RunEventOutcome.InterventionRequired)]
    public async Task Replay_maps_operator_terminal_decisions_consistently(
        OperatorDecision decision,
        ReplayRunStatus expectedStatus,
        RunEventOutcome expectedTerminalOutcome)
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Risk = RiskLevel.Irreversible,
            Steps = [original.Steps[1] with { Risk = RiskLevel.Irreversible }]
        };
        var sink = new RecordingRunEventSink();
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface),
            eventSink: sink,
            interventionHandler: async (operatorSurface, cancellationToken) =>
            {
                if (decision == OperatorDecision.Complete)
                {
                    await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version, cancellationToken);
                    await operatorSurface.ExecuteHumanActionAsync(
                        operatorSurface.Request.ProposedAction,
                        operatorSurface.State.Version,
                        cancellationToken);
                }

                return decision;
            });

        Assert.Equal(expectedStatus, result.Status);
        Assert.Equal(decision, result.OperatorDecision);
        Assert.Equal(RunEventKind.RunCompleted, sink.Events[^1].Kind);
        Assert.Equal(expectedTerminalOutcome, sink.Events[^1].Outcome);
    }

    [Fact]
    public async Task Replay_events_record_effective_risk_after_runtime_target_binding()
    {
        const string runId = "replay_bound_risk_001";
        var original = CreateArtifact();
        var artifact = original with
        {
            Risk = RiskLevel.Safe,
            Steps =
            [
                original.Steps[1] with
                {
                    Id = "bound-click",
                    Risk = RiskLevel.Safe,
                    Action = new SemanticAction
                    {
                        Kind = ActionKind.Click,
                        Target = Target("open-${query}")
                    }
                }
            ]
        };
        var sink = new RecordingRunEventSink();
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            artifact,
            ParseInputs("""{"query":"confirm purchase"}"""),
            _ => Task.FromResult<IComputerSurface>(surface),
            eventSink: sink,
            runId: runId);

        Assert.Equal(ReplayRunStatus.InterventionRequired, result.Status);
        Assert.Contains(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.PolicyEvaluated &&
            runEvent.Outcome == RunEventOutcome.Denied &&
            runEvent.Risk == RiskLevel.Irreversible);
        Assert.Contains(sink.Events, runEvent =>
            runEvent.Kind == RunEventKind.InterventionRequested &&
            runEvent.Risk == RiskLevel.Irreversible);
        Assert.Empty(surface.Actions);
    }

    [Fact]
    public async Task Replay_success_is_not_changed_by_logging_failure()
    {
        var surface = new FakeSurface();

        var result = await new ReplayEngine().RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface),
            eventSink: new ThrowingRunEventSink());

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(3, surface.Actions.Count);
    }

    [Fact]
    public async Task Replay_exception_records_failed_terminal_event()
    {
        const string runId = "replay_exception_001";
        var sink = new RecordingRunEventSink();

        await Assert.ThrowsAsync<InvalidOperationException>(() => new ReplayEngine().RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => throw new InvalidOperationException("Synthetic surface failure."),
            eventSink: sink,
            runId: runId));

        Assert.Equal(RunEventKind.RunStarted, sink.Events[0].Kind);
        Assert.Equal(RunEventKind.RunCompleted, sink.Events[^1].Kind);
        Assert.Equal(RunEventOutcome.Failed, sink.Events[^1].Outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Redirect_or_popup_escape_is_blocked_after_action(bool includeAllowedPage)
    {
        var escapedLocations = includeAllowedPage
            ? new[] { new Uri("https://example.test/"), new Uri("https://outside.test/") }
            : new[] { new Uri("https://outside.test/") };
        var surface = new FakeSurface(locationsAfterAction: escapedLocations);

        var result = await new ReplayEngine(Policy(maximumActions: 10)).RunAsync(
            CreateArtifact(),
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("navigation-not-allowed", result.Failure!.Code);
        Assert.Single(surface.Actions);
    }

    [Fact]
    public async Task Navigation_policy_failure_cannot_be_reclassified_as_business_outcome()
    {
        var original = CreateArtifact();
        var artifact = original with
        {
            Outcomes =
            [
                new KnownOutcome
                {
                    Code = "no-results",
                    Description = "A visible business outcome that must not override policy.",
                    Kind = OutcomeKind.BusinessOutcome,
                    Detection = new Condition { Kind = ConditionKind.Visible, Target = Target("no-results") }
                }
            ]
        };
        var surface = new FakeSurface(
            visibleTargetIds: ["result", "no-results"],
            locationsAfterAction: [new Uri("https://outside.test/")]);

        var result = await new ReplayEngine(Policy(maximumActions: 10)).RunAsync(
            artifact,
            ParseInputs("""{"query":"Aurora"}"""),
            _ => Task.FromResult<IComputerSurface>(surface));

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("navigation-not-allowed", result.Failure!.Code);
        Assert.Null(result.OutcomeCode);
    }

    private static CapabilityArtifact CreateArtifact() => new()
    {
        CapabilityId = "generic-search.v1",
        Name = "Generic search",
        Description = "Exercises ordered parameterized replay against any compatible surface.",
        Target = new CapabilityTarget
        {
            Surface = SurfaceKind.Web,
            EntryPointTemplate = new Uri("https://example.test/")
        },
        Inputs =
        [
            new InputDefinition { Name = "query", Type = ValueTypeKind.String, Pattern = "^.{1,100}$" }
        ],
        Outputs =
        [
            new OutputDefinition
            {
                Name = "result",
                Type = ValueTypeKind.String,
                Extraction = new ValueExtraction { Target = Target("result"), Kind = ExtractionKind.Text }
            }
        ],
        Steps =
        [
            new CapabilityStep
            {
                Id = "enter-query",
                Action = new SemanticAction
                {
                    Kind = ActionKind.Type,
                    Target = Target("query"),
                    ValueTemplate = "${query}"
                }
            },
            new CapabilityStep
            {
                Id = "submit-search",
                Action = new SemanticAction { Kind = ActionKind.Click, Target = Target("search") }
            },
            new CapabilityStep
            {
                Id = "settle",
                Action = new SemanticAction { Kind = ActionKind.Wait, Duration = TimeSpan.FromMilliseconds(1) }
            }
        ],
        Checkpoint = new Condition { Kind = ConditionKind.Visible, Target = Target("result") },
        Provenance = new CapabilityProvenance
        {
            DiscoveryRunId = "deterministic-fixture",
            CreatedAt = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero),
            GeneratorVersion = "0.1.0"
        }
    };

    private static TargetDescriptor Target(string id) => new()
    {
        Strategies = [new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = id }]
    };

    private static AutomationPolicy Policy(int maximumActions) => AutomationPolicy.SameOrigin(
        new Uri("https://example.test/"),
        maximumActions);

    private static KnownOutcome RecoverableOutcome(string code) => new()
    {
        Code = code,
        Description = "The synthetic surface is temporarily unavailable.",
        Kind = OutcomeKind.RecoverableCondition,
        Detection = new Condition { Kind = ConditionKind.Visible, Target = Target("loading") }
    };

    private static IReadOnlyDictionary<string, JsonElement> EmptyInputs() =>
        new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, JsonElement> ParseInputs(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }

    private static SurfaceActionResult Success() => new() { Succeeded = true };

    private static SurfaceActionResult Failure(string code) => new()
    {
        Succeeded = false,
        ErrorCode = code,
        SafeMessage = $"Synthetic {code} failure."
    };

    private sealed class FakeSurface : IComputerSurface
    {
        private readonly Queue<SurfaceActionResult> results;
        private readonly TimeSpan delay;
        private readonly HashSet<string> visibleTargetIds;
        private readonly string? textValue;
        private readonly IReadOnlyList<Uri> initialLocations;
        private readonly IReadOnlyList<Uri>? locationsAfterAction;
        private bool humanControl;

        public FakeSurface(
            Queue<SurfaceActionResult>? results = null,
            TimeSpan delay = default,
            IEnumerable<string>? visibleTargetIds = null,
            string? textValue = "Borealis Atlas",
            IReadOnlyList<Uri>? initialLocations = null,
            IReadOnlyList<Uri>? locationsAfterAction = null)
        {
            this.results = results ?? new Queue<SurfaceActionResult>();
            this.delay = delay;
            this.visibleTargetIds = (visibleTargetIds ?? ["result"]).ToHashSet(StringComparer.Ordinal);
            this.textValue = textValue;
            this.initialLocations = initialLocations ?? [new Uri("https://example.test/")];
            this.locationsAfterAction = locationsAfterAction;
        }

        public List<SemanticAction> Actions { get; } = [];

        public List<SemanticAction> HumanActions { get; } = [];

        public Task<SurfaceObservation> ObserveAsync(CancellationToken cancellationToken) => Task.FromResult(new SurfaceObservation
        {
            Url = new Uri("https://example.test/"),
            Title = "Synthetic surface",
            VisibleText = "Result available",
            InteractiveElements = [],
            Fingerprint = "synthetic-state"
        });

        public async Task<SurfaceActionResult> ExecuteAsync(
            SemanticAction action,
            CancellationToken cancellationToken)
        {
            if (humanControl)
            {
                return Failure("automation-paused");
            }

            Actions.Add(action);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken);
            }

            return results.Count > 0 ? results.Dequeue() : Success();
        }

        public Task<SurfaceInspectionResult> InspectAsync(
            SurfaceInspection inspection,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = inspection.Target?.Strategies.FirstOrDefault()?.Value;
            var value = inspection.Kind switch
            {
                SurfaceInspectionKind.Visible => visibleTargetIds.Contains(id ?? string.Empty).ToString().ToLowerInvariant(),
                SurfaceInspectionKind.Text => textValue,
                SurfaceInspectionKind.Value => textValue,
                SurfaceInspectionKind.Url => "https://example.test/",
                SurfaceInspectionKind.State => "enabled",
                SurfaceInspectionKind.Attribute => textValue,
                _ => null
            };

            return Task.FromResult(new SurfaceInspectionResult { Succeeded = true, Value = value });
        }

        public Task<IReadOnlyList<Uri>> GetActiveLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Actions.Count > 0 && locationsAfterAction is not null
                ? locationsAfterAction
                : initialLocations);

        public Task<EvidenceReference> CaptureEvidenceAsync(EvidenceKind kind, CancellationToken cancellationToken) =>
            Task.FromResult(new EvidenceReference
            {
                Kind = kind.ToString().ToLowerInvariant(),
                RelativePath = $"synthetic.{(kind == EvidenceKind.Screenshot ? "png" : "txt")}",
                Protection = EvidenceProtection.Synthetic
            });

        public Task<IHumanControlSession> TransferControlToHumanAsync(
            HumanControlAuthorization authorization,
            CancellationToken cancellationToken)
        {
            if (humanControl)
            {
                throw new InvalidOperationException("Human control is already active.");
            }

            humanControl = true;
            return Task.FromResult<IHumanControlSession>(new FakeHumanSession(this));
        }

        private sealed class FakeHumanSession(FakeSurface owner) : IHumanControlSession
        {
            private bool ended;

            public Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken)
            {
                ObjectDisposedException.ThrowIf(ended, this);
                owner.HumanActions.Add(action);
                return Task.FromResult(Success());
            }

            public Task ResumeAutomationAsync(CancellationToken cancellationToken) => EndAsync(cancellationToken);

            public Task CancelAsync(CancellationToken cancellationToken) => EndAsync(cancellationToken);

            private Task EndAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ended)
                {
                    owner.humanControl = false;
                    ended = true;
                }
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync() => await EndAsync(CancellationToken.None);
        }

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
