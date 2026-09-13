using ComputerUse.Core.Contracts;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Surfaces;
using System.Text.RegularExpressions;

namespace ComputerUse.Core.Intervention;

public sealed class LocalOperatorSurface : IAsyncDisposable
{
    private readonly IComputerSurface surface;
    private readonly IHumanControlSession humanSession;
    private readonly IRunEventSink eventSink;
    private readonly string? initialSessionContinuityCommitment;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly List<ControlTransition> transitions = [];
    private readonly List<HumanActionRecord> humanActions = [];
    private long? approvedControlVersion;

    private LocalOperatorSurface(
        InterventionRequest request,
        IComputerSurface surface,
        IHumanControlSession humanSession,
        IRunEventSink eventSink,
        string? initialSessionContinuityCommitment)
    {
        Request = request;
        this.surface = surface;
        this.humanSession = humanSession;
        this.eventSink = eventSink;
        this.initialSessionContinuityCommitment = initialSessionContinuityCommitment;
        State = new ControlState { Phase = ControlPhase.Automation, Owner = ControlOwner.Automation, Version = 0 };
    }

    public InterventionRequest Request { get; }

    public ControlState State { get; private set; }

    public IReadOnlyList<ControlTransition> Transitions => transitions;

    public IReadOnlyList<HumanActionRecord> HumanActions => humanActions;

    public bool ApprovalWasRecorded { get; private set; }

    public string? VerifiedSessionContinuityCommitment { get; private set; }

