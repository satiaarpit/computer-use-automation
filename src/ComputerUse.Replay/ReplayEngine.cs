using System.Text.Json;
using System.Globalization;
using System.Text.RegularExpressions;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Intervention;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Surfaces;
using ComputerUse.Core.Validation;

namespace ComputerUse.Replay;

public sealed class ReplayEngine
{
    private readonly AutomationPolicy? policy;

    public ReplayEngine(AutomationPolicy? policy = null)
    {
        this.policy = policy;
    }

    public async Task<ReplayRunResult> RunAsync(
        CapabilityArtifact artifact,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs,
        Func<CancellationToken, Task<IComputerSurface>> surfaceFactory,
        CancellationToken cancellationToken = default,
        IRunEventSink? eventSink = null,
        string? runId = null,
        Func<LocalOperatorSurface, CancellationToken, Task<OperatorDecision>>? interventionHandler = null)
    {
        runId ??= Guid.NewGuid().ToString("N");
        eventSink ??= NullRunEventSink.Instance;
        await WriteEventAsync(
            eventSink,
            runId,
            RunEventKind.RunStarted,
            RunEventOutcome.Started,
            cancellationToken).ConfigureAwait(false);

        ReplayRunResult result;
        try
        {
            result = await RunCoreAsync(
                artifact,
                suppliedInputs,
                surfaceFactory,
                eventSink,
                runId,
                cancellationToken,
                interventionHandler).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.RunCompleted,
                RunEventOutcome.Cancelled,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.RunCompleted,
                RunEventOutcome.Failed,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (result.Status == ReplayRunStatus.InterventionRequired)
        {
            var stepId = result.Failure?.StepId ?? result.Steps.LastOrDefault()?.StepId;
            var interventionStep = artifact.Steps?.FirstOrDefault(step => step.Id == stepId);
            var action = interventionStep?.Action;
            var interventionRisk = interventionStep?.Risk;
            if (interventionStep is not null)
            {
                var prepared = ArtifactLoader.Bind(artifact, suppliedInputs);
                if (prepared.IsValid)
                {
                    action = RuntimeTemplateBinder.Bind(interventionStep.Action, prepared.Inputs!);
                    interventionRisk = ActionRiskClassifier.EffectiveRisk(action, interventionStep.Risk);
                }
            }
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.InterventionRequested,
                RunEventOutcome.InterventionRequired,
                CancellationToken.None,
                stepId,
                action?.Kind,
                risk: interventionRisk).ConfigureAwait(false);
        }

        await WriteEventAsync(
            eventSink,
            runId,
            RunEventKind.RunCompleted,
            ToEventOutcome(result.Status),
            CancellationToken.None).ConfigureAwait(false);
        return result with { RunId = runId };
    }

    private async Task<ReplayRunResult> RunCoreAsync(
        CapabilityArtifact artifact,
        IReadOnlyDictionary<string, JsonElement> suppliedInputs,
        Func<CancellationToken, Task<IComputerSurface>> surfaceFactory,
        IRunEventSink eventSink,
        string runId,
        CancellationToken cancellationToken,
        Func<LocalOperatorSurface, CancellationToken, Task<OperatorDecision>>? interventionHandler)
    {
        ArgumentNullException.ThrowIfNull(surfaceFactory);

        var prepared = ArtifactLoader.Bind(artifact, suppliedInputs);
        if (!prepared.IsValid)
        {
            return ReplayRunResult.Invalid(prepared.Validation);
        }

        IReadOnlyList<(CapabilityStep Step, SemanticAction Action)> boundSteps;
        Uri boundEntryPoint;
        try
        {
            boundEntryPoint = RuntimeTemplateBinder.Bind(prepared.Artifact!.Target.EntryPointTemplate, prepared.Inputs!);
            boundSteps = prepared.Artifact!.Steps
                .Select(step => (step, RuntimeTemplateBinder.Bind(step.Action, prepared.Inputs!)))
                .ToArray();
        }
        catch (Exception exception) when (exception is InvalidOperationException or UriFormatException)
        {
            return ReplayRunResult.Invalid(new ValidationResult(
            [
                new ValidationIssue("artifact.templates", "input-binding-failed", exception.Message)
            ]));
        }

        PolicyEnforcer policyEnforcer;
        try
        {
            policyEnforcer = new PolicyEnforcer(policy ?? AutomationPolicy.SameOrigin(boundEntryPoint));
        }
        catch (ArgumentException exception)
        {
            return ReplayRunResult.Invalid(new ValidationResult(
            [
                new ValidationIssue("policy", "policy-invalid", exception.Message)
            ]));
        }

        await using var surface = await surfaceFactory(cancellationToken).ConfigureAwait(false);
        await surface.SetNavigationPolicyAsync(
            policy ?? AutomationPolicy.SameOrigin(boundEntryPoint),
            cancellationToken).ConfigureAwait(false);
        var executions = new List<ReplayStepExecution>();
        OperatorDecision? completedOperatorDecision = null;
        IReadOnlyList<EvidenceReference> completedInterventionEvidence = [];
        ReplayInterventionSummary? completedIntervention = null;
        if ((await surface.GetActiveLocationsAsync(cancellationToken).ConfigureAwait(false)).Count == 0)
        {
            var navigation = new SemanticAction
            {
                Kind = ActionKind.Navigate,
                Destination = boundEntryPoint
            };
            var navigationDecision = (policy ?? AutomationPolicy.SameOrigin(boundEntryPoint))
                .EvaluateAction(navigation, priorActionCount: 0);
            if (!navigationDecision.Allowed)
            {
                return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                    executions,
                    "surface",
                    navigationDecision.Code!,
                    navigationDecision.SafeMessage!), surface, cancellationToken).ConfigureAwait(false);
            }

