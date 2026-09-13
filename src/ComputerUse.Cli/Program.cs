using System.Text.Json;
using System.Globalization;
using ComputerUse.Core;
using ComputerUse.Core.Configuration;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Logging;
using ComputerUse.Core.Serialization;
using ComputerUse.Core.Validation;
using ComputerUse.Playwright;
using ComputerUse.Replay;

if (args.Length == 6 && string.Equals(args[0], "discover", StringComparison.OrdinalIgnoreCase))
{
    return await Discover(args[1], args[2], args[3], args[4], args[5]);
}

if (args.Length >= 2 && args.Length % 2 == 0 && string.Equals(args[0], "replay", StringComparison.OrdinalIgnoreCase))
{
    return await Replay(args[1], args.Skip(2).ToArray(), interactiveApproval: false);
}

if (args.Length >= 2 && args.Length % 2 == 0 && string.Equals(args[0], "replay-interactive", StringComparison.OrdinalIgnoreCase))
{
    return await Replay(args[1], args.Skip(2).ToArray(), interactiveApproval: true);
}

if (args.Length == 2 && string.Equals(args[0], "validate", StringComparison.OrdinalIgnoreCase))
{
    return await ValidateCapability(args[1]);
}

if (args.Length == 3 && string.Equals(args[0], "check-config", StringComparison.OrdinalIgnoreCase))
{
    if (!Enum.TryParse<RuntimeMode>(args[1], ignoreCase: true, out var mode))
    {
        Console.Error.WriteLine("Mode must be 'discovery' or 'replay'.");
        return 2;
    }

    return await CheckConfiguration(mode, args[2]);
}

Console.Error.WriteLine("Usage:");
Console.Error.WriteLine("  computer-use discover <url> <goal> <input-name> <input-value> <artifact-path>");
Console.Error.WriteLine("  computer-use replay <artifact-path> [<input-name> <input-value> ...]");
Console.Error.WriteLine("  computer-use replay-interactive <artifact-path> [<input-name> <input-value> ...]");
Console.Error.WriteLine("  computer-use validate <artifact-path>");
Console.Error.WriteLine("  computer-use check-config <discovery|replay> <settings-path>");
return 2;

static async Task<int> Discover(string url, string goal, string inputName, string inputValue, string artifactPath)
{
    try
    {
        var request = new GoalRequest(new Uri(url, UriKind.Absolute), goal, new Dictionary<string, string> { [inputName] = inputValue });
        var runId = Guid.NewGuid().ToString("N");
        await using var eventSink = BestEffortJsonLinesRunEventSink.Create(Path.Combine("evidence", "runtime"), runId);
        await using var surface = await PlaywrightComputerSurface.CreateAsync();
        var model = new GeminiDecisionModel();
        var result = await new AutomationLoop().RunAsync(
            request,
            model,
            surface,
            onStep: step => Console.WriteLine(
                $"Discovery step {step.Index + 1}: {step.Decision.Kind}" +
                (step.Decision.Action is null ? string.Empty : $"/{step.Decision.Action.Kind}") +
                (step.ActionResult is null ? string.Empty : step.ActionResult.Succeeded ? " succeeded" : " failed")),
            eventSink: eventSink,
            runId: runId);
        var artifact = TraceCompiler.Compile(request, result, model: model.ModelName);
        var directory = Path.GetDirectoryName(Path.GetFullPath(artifactPath));
        if (directory is not null) Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(artifactPath, CapabilityJson.Serialize(artifact));
        await DiscoveryEvidenceWriter.WriteAsync(
            request,
            result,
            artifact,
            artifactPath,
            "gemini",
            model.ModelName,
            Path.Combine("evidence", "runtime"));
        Console.WriteLine($"Discovery run {result.RunId} completed in {result.ModelCalls} model calls. Capability written to {artifactPath}.");
        return 0;
    }
    catch (Exception exception) when (exception is InvalidOperationException or UriFormatException or HttpRequestException or JsonException)
    {
        Console.Error.WriteLine($"Discovery failed: {exception.Message}");
        return 1;
    }
}

