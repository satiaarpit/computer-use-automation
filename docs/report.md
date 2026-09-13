# Computer-use capability generator report

## Architecture

The system separates probabilistic workflow discovery from deterministic execution. A caller supplies a URL, natural-language goal, inputs, and policy. `AutomationLoop` repeatedly obtains a bounded, redacted observation from an `IComputerSurface`, asks an `IStructuredDecisionModel` for one typed decision, validates that decision, applies independent policy and risk classification, and executes one semantic action. Discovery stops on verified completion, timeout, maximum steps, intervention, or repeated state and action.

`TraceCompiler` accepts only a successful trace. It removes run-specific values, infers conservative primitive contracts, and emits a versioned `CapabilityArtifact`. `ReplayEngine` validates and binds the artifact before creating a surface. It then executes ordered semantic actions without an LLM. Playwright is the first `IComputerSurface`; the abstraction leaves room for native desktop or legacy adapters without changing capability and replay contracts.

The major trade-off is intentional narrowness. The implementation proves a complete web vertical slice instead of claiming universal production coverage. The local ASP.NET Core fixture provides repeatable search, navigation, reversible input, business-outcome, recovery, hard-failure, and consequential-action states.

## Artifact schema

A schema-versioned capability declares identity, target surface and entry point, aggregate risk, typed inputs, typed outputs, ordered steps, known outcomes, completion checkpoint, and provenance. Each step contains a semantic action, stable target strategies, timeout, retry policy, and declared risk. Inputs carry type, validation, and data classification. Outputs carry type, classification, and extraction instructions.

Compilation parameterizes action values, targets, destinations, descriptions, entry points, and completion data. It rejects invalid placeholder names, duplicate discovery values that make inference ambiguous, unsafe embedded substitution, and successful reads without values. Read actions and output definitions share one unique output identity. Raw runtime values are not stored in events.

Version 1 is explicit and reviewable JSON. Breaking contract changes require a future schema version and loader compatibility policy; the current implementation does not silently reinterpret unknown members.

## Determinism & error handling

Replay has no decision-model dependency or fallback. Runtime inputs are type-checked, bound to templates, and used to construct the entry URI, policy, actions, conditions, and extraction rules. Target resolution uses ordered semantic strategies, requires a unique match by default, and fails closed on ambiguity or unsupported strategies.

Success requires more than completed clicks. The engine evaluates declared business outcomes, verifies an independent checkpoint, and extracts every required output into its declared type. Results distinguish invalid input or artifact, success, business outcome, intervention required, cancellation, unresolved intervention, and hard failure. Recoverable errors retry only when the artifact names the error code and bounds attempts and delay.

The synthetic evidence demonstrates typed extraction of `Borealis Atlas`, navigation to a review page, reversible form preparation, four bounded attempts ending in `no-results`, a recovery that succeeds on attempt three after two transient failures, and a deliberate checkpoint failure with screenshot and redacted snapshot references.

## Heterogeneity & multi-tenant

The portable boundary is `IComputerSurface`: observe, execute semantic action, inspect condition, capture evidence, report active locations, and transfer control. A different adapter can map the same contracts to a legacy browser, remote desktop, or native accessibility tree. Product-specific selectors belong in capability artifacts, not the orchestration engine.

Tenant reuse should layer reviewed overrides over a base capability. Overrides may replace entry points, target strategies, or policy bounds but must preserve schema validation and cannot lower risk. Runtime credentials remain outside artifacts. Drift is detected by failed target resolution, checkpoints, or output extraction; it does not trigger open-ended model recovery during replay. A future catalog can route failed artifacts back through controlled rediscovery and review.

The implemented slice validates architecture, not every platform. It does not claim compatibility with banking, shopping, media, or maps sites without target-specific authorization, policy, and testing.

## Escalation & handoff

Discovery detects dead ends through timeout, maximum steps, and progress-aware repeated state. Policy also intercepts any irreversible action before execution. Replay emits an `InterventionRequest` containing the capability, step, exact proposed action, effective risk, redacted state summary, evidence references, and a trusted resume condition.

`LocalOperatorSurface` transfers the existing browser session through an opaque `IHumanControlSession`. Automation is blocked while control is pending or human-owned. The operator can execute only the approved action; stale control versions and modified actions are rejected. Resume requires fresh origin and postcondition validation before ownership returns to automation.

The interactive demonstration paused at `submit-final`, required the exact confirmation `APPROVE`, recorded control transfer to human, recorded one successful human click, revalidated the blocked-for-review postcondition, transferred ownership back to automation, and completed in the same live browser session. Decline, cancel, complete, and unresolved remain distinct terminal decisions.

## Safety

Navigation policy constrains schemes, origins, routes, action kinds, action count, and risk. Model-provided risk is advisory: `ActionRiskClassifier` computes a minimum classification from the action and target. Irreversible actions cannot run unattended. The model cannot provide arbitrary code, shell commands, script evaluation, or unrestricted browser URLs.

Evidence uses typed compact events and does not persist bound values. Text evidence is redacted before writing; screenshots mask configured-sensitive and form content. Evidence paths are confined to an approved root and use simple relative names. Event and evidence writes are best effort so observability failure cannot convert a safe runtime failure into success or change execution behavior.

This is a prototype, not authorization for automation against arbitrary third-party services. Operators must have permission, use synthetic or non-sensitive data, configure narrow policies, and inspect generated artifacts before reuse. Runtime secrets stay in process environment only. The repository and history require a final credential scan before submission.

## Cuts

- No production authentication, credential vault, distributed scheduler, queue, or multi-tenant control plane.
- No remote co-browsing; the operator surface is local and intentionally minimal.
- No native desktop adapter in the mandatory slice. WPF and FlaUI remain optional follow-up work.
- No automatic artifact approval, confidence score, self-healing replay, or silent model fallback.
- No broad external-site demonstration that could violate terms or require real accounts.
- No guarantee that one learned capability survives arbitrary UI redesign; checkpoints make drift visible and safe.
- No final email submission without explicit user approval.

The next work is clean-clone validation on Windows and Linux, dependency and history scans, schema reconciliation of the curated evidence package, and the final delivery checklist. Optional platform breadth begins only after those gates pass.
