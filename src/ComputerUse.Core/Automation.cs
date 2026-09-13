using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Surfaces;

namespace ComputerUse.Core;

public sealed record GoalRequest(Uri Url, string Goal, IReadOnlyDictionary<string, string> Inputs);

public enum DecisionKind { Action, Complete }

public sealed record ModelDecision
{
    public required DecisionKind Kind { get; init; }
    public SemanticAction? Action { get; init; }
    public RiskLevel? Risk { get; init; }
    public string? CompletionText { get; init; }
    public TargetDescriptor? CompletionTarget { get; init; }
    public string? Reason { get; init; }
}

public interface IStructuredDecisionModel
{
    Task<ModelDecision> DecideAsync(SurfaceObservation observation, GoalRequest request, CancellationToken cancellationToken);
}

public sealed record DiscoveryStep(int Index, SurfaceObservation Observation, ModelDecision Decision, SurfaceActionResult? ActionResult);

public sealed record LoopResult(
    IReadOnlyList<DiscoveryStep> Steps,
    bool Completed,
    int ModelCalls,
    SurfaceObservation FinalObservation,
    DiscoveryRunStatus Status = DiscoveryRunStatus.Completed,
    ModelDecision? PendingIntervention = null,
    string RunId = "");

public enum DiscoveryRunStatus
{
    Completed,
    InterventionRequired,
    RepeatedState
}