            var navigationResult = await surface.ExecuteAsync(navigation, cancellationToken).ConfigureAwait(false);
            if (!navigationResult.Succeeded)
            {
                return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                    executions,
                    "surface",
                    navigationResult.ErrorCode ?? "surface-action-failed",
                    navigationResult.SafeMessage ?? "The surface could not open the capability entry point."), surface, cancellationToken).ConfigureAwait(false);
            }
        }
        var initialLocationDecision = await policyEnforcer.ValidateSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
        if (!initialLocationDecision.Allowed)
        {
            return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                executions,
                "surface",
                initialLocationDecision.Code!,
                initialLocationDecision.SafeMessage!), surface, cancellationToken).ConfigureAwait(false);
        }

        foreach (var (step, action) in boundSteps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            for (var conditionIndex = 0; conditionIndex < step.Preconditions.Count; conditionIndex++)
            {
                var condition = RuntimeTemplateBinder.Bind(step.Preconditions[conditionIndex], prepared.Inputs!);
                var evaluation = await EvaluateConditionAsync(surface, condition, cancellationToken).ConfigureAwait(false);
                var failure = ConditionFailure(
                    executions,
                    step.Id,
                    $"precondition[{conditionIndex}]",
                    "precondition-not-satisfied",
                    evaluation);
                if (failure is not null)
                {
                    return await WithFailureEvidenceAsync(failure, surface, cancellationToken).ConfigureAwait(false);
                }
            }

            var effectiveRisk = ActionRiskClassifier.EffectiveRisk(action, step.Risk);
            var riskDecision = (policy ?? AutomationPolicy.SameOrigin(prepared.Artifact!.Target.EntryPointTemplate))
                .EvaluateRisk(action, step.Risk);
            if (!riskDecision.Allowed)
            {
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.PolicyEvaluated,
                    RunEventOutcome.Denied,
                    cancellationToken,
                    step.Id,
                    action.Kind,
                    risk: effectiveRisk).ConfigureAwait(false);
                if (interventionHandler is null)
                {
                    return ReplayRunResult.Intervention(
                        executions,
                        step.Id,
                        riskDecision.Code!,
                        riskDecision.SafeMessage!);
                }

                var evidence = await CaptureEvidenceAsync(surface, cancellationToken).ConfigureAwait(false);
                var request = new InterventionRequest
                {
                    RunId = runId,
                    CapabilityId = prepared.Artifact.CapabilityId,
                    Goal = $"Complete capability '{prepared.Artifact.Name}'.",
                    Target = boundEntryPoint,
                    StepId = step.Id,
                    ProposedAction = action,
                    Reason = riskDecision.SafeMessage!,
                    Risk = effectiveRisk,
                    RedactedStateSummary = $"Current surface state fingerprint: {(await surface.ObserveAsync(cancellationToken).ConfigureAwait(false)).Fingerprint}",
                    ResumeCondition = step.Postcondition is null
                        ? null
                        : RuntimeTemplateBinder.Bind(step.Postcondition, prepared.Inputs!),
                    Evidence = evidence
                };
                await using var operatorSurface = await LocalOperatorSurface.BeginAsync(
                    request,
                    surface,
                    eventSink,
                    cancellationToken).ConfigureAwait(false);
                var operatorDecision = await interventionHandler(operatorSurface, cancellationToken).ConfigureAwait(false);
                var resolution = await operatorSurface.ChooseAsync(
                    operatorDecision,
                    operatorSurface.State.Version,
                    cancellationToken).ConfigureAwait(false);
                var interventionSummary = new ReplayInterventionSummary(
                    operatorSurface.ApprovalWasRecorded,
                    operatorSurface.HumanActions.Any(humanAction => humanAction.Succeeded),
                    operatorSurface.VerifiedSessionContinuityCommitment,
                    resolution.State.Owner,
                    resolution.State.Version,
                    resolution.State is { Phase: ControlPhase.Automation, Owner: ControlOwner.Automation });
                if (resolution.State is { Phase: ControlPhase.Automation, Owner: ControlOwner.Automation })
                {
                    executions.Add(new ReplayStepExecution(step.Id, true, 1, null, "human", null, null));
                    completedOperatorDecision = OperatorDecision.Resume;
                    completedInterventionEvidence = evidence;
                    completedIntervention = interventionSummary;
                    continue;
                }

                if (resolution.State is { Phase: ControlPhase.Completed, Outcome: OperatorDecision.Complete })
                {
                    executions.Add(new ReplayStepExecution(step.Id, true, 1, null, "human", null, null));
                    return new ReplayRunResult
                    {
                        Status = ReplayRunStatus.Success,
                        Steps = executions,
                        Evidence = evidence,
                        OperatorDecision = OperatorDecision.Complete,
                        InterventionSummary = interventionSummary
                    };
                }

                if (resolution.State is { Phase: ControlPhase.Cancelled })
                {
                    return new ReplayRunResult
                    {
                        Status = ReplayRunStatus.Cancelled,
                        Steps = executions,
                        Evidence = evidence,
                        OperatorDecision = resolution.State.Outcome,
                        InterventionSummary = interventionSummary
                    };
                }

                return ReplayRunResult.Intervention(
                    executions,
                    step.Id,
                    riskDecision.Code!,
                    resolution.SafeMessage ?? riskDecision.SafeMessage!) with
                {
                    Evidence = evidence,
                    OperatorDecision = resolution.State.Outcome,
                    InterventionSummary = interventionSummary
                };
            }

            var execution = await ExecuteWithRetryAsync(
                surface,
                step,
                action,
                policyEnforcer,
                eventSink,
                runId,
                effectiveRisk,
                cancellationToken).ConfigureAwait(false);
            executions.Add(execution);
            if (!execution.Succeeded)
            {
                if (IsPolicyFailure(execution.ErrorCode))
                {
                    return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                        executions,
                        step.Id,
                        execution.ErrorCode!,
                        execution.SafeMessage ?? "Policy blocked the surface action."), surface, cancellationToken).ConfigureAwait(false);
                }

                var detectedOutcome = await DetectKnownOutcomeAsync(
                    surface,
                    prepared.Artifact.Outcomes,
                    prepared.Inputs!,
                    executions,
                    cancellationToken).ConfigureAwait(false);
                if (detectedOutcome is not null)
                {
                    return await WithFailureEvidenceAsync(detectedOutcome, surface, cancellationToken).ConfigureAwait(false);
                }

                return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                    executions,
                    step.Id,
                    execution.ErrorCode ?? "surface-action-failed",
                    execution.SafeMessage ?? "The surface action failed."), surface, cancellationToken).ConfigureAwait(false);
            }


            if (step.Postcondition is not null)
            {
                var condition = RuntimeTemplateBinder.Bind(step.Postcondition, prepared.Inputs!);
                var evaluation = await EvaluateConditionAsync(surface, condition, cancellationToken).ConfigureAwait(false);
                var failure = ConditionFailure(
                    executions,
                    step.Id,
                    "postcondition",
                    "postcondition-not-satisfied",
                    evaluation);
                if (failure is not null)
                {
                    return await WithFailureEvidenceAsync(failure, surface, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var finalOutcome = await DetectKnownOutcomeAsync(
            surface,
            prepared.Artifact.Outcomes,
            prepared.Inputs!,
            executions,
            cancellationToken).ConfigureAwait(false);
        if (finalOutcome is not null)
        {
            return await WithFailureEvidenceAsync(finalOutcome, surface, cancellationToken).ConfigureAwait(false);
        }

        var checkpoint = RuntimeTemplateBinder.Bind(prepared.Artifact.Checkpoint, prepared.Inputs!);
        var checkpointEvaluation = await EvaluateConditionAsync(surface, checkpoint, cancellationToken).ConfigureAwait(false);
        if (!checkpointEvaluation.Succeeded)
        {
            return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                executions,
                "checkpoint",
                checkpointEvaluation.ErrorCode ?? "checkpoint-evaluation-failed",
                checkpointEvaluation.SafeMessage ?? "The completion checkpoint could not be evaluated."), surface, cancellationToken).ConfigureAwait(false);
        }

        if (!checkpointEvaluation.Matched)
        {
            return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                executions,
                "checkpoint",
                "checkpoint-not-satisfied",
                "The replay steps completed, but the declared completion checkpoint was not satisfied."), surface, cancellationToken).ConfigureAwait(false);
        }

        var outputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var output in prepared.Artifact.Outputs)
        {
            var extraction = RuntimeTemplateBinder.Bind(output.Extraction, prepared.Inputs!);
            var inspection = await surface.InspectAsync(ToInspection(extraction), cancellationToken).ConfigureAwait(false);
            if (!inspection.Succeeded)
            {
                if (!output.Required && string.Equals(inspection.ErrorCode, "target-not-found", StringComparison.Ordinal))
                {
                    continue;
                }

                return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                    executions,
                    $"output:{output.Name}",
                    inspection.ErrorCode ?? "output-extraction-failed",
                    inspection.SafeMessage ?? $"Output '{output.Name}' could not be extracted."), surface, cancellationToken).ConfigureAwait(false);
            }

            if (inspection.Value is null && !output.Required)
            {
                continue;
            }

            if (!TryConvertOutput(inspection.Value, output.Type, out var value))
            {
                return await WithFailureEvidenceAsync(ReplayRunResult.Failed(
                    executions,
                    $"output:{output.Name}",
                    "output-type-mismatch",
                    $"Output '{output.Name}' could not be converted to {output.Type}."), surface, cancellationToken).ConfigureAwait(false);
            }

            outputs.Add(output.Name, value);
        }

        return ReplayRunResult.Completed(executions, outputs) with
        {
            Evidence = completedInterventionEvidence,
            OperatorDecision = completedOperatorDecision,
            InterventionSummary = completedIntervention
        };
    }

    private static async Task<ReplayRunResult> WithFailureEvidenceAsync(
        ReplayRunResult result,
        IComputerSurface surface,
        CancellationToken cancellationToken)
    {
        if (result.Status != ReplayRunStatus.Failed)
        {
            return result;
        }

        var evidence = await CaptureEvidenceAsync(surface, cancellationToken).ConfigureAwait(false);
        return result with { Evidence = evidence };
    }

    private static async Task<IReadOnlyList<EvidenceReference>> CaptureEvidenceAsync(
        IComputerSurface surface,
        CancellationToken cancellationToken)
    {
        var evidence = new List<EvidenceReference>();
        foreach (var kind in new[] { EvidenceKind.Screenshot, EvidenceKind.Snapshot })
        {
            try
            {
                evidence.Add(await surface.CaptureEvidenceAsync(kind, cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // Failure evidence is best effort and must not replace the primary failure result.
            }
        }

        return evidence;
    }

    private static RunEventOutcome ToEventOutcome(ReplayRunStatus status) => status switch
    {
        ReplayRunStatus.Success => RunEventOutcome.Completed,
        ReplayRunStatus.BusinessOutcome => RunEventOutcome.BusinessOutcome,
        ReplayRunStatus.RecoverableCondition => RunEventOutcome.RecoverableCondition,
        ReplayRunStatus.InterventionRequired => RunEventOutcome.InterventionRequired,
        ReplayRunStatus.Cancelled => RunEventOutcome.Cancelled,
        ReplayRunStatus.InvalidRequest => RunEventOutcome.InvalidRequest,
        _ => RunEventOutcome.Failed
    };

    private static async Task WriteEventAsync(
        IRunEventSink eventSink,
        string runId,
        RunEventKind kind,
        RunEventOutcome outcome,
        CancellationToken cancellationToken,
        string? stepId = null,
        ActionKind? action = null,
        int? attempts = null,
        RiskLevel? risk = null)
    {
        try
        {
            await eventSink.WriteAsync(new RunEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                Mode = RuntimeMode.Replay,
                Kind = kind,
                Outcome = outcome,
                StepId = stepId,
                Action = action,
                Risk = risk,
                Attempts = attempts
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Run-event logging is non-authoritative and must not alter replay behavior.
        }
    }

    private static bool IsPolicyFailure(string? code) =>
        code is "navigation-not-allowed" or "action-not-allowed" or "action-limit-exceeded";

    private static async Task<ConditionEvaluation> EvaluateConditionAsync(
        IComputerSurface surface,
        Condition condition,
        CancellationToken cancellationToken)
    {
        var inspection = await surface.InspectAsync(ToInspection(condition), cancellationToken).ConfigureAwait(false);
        if (!inspection.Succeeded)
        {
            if (string.Equals(inspection.ErrorCode, "target-not-found", StringComparison.Ordinal))
            {
                var absentMatches = condition.Kind == ConditionKind.Hidden;
                return new ConditionEvaluation(true, condition.Negate ? !absentMatches : absentMatches, null, null);
            }

            return new ConditionEvaluation(false, false, inspection.ErrorCode, inspection.SafeMessage);
        }

        bool matched;
        try
        {
            matched = condition.Kind switch
            {
                ConditionKind.Visible => ParseBoolean(inspection.Value),
                ConditionKind.Hidden => !ParseBoolean(inspection.Value),
                ConditionKind.TextEquals or ConditionKind.ValueEquals or ConditionKind.StateEquals =>
                    string.Equals(inspection.Value, condition.ExpectedTemplate, StringComparison.Ordinal),
                ConditionKind.TextContains =>
                    inspection.Value?.Contains(condition.ExpectedTemplate!, StringComparison.Ordinal) == true,
                ConditionKind.UrlMatches => Regex.IsMatch(
                    inspection.Value ?? string.Empty,
                    condition.ExpectedTemplate!,
                    RegexOptions.CultureInvariant,
                    TimeSpan.FromSeconds(1)),
                _ => false
            };
        }
        catch (ArgumentException)
        {
            return new ConditionEvaluation(
                false,
                false,
                "invalid-condition-pattern",
                "The bound URL checkpoint pattern is invalid.");
        }

        return new ConditionEvaluation(true, condition.Negate ? !matched : matched, null, null);
    }

    private static async Task<ReplayRunResult?> DetectKnownOutcomeAsync(
        IComputerSurface surface,
        IReadOnlyList<KnownOutcome> outcomes,
        BoundInputs inputs,
        IReadOnlyList<ReplayStepExecution> executions,
        CancellationToken cancellationToken)
    {
        foreach (var outcome in outcomes)
        {
            var condition = RuntimeTemplateBinder.Bind(outcome.Detection, inputs);
            var evaluation = await EvaluateConditionAsync(surface, condition, cancellationToken).ConfigureAwait(false);
            if (!evaluation.Succeeded)
            {
                return ReplayRunResult.Failed(
                    executions,
                    "outcome-evaluation",
                    evaluation.ErrorCode ?? "outcome-evaluation-failed",
                    evaluation.SafeMessage ?? "A known outcome could not be evaluated.");
            }

            if (evaluation.Matched)
            {
                return ReplayRunResult.KnownOutcome(executions, outcome);
            }
        }

        return null;
    }

    private static ReplayRunResult? ConditionFailure(
        IReadOnlyList<ReplayStepExecution> executions,
        string stepId,
        string conditionName,
        string unsatisfiedCode,
        ConditionEvaluation evaluation)
    {
        if (!evaluation.Succeeded)
        {
            return ReplayRunResult.Failed(
                executions,
                stepId,
                evaluation.ErrorCode ?? "condition-evaluation-failed",
                evaluation.SafeMessage ?? $"The {conditionName} could not be evaluated.");
        }

        return evaluation.Matched
            ? null
            : ReplayRunResult.Failed(
                executions,
                stepId,
                unsatisfiedCode,
                $"The declared {conditionName} was not satisfied.");
    }

    private static SurfaceInspection ToInspection(Condition condition) => new()
    {
        Kind = condition.Kind switch
        {
            ConditionKind.Visible or ConditionKind.Hidden => SurfaceInspectionKind.Visible,
            ConditionKind.TextEquals or ConditionKind.TextContains => SurfaceInspectionKind.Text,
            ConditionKind.ValueEquals => SurfaceInspectionKind.Value,
            ConditionKind.UrlMatches => SurfaceInspectionKind.Url,
            ConditionKind.StateEquals => SurfaceInspectionKind.State,
            _ => throw new InvalidOperationException($"Condition '{condition.Kind}' is not supported.")
        },
        Target = condition.Target
    };

    private static SurfaceInspection ToInspection(ValueExtraction extraction) => new()
    {
        Kind = extraction.Kind switch
        {
            ExtractionKind.Text => SurfaceInspectionKind.Text,
            ExtractionKind.Value => SurfaceInspectionKind.Value,
            ExtractionKind.Attribute => SurfaceInspectionKind.Attribute,
            ExtractionKind.Url => SurfaceInspectionKind.Url,
            ExtractionKind.State => SurfaceInspectionKind.State,
            _ => throw new InvalidOperationException($"Extraction '{extraction.Kind}' is not supported.")
        },
        Target = extraction.Target,
        AttributeName = extraction.AttributeName
    };

    private static bool TryConvertOutput(string? raw, ValueTypeKind type, out JsonElement value)
    {
        object? converted = type switch
        {
            ValueTypeKind.String or ValueTypeKind.Enum => raw,
            ValueTypeKind.Integer when long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) => integer,
            ValueTypeKind.Decimal when decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
            ValueTypeKind.Boolean when bool.TryParse(raw, out var boolean) => boolean,
            ValueTypeKind.Date when DateOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date,
            _ => null
        };

        if (converted is null)
        {
            value = default;
            return false;
        }

        value = JsonSerializer.SerializeToElement(converted);
        return true;
    }

    private static bool ParseBoolean(string? value) =>
        bool.TryParse(value, out var parsed) && parsed;

    private static async Task<ReplayStepExecution> ExecuteWithRetryAsync(
        IComputerSurface surface,
        CapabilityStep step,
        SemanticAction action,
        PolicyEnforcer policyEnforcer,
        IRunEventSink eventSink,
        string runId,
        RiskLevel effectiveRisk,
        CancellationToken cancellationToken)
    {
        SurfaceActionResult? result = null;

        for (var attempt = 1; attempt <= step.Retry.MaxAttempts; attempt++)
        {
            var actionDecision = policyEnforcer.BeforeAction(action);
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.PolicyEvaluated,
                actionDecision.Allowed ? RunEventOutcome.Allowed : RunEventOutcome.Denied,
                cancellationToken,
                step.Id,
                action.Kind,
                attempt,
                effectiveRisk).ConfigureAwait(false);
            if (!actionDecision.Allowed)
            {
                return new ReplayStepExecution(
                    step.Id,
                    false,
                    attempt,
                    null,
                    null,
                    actionDecision.Code,
                    actionDecision.SafeMessage);
            }

            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(step.Timeout);

            try
            {
                result = await surface.ExecuteAsync(action, attemptCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                result = new SurfaceActionResult
                {
                    Succeeded = false,
                    ErrorCode = "step-timeout",
                    SafeMessage = $"Step '{step.Id}' exceeded its timeout."
                };
            }

            var locationDecision = await policyEnforcer.ValidateSurfaceAsync(surface, cancellationToken).ConfigureAwait(false);
            if (!locationDecision.Allowed)
            {
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.ActionCompleted,
                    RunEventOutcome.Failed,
                    cancellationToken,
                    step.Id,
                    action.Kind,
                    attempt,
                    effectiveRisk).ConfigureAwait(false);
                return new ReplayStepExecution(
                    step.Id,
                    false,
                    attempt,
                    result.Value,
                    result.MatchedStrategy,
                    locationDecision.Code,
                    locationDecision.SafeMessage);
            }

            if (result.Succeeded)
            {
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.ActionCompleted,
                    RunEventOutcome.Succeeded,
                    cancellationToken,
                    step.Id,
                    action.Kind,
                    attempt,
                    effectiveRisk).ConfigureAwait(false);
                return new ReplayStepExecution(step.Id, true, attempt, result.Value, result.MatchedStrategy, null, null);
            }

            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.ActionCompleted,
                RunEventOutcome.Failed,
                cancellationToken,
                step.Id,
                action.Kind,
                attempt,
                effectiveRisk).ConfigureAwait(false);

            var canRetry = attempt < step.Retry.MaxAttempts &&
                result.ErrorCode is not null &&
                step.Retry.RecoverableConditionCodes.Contains(result.ErrorCode, StringComparer.Ordinal);
            if (!canRetry)
            {
                return new ReplayStepExecution(
                    step.Id,
                    false,
                    attempt,
                    result.Value,
                    result.MatchedStrategy,
                    result.ErrorCode,
                    result.SafeMessage);
            }

            if (step.Retry.Delay > TimeSpan.Zero)
            {
                await Task.Delay(step.Retry.Delay, cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidOperationException("The validated retry policy did not execute an attempt.");
    }

    private sealed record ConditionEvaluation(
        bool Succeeded,
        bool Matched,
        string? ErrorCode,
        string? SafeMessage);
}

public sealed record ReplayRunResult
{
    public string RunId { get; init; } = string.Empty;

    public required ReplayRunStatus Status { get; init; }

    public IReadOnlyList<ReplayStepExecution> Steps { get; init; } = [];

    public ValidationResult Validation { get; init; } = ValidationResult.Success;

    public ReplayFailure? Failure { get; init; }

    public IReadOnlyDictionary<string, JsonElement> Outputs { get; init; }
        = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    public string? OutcomeCode { get; init; }

    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];

    public OperatorDecision? OperatorDecision { get; init; }

    public ReplayInterventionSummary? InterventionSummary { get; init; }

    public static ReplayRunResult Completed(
        IReadOnlyList<ReplayStepExecution> steps,
        IReadOnlyDictionary<string, JsonElement> outputs) => new()
    {
        Status = ReplayRunStatus.Success,
        Steps = steps,
        Outputs = outputs
    };

    public static ReplayRunResult KnownOutcome(IReadOnlyList<ReplayStepExecution> steps, KnownOutcome outcome) => new()
    {
        Status = outcome.Kind switch
        {
            OutcomeKind.BusinessOutcome => ReplayRunStatus.BusinessOutcome,
            OutcomeKind.RecoverableCondition => ReplayRunStatus.RecoverableCondition,
            OutcomeKind.InterventionRequired => ReplayRunStatus.InterventionRequired,
            OutcomeKind.HardFailure => ReplayRunStatus.Failed,
            _ => ReplayRunStatus.Failed
        },
        Steps = steps,
        OutcomeCode = outcome.Code,
        Failure = outcome.Kind == OutcomeKind.HardFailure
            ? new ReplayFailure("outcome-evaluation", outcome.Code, outcome.Description)
            : null
    };

    public static ReplayRunResult Invalid(ValidationResult validation) => new()
    {
        Status = ReplayRunStatus.InvalidRequest,
        Validation = validation
    };

    public static ReplayRunResult Intervention(
        IReadOnlyList<ReplayStepExecution> steps,
        string stepId,
        string code,
        string message) => new()
    {
        Status = ReplayRunStatus.InterventionRequired,
        Steps = steps,
        Failure = new ReplayFailure(stepId, code, message)
    };

    public static ReplayRunResult Failed(
        IReadOnlyList<ReplayStepExecution> steps,
        string stepId,
        string code,
        string message) => new()
    {
        Status = ReplayRunStatus.Failed,
        Steps = steps,
        Failure = new ReplayFailure(stepId, code, message)
    };
}

public sealed record ReplayStepExecution(
    string StepId,
    bool Succeeded,
    int Attempts,
    string? Value,
    string? MatchedStrategy,
    string? ErrorCode,
    string? SafeMessage);

public sealed record ReplayFailure(string StepId, string Code, string Message);

public sealed record ReplayInterventionSummary(
    bool ApprovalRecorded,
    bool HumanActionSucceeded,
    string? SessionContinuityCommitment,
    ControlOwner FinalOwner,
    long FinalControlVersion,
    bool ResumeValidationSucceeded);

public enum ReplayRunStatus
{
    InvalidRequest,
    Success,
    BusinessOutcome,
    RecoverableCondition,
    InterventionRequired,
    Cancelled,
    Failed
}
