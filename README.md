# Computer-Use Capability Generator

[![CI](https://github.com/satiaarpit/compute-use-automation/actions/workflows/ci.yml/badge.svg)](https://github.com/satiaarpit/compute-use-automation/actions/workflows/ci.yml)

A generic computer-use system that uses an LLM to discover workflows on live user interfaces, compiles successful runs into typed and versioned capabilities, and replays those capabilities deterministically for agent invocation.

## Status

The narrow discovery-to-replay MVP is implemented and verified with a genuine Gemini run. It accepts a URL, goal, and typed input; runs a bounded Gemini observe-decide-act loop over Playwright; verifies completion against the observed UI; compiles the successful trace into the versioned capability artifact; and replays the artifact with a different input and no model credentials. The verified fixture run used Gemini 3.6 Flash to discover `Aurora Field Guide`, then replayed deterministically for `Borealis Atlas`.

See the [setup and demonstration guide](docs/setup-and-demo.md), [final report](docs/report.md), [curated evidence](evidence/README.md), [design specification](docs/design-spec.md), [implementation plan](docs/implementation-plan.md), and [delivery progress](PROGRESS.md).

## Repository file guide

This catalog tells reviewers what every tracked file contributes. Run identifiers under `evidence/runtime/` link a replay or discovery result to its event stream and to the cryptographic [demonstration manifest](evidence/demonstration-manifest.json).

### Repository configuration

| File | Purpose |
|---|---|
| [.editorconfig](.editorconfig) | Defines repository-wide encoding, line-ending, whitespace, indentation, and analyzer conventions. |
| [.gitattributes](.gitattributes) | Normalizes text files across operating systems while preserving required Windows formats. |
| [.gitignore](.gitignore) | Excludes build output, local secrets, unreviewed generated artifacts, and runtime evidence; explicitly allows the curated submission evidence. |
| [.github/workflows/ci.yml](.github/workflows/ci.yml) | Builds and tests on Windows and Linux and scans the complete Git history for secrets. |
| [ComputerUse.sln](ComputerUse.sln) | Groups all production and test projects into the .NET solution. |
| [Directory.Build.props](Directory.Build.props) | Applies shared .NET compiler, language, nullable, analyzer, and warning settings. |
| [Directory.Packages.props](Directory.Packages.props) | Centrally pins NuGet package versions. |
| [global.json](global.json) | Pins the expected .NET SDK family for local development and CI. |
| [config/appsettings.example.json](config/appsettings.example.json) | Provides a safe, non-secret example of runtime policy and application settings. |

### Reviewer documentation

| File | Purpose |
|---|---|
| [README.md](README.md) | Entry point for the project, setup, architecture overview, demonstrations, security guidance, and this file catalog. |
| [PROGRESS.md](PROGRESS.md) | Records phase completion, requirement coverage, decisions, and final submission readiness. |
| [docs/design-spec.md](docs/design-spec.md) | Defines requirements, architecture, contracts, safety boundaries, replay semantics, and extensibility. |
| [docs/implementation-plan.md](docs/implementation-plan.md) | Maps requirements to phased implementation tasks and executable verification gates. |
| [docs/report.md](docs/report.md) | Contains the assignment's required seven-section final design and implementation report. |
| [docs/setup-and-demo.md](docs/setup-and-demo.md) | Gives exact clone, build, discovery, replay, recovery, failure, and human-handoff commands. |
| [evidence/README.md](evidence/README.md) | Explains the curated evidence package, run identifiers, privacy treatment, and verification procedure. |

### Capability artifacts

All files below are replayable capability definitions. Top-level artifacts are reviewed demonstrations; `generated/search.json` is the output of genuine Gemini-driven discovery. The generated artifact is not input to Gemini: Gemini discovers a successful path, the compiler creates the artifact, and replay later executes it without a model.

| File | Origin and purpose |
|---|---|
| [artifacts/.gitkeep](artifacts/.gitkeep) | Keeps the artifact output directory present in a fresh clone when generated files are otherwise ignored. |
| [artifacts/search-and-extract.capability.json](artifacts/search-and-extract.capability.json) | Hand-authored reference that searches with a typed query, verifies the result, and extracts a typed title. |
| [artifacts/navigation-to-review.capability.json](artifacts/navigation-to-review.capability.json) | Navigates to the synthetic review form and verifies the expected review state. |
| [artifacts/reversible-interaction.capability.json](artifacts/reversible-interaction.capability.json) | Enters typed form values without performing the consequential final action. |
| [artifacts/risk-gated-final-action.capability.json](artifacts/risk-gated-final-action.capability.json) | Pauses before an irreversible action and requires explicit same-session human approval. |
| [artifacts/recoverable-read.capability.json](artifacts/recoverable-read.capability.json) | Demonstrates bounded retry and recovery after transient read failures. |
| [artifacts/hard-failure-demonstration.capability.json](artifacts/hard-failure-demonstration.capability.json) | Intentionally fails a checkpoint to demonstrate structured errors and debugging evidence. |
| [artifacts/generated/search.json](artifacts/generated/search.json) | Capability compiled from the successful Gemini discovery run and replayed deterministically without Gemini. |

### Core library

| File | Purpose |
|---|---|
| [src/ComputerUse.Core/ComputerUse.Core.csproj](src/ComputerUse.Core/ComputerUse.Core.csproj) | Defines the provider-neutral core library project. |
| [src/ComputerUse.Core/Automation.cs](src/ComputerUse.Core/Automation.cs) | Implements bounded discovery orchestration and the structured Gemini decision provider. |
| [src/ComputerUse.Core/Binding/InputBinder.cs](src/ComputerUse.Core/Binding/InputBinder.cs) | Validates and converts invocation values into declared input types. |
| [src/ComputerUse.Core/Configuration/ApplicationSettings.cs](src/ComputerUse.Core/Configuration/ApplicationSettings.cs) | Defines typed runtime configuration. |
| [src/ComputerUse.Core/Configuration/SettingsJson.cs](src/ComputerUse.Core/Configuration/SettingsJson.cs) | Loads and validates JSON application settings. |
| [src/ComputerUse.Core/Contracts/Actions.cs](src/ComputerUse.Core/Contracts/Actions.cs) | Defines semantic UI actions, targets, checkpoints, retries, and risk metadata. |
| [src/ComputerUse.Core/Contracts/CapabilityArtifact.cs](src/ComputerUse.Core/Contracts/CapabilityArtifact.cs) | Defines the versioned, typed capability artifact contract. |
| [src/ComputerUse.Core/Contracts/ExecutionResult.cs](src/ComputerUse.Core/Contracts/ExecutionResult.cs) | Defines success, business-outcome, intervention, and failure results. |
| [src/ComputerUse.Core/Contracts/TaskRequest.cs](src/ComputerUse.Core/Contracts/TaskRequest.cs) | Defines discovery goal and input requests. |
| [src/ComputerUse.Core/Contracts/ValueDefinition.cs](src/ComputerUse.Core/Contracts/ValueDefinition.cs) | Defines supported typed input and output values and data classifications. |
| [src/ComputerUse.Core/Evidence/EvidenceSafety.cs](src/ComputerUse.Core/Evidence/EvidenceSafety.cs) | Redacts sensitive values and confines evidence paths on Windows and Linux. |
| [src/ComputerUse.Core/Intervention/InterventionContracts.cs](src/ComputerUse.Core/Intervention/InterventionContracts.cs) | Defines control-transfer, approval, human-action, and resume contracts. |
| [src/ComputerUse.Core/Intervention/LocalOperatorSurface.cs](src/ComputerUse.Core/Intervention/LocalOperatorSurface.cs) | Enforces authoritative approval and safe same-session human control. |
| [src/ComputerUse.Core/Logging/RunEvents.cs](src/ComputerUse.Core/Logging/RunEvents.cs) | Defines and writes correlated, value-safe JSONL run events. |
| [src/ComputerUse.Core/Policy/AutomationPolicy.cs](src/ComputerUse.Core/Policy/AutomationPolicy.cs) | Enforces navigation, route, action, count, and risk policy. |
| [src/ComputerUse.Core/Serialization/CapabilityJson.cs](src/ComputerUse.Core/Serialization/CapabilityJson.cs) | Centralizes strict JSON serialization for capability contracts. |
| [src/ComputerUse.Core/Surfaces/IComputerSurface.cs](src/ComputerUse.Core/Surfaces/IComputerSurface.cs) | Abstracts observation, action, evidence capture, and pause/resume across UI platforms. |
| [src/ComputerUse.Core/Validation/CapabilityValidator.cs](src/ComputerUse.Core/Validation/CapabilityValidator.cs) | Rejects malformed or unsafe capability definitions before execution. |
| [src/ComputerUse.Core/Validation/ValidationResult.cs](src/ComputerUse.Core/Validation/ValidationResult.cs) | Represents structured validation issues and outcomes. |

### Replay and evidence library

| File | Purpose |
|---|---|
| [src/ComputerUse.Replay/ComputerUse.Replay.csproj](src/ComputerUse.Replay/ComputerUse.Replay.csproj) | Defines the compiler, replay, template-binding, and evidence project. |
| [src/ComputerUse.Replay/ArtifactLoader.cs](src/ComputerUse.Replay/ArtifactLoader.cs) | Loads and validates artifacts before a UI surface is created. |
| [src/ComputerUse.Replay/AutomationReplay.cs](src/ComputerUse.Replay/AutomationReplay.cs) | Compiles successful normalized discovery traces into typed capabilities. |
| [src/ComputerUse.Replay/DiscoveryEvidenceWriter.cs](src/ComputerUse.Replay/DiscoveryEvidenceWriter.cs) | Writes the provider/model discovery receipt and artifact commitment. |
| [src/ComputerUse.Replay/EvidenceDigest.cs](src/ComputerUse.Replay/EvidenceDigest.cs) | Computes cross-platform canonical SHA-256 digests for text evidence and byte-exact digests for binary evidence. |
| [src/ComputerUse.Replay/ReplayEngine.cs](src/ComputerUse.Replay/ReplayEngine.cs) | Executes validated capabilities deterministically with bounded retries, checkpoints, outcomes, and handoff. |
| [src/ComputerUse.Replay/ReplayResultEvidenceWriter.cs](src/ComputerUse.Replay/ReplayResultEvidenceWriter.cs) | Persists classification-aware sanitized replay results and output commitments. |
| [src/ComputerUse.Replay/RuntimeTemplateBinder.cs](src/ComputerUse.Replay/RuntimeTemplateBinder.cs) | Resolves typed invocation placeholders without modifying the stored artifact. |

### Playwright web adapter

| File | Purpose |
|---|---|
| [src/ComputerUse.Playwright/ComputerUse.Playwright.csproj](src/ComputerUse.Playwright/ComputerUse.Playwright.csproj) | Defines the Playwright surface adapter project. |
| [src/ComputerUse.Playwright/PlaywrightComputerSurface.cs](src/ComputerUse.Playwright/PlaywrightComputerSurface.cs) | Observes and operates a live browser while masking values and retaining session continuity. |
| [src/ComputerUse.Playwright/PlaywrightSurfaceOptions.cs](src/ComputerUse.Playwright/PlaywrightSurfaceOptions.cs) | Defines browser limits, evidence settings, and adapter options. |
| [src/ComputerUse.Playwright/PlaywrightTargetResolver.cs](src/ComputerUse.Playwright/PlaywrightTargetResolver.cs) | Resolves semantic targets through ordered strategies and rejects ambiguity. |
| [src/ComputerUse.Playwright/TargetResolutionException.cs](src/ComputerUse.Playwright/TargetResolutionException.cs) | Carries structured target-not-found and ambiguous-target failures. |

### Command-line application

| File | Purpose |
|---|---|
| [src/ComputerUse.Cli/ComputerUse.Cli.csproj](src/ComputerUse.Cli/ComputerUse.Cli.csproj) | Defines the executable CLI project and its dependencies. |
| [src/ComputerUse.Cli/Program.cs](src/ComputerUse.Cli/Program.cs) | Implements `discover`, `replay`, `replay-interactive`, `validate`, and `check-config`. |

### Deterministic test application

| File | Purpose |
|---|---|
| [src/ComputerUse.TestApp/ComputerUse.TestApp.csproj](src/ComputerUse.TestApp/ComputerUse.TestApp.csproj) | Defines the local ASP.NET Core synthetic UI fixture. |
| [src/ComputerUse.TestApp/Program.cs](src/ComputerUse.TestApp/Program.cs) | Hosts deterministic success, no-result, recovery, review, and risky-action states. |
| [src/ComputerUse.TestApp/wwwroot/index.html](src/ComputerUse.TestApp/wwwroot/index.html) | Provides the fixture's accessible semantic HTML structure. |
| [src/ComputerUse.TestApp/wwwroot/app.js](src/ComputerUse.TestApp/wwwroot/app.js) | Drives deterministic fixture state transitions and delayed recovery behavior. |
| [src/ComputerUse.TestApp/wwwroot/styles.css](src/ComputerUse.TestApp/wwwroot/styles.css) | Styles the synthetic fixture and masked evidence views. |

### Core tests

| File | Purpose |
|---|---|
| [tests/ComputerUse.Core.Tests/ComputerUse.Core.Tests.csproj](tests/ComputerUse.Core.Tests/ComputerUse.Core.Tests.csproj) | Defines the core xUnit test project. |
| [tests/ComputerUse.Core.Tests/Usings.cs](tests/ComputerUse.Core.Tests/Usings.cs) | Supplies shared xUnit imports for core tests. |
| [tests/ComputerUse.Core.Tests/CapabilityFixture.cs](tests/ComputerUse.Core.Tests/CapabilityFixture.cs) | Builds reusable valid capability objects for tests. |
| [tests/ComputerUse.Core.Tests/AutomationPolicyTests.cs](tests/ComputerUse.Core.Tests/AutomationPolicyTests.cs) | Verifies host, route, action, count, and risk enforcement. |
| [tests/ComputerUse.Core.Tests/AutomationTests.cs](tests/ComputerUse.Core.Tests/AutomationTests.cs) | Verifies bounded discovery, model decisions, stopping, and completion behavior. |
| [tests/ComputerUse.Core.Tests/CapabilityContractTests.cs](tests/ComputerUse.Core.Tests/CapabilityContractTests.cs) | Verifies typed artifact serialization and contract shape. |
| [tests/ComputerUse.Core.Tests/CapabilityValidationTests.cs](tests/ComputerUse.Core.Tests/CapabilityValidationTests.cs) | Verifies invalid and unsafe artifacts are rejected. |
| [tests/ComputerUse.Core.Tests/DemonstrationManifestTests.cs](tests/ComputerUse.Core.Tests/DemonstrationManifestTests.cs) | Cryptographically reconciles every curated demonstration file and semantic claim. |
| [tests/ComputerUse.Core.Tests/EvidenceSafetyTests.cs](tests/ComputerUse.Core.Tests/EvidenceSafetyTests.cs) | Verifies redaction and cross-platform path confinement. |
| [tests/ComputerUse.Core.Tests/ExecutionResultTests.cs](tests/ComputerUse.Core.Tests/ExecutionResultTests.cs) | Verifies structured terminal-result contracts. |
| [tests/ComputerUse.Core.Tests/InputBinderTests.cs](tests/ComputerUse.Core.Tests/InputBinderTests.cs) | Verifies required, unknown, duplicate, and typed input handling. |
| [tests/ComputerUse.Core.Tests/InterventionTests.cs](tests/ComputerUse.Core.Tests/InterventionTests.cs) | Verifies approval authority, action matching, continuity, and resume safeguards. |
| [tests/ComputerUse.Core.Tests/PersistenceContractTests.cs](tests/ComputerUse.Core.Tests/PersistenceContractTests.cs) | Verifies persisted artifacts omit sensitive runtime-only values. |
| [tests/ComputerUse.Core.Tests/RunEventLoggingTests.cs](tests/ComputerUse.Core.Tests/RunEventLoggingTests.cs) | Verifies event correlation, ordering, risk fields, and redaction. |
| [tests/ComputerUse.Core.Tests/StartupValidatorTests.cs](tests/ComputerUse.Core.Tests/StartupValidatorTests.cs) | Verifies discovery and replay configuration prerequisites. |

### Replay, browser, CLI, and fixture tests

| File | Purpose |
|---|---|
| [tests/ComputerUse.Replay.Tests/ComputerUse.Replay.Tests.csproj](tests/ComputerUse.Replay.Tests/ComputerUse.Replay.Tests.csproj) | Defines replay-engine tests. |
| [tests/ComputerUse.Replay.Tests/Usings.cs](tests/ComputerUse.Replay.Tests/Usings.cs) | Supplies shared xUnit imports for replay tests. |
| [tests/ComputerUse.Replay.Tests/ReplayEngineTests.cs](tests/ComputerUse.Replay.Tests/ReplayEngineTests.cs) | Verifies ordered model-free execution, retries, checkpoints, outcomes, outputs, and handoff. |
| [tests/ComputerUse.Replay.Tests/ReplayResultEvidenceWriterTests.cs](tests/ComputerUse.Replay.Tests/ReplayResultEvidenceWriterTests.cs) | Verifies complete, sanitized, classification-aware replay evidence. |
| [tests/ComputerUse.Playwright.Tests/ComputerUse.Playwright.Tests.csproj](tests/ComputerUse.Playwright.Tests/ComputerUse.Playwright.Tests.csproj) | Defines browser integration tests and Playwright dependencies. |
| [tests/ComputerUse.Playwright.Tests/Usings.cs](tests/ComputerUse.Playwright.Tests/Usings.cs) | Supplies shared xUnit imports for Playwright tests. |
| [tests/ComputerUse.Playwright.Tests/FixtureServer.cs](tests/ComputerUse.Playwright.Tests/FixtureServer.cs) | Starts and stops an isolated fixture process for browser tests. |
| [tests/ComputerUse.Playwright.Tests/PlaywrightComputerSurfaceTests.cs](tests/ComputerUse.Playwright.Tests/PlaywrightComputerSurfaceTests.cs) | Exercises real Chromium observations, actions, targeting, evidence, and pause/resume. |
| [tests/ComputerUse.Cli.Tests/ComputerUse.Cli.Tests.csproj](tests/ComputerUse.Cli.Tests/ComputerUse.Cli.Tests.csproj) | Defines black-box CLI tests. |
| [tests/ComputerUse.Cli.Tests/Usings.cs](tests/ComputerUse.Cli.Tests/Usings.cs) | Supplies shared xUnit imports for CLI tests. |
| [tests/ComputerUse.Cli.Tests/CliBoundaryTests.cs](tests/ComputerUse.Cli.Tests/CliBoundaryTests.cs) | Runs CLI subprocesses to verify commands, errors, approval, redaction, and typed inputs. |
| [tests/ComputerUse.TestApp.Tests/ComputerUse.TestApp.Tests.csproj](tests/ComputerUse.TestApp.Tests/ComputerUse.TestApp.Tests.csproj) | Defines fixture application tests. |
| [tests/ComputerUse.TestApp.Tests/Usings.cs](tests/ComputerUse.TestApp.Tests/Usings.cs) | Supplies shared xUnit imports for fixture tests. |
| [tests/ComputerUse.TestApp.Tests/FixtureApplicationTests.cs](tests/ComputerUse.TestApp.Tests/FixtureApplicationTests.cs) | Verifies every deterministic fixture route and business state. |

### Curated execution evidence

| File | Purpose |
|---|---|
| [evidence/.gitkeep](evidence/.gitkeep) | Keeps the evidence output directory present when no local runs exist. |
| [evidence/demonstration-manifest.json](evidence/demonstration-manifest.json) | Binds scenarios, commands, statuses, expected events, output templates, and file SHA-256 digests. |
| [evidence/runtime/c6243a4d110f48cf9da5e46663e97bf4.events.jsonl](evidence/runtime/c6243a4d110f48cf9da5e46663e97bf4.events.jsonl) | Genuine Gemini discovery event stream, including two selected model decisions and completion. |
| [evidence/runtime/c6243a4d110f48cf9da5e46663e97bf4.discovery.json](evidence/runtime/c6243a4d110f48cf9da5e46663e97bf4.discovery.json) | Hash-bound local receipt naming the Gemini provider/model, call count, run, and generated artifact digest. |
| [evidence/runtime/5d78ca5a6d3341ce9886f773a958d534.events.jsonl](evidence/runtime/5d78ca5a6d3341ce9886f773a958d534.events.jsonl) | Event stream for model-free replay of the Gemini-generated capability. |
| [evidence/runtime/5d78ca5a6d3341ce9886f773a958d534.result.json](evidence/runtime/5d78ca5a6d3341ce9886f773a958d534.result.json) | Sanitized successful result and output commitment for generated-capability replay. |
| [evidence/runtime/046ca658c36443ab9e1b43387f558f01.events.jsonl](evidence/runtime/046ca658c36443ab9e1b43387f558f01.events.jsonl) | Event stream for the hand-authored typed search-and-extract replay. |
| [evidence/runtime/046ca658c36443ab9e1b43387f558f01.result.json](evidence/runtime/046ca658c36443ab9e1b43387f558f01.result.json) | Sanitized result and exact-output commitment for typed replay. |
| [evidence/runtime/a6bc4b49d5c9449f97c387b1a8d3c6cb.events.jsonl](evidence/runtime/a6bc4b49d5c9449f97c387b1a8d3c6cb.events.jsonl) | Event stream for navigation to the verified review state. |
| [evidence/runtime/a6bc4b49d5c9449f97c387b1a8d3c6cb.result.json](evidence/runtime/a6bc4b49d5c9449f97c387b1a8d3c6cb.result.json) | Sanitized successful navigation result. |
| [evidence/runtime/b524623270a94804afb41f7c34473745.events.jsonl](evidence/runtime/b524623270a94804afb41f7c34473745.events.jsonl) | Event stream for the reversible form interaction. |
| [evidence/runtime/b524623270a94804afb41f7c34473745.result.json](evidence/runtime/b524623270a94804afb41f7c34473745.result.json) | Sanitized successful reversible-interaction result. |
| [evidence/runtime/d6641853110a4a0995bd06b2424dd3d3.events.jsonl](evidence/runtime/d6641853110a4a0995bd06b2424dd3d3.events.jsonl) | Event stream proving two bounded transient failures followed by recovery. |
| [evidence/runtime/d6641853110a4a0995bd06b2424dd3d3.result.json](evidence/runtime/d6641853110a4a0995bd06b2424dd3d3.result.json) | Sanitized recovered result and output commitment. |
| [evidence/runtime/4f2b3da6e4da41a2a35cb02ff70a2c14.events.jsonl](evidence/runtime/4f2b3da6e4da41a2a35cb02ff70a2c14.events.jsonl) | Event stream for the bounded no-results business outcome. |
| [evidence/runtime/4f2b3da6e4da41a2a35cb02ff70a2c14.result.json](evidence/runtime/4f2b3da6e4da41a2a35cb02ff70a2c14.result.json) | Structured result identifying a legitimate business outcome rather than a technical failure. |
| [evidence/runtime/0b6b97a7ce034969b6ae7249778ede4d.events.jsonl](evidence/runtime/0b6b97a7ce034969b6ae7249778ede4d.events.jsonl) | Event stream for the intentional hard checkpoint failure. |
| [evidence/runtime/0b6b97a7ce034969b6ae7249778ede4d.result.json](evidence/runtime/0b6b97a7ce034969b6ae7249778ede4d.result.json) | Structured failure result referencing the captured debugging evidence. |
| [evidence/runtime/surface-20260912170037235.png](evidence/runtime/surface-20260912170037235.png) | Masked Chromium screenshot captured for the hard-failure scenario. |
| [evidence/runtime/surface-20260912170037481.txt](evidence/runtime/surface-20260912170037481.txt) | Value-omitting structural snapshot captured for the hard-failure scenario. |
| [evidence/runtime/3d3b230e0793481e8dec0d99c0eb36f8.events.jsonl](evidence/runtime/3d3b230e0793481e8dec0d99c0eb36f8.events.jsonl) | Event stream proving approval, human action, continuity verification, and control return. |
| [evidence/runtime/3d3b230e0793481e8dec0d99c0eb36f8.result.json](evidence/runtime/3d3b230e0793481e8dec0d99c0eb36f8.result.json) | Complete approved-handoff result and session-continuity commitment. |
| [evidence/runtime/surface-20260912221129821.png](evidence/runtime/surface-20260912221129821.png) | Masked Chromium screenshot associated with the human-handoff scenario. |
| [evidence/runtime/surface-20260912221130230.txt](evidence/runtime/surface-20260912221130230.txt) | Value-omitting structural snapshot associated with the human-handoff scenario. |

## Prerequisites

- .NET 8 SDK
- Git

Gemini access is required only for `discover`. Create a Google AI Studio key at https://aistudio.google.com/apikey and set `GEMINI_API_KEY` in the current process environment. `GEMINI_MODEL` optionally overrides the verified default `gemini-3.6-flash`. Do not commit API keys or place them in configuration files. Replay never uses Gemini.

## Build and test

Run these commands from the repository root:

```text
dotnet restore ComputerUse.sln
dotnet build ComputerUse.sln --no-restore
pwsh tests/ComputerUse.Playwright.Tests/bin/Debug/net8.0/playwright.ps1 install chromium
dotnet test ComputerUse.sln --no-build
```

Use `Release` instead of `Debug` in the browser-install path when building with `--configuration Release`.

## Deterministic UI fixture

Start the domain-neutral local fixture:

`dotnet run --project src/ComputerUse.TestApp --urls http://127.0.0.1:5187`

The fixture exposes predictable browser states through routes and query parameters:

- Success: `http://127.0.0.1:5187/?state=success&query=Aurora`
- No results: `http://127.0.0.1:5187/?state=no-results`
- Slow load: `http://127.0.0.1:5187/?state=slow-load&query=Aurora`
- Permission outcome: `http://127.0.0.1:5187/?state=permission`
- Recoverable dialog: `http://127.0.0.1:5187/?state=dialog&query=Aurora`
- Risk-gated form: `http://127.0.0.1:5187/review?state=risky-submit`

All records and outcomes are synthetic and deterministic.

The Playwright adapter now provides bounded observations, stable state fingerprints, semantic actions, ordered target-strategy fallback, ambiguity rejection, and pause/resume control. Browser integration tests exercise these behaviors against the running fixture without model access.

The replay library validates artifacts and inputs before surface creation, binds runtime values without persisting them, and records deterministic per-step attempt results. It reports `Success` only after evaluating known outcomes, independently satisfying the declared checkpoint, and extracting every required output into its declared type.

## MVP CLI

Start the deterministic fixture, then discover a capability:

`dotnet run --project src/ComputerUse.Cli -- discover http://127.0.0.1:5187 "Search for the requested record, read its result, and complete when the requested value is visible" query Aurora artifacts/generated/search.json`

Replay the generated capability with a different input and no model call:

`dotnet run --project src/ComputerUse.Cli -- replay artifacts/generated/search.json query Borealis`

Validate a capability artifact:

`dotnet run --project src/ComputerUse.Cli -- validate {artifact-path}`

Validate replay configuration without requiring model credentials:

`dotnet run --project src/ComputerUse.Cli -- check-config replay config/appsettings.example.json`

Validate discovery configuration and report missing model access clearly:

`dotnet run --project src/ComputerUse.Cli -- check-config discovery config/appsettings.example.json`

The synthetic example under `artifacts` is safe to commit and validates the schema without binding the product to a particular service or domain.

Reviewed examples also cover navigation to a verified review state, reversible form preparation, a risk-gated final action, and a deliberate hard failure. Run every evaluator path from the [setup and demonstration guide](docs/setup-and-demo.md).

## Security

Use synthetic or non-sensitive data during development. Local secrets, runtime evidence, screenshots, traces, and generated artifacts are excluded from source control by default.
