using System.Text.Json;
using ComputerUse.Core.Contracts;
using ComputerUse.Core.Intervention;
using ComputerUse.Core.Policy;
using ComputerUse.Core.Serialization;
using ComputerUse.Core.Surfaces;
using ComputerUse.Playwright;
using ComputerUse.Replay;

namespace ComputerUse.Playwright.Tests;

public sealed class PlaywrightComputerSurfaceTests : IAsyncLifetime, IClassFixture<PlaywrightFixture>
{
    private readonly FixtureServer server;
    private PlaywrightComputerSurface surface = null!;

    public PlaywrightComputerSurfaceTests(PlaywrightFixture fixture)
    {
        server = fixture.Server;
    }

    public async Task InitializeAsync()
    {
        surface = await PlaywrightComputerSurface.CreateAsync(new PlaywrightSurfaceOptions
        {
            Headless = true,
            MaximumObservationCharacters = 4_000,
            MaximumInteractiveElements = 30
        });
        await NavigateAsync("/?state=success&query=Aurora");
    }

    public async Task DisposeAsync()
    {
        await surface.DisposeAsync();
    }

    [Fact]
    public async Task Observation_is_bounded_and_has_stable_fingerprint()
    {
        var first = await surface.ObserveAsync(CancellationToken.None);
        var second = await surface.ObserveAsync(CancellationToken.None);

        Assert.Equal("Deterministic UI Fixture", first.Title);
        Assert.Contains("Aurora Field Guide", first.VisibleText, StringComparison.Ordinal);
        Assert.Contains(
            first.InteractiveElements,
            element => element.StableId == "query" && element.AccessibleName == "Record title or identifier");
        Assert.Contains(
            first.InteractiveElements,
            element => element.AccessibleName == "Aurora Field Guide" && element.CssSelector == "#results > article > div > h3");
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.False(first.IsTruncated);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(10, 0)]
    public async Task Invalid_observation_limits_fail_before_browser_start(int characterLimit, int elementLimit)
    {
        var options = new PlaywrightSurfaceOptions
        {
            MaximumObservationCharacters = characterLimit,
            MaximumInteractiveElements = elementLimit
        };

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => PlaywrightComputerSurface.CreateAsync(options));
    }

    [Fact]
    public async Task Observation_reports_truncation_at_configured_limit()
    {
        await surface.DisposeAsync();
        surface = await PlaywrightComputerSurface.CreateAsync(new PlaywrightSurfaceOptions
        {
            MaximumObservationCharacters = 40,
            MaximumInteractiveElements = 2
        });
        await NavigateAsync("/?state=success&query=Aurora");

        var observation = await surface.ObserveAsync(CancellationToken.None);

        Assert.Equal(40, observation.VisibleText.Length);
        Assert.True(observation.IsTruncated);
        Assert.True(observation.InteractiveElements.Count <= 2);
    }

    [Fact]
    public async Task Ordered_strategies_fall_back_to_associated_label()
    {
        var result = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Type,
            ValueTemplate = "Borealis",
            Target = Target(
                new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "missing" },
                new TargetStrategy { Kind = TargetStrategyKind.Label, Value = "Record title or identifier" })
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(nameof(TargetStrategyKind.Label), result.MatchedStrategy);
    }

    [Fact]
    public async Task Semantic_actions_type_click_and_read_through_accessibility_targets()
    {
        var typed = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Type,
            ValueTemplate = "Borealis",
            Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "query" })
        }, CancellationToken.None);
        var clicked = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Click,
            Target = Target(new TargetStrategy { Kind = TargetStrategyKind.AccessibleRoleAndName, Value = "button:Search" })
        }, CancellationToken.None);

        await WaitForTextAsync("Borealis Atlas");
        var read = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Read,
            Target = Target(new TargetStrategy { Kind = TargetStrategyKind.AccessibleRoleAndName, Value = "heading:Borealis Atlas" })
        }, CancellationToken.None);

        Assert.True(typed.Succeeded);
        Assert.True(clicked.Succeeded);
        Assert.True(read.Succeeded);
        Assert.Equal("Borealis Atlas", read.Value);
    }

    [Fact]
    public async Task Inspections_read_checkpoint_and_output_values_without_actions()
    {
        var heading = Target(new TargetStrategy { Kind = TargetStrategyKind.Css, Value = "#results > article > div > h3" });
        var query = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "query" });
        var missing = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "missing" });

        var text = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Text, Target = heading },
            CancellationToken.None);
        var value = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Value, Target = query },
            CancellationToken.None);
        var hidden = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Visible, Target = missing },
            CancellationToken.None);
        var url = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Url },
            CancellationToken.None);

        Assert.True(text.Succeeded);
        Assert.Equal("Aurora Field Guide", text.Value);
        Assert.Equal("Aurora", value.Value);
        Assert.Equal("false", hidden.Value);
        Assert.Contains("state=success", url.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Aurora", "Aurora Field Guide")]
    [InlineData("Borealis", "Borealis Atlas")]
    public async Task Committed_search_capability_replays_with_typed_output(
        string query,
        string expectedTitle)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "artifacts", "search-and-extract.capability.json");
        var artifact = CapabilityJson.Deserialize(await File.ReadAllTextAsync(path));
        artifact = artifact with
        {
            Target = artifact.Target with
            {
                EntryPointTemplate = new Uri(server.BaseAddress, "/?state=success")
            }
        };
        var inputs = new Dictionary<string, JsonElement>
        {
            ["query"] = JsonSerializer.SerializeToElement(query)
        };

        var result = await new ReplayEngine().RunAsync(artifact, inputs, async cancellationToken =>
        {
            return await PlaywrightComputerSurface.CreateAsync(cancellationToken: cancellationToken);
        });

        Assert.Equal(ReplayRunStatus.Success, result.Status);
        Assert.Equal(expectedTitle, result.Outputs["title"].GetString());
    }

    [Fact]
    public async Task Replay_blocks_redirect_escape()
    {
        var entryPoint = new Uri(server.BaseAddress, "/?state=redirect-escape");
        var artifact = new CapabilityArtifact
        {
            CapabilityId = "redirect-escape.v1",
            Name = "Escape policy fixture",
            Description = "Attempts to leave the configured origin.",
            Target = new CapabilityTarget { Surface = SurfaceKind.Web, EntryPointTemplate = entryPoint },
            Steps =
            [
                new CapabilityStep
                {
                    Id = "attempt-escape",
                    Action = new SemanticAction
                    {
                        Kind = ActionKind.Click,
                        Target = Target(new TargetStrategy
                        {
                            Kind = TargetStrategyKind.AccessibleRoleAndName,
                            Value = "button:Open disallowed redirect"
                        })
                    }
                }
            ],
            Checkpoint = new Condition
            {
                Kind = ConditionKind.Visible,
                Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "status" })
            },
            Provenance = new CapabilityProvenance
            {
                DiscoveryRunId = "policy-fixture",
                CreatedAt = new DateTimeOffset(2026, 9, 11, 0, 0, 0, TimeSpan.Zero),
                GeneratorVersion = "0.3.0"
            }
        };

        var result = await new ReplayEngine().RunAsync(
            artifact,
            new Dictionary<string, JsonElement>(),
            async cancellationToken =>
            {
                return await PlaywrightComputerSurface.CreateAsync(cancellationToken: cancellationToken);
            });

        Assert.Equal(ReplayRunStatus.Failed, result.Status);
        Assert.Equal("navigation-not-allowed", result.Failure!.Code);
    }

    [Fact]
    public async Task Navigation_policy_aborts_redirect_before_disallowed_document_loads()
    {
        var entryPoint = new Uri(server.BaseAddress, "/?state=redirect-escape");
        await surface.SetNavigationPolicyAsync(
            AutomationPolicy.SameOrigin(entryPoint),
            CancellationToken.None);
        var navigation = await surface.ExecuteAsync(
            new SemanticAction { Kind = ActionKind.Navigate, Destination = entryPoint },
            CancellationToken.None);

        var clicked = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Click,
            Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "redirect-escape" })
        }, CancellationToken.None);
        var currentUrl = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Url },
            CancellationToken.None);
        var observedLocations = await surface.GetActiveLocationsAsync(CancellationToken.None);

        Assert.True(navigation.Succeeded);
        Assert.False(clicked.Succeeded);
        Assert.Equal("navigation-not-allowed", clicked.ErrorCode);
        Assert.DoesNotContain("localhost", currentUrl.Value, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(observedLocations, location => location.Host == "localhost");
    }

    [Theory]
    [InlineData(TargetStrategyKind.Text, "Aurora Field Guide", null, "Aurora Field Guide")]
    [InlineData(TargetStrategyKind.Css, "[data-field='price']", null, "12.50")]
    [InlineData(TargetStrategyKind.XPath, "//*[@data-field='available']", null, "true")]
    [InlineData(TargetStrategyKind.StructuralRelation, "[data-field='publishedOn']", ".result-card", "2026-09-01")]
    public async Task Resolver_supports_text_and_structural_strategies(
        TargetStrategyKind kind,
        string value,
        string? scope,
        string expected)
    {
        var result = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Read,
            Target = Target(new TargetStrategy { Kind = kind, Value = value, Scope = scope })
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(expected, result.Value);
    }

    [Fact]
    public async Task Ambiguous_target_fails_instead_of_selecting_first_match()
    {
        var result = await surface.ExecuteAsync(new SemanticAction
        {
            Kind = ActionKind.Click,
            Target = Target(new TargetStrategy { Kind = TargetStrategyKind.Text, Value = "Search" })
        }, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("ambiguous-target", result.ErrorCode);
    }

    [Fact]
    public async Task Paused_surface_rejects_actions_until_resumed()
    {
        var request = new InterventionRequest
        {
            RunId = "pause_test_001",
            CapabilityId = "pause-test.v1",
            Goal = "Test control transfer.",
            Target = server.BaseAddress,
            StepId = "pause-step",
            ProposedAction = new SemanticAction { Kind = ActionKind.Wait, Duration = TimeSpan.Zero },
            Reason = "Test pause ownership.",
            Risk = RiskLevel.Irreversible,
            RedactedStateSummary = "Synthetic state."
        };
        await using var operatorSurface = await LocalOperatorSurface.BeginAsync(request, surface);
        var blocked = await NavigateAsync("/?state=no-results");
        await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version);
        await operatorSurface.ExecuteHumanActionAsync(request.ProposedAction, operatorSurface.State.Version);
        await operatorSurface.ChooseAsync(OperatorDecision.Resume, operatorSurface.State.Version);
        var resumed = await NavigateAsync("/?state=no-results");

        Assert.False(blocked.Succeeded);
        Assert.Equal("automation-paused", blocked.ErrorCode);
        Assert.True(resumed.Succeeded);
    }

    [Fact]
    public async Task Snapshot_redacts_canary_and_returns_confined_reference()
    {
        const string canary = "canary-evidence-secret-7284";
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"computer-use-evidence-{Guid.NewGuid():N}");
        await surface.DisposeAsync();
        surface = await PlaywrightComputerSurface.CreateAsync(new PlaywrightSurfaceOptions
        {
            EvidenceDirectory = evidenceRoot,
            SensitiveValues = [canary]
        });
        try
        {
            await NavigateAsync("/?state=success");
            var typed = await surface.ExecuteAsync(new SemanticAction
            {
                Kind = ActionKind.Type,
                ValueTemplate = canary,
                Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "query" })
            }, CancellationToken.None);

            var reference = await surface.CaptureEvidenceAsync(EvidenceKind.Snapshot, CancellationToken.None);
            var fullPath = Path.Combine(evidenceRoot, reference.RelativePath);
            var persisted = await File.ReadAllTextAsync(fullPath);

            Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
            Assert.True(typed.Succeeded);
            Assert.Equal(Path.GetFileName(reference.RelativePath), reference.RelativePath);
            Assert.Equal(EvidenceProtection.Redacted, reference.Protection);
            Assert.Equal(Path.GetFullPath(evidenceRoot), Path.GetDirectoryName(fullPath));
        }
        finally
        {
            if (Directory.Exists(evidenceRoot))
            {
                Directory.Delete(evidenceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Hard_failure_returns_redacted_screenshot_and_snapshot_references()
    {
        const string canary = "canary-failure-secret-6173";
        var evidenceRoot = Path.Combine(Path.GetTempPath(), $"computer-use-failure-{Guid.NewGuid():N}");
        var artifact = new CapabilityArtifact
        {
            CapabilityId = "hard-failure-evidence.v1",
            Name = "Hard failure evidence",
            Description = "Forces a checkpoint failure after entering a synthetic canary.",
            Target = new CapabilityTarget { Surface = SurfaceKind.Web, EntryPointTemplate = new Uri(server.BaseAddress, "/?state=success") },
            Risk = RiskLevel.ReversibleWrite,
            Inputs = [new InputDefinition { Name = "query", Type = ValueTypeKind.String, Classification = DataClassification.Secret }],
            Steps =
            [
                new CapabilityStep
                {
                    Id = "enter-secret",
                    Risk = RiskLevel.ReversibleWrite,
                    Action = new SemanticAction { Kind = ActionKind.Type, Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "query" }), ValueTemplate = "${query}" }
                }
            ],
            Checkpoint = new Condition { Kind = ConditionKind.Visible, Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "never-present" }) },
            Provenance = new CapabilityProvenance { DiscoveryRunId = "synthetic", CreatedAt = DateTimeOffset.UnixEpoch, GeneratorVersion = "test" }
        };
        try
        {
            var result = await new ReplayEngine().RunAsync(
                artifact,
                new Dictionary<string, JsonElement> { ["query"] = JsonSerializer.SerializeToElement(canary) },
                async cancellationToken => await PlaywrightComputerSurface.CreateAsync(new PlaywrightSurfaceOptions
                {
                    EvidenceDirectory = evidenceRoot,
                    SensitiveValues = [canary]
                }, cancellationToken));

            Assert.Equal(ReplayRunStatus.Failed, result.Status);
            Assert.Equal(2, result.Evidence.Count);
            Assert.Contains(result.Evidence, item => item.Kind == "screenshot");
            var snapshot = Assert.Single(result.Evidence, item => item.Kind == "snapshot");
            var persisted = await File.ReadAllTextAsync(Path.Combine(evidenceRoot, snapshot.RelativePath));
            Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
            Assert.Contains("[REDACTED]", persisted, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(evidenceRoot))
            {
                Directory.Delete(evidenceRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Consequential_action_handoff_keeps_the_same_browser_session()
    {
        await NavigateAsync("/review?state=risky-submit");
        var sessionTarget = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "session-id" });
        await WaitForTextValueAsync(sessionTarget, value => !string.IsNullOrWhiteSpace(value) && value != "loading");
        var submit = new SemanticAction
        {
            Kind = ActionKind.Click,
            Target = Target(new TargetStrategy { Kind = TargetStrategyKind.StableId, Value = "submit-final" })
        };
        var sessionBefore = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Text, Target = sessionTarget },
            CancellationToken.None);
        var request = new InterventionRequest
        {
            RunId = "browser_handoff_001",
            CapabilityId = "risk-gated-submit.v1",
            Goal = "Submit the reviewed synthetic change.",
            Target = new Uri(server.BaseAddress, "/review?state=risky-submit"),
            StepId = "submit-final-change",
            ProposedAction = submit,
            Reason = "Final submission requires human control.",
            Risk = RiskLevel.Irreversible,
            RedactedStateSummary = "Synthetic review form ready; values omitted."
        };

        await using var operatorSurface = await LocalOperatorSurface.BeginAsync(request, surface);
        var blocked = await surface.ExecuteAsync(submit, CancellationToken.None);
        await operatorSurface.RecordApprovalAsync(operatorSurface.State.Version);
        var humanAction = await operatorSurface.ExecuteHumanActionAsync(submit, operatorSurface.State.Version);
        await WaitForTextAsync("Final submission is intentionally blocked for human review.");
        var resumed = await operatorSurface.ChooseAsync(
            OperatorDecision.Resume,
            operatorSurface.State.Version);
        var sessionAfter = await surface.InspectAsync(
            new SurfaceInspection { Kind = SurfaceInspectionKind.Text, Target = sessionTarget },
            CancellationToken.None);

        Assert.False(blocked.Succeeded);
        Assert.Equal("automation-paused", blocked.ErrorCode);
        Assert.True(humanAction.Succeeded);
        Assert.Equal(ControlOwner.Automation, resumed.State.Owner);
        Assert.Equal(sessionBefore.Value, sessionAfter.Value);
        Assert.NotEqual("loading", sessionAfter.Value);
        Assert.Single(operatorSurface.HumanActions);
    }

    private Task<SurfaceActionResult> NavigateAsync(string relativePath) => surface.ExecuteAsync(new SemanticAction
    {
        Kind = ActionKind.Navigate,
        Destination = new Uri(server.BaseAddress, relativePath)
    }, CancellationToken.None);

    private async Task WaitForTextAsync(string expected)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var observation = await surface.ObserveAsync(CancellationToken.None);
            if (observation.VisibleText.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Expected fixture text '{expected}' was not observed.");
    }

    private async Task WaitForTextValueAsync(TargetDescriptor target, Func<string?, bool> predicate)
    {
        var timeout = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < timeout)
        {
            var inspection = await surface.InspectAsync(
                new SurfaceInspection { Kind = SurfaceInspectionKind.Text, Target = target },
                CancellationToken.None);
            if (inspection.Succeeded && predicate(inspection.Value))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("The expected fixture value was not observed.");
    }

    private static TargetDescriptor Target(params TargetStrategy[] strategies) => new()
    {
        Strategies = strategies,
        RequireUniqueMatch = true
    };
}

public sealed class PlaywrightFixture : IAsyncLifetime
{
    public FixtureServer Server { get; } = new();

    public Task InitializeAsync() => Server.StartAsync();

    public async Task DisposeAsync() => await Server.DisposeAsync();
}