public sealed class AutomationLoop
{
    public async Task<LoopResult> RunAsync(
        GoalRequest request,
        IStructuredDecisionModel model,
        IComputerSurface surface,
        int maxSteps = 8,
        TimeSpan? timeout = null,
        Action<DiscoveryStep>? onStep = null,
        CancellationToken cancellationToken = default,
        AutomationPolicy? policy = null,
        IRunEventSink? eventSink = null,
        string? runId = null,
        int repeatedStateLimit = 3)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(surface);
        if (maxSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxSteps));
        if (repeatedStateLimit < 2) throw new ArgumentOutOfRangeException(nameof(repeatedStateLimit));

        runId ??= Guid.NewGuid().ToString("N");
        eventSink ??= NullRunEventSink.Instance;
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var steps = new List<DiscoveryStep>();
        string? previousStateKey = null;
        string? previousActionKey = null;
        var repeatedStateCount = 0;
        var effectivePolicy = policy ?? AutomationPolicy.SameOrigin(request.Url, maxSteps);
        var policyEnforcer = new PolicyEnforcer(effectivePolicy);
        await WriteEventAsync(eventSink, runId, RunEventKind.RunStarted, RunEventOutcome.Started, runCancellation.Token)
            .ConfigureAwait(false);
        try
        {
            await surface.SetNavigationPolicyAsync(effectivePolicy, runCancellation.Token).ConfigureAwait(false);
        if ((await surface.GetActiveLocationsAsync(runCancellation.Token).ConfigureAwait(false)).Count == 0)
        {
            var navigation = new SemanticAction { Kind = ActionKind.Navigate, Destination = request.Url };
            EnsureAllowed(effectivePolicy.EvaluateAction(navigation, priorActionCount: 0));
            var navigationResult = await surface.ExecuteAsync(navigation, runCancellation.Token).ConfigureAwait(false);
            EnsureSucceeded(navigationResult);
        }
        EnsureAllowed(await policyEnforcer.ValidateSurfaceAsync(surface, runCancellation.Token).ConfigureAwait(false));

        for (var index = 0; index < maxSteps; index++)
        {
            EnsureAllowed(await policyEnforcer.ValidateSurfaceAsync(surface, runCancellation.Token).ConfigureAwait(false));
            var observation = await surface.ObserveAsync(runCancellation.Token).ConfigureAwait(false);
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.ObservationCompleted,
                RunEventOutcome.Observed,
                runCancellation.Token,
                stepIndex: index).ConfigureAwait(false);
            var stateKey = $"{observation.Fingerprint}\u001f{previousActionKey ?? "initial"}";
            repeatedStateCount = string.Equals(previousStateKey, stateKey, StringComparison.Ordinal)
                ? checked(repeatedStateCount + 1)
                : 1;
            previousStateKey = stateKey;
            if (repeatedStateCount >= repeatedStateLimit)
            {
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.RunCompleted,
                    RunEventOutcome.RepeatedState,
                    CancellationToken.None,
                    stepIndex: index).ConfigureAwait(false);
                return new LoopResult(
                    steps,
                    false,
                    index,
                    observation,
                    DiscoveryRunStatus.RepeatedState,
                    RunId: runId);
            }
            var decision = await model.DecideAsync(observation, request, runCancellation.Token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The model returned no decision.");
            Validate(decision);
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.DecisionSelected,
                RunEventOutcome.Selected,
                runCancellation.Token,
                stepIndex: index,
                action: decision.Action?.Kind,
                risk: decision.Risk).ConfigureAwait(false);

            if (decision.Kind == DecisionKind.Complete)
            {
                if (!observation.VisibleText.Contains(decision.CompletionText!, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The model claimed completion but the required text was not observed.");
                var completionStep = new DiscoveryStep(index, observation, decision, null);
                steps.Add(completionStep);
                onStep?.Invoke(completionStep);
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.RunCompleted,
                    RunEventOutcome.Completed,
                    CancellationToken.None).ConfigureAwait(false);
                return new LoopResult(steps, true, index + 1, observation, RunId: runId);
            }

            var riskDecision = effectivePolicy.EvaluateRisk(decision.Action!, decision.Risk!.Value);
            var effectiveRisk = ActionRiskClassifier.EffectiveRisk(decision.Action!, decision.Risk.Value);
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.PolicyEvaluated,
                riskDecision.Allowed ? RunEventOutcome.Allowed : RunEventOutcome.Denied,
                runCancellation.Token,
                stepIndex: index,
                action: decision.Action!.Kind,
                risk: effectiveRisk).ConfigureAwait(false);
            if (!riskDecision.Allowed)
            {
                var interventionStep = new DiscoveryStep(index, observation, decision, null);
                steps.Add(interventionStep);
                onStep?.Invoke(interventionStep);
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.InterventionRequested,
                    RunEventOutcome.InterventionRequired,
                    CancellationToken.None,
                    stepIndex: index,
                    action: decision.Action.Kind,
                    risk: effectiveRisk).ConfigureAwait(false);
                await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.RunCompleted,
                    RunEventOutcome.InterventionRequired,
                    CancellationToken.None).ConfigureAwait(false);
                return new LoopResult(
                    steps,
                    false,
                    index + 1,
                    observation,
                    DiscoveryRunStatus.InterventionRequired,
                        decision,
                        runId);
            }
                    var actionDecision = policyEnforcer.BeforeAction(decision.Action!);
                    await WriteEventAsync(
                    eventSink,
                    runId,
                    RunEventKind.PolicyEvaluated,
                    actionDecision.Allowed ? RunEventOutcome.Allowed : RunEventOutcome.Denied,
                    runCancellation.Token,
                    stepIndex: index,
                    action: decision.Action.Kind,
                    risk: decision.Risk).ConfigureAwait(false);
                    EnsureAllowed(actionDecision);
            var actionResult = await surface.ExecuteAsync(decision.Action!, runCancellation.Token).ConfigureAwait(false);
            var surfaceDecision = await policyEnforcer.ValidateSurfaceAsync(surface, runCancellation.Token).ConfigureAwait(false);
            if (!surfaceDecision.Allowed)
            {
                actionResult = new SurfaceActionResult
                {
                    Succeeded = false,
                    ErrorCode = surfaceDecision.Code,
                    SafeMessage = surfaceDecision.SafeMessage
                };
            }
            var actionStep = new DiscoveryStep(index, observation, decision, actionResult);
            steps.Add(actionStep);
            onStep?.Invoke(actionStep);
            await WriteEventAsync(
                eventSink,
                runId,
                RunEventKind.ActionCompleted,
                actionResult.Succeeded ? RunEventOutcome.Succeeded : RunEventOutcome.Failed,
                runCancellation.Token,
                stepIndex: index,
                action: decision.Action.Kind,
                risk: decision.Risk).ConfigureAwait(false);
            if (!actionResult.Succeeded)
                throw new InvalidOperationException(actionResult.SafeMessage ?? "The proposed UI action failed.");
            previousActionKey = ActionProgressKey(decision.Action);
        }

            throw new InvalidOperationException($"Maximum steps exceeded: {maxSteps}.");
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
    }

    private static async Task WriteEventAsync(
        IRunEventSink eventSink,
        string runId,
        RunEventKind kind,
        RunEventOutcome outcome,
        CancellationToken cancellationToken,
        int? stepIndex = null,
        ActionKind? action = null,
        RiskLevel? risk = null)
    {
        try
        {
            await eventSink.WriteAsync(new RunEvent
            {
                RunId = runId,
                Timestamp = DateTimeOffset.UtcNow,
                Mode = RuntimeMode.Discovery,
                Kind = kind,
                Outcome = outcome,
                StepIndex = stepIndex,
                Action = action,
                Risk = risk
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Run-event logging is non-authoritative and must not alter automation behavior.
        }
    }

    private static void EnsureAllowed(PolicyDecision decision)
    {
        if (!decision.Allowed)
        {
            throw new InvalidOperationException($"Policy blocked execution: [{decision.Code}] {decision.SafeMessage}");
        }
    }

    private static void EnsureSucceeded(SurfaceActionResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(result.SafeMessage ?? "The surface could not open the requested target.");
        }
    }

    private static void Validate(ModelDecision decision)
    {
        if (decision.Kind == DecisionKind.Complete)
        {
            if (string.IsNullOrWhiteSpace(decision.CompletionText))
                throw new InvalidOperationException("A completion decision requires independently observable text.");
            if (decision.Risk != RiskLevel.Safe)
                throw new InvalidOperationException("A completion decision requires an explicit safe risk classification.");
            return;
        }

        if (decision.Action is null || decision.Action.Kind is ActionKind.Complete or ActionKind.RequestIntervention)
            throw new InvalidOperationException("An action decision requires an executable semantic action.");
        if (decision.Risk is null || !Enum.IsDefined(decision.Risk.Value))
            throw new InvalidOperationException("An action decision requires a recognized risk classification.");
    }

    private static string ActionProgressKey(SemanticAction action) => string.Join(
        "\u001f",
        action.Kind,
        action.ValueTemplate,
        action.Destination?.OriginalString,
        action.OutputName,
        action.Duration,
        action.Target?.RequireUniqueMatch,
        action.Target is null
            ? null
            : string.Join("\u001e", action.Target.Strategies.Select(strategy =>
            $"{strategy.Kind}:{strategy.Value}:{strategy.Scope}:{strategy.ControlType}")));
}

