using ComputerUse.Core.Contracts;
using ComputerUse.Core.Intervention;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Surfaces;

namespace ComputerUse.Core.Tests;

public sealed class InterventionTests
{
    [Fact]
    public async Task Begin_transfers_versioned_control_and_blocks_automation()
    {
        var surface = new ControlledSurface();
        var sink = new RecordingSink();

        var operatorSurface = await LocalOperatorSurface.BeginAsync(Request(), surface, sink);
        var blocked = await surface.ExecuteAsync(Request().ProposedAction, CancellationToken.None);

        Assert.Equal(ControlPhase.Human, operatorSurface.State.Phase);
        Assert.Equal(ControlOwner.Human, operatorSurface.State.Owner);
        Assert.Equal(2, operatorSurface.State.Version);
        Assert.False(blocked.Succeeded);
        Assert.Equal("automation-paused", blocked.ErrorCode);
        Assert.Equal([ControlPhase.Pausing, ControlPhase.Human], operatorSurface.Transitions.Select(item => item.To));
        Assert.Contains(sink.Events, item => item.Kind == RunEventKind.ControlTransferred && item.ControlOwner == ControlOwner.Human);
    }

    [Fact]
    public async Task Human_action_uses_current_control_version_and_is_recorded_separately()
    {
        var surface = new ControlledSurface();
        var sink = new RecordingSink();
        var operatorSurface = await LocalOperatorSurface.BeginAsync(Request(), surface, sink);

        await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version);
        var result = await operatorSurface.ExecuteHumanActionAsync(
            Request().ProposedAction,
            operatorSurface.State.Version);

