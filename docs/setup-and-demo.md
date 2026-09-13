# Setup and demonstration

## Prerequisites

- Git
- .NET 8 SDK
- PowerShell 7 on Windows, or a compatible shell on Linux
- A Gemini API key only for live discovery

All examples use the local deterministic fixture and synthetic records. Replay does not need network access to Gemini or any model credential.

## Clone, build, and install Chromium

Run from a terminal:

```text
git clone https://github.com/satiaarpit/compute-use-automatuion.git
cd compute-use-automatuion
dotnet restore ComputerUse.sln
dotnet build ComputerUse.sln --configuration Release --no-restore
pwsh tests/ComputerUse.Playwright.Tests/bin/Release/net8.0/playwright.ps1 install chromium
dotnet test ComputerUse.sln --configuration Release --no-build
```

On Linux, invoke the generated Playwright script with PowerShell 7 or use the Playwright installation command shown by the build output.

## Start the synthetic fixture

Keep this process running:

```text
dotnet run --project src/ComputerUse.TestApp --configuration Release --no-build --urls http://127.0.0.1:5187
```

## Run genuine discovery

Set the key in the current process only. Never place it in source, an artifact, logs, or configuration:

```text
$env:GEMINI_API_KEY = "<your-key>"
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- discover http://127.0.0.1:5187 "Search for the requested record, read its title, and complete after one result is visible" query Aurora artifacts/generated/search.json
```

`GEMINI_MODEL` can override the default `gemini-3.6-flash`. The generated artifact remains ignored until reviewed.

## Validate and replay without Gemini

Remove the key from the replay process, validate the artifact, and replay with a different value:

```text
Remove-Item Env:GEMINI_API_KEY -ErrorAction SilentlyContinue
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- validate artifacts/generated/search.json
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/generated/search.json query Borealis
```

Expected output includes `Borealis Atlas`. Replay binds inputs locally and never calls the decision model.

## Run the mandatory synthetic demonstrations

```text
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/search-and-extract.capability.json query Borealis
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/navigation-to-review.capability.json
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/reversible-interaction.capability.json reference REC-300 note "Review synthetic record."
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/recoverable-read.capability.json query Aurora
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/search-and-extract.capability.json query ZZZ-No-Match
```

The recovery command records two transient `target-not-found` attempts before the delayed synthetic result succeeds. The last command returns the declared `no-results` business outcome after bounded read attempts rather than reporting a technical failure.

## Run the same-session human handoff

```text
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay-interactive artifacts/risk-gated-final-action.capability.json reference REC-100
```

The runtime pauses before the irreversible click. Inspect the displayed request, then type the exact word `APPROVE` to record authorization and execute only that action through the opaque human-control session. Any other input declines. Approval persistence is mandatory; a failed approval write blocks execution. Before returning control, replay compares a non-sensitive browser-session commitment. The local endpoint intentionally returns a blocked-for-review message; no external system is changed.

## Reproduce rich failure evidence

```text
dotnet run --project src/ComputerUse.Cli --configuration Release --no-build -- replay artifacts/hard-failure-demonstration.capability.json
```

This reviewed artifact intentionally fails its checkpoint. The result identifies `checkpoint`, returns `checkpoint-not-satisfied`, and references a masked screenshot and allowlisted snapshot. Runtime evidence is best effort and does not change the replay result.
