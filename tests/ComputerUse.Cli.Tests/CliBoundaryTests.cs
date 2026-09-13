using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ComputerUse.Replay;

namespace ComputerUse.Cli.Tests;

public sealed class CliBoundaryTests : IAsyncLifetime
{
    private readonly string workingDirectory = Path.Combine(Path.GetTempPath(), $"computer-use-cli-{Guid.NewGuid():N}");
    private Process? fixture;
    private Uri baseAddress = null!;

    private static string CliAssembly => Path.Combine(
        Path.GetDirectoryName(typeof(ReplayEngine).Assembly.Location)!,
        "ComputerUse.Cli.dll");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(workingDirectory);
        baseAddress = new Uri($"http://127.0.0.1:{ReservePort()}");
        var fixtureAssembly = typeof(global::Program).Assembly.Location;
        fixture = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            ArgumentList = { fixtureAssembly, "--urls", baseAddress.ToString().TrimEnd('/') },
            WorkingDirectory = Path.GetDirectoryName(fixtureAssembly),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start the deterministic fixture.");

        using var client = new HttpClient { BaseAddress = baseAddress };
        var timeout = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < timeout)
        {
            if (fixture.HasExited)
            {
                throw new InvalidOperationException($"Fixture exited with code {fixture.ExitCode}: {await fixture.StandardError.ReadToEndAsync()}");
            }

            try
            {
                using var response = await client.GetAsync("/api/state/success");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
                // The fixture has not bound the reserved port yet.
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The deterministic fixture did not become ready within 15 seconds.");
    }

    [Fact]
    public async Task Replay_accepts_zero_inputs()
    {
        var artifact = WriteCapability("zero-inputs.json", inputs: "[]");

        var result = await RunCliAsync(["replay", artifact]);

        Assert.True(result.ExitCode == 0, result.StandardOutput + Environment.NewLine + result.StandardError);
        Assert.Contains("\"status\": \"success\"", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_accepts_multiple_inputs()
    {
        var artifact = WriteCapability(
            "multiple-inputs.json",
            inputs: """
              [
                { "name": "first", "type": "string", "required": true, "classification": "internal" },
                { "name": "second", "type": "boolean", "required": true, "classification": "internal" }
              ]
              """);

        var result = await RunCliAsync(["replay", artifact, "first", "synthetic", "second", "true"]);

        Assert.True(result.ExitCode == 0, result.StandardOutput + Environment.NewLine + result.StandardError);
    }

    [Fact]
    public async Task Replay_rejects_odd_input_argument_count()
    {
        var result = await RunCliAsync(["replay", "artifact.json", "orphan"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Usage:", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unknown", "value", "The capability does not declare input 'unknown'.")]
    [InlineData("known", "one", "known", "two", "Input 'known' was supplied more than once.")]
    public async Task Replay_rejects_unknown_or_duplicate_inputs(params string[] inputArguments)
    {
        var artifact = WriteCapability(
            "declared-input.json",
            inputs: "[{ \"name\": \"known\", \"type\": \"string\", \"required\": true, \"classification\": \"internal\" }]");

        var result = await RunCliAsync(["replay", artifact, .. inputArguments[..^1]]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(inputArguments[^1], result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("integer", "not-an-integer")]
    [InlineData("integer", "9223372036854775808")]
    [InlineData("decimal", "not-a-decimal")]
    [InlineData("boolean", "yes")]
    public async Task Replay_rejects_invalid_primitive_values(string type, string suppliedValue)
    {
        var artifact = WriteCapability(
            $"primitive-{type}.json",
            inputs: $"[{{ \"name\": \"value\", \"type\": \"{type}\", \"required\": true, \"classification\": \"internal\" }}]");

        var result = await RunCliAsync(["replay", artifact, "value", suppliedValue]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Replay failed:", result.StandardError, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("APPROVE", "success", "humanApprovalRecorded")]
    [InlineData("approve", "cancelled", "humanApprovalRecorded")]
    [InlineData(" APPROVE ", "cancelled", "humanApprovalRecorded")]
    [InlineData("", "cancelled", "humanApprovalRecorded")]
    public async Task Interactive_replay_requires_exact_approval(
        string response,
        string expectedStatus,
        string approvalEvent)
    {
        var artifact = WriteRiskyCapability("interactive.json");

        var result = await RunCliAsync(["replay-interactive", artifact], response + Environment.NewLine);

        Assert.True(
            result.ExitCode == (expectedStatus == "success" ? 0 : 1),
            result.StandardOutput + Environment.NewLine + result.StandardError);
        Assert.Contains($"\"status\": \"{expectedStatus}\"", result.StandardOutput, StringComparison.Ordinal);
        var eventLog = Assert.Single(Directory.GetFiles(Path.Combine(workingDirectory, "evidence", "runtime"), "*.events.jsonl"));
        var events = await File.ReadAllTextAsync(eventLog);
        Assert.Equal(response == "APPROVE", events.Contains($"\"kind\":\"{approvalEvent}\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Failure_textual_evidence_does_not_persist_sensitive_input()
    {
        const string canary = "sensitive-cli-canary-741852";
        var artifact = WriteFailureCapability("failure.json");

        var result = await RunCliAsync(["replay", artifact, "secret", canary]);

        Assert.Equal(1, result.ExitCode);
        var files = Directory.GetFiles(Path.Combine(workingDirectory, "evidence", "runtime"));
        Assert.Contains(files, path => path.EndsWith(".result.json", StringComparison.Ordinal));
        foreach (var file in files)
        {
            if (Path.GetExtension(file).Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Assert.DoesNotContain(canary, await File.ReadAllTextAsync(file), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Validate_reports_valid_and_invalid_artifacts()
    {
        var validArtifact = WriteCapability("valid.json", inputs: "[]");
        var invalidArtifact = WriteFile("invalid.json", "{not-json");

        var valid = await RunCliAsync(["validate", validArtifact]);
        var invalid = await RunCliAsync(["validate", invalidArtifact]);

        Assert.Equal(0, valid.ExitCode);
        Assert.Contains("Valid capability:", valid.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(1, invalid.ExitCode);
        Assert.Contains("Unable to validate artifact:", invalid.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_config_distinguishes_replay_from_discovery_requirements()
    {
                var settings = WriteFile("settings.json", """
                    {
                        "Gemini": {
                            "ApiKeyEnvironmentVariable": "GEMINI_API_KEY",
                            "Model": "gemini-3.6-flash"
                        },
                        "ComputerUse": {
                            "ArtifactRoot": "artifacts",
                            "EvidenceRoot": "evidence/runtime",
                            "DefaultMaxSteps": 20,
                            "DefaultTimeoutSeconds": 180
                        }
                    }
                    """);

        var replay = await RunCliAsync(["check-config", "replay", settings]);
        var discovery = await RunCliAsync(
            ["check-config", "discovery", settings],
            environment: new Dictionary<string, string?> { ["GEMINI_API_KEY"] = null });

        Assert.Equal(0, replay.ExitCode);
        Assert.Contains("valid for replay", replay.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, discovery.ExitCode);
        Assert.Contains("GEMINI_API_KEY", discovery.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_rejects_malformed_url_without_contacting_provider()
    {
        var result = await RunCliAsync([
            "discover", "not-an-absolute-url", "Synthetic goal", "query", "synthetic", "artifact.json"
        ]);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Discovery failed:", result.StandardError, StringComparison.Ordinal);
    }

    public Task DisposeAsync()
    {
        if (fixture is { HasExited: false })
        {
            fixture.Kill(entireProcessTree: true);
            fixture.WaitForExit(5_000);
        }

        fixture?.Dispose();
        if (Directory.Exists(workingDirectory))
        {
            Directory.Delete(workingDirectory, recursive: true);
        }

        return Task.CompletedTask;
    }

    private async Task<ProcessResult> RunCliAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = standardInput is not null
        };
        startInfo.ArgumentList.Add(CliAssembly);
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start the CLI process.");
        if (standardInput is not null)
        {
            await process.StandardInput.WriteAsync(standardInput);
            process.StandardInput.Close();
        }

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(process.ExitCode, await outputTask, await errorTask);
    }

    private string WriteCapability(string name, string inputs)
    {
        var json = $$"""
          {
            "schemaVersion": 1,
            "capabilityId": "cli-boundary.v1",
            "name": "CLI boundary fixture",
            "description": "Synthetic black-box CLI test capability.",
            "target": { "surface": "web", "entryPointTemplate": "{{baseAddress}}?state=success" },
            "risk": "safe",
            "inputs": {{inputs}},
            "outputs": [],
            "steps": [{ "id": "open-review", "action": { "kind": "click", "target": { "strategies": [{ "kind": "stableId", "value": "open-review" }], "requireUniqueMatch": true } }, "preconditions": [], "timeout": "00:00:15", "retry": { "maxAttempts": 1, "delay": "00:00:00", "recoverableConditionCodes": [] }, "risk": "safe" }],
            "outcomes": [],
            "checkpoint": { "kind": "visible", "target": { "strategies": [{ "kind": "stableId", "value": "review-title" }], "requireUniqueMatch": true }, "negate": false },
            "provenance": { "discoveryRunId": "cli-boundary", "createdAt": "2026-09-12T00:00:00+00:00", "generatorVersion": "test", "model": "test" }
          }
          """;
        return WriteFile(name, json);
    }

    private string WriteRiskyCapability(string name)
    {
        var json = $$"""
          {
            "schemaVersion": 1,
            "capabilityId": "cli-interactive.v1",
            "name": "CLI interactive fixture",
            "description": "Synthetic exact-approval capability.",
            "target": { "surface": "web", "entryPointTemplate": "{{baseAddress}}?state=dialog" },
            "risk": "irreversible",
            "inputs": [],
            "outputs": [],
            "steps": [{ "id": "dismiss-dialog", "action": { "kind": "click", "target": { "strategies": [{ "kind": "stableId", "value": "dismiss-dialog" }], "requireUniqueMatch": true } }, "preconditions": [], "postcondition": { "kind": "hidden", "target": { "strategies": [{ "kind": "stableId", "value": "fixture-dialog" }], "requireUniqueMatch": true }, "negate": false }, "timeout": "00:00:15", "retry": { "maxAttempts": 1, "delay": "00:00:00", "recoverableConditionCodes": [] }, "risk": "irreversible" }],
            "outcomes": [],
            "checkpoint": { "kind": "hidden", "target": { "strategies": [{ "kind": "stableId", "value": "fixture-dialog" }], "requireUniqueMatch": true }, "negate": false },
            "provenance": { "discoveryRunId": "cli-interactive", "createdAt": "2026-09-12T00:00:00+00:00", "generatorVersion": "test", "model": "test" }
          }
          """;
        return WriteFile(name, json);
    }

    private string WriteFailureCapability(string name)
    {
        var json = $$"""
          {
            "schemaVersion": 1,
            "capabilityId": "cli-failure.v1",
            "name": "CLI failure fixture",
            "description": "Synthetic redaction capability.",
            "target": { "surface": "web", "entryPointTemplate": "{{baseAddress}}review?state=risky-submit" },
            "risk": "reversibleWrite",
            "inputs": [{ "name": "secret", "type": "string", "required": true, "classification": "secret" }],
            "outputs": [],
            "steps": [{ "id": "enter-note", "action": { "kind": "type", "target": { "strategies": [{ "kind": "stableId", "value": "note" }], "requireUniqueMatch": true }, "valueTemplate": "${secret}" }, "preconditions": [], "timeout": "00:00:15", "retry": { "maxAttempts": 1, "delay": "00:00:00", "recoverableConditionCodes": [] }, "risk": "reversibleWrite" }],
            "outcomes": [],
            "checkpoint": { "kind": "textEquals", "target": { "strategies": [{ "kind": "stableId", "value": "review-title" }], "requireUniqueMatch": true }, "expectedTemplate": "impossible", "negate": false },
            "provenance": { "discoveryRunId": "cli-failure", "createdAt": "2026-09-12T00:00:00+00:00", "generatorVersion": "test", "model": "test" }
          }
          """;
        return WriteFile(name, json);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(workingDirectory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static int ReservePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}