    public async Task RecordApprovalAsync(
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertHumanControl(expectedVersion);
            await eventSink.WriteAsync(new RunEvent
            {
                RunId = Request.RunId,
                Timestamp = DateTimeOffset.UtcNow,
                Mode = RuntimeMode.Replay,
                Kind = RunEventKind.HumanApprovalRecorded,
                Outcome = RunEventOutcome.Selected,
                StepId = Request.StepId,
                Action = Request.ProposedAction.Kind,
                Risk = Request.Risk,
                ControlOwner = State.Owner,
                ControlVersion = State.Version,
                OperatorDecision = OperatorDecision.Resume
            }, cancellationToken).ConfigureAwait(false);
            approvedControlVersion = expectedVersion;
            ApprovalWasRecorded = true;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public static async Task<LocalOperatorSurface> BeginAsync(
        InterventionRequest request,
        IComputerSurface surface,
        IRunEventSink? eventSink = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(surface);
        Validate(request);

        var sessionContinuityCommitment = await surface.GetSessionContinuityCommitmentAsync(cancellationToken).ConfigureAwait(false);
        var authorization = new HumanControlAuthorization(request.RunId, request.StepId);
        var humanSession = await surface.TransferControlToHumanAsync(authorization, cancellationToken).ConfigureAwait(false);
        var operatorSurface = new LocalOperatorSurface(
            request,
            surface,
            humanSession,
            eventSink ?? NullRunEventSink.Instance,
            sessionContinuityCommitment);
        operatorSurface.Transition(ControlPhase.Pausing, ControlOwner.Automation);
        operatorSurface.Transition(ControlPhase.Human, ControlOwner.Human);
        await operatorSurface.WriteEventAsync(
            RunEventKind.ControlTransferred,
            RunEventOutcome.InterventionRequired,
            cancellationToken).ConfigureAwait(false);
        return operatorSurface;
    }

    public async Task<SurfaceActionResult> ExecuteHumanActionAsync(
        SemanticAction action,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertHumanControl(expectedVersion);
            if (approvedControlVersion != expectedVersion)
            {
                throw new InvalidOperationException("The proposed human action requires recorded approval for the current control version.");
            }
            if (!MatchesProposedAction(action, Request.ProposedAction))
            {
                throw new InvalidOperationException("The human action must match the approved proposed action.");
            }

            approvedControlVersion = null;
            var result = await humanSession.ExecuteAsync(action, cancellationToken).ConfigureAwait(false);
            humanActions.Add(new HumanActionRecord(
                DateTimeOffset.UtcNow,
                State.Version,
                action.Kind,
                result.Succeeded,
                NormalizeErrorCode(result.ErrorCode)));
            await WriteEventAsync(
                RunEventKind.HumanActionCompleted,
                result.Succeeded ? RunEventOutcome.Succeeded : RunEventOutcome.Failed,
                CancellationToken.None,
                action.Kind).ConfigureAwait(false);
            return result;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<InterventionResolution> ChooseAsync(
        OperatorDecision decision,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        await operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            AssertHumanControl(expectedVersion);
            if (!Request.AllowedDecisions.Contains(decision))
            {
                throw new InvalidOperationException($"Operator decision '{decision}' is not allowed for this request.");
            }

            if (decision == OperatorDecision.Resume)
            {
                if (!humanActions.Any(action => action.Succeeded))
                {
                    return new InterventionResolution(State, SafeMessage: "The proposed human action has not completed successfully.");
                }

                Transition(ControlPhase.Resuming, ControlOwner.Human, decision);
                try
                {
                    var observation = await surface.ObserveAsync(cancellationToken).ConfigureAwait(false);
                    if (!await ResumeStateIsValidAsync(cancellationToken).ConfigureAwait(false))
                    {
                        Transition(ControlPhase.Human, ControlOwner.Human, OperatorDecision.Unresolved);
                        await WriteEventAsync(
                            RunEventKind.ControlTransferred,
                            RunEventOutcome.InterventionRequired,
                            CancellationToken.None).ConfigureAwait(false);
                        return new InterventionResolution(State, observation, "The fresh surface state did not satisfy mandatory resume validation.");
                    }

                    var currentCommitment = await surface.GetSessionContinuityCommitmentAsync(cancellationToken).ConfigureAwait(false);
                    if (initialSessionContinuityCommitment is not null &&
                        !string.Equals(initialSessionContinuityCommitment, currentCommitment, StringComparison.Ordinal))
                    {
                        Transition(ControlPhase.Human, ControlOwner.Human, OperatorDecision.Unresolved);
                        await WriteEventAsync(
                            RunEventKind.ControlTransferred,
                            RunEventOutcome.InterventionRequired,
                            CancellationToken.None).ConfigureAwait(false);
                        return new InterventionResolution(State, observation, "The browser session changed while human control was active.");
                    }

                    await WriteEventAsync(
                        RunEventKind.SessionContinuityVerified,
                        RunEventOutcome.Succeeded,
                        CancellationToken.None,
                        sessionContinuityCommitment: currentCommitment).ConfigureAwait(false);
                    VerifiedSessionContinuityCommitment = currentCommitment;
                    await humanSession.ResumeAutomationAsync(cancellationToken).ConfigureAwait(false);
                    Transition(ControlPhase.Automation, ControlOwner.Automation, decision);
                    await WriteEventAsync(
                        RunEventKind.ControlTransferred,
                        RunEventOutcome.Succeeded,
                        CancellationToken.None).ConfigureAwait(false);
                    return new InterventionResolution(State, observation);
                }
                catch
                {
                    Transition(ControlPhase.Human, ControlOwner.Human, OperatorDecision.Unresolved);
                    throw;
                }
            }

            if (decision == OperatorDecision.Unresolved)
            {
                Transition(ControlPhase.Human, ControlOwner.Human, decision);
                await WriteEventAsync(
                    RunEventKind.ControlTransferred,
                    RunEventOutcome.InterventionRequired,
                    CancellationToken.None).ConfigureAwait(false);
                return new InterventionResolution(State, SafeMessage: "The intervention remains unresolved under human control.");
            }

            if (decision == OperatorDecision.Complete && !humanActions.Any(action => action.Succeeded))
            {
                return new InterventionResolution(State, SafeMessage: "Completion requires a successful approved human action.");
            }

            await humanSession.CancelAsync(CancellationToken.None).ConfigureAwait(false);
            var terminalPhase = decision == OperatorDecision.Complete
                ? ControlPhase.Completed
                : ControlPhase.Cancelled;
            Transition(terminalPhase, ControlOwner.None, decision);
            await WriteEventAsync(
                RunEventKind.ControlTransferred,
                decision == OperatorDecision.Complete ? RunEventOutcome.Completed : RunEventOutcome.Cancelled,
                CancellationToken.None).ConfigureAwait(false);
            return new InterventionResolution(State);
        }
        finally
        {
            operationGate.Release();
        }
    }

    private static void Validate(InterventionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.RunId) || string.IsNullOrWhiteSpace(request.CapabilityId) ||
            string.IsNullOrWhiteSpace(request.StepId) || string.IsNullOrWhiteSpace(request.Goal) ||
            string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(request.RedactedStateSummary))
        {
            throw new ArgumentException("Intervention requests require opaque identifiers, goal, reason, and redacted state summary.", nameof(request));
        }