public sealed class OpenAiDecisionModel : IStructuredDecisionModel
{
    private readonly HttpClient client;
    private readonly string model;

    public OpenAiDecisionModel(HttpClient? client = null, string? model = null, string environmentVariable = "OPENAI_API_KEY")
    {
        var apiKey = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException($"Environment variable '{environmentVariable}' is absent.");
        this.client = client ?? new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
        this.client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        this.model = model ?? Environment.GetEnvironmentVariable("OPENAI_MODEL") ?? "gpt-4.1-mini";
    }

    public async Task<ModelDecision> DecideAsync(SurfaceObservation observation, GoalRequest request, CancellationToken cancellationToken)
    {
        var prompt = $"""
            Goal: {request.Goal}
            URL: {observation.Url}
            Inputs: {JsonSerializer.Serialize(request.Inputs)}
            Visible text:
            {observation.VisibleText}

            Return one JSON object only. Always include risk as safe, reversibleWrite, or irreversible. For an action use kind=action and an action with kind type, click, read, or wait. Targets use strategies with kind stableId, label, accessibleRoleAndName, text, or css. Reading, waiting, and navigation are safe. Draft typing and selection are reversibleWrite. Consequential purchase, transfer, deletion, publication, permission, or final-submit actions are irreversible. For completion use kind=complete, risk=safe, and completionText copied from visible evidence. Never claim completion without visible evidence.
            """;
        var payload = new
        {
            model,
            temperature = 0,
            response_format = new { type = "json_object" },
            messages = new object[]
            {
                new { role = "system", content = "Choose one bounded semantic browser action or independently verifiable completion." },
                new { role = "user", content = prompt }
            }
        };

        using var response = await client.PostAsJsonAsync("v1/chat/completions", payload, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), cancellationToken: cancellationToken).ConfigureAwait(false);
        var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
        return JsonSerializer.Deserialize<ModelDecision>(content!, DecisionJson.Options) ?? throw new JsonException("OpenAI returned an empty decision.");
    }
}