        Assert.True(result.Succeeded);
        var action = Assert.Single(operatorSurface.HumanActions);
        Assert.Equal(ActionKind.Click, action.Action);
        Assert.True(action.Succeeded);
        Assert.Contains(sink.Events, item => item.Kind == RunEventKind.HumanActionCompleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operatorSurface.ExecuteHumanActionAsync(Request().ProposedAction, expectedVersion: 1));
    }

    [Fact]
    public async Task Resume_requires_fresh_state_to_satisfy_precondition()
    {
        var surface = new ControlledSurface { VisibleText = "changed state" };
        var sink = new RecordingSink();
        var request = Request() with
        {
            ResumeCondition = new Condition
            {
                Kind = ConditionKind.TextEquals,
                Target = Target("state"),
                ExpectedTemplate = "expected state"
            }
        };
        var operatorSurface = await LocalOperatorSurface.BeginAsync(request, surface, sink);
        await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version);
        await operatorSurface.ExecuteHumanActionAsync(Request().ProposedAction, operatorSurface.State.Version);

        var unresolved = await operatorSurface.ChooseAsync(
            OperatorDecision.Resume,
            operatorSurface.State.Version);

        Assert.Equal(ControlPhase.Human, unresolved.State.Phase);
        Assert.Equal(ControlOwner.Human, unresolved.State.Owner);
        Assert.False(surface.ResumeCalled);
        Assert.NotNull(unresolved.FreshObservation);

        surface.VisibleText = "expected state";
        var resumed = await operatorSurface.ChooseAsync(
            OperatorDecision.Resume,
            operatorSurface.State.Version);

        Assert.Equal(ControlPhase.Automation, resumed.State.Phase);
        Assert.Equal(ControlOwner.Automation, resumed.State.Owner);
        Assert.True(surface.ResumeCalled);
        Assert.Contains(sink.Events, item =>
            item.Kind == RunEventKind.SessionContinuityVerified &&
            item.Outcome == RunEventOutcome.Succeeded &&
            item.ControlOwner == ControlOwner.Human &&
            item.SessionContinuityCommitment == "test-session-commitment");
    }

    [Fact]
    public async Task Resume_applies_negated_condition_semantics()
    {
        var surface = new ControlledSurface { VisibleText = "unsafe" };
        var request = Request() with
        {
            ResumeCondition = new Condition
            {
                Kind = ConditionKind.TextEquals,
                Target = Target("state"),
                ExpectedTemplate = "unsafe",
                Negate = true
            }
        };
        var operatorSurface = await LocalOperatorSurface.BeginAsync(request, surface);
        await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version);
        await operatorSurface.ExecuteHumanActionAsync(request.ProposedAction, operatorSurface.State.Version);

        var blocked = await operatorSurface.ChooseAsync(OperatorDecision.Resume, operatorSurface.State.Version);

        Assert.Equal(ControlPhase.Human, blocked.State.Phase);
        Assert.False(surface.ResumeCalled);
    }

    [Theory]
    [InlineData(OperatorDecision.Complete, ControlPhase.Completed, ControlOwner.None)]
    [InlineData(OperatorDecision.Decline, ControlPhase.Cancelled, ControlOwner.None)]
    [InlineData(OperatorDecision.Cancel, ControlPhase.Cancelled, ControlOwner.None)]
    [InlineData(OperatorDecision.Unresolved, ControlPhase.Human, ControlOwner.Human)]
    public async Task Operator_outcomes_have_explicit_terminal_or_held_states(
        OperatorDecision decision,
        ControlPhase expectedPhase,
        ControlOwner expectedOwner)
    {
        var operatorSurface = await LocalOperatorSurface.BeginAsync(Request(), new ControlledSurface());

        if (decision == OperatorDecision.Complete)
        {
            await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version);
            await operatorSurface.ExecuteHumanActionAsync(
                operatorSurface.Request.ProposedAction,
                operatorSurface.State.Version);
        }

        var resolution = await operatorSurface.ChooseAsync(decision, operatorSurface.State.Version);

        Assert.Equal(expectedPhase, resolution.State.Phase);
        Assert.Equal(expectedOwner, resolution.State.Owner);
        Assert.Equal(decision, resolution.State.Outcome);
    }

    [Fact]
    public async Task Human_action_is_blocked_until_approval_is_recorded()
    {
        var operatorSurface = await LocalOperatorSurface.BeginAsync(Request(), new ControlledSurface());

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operatorSurface.ExecuteHumanActionAsync(
                operatorSurface.Request.ProposedAction,
                operatorSurface.State.Version));

        Assert.Contains("recorded approval", exception.Message, StringComparison.Ordinal);
        Assert.Empty(operatorSurface.HumanActions);
    }

    [Fact]
    public async Task Failed_approval_persistence_does_not_authorize_human_action()
    {
        var operatorSurface = await LocalOperatorSurface.BeginAsync(Request(), new ControlledSurface(), new FailingApprovalSink());

        await Assert.ThrowsAsync<IOException>(() =>
            operatorSurface.RecordApprovalAsync(operatorSurface.State.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operatorSurface.ExecuteHumanActionAsync(
                operatorSurface.Request.ProposedAction,
                operatorSurface.State.Version));
        Assert.Empty(operatorSurface.HumanActions);
    }

    private static InterventionRequest Request() => new()
    {
        RunId = "intervention_run_001",
        CapabilityId = "risk-gated-submit.v1",
        Goal = "Submit the reviewed synthetic change.",
        Target = new Uri("https://fixture.test/review"),
        StepId = "submit-final-change",
        ProposedAction = new SemanticAction { Kind = ActionKind.Click, Target = Target("submit") },
        Reason = "Final submission is consequential.",
        Risk = RiskLevel.Irreversible,
        RedactedStateSummary = "Review form is ready; values omitted."
    };

    private static TargetDescriptor Target(string id) => new()
    {
        Strategies = [new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = id }]
    };

    private sealed class ControlledSurface : IComputerSurface
    {
        private bool paused;

        public string VisibleText { get; set; } = "expected state";

        public bool ResumeCalled { get; private set; }

        public Task<SurfaceObservation> ObserveAsync(CancellationToken cancellationToken) => Task.FromResult(new SurfaceObservation
        {
            Url = new Uri("https://fixture.test/review"),
            Title = "Review",
            VisibleText = VisibleText,
            InteractiveElements = [],
            Fingerprint = VisibleText
        });

        public Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken) =>
            Task.FromResult(paused
                ? new SurfaceActionResult { Succeeded = false, ErrorCode = "automation-paused", SafeMessage = "Automation is paused." }
                : new SurfaceActionResult { Succeeded = true });

        public Task<SurfaceInspectionResult> InspectAsync(
            SurfaceInspection inspection,
            CancellationToken cancellationToken) => Task.FromResult(new SurfaceInspectionResult
            {
                Succeeded = true,
                Value = VisibleText
            });

        public Task<IReadOnlyList<Uri>> GetActiveLocationsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Uri>>([new Uri("https://fixture.test/review")]);

        public Task<string?> GetSessionContinuityCommitmentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<string?>("test-session-commitment");

        public Task<EvidenceReference> CaptureEvidenceAsync(EvidenceKind kind, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IHumanControlSession> TransferControlToHumanAsync(
            HumanControlAuthorization authorization,
            CancellationToken cancellationToken)
        {
            paused = true;
            return Task.FromResult<IHumanControlSession>(new ControlledHumanSession(this));
        }

        private sealed class ControlledHumanSession(ControlledSurface owner) : IHumanControlSession
        {
            private bool ended;

            public Task<SurfaceActionResult> ExecuteAsync(SemanticAction action, CancellationToken cancellationToken) =>
                Task.FromResult(new SurfaceActionResult { Succeeded = true });

            public Task ResumeAutomationAsync(CancellationToken cancellationToken)
            {
                owner.paused = false;
                owner.ResumeCalled = true;
                ended = true;
                return Task.CompletedTask;
            }

            public Task CancelAsync(CancellationToken cancellationToken)
            {
                owner.paused = false;
                ended = true;
                return Task.CompletedTask;
            }

            public async ValueTask DisposeAsync()
            {
                if (!ended)
                {
                    await CancelAsync(CancellationToken.None);
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingSink : IRunEventSink
    {
        public List<RunEvent> Events { get; } = [];

        public Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken)
        {
            Events.Add(runEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingApprovalSink : IRunEventSink
    {
        public Task WriteAsync(RunEvent runEvent, CancellationToken cancellationToken) =>
            runEvent.Kind == RunEventKind.HumanApprovalRecorded
                ? Task.FromException(new IOException("Synthetic approval persistence failure."))
                : Task.CompletedTask;
    }
}