        if (request.Risk != RiskLevel.Irreversible)
        {
            throw new ArgumentException("The mandatory operator handoff accepts only irreversible actions.", nameof(request));
        }
    }

    private void AssertHumanControl(long expectedVersion)
    {
        if (State.Owner != ControlOwner.Human || State.Phase != ControlPhase.Human)
        {
            throw new InvalidOperationException("The human operator does not own control in the current state.");
        }

        if (State.Version != expectedVersion)
        {
            throw new InvalidOperationException("The operator control version is stale.");
        }
    }

    private static bool MatchesProposedAction(SemanticAction action, SemanticAction proposed) =>
        action.Kind == proposed.Kind &&
        Equals(action.Destination, proposed.Destination) &&
        string.Equals(action.ValueTemplate, proposed.ValueTemplate, StringComparison.Ordinal) &&
        action.Duration == proposed.Duration &&
        ((action.Target is null && proposed.Target is null) ||
         (action.Target is not null && proposed.Target is not null &&
          action.Target.RequireUniqueMatch == proposed.Target.RequireUniqueMatch &&
          action.Target.Strategies.SequenceEqual(proposed.Target.Strategies)));

    private async Task<bool> ResumeStateIsValidAsync(CancellationToken cancellationToken)
    {
        var locations = await surface.GetActiveLocationsAsync(cancellationToken).ConfigureAwait(false);
        if (locations.Count == 0 || locations.Any(location =>
            !string.Equals(location.Scheme, Request.Target.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(location.Host, Request.Target.Host, StringComparison.OrdinalIgnoreCase) ||
            location.Port != Request.Target.Port))
        {
            return false;
        }

        if (Request.ResumeCondition is null)
        {
            return true;
        }

        var inspection = await surface.InspectAsync(ToInspection(Request.ResumeCondition), cancellationToken).ConfigureAwait(false);
        if (!inspection.Succeeded)
        {
            return false;
        }

        var matched = Request.ResumeCondition.Kind switch
        {
            ConditionKind.Visible => bool.TryParse(inspection.Value, out var visible) && visible,
            ConditionKind.Hidden => bool.TryParse(inspection.Value, out var visible) && !visible,
            ConditionKind.TextEquals or ConditionKind.ValueEquals or ConditionKind.StateEquals =>
                string.Equals(inspection.Value, Request.ResumeCondition.ExpectedTemplate, StringComparison.Ordinal),
            ConditionKind.TextContains =>
                inspection.Value?.Contains(Request.ResumeCondition.ExpectedTemplate!, StringComparison.Ordinal) == true,
            ConditionKind.UrlMatches => Regex.IsMatch(
                inspection.Value ?? string.Empty,
                Request.ResumeCondition.ExpectedTemplate!,
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1)),
            _ => false
        };
        return Request.ResumeCondition.Negate ? !matched : matched;
    }

    private static SurfaceInspection ToInspection(Condition condition) => new()
    {
        Kind = condition.Kind switch
        {
            ConditionKind.Visible or ConditionKind.Hidden => SurfaceInspectionKind.Visible,
            ConditionKind.TextEquals or ConditionKind.TextContains => SurfaceInspectionKind.Text,
            ConditionKind.ValueEquals => SurfaceInspectionKind.Value,
            ConditionKind.StateEquals => SurfaceInspectionKind.State,
            ConditionKind.UrlMatches => SurfaceInspectionKind.Url,
            _ => throw new InvalidOperationException($"Unsupported resume condition '{condition.Kind}'.")
        },
        Target = condition.Target
    };

    private static string? NormalizeErrorCode(string? errorCode) => errorCode switch
    {
        null => null,
        "ambiguous-target" or "automation-paused" or "invalid-action" or "navigation-not-allowed" or
        "surface-action-failed" or "target-not-found" or "unsupported-action" => errorCode,
        _ => "human-action-failed"
    };

    private void Transition(ControlPhase phase, ControlOwner owner, OperatorDecision? decision = null)
    {
        var previous = State;
        State = new ControlState
        {
            Phase = phase,
            Owner = owner,
            Version = checked(previous.Version + 1),
            Outcome = decision
        };
        transitions.Add(new ControlTransition(DateTimeOffset.UtcNow, previous.Phase, phase, owner, State.Version, decision));
    }

    private async Task WriteEventAsync(
        RunEventKind kind,
        RunEventOutcome outcome,
        CancellationToken cancellationToken,
        ActionKind? action = null,
        string? sessionContinuityCommitment = null)
        {
        try
        {
            await eventSink.WriteAsync(new RunEvent
            {
                RunId = Request.RunId,
                Timestamp = DateTimeOffset.UtcNow,
                Mode = RuntimeMode.Replay,
                Kind = kind,
                Outcome = outcome,
                StepId = Request.StepId,
                Action = action ?? Request.ProposedAction.Kind,
                Risk = Request.Risk,
                ControlOwner = State.Owner,
                ControlVersion = State.Version,
                OperatorDecision = State.Outcome,
                SessionContinuityCommitment = sessionContinuityCommitment
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Evidence telemetry is non-authoritative and cannot alter control ownership.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (State.Owner == ControlOwner.Human)
            {
                await humanSession.CancelAsync(CancellationToken.None).ConfigureAwait(false);
                Transition(ControlPhase.Cancelled, ControlOwner.None, OperatorDecision.Cancel);
            }
        }
        finally
        {
            operationGate.Release();
            operationGate.Dispose();
        }

        await humanSession.DisposeAsync().ConfigureAwait(false);
    }
}