static async Task<int> Replay(
    string artifactPath,
    IReadOnlyList<string> inputArguments,
    bool interactiveApproval)
{
    try
    {
        var artifact = CapabilityJson.Deserialize(await File.ReadAllTextAsync(artifactPath));
        var definitions = artifact.Inputs.ToDictionary(input => input.Name, StringComparer.Ordinal);
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        for (var index = 0; index < inputArguments.Count; index += 2)
        {
            var inputName = inputArguments[index];
            if (!definitions.TryGetValue(inputName, out var inputDefinition))
            {
                throw new InvalidOperationException($"The capability does not declare input '{inputName}'.");
            }

            if (!inputs.TryAdd(inputName, ParseInputValue(inputDefinition.Type, inputArguments[index + 1])))
            {
                throw new InvalidOperationException($"Input '{inputName}' was supplied more than once.");
            }
        }
        var runId = Guid.NewGuid().ToString("N");
        var evidenceRoot = Path.Combine("evidence", "runtime");
        await using var eventSinkLifetime = interactiveApproval
            ? (IAsyncDisposable)new JsonLinesRunEventSink(evidenceRoot, runId)
            : BestEffortJsonLinesRunEventSink.Create(evidenceRoot, runId);
        var eventSink = (IRunEventSink)eventSinkLifetime;
        var result = await new ReplayEngine().RunAsync(artifact, inputs, async cancellationToken =>
        {
            return await PlaywrightComputerSurface.CreateAsync(new PlaywrightSurfaceOptions
            {
                SensitiveValues = inputs.Values.Select(value => value.ToString()).ToArray()
            }, cancellationToken);
        }, eventSink: eventSink, runId: runId, interventionHandler: interactiveApproval
            ? async (operatorSurface, cancellationToken) =>
            {
                Console.WriteLine($"Human approval required for {operatorSurface.Request.ProposedAction.Kind} at step '{operatorSurface.Request.StepId}'.");
                Console.Write("Type APPROVE to execute the displayed action in this live session, or press Enter to decline: ");
                var response = Console.ReadLine();
                if (!string.Equals(response, "APPROVE", StringComparison.Ordinal))
                {
                    return ComputerUse.Core.Intervention.OperatorDecision.Decline;
                }

                await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version, cancellationToken);
                var actionResult = await operatorSurface.ExecuteHumanActionAsync(
                    operatorSurface.Request.ProposedAction,
                    operatorSurface.State.Version,
                    cancellationToken);
                return actionResult.Succeeded
                    ? ComputerUse.Core.Intervention.OperatorDecision.Resume
                    : ComputerUse.Core.Intervention.OperatorDecision.Unresolved;
            }
            : null);
        var resultPath = await ReplayResultEvidenceWriter.WriteBestEffortAsync(
            result,
            artifact,
            inputs,
            evidenceRoot);
        if (resultPath is null)
        {
            Console.Error.WriteLine("Replay evidence warning: the sanitized result file could not be persisted.");
        }
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(CapabilityJson.Options) { WriteIndented = true }));
        return result.Status is ReplayRunStatus.Success or ReplayRunStatus.BusinessOutcome ? 0 : 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or OverflowException or JsonException)
    {
        Console.Error.WriteLine($"Replay failed: {exception.Message}");
        return 1;
    }
}

static JsonElement ParseInputValue(ValueTypeKind type, string value) => type switch
{
    ValueTypeKind.Integer => JsonSerializer.SerializeToElement(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
    ValueTypeKind.Decimal => JsonSerializer.SerializeToElement(decimal.Parse(value, NumberStyles.Number, CultureInfo.InvariantCulture)),
    ValueTypeKind.Boolean => JsonSerializer.SerializeToElement(bool.Parse(value)),
    _ => JsonSerializer.SerializeToElement(value)
};

static async Task<int> ValidateCapability(string path)
{
    try
    {
        var json = await File.ReadAllTextAsync(path);
        var artifact = CapabilityJson.Deserialize(json);
        var result = CapabilityValidator.Validate(artifact);

        if (result.IsValid)
        {
            Console.WriteLine($"Valid capability: {artifact.CapabilityId} (schema v{artifact.SchemaVersion})");
            return 0;
        }

        WriteIssues(result);
        return 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"Unable to validate artifact: {exception.Message}");
        return 1;
    }
}

static async Task<int> CheckConfiguration(RuntimeMode mode, string path)
{
    try
    {
        var settings = SettingsJson.Deserialize(await File.ReadAllTextAsync(path));
        var result = StartupValidator.Validate(settings, mode);

        if (result.IsValid)
        {
            Console.WriteLine($"Configuration is valid for {mode.ToString().ToLowerInvariant()} mode.");
            return 0;
        }

        WriteIssues(result);
        return 1;
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"Unable to validate configuration: {exception.Message}");
        return 1;
    }
}

static void WriteIssues(ValidationResult result)
{
    foreach (var issue in result.Issues)
    {
        Console.Error.WriteLine($"{issue.Path}: [{issue.Code}] {issue.Message}");
    }
}