public sealed class GeminiDecisionModel : IStructuredDecisionModel
{
    private readonly HttpClient client;
    private readonly string model;
    private readonly List<object> priorDecisions = [];

    public string ModelName => model;

    public GeminiDecisionModel(
        HttpClient? client = null,
        string? model = null,
        string environmentVariable = "GEMINI_API_KEY",
        string? apiKey = null)
    {
        apiKey ??= Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"Environment variable '{environmentVariable}' is absent.");

        this.client = client ?? new HttpClient();
        this.client.BaseAddress ??= new Uri("https://generativelanguage.googleapis.com/");
        this.client.DefaultRequestHeaders.Remove("x-goog-api-key");
        this.client.DefaultRequestHeaders.TryAddWithoutValidation("x-goog-api-key", apiKey);
        this.model = model ?? Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-3.6-flash";
    }

    public async Task<ModelDecision> DecideAsync(
        SurfaceObservation observation,
        GoalRequest request,
        CancellationToken cancellationToken)
    {
        var prompt = $"""
            Goal: {request.Goal}
            Requested inputs: {JsonSerializer.Serialize(request.Inputs)}
            Current page: {observation.Url}
            Page title: {observation.Title}
            Visible text:
            {observation.VisibleText}
            Interactive elements:
            {JsonSerializer.Serialize(observation.InteractiveElements)}
            Prior decisions in this run:
            {JsonSerializer.Serialize(priorDecisions)}

            Choose exactly one next decision. Use stableId targets when available. For accessibleRoleAndName,
            format the strategy value as "role:accessible name". Prefer accessibleRoleAndName for named buttons
            and links, inferring their native role, and never use a broad CSS selector that could match multiple
            elements. Type and select actions must put the literal requested input in valueTemplate. Read the
            requested result before completing. Complete only when
            completionText is copied exactly from the currently visible text. Never repeat the same action on
            the same target. Do not type into a control when its current value already equals the requested input;
            operate the next control instead. Every prior action listed above succeeded, so returning any prior
            action kind and target again is forbidden. When the requested result is present in visible text,
            choose read and copy its reusable cssSelector from the observed element exactly, never use its input-dependent
            exact text. After reading the requested result, complete on the next turn rather than operating another
            control. Use completionText from a stable success indicator such as a result count rather than from
            the input-dependent extracted value. Classify every action as safe, reversibleWrite, or irreversible.
            Reading, waiting, and navigation are safe. Typing or selecting draft values is reversibleWrite. A click
            that submits a purchase, transfer, deletion, publication, permission, or other consequential commitment
            is irreversible. Never classify an irreversible action as safe or reversibleWrite.
            """;
        var payload = new
        {
            systemInstruction = new
            {
                parts = new[] { new { text = "Control a browser with one bounded semantic decision per turn. Never invent UI state." } }
            },
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = prompt } } }
            },
            tools = new[]
            {
                new
                {
                    functionDeclarations = new[]
                    {
                        new
                        {
                            name = "submit_decision",
                            description = "Submit one semantic browser action or a verifiable completion claim.",
                            parameters = DecisionFunctionSchema
                        }
                    }
                }
            },
            toolConfig = new
            {
                functionCallingConfig = new
                {
                    mode = "ANY",
                    allowedFunctionNames = new[] { "submit_decision" }
                }
            },
            generationConfig = new { temperature = 0 }
        };

        var endpoint = $"v1beta/models/{Uri.EscapeDataString(model)}:generateContent";
        using var response = await SendAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);

        using var document = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var parts = document.RootElement.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts");
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("functionCall", out var call)
                && call.TryGetProperty("args", out var arguments))
            {
                return Record(arguments.Deserialize<ModelDecision>(DecisionJson.Options));
            }

            if (part.TryGetProperty("text", out var text))
            {
                return Record(JsonSerializer.Deserialize<ModelDecision>(text.GetString()!, DecisionJson.Options));
            }
        }

        throw new JsonException("Gemini returned neither a function call nor a JSON decision.");
    }

    private async Task<HttpResponseMessage> SendAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 3;
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var response = await client.PostAsJsonAsync(endpoint, payload, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
                return response;

            var isTransient = response.StatusCode is HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError
                or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable
                or HttpStatusCode.GatewayTimeout;
            if (!isTransient || attempt == maximumAttempts)
            {
                var statusCode = response.StatusCode;
                var reasonPhrase = response.ReasonPhrase;
                response.Dispose();
                throw new HttpRequestException($"Gemini request failed with HTTP {(int)statusCode} ({reasonPhrase}).");
            }

            response.Dispose();
            await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Gemini retry handling reached an invalid state.");
    }

    private ModelDecision Record(ModelDecision? decision)
    {
        if (decision is null)
            throw new JsonException("Gemini returned an empty decision.");

        priorDecisions.Add(new
        {
            decision.Kind,
            Action = decision.Action?.Kind,
            decision.Risk,
            Targets = decision.Action?.Target?.Strategies.Select(strategy => new { strategy.Kind, strategy.Value })
        });
        return decision;
    }

    private static object TargetFunctionSchema { get; } = new
    {
        type = "OBJECT",
        properties = new
        {
            strategies = new
            {
                type = "ARRAY",
                items = new
                {
                    type = "OBJECT",
                    properties = new
                    {
                        kind = new { type = "STRING", @enum = new[] { "accessibleRoleAndName", "label", "stableId", "text", "css" } },
                        value = new { type = "STRING" }
                    },
                    required = new[] { "kind", "value" }
                }
            },
            requireUniqueMatch = new { type = "BOOLEAN" }
        },
        required = new[] { "strategies" }
    };

    private static object DecisionFunctionSchema { get; } = new
    {
        type = "OBJECT",
        properties = new
        {
            kind = new { type = "STRING", @enum = new[] { "action", "complete" } },
            action = new
            {
                type = "OBJECT",
                properties = new
                {
                    kind = new { type = "STRING", @enum = new[] { "click", "type", "select", "read" } },
                    target = TargetFunctionSchema,
                    valueTemplate = new { type = "STRING" },
                    outputName = new { type = "STRING" }
                },
                required = new[] { "kind", "target" }
            },
            completionText = new { type = "STRING" },
            risk = new { type = "STRING", @enum = new[] { "safe", "reversibleWrite", "irreversible" } },
            reason = new { type = "STRING" }
        },
        required = new[] { "kind", "risk" }
    };

}

public sealed class FakeDecisionModel(IEnumerable<ModelDecision> decisions) : IStructuredDecisionModel
{
    private readonly Queue<ModelDecision> decisions = new(decisions);
    public int Calls { get; private set; }

    public Task<ModelDecision> DecideAsync(SurfaceObservation observation, GoalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return Task.FromResult(decisions.Dequeue());
    }
}

internal static class DecisionJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
