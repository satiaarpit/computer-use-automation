# Curated demonstration evidence

This directory contains only synthetic, redacted evidence selected for evaluation. Runtime inputs are absent from JSONL events. The local fixture contains no real accounts, credentials, people, or external services.

| Requirement | Artifact or run | Expected signal |
|---|---|---|
| Genuine discovery and compilation | `artifacts/generated/search.json`, `runtime/c6243a4d110f48cf9da5e46663e97bf4.discovery.json`, discovery event log, and replay `runtime/5d78ca5a6d3341ce9886f773a958d534.result.json` | Provider/model receipt and discovery events bind the generated artifact; replay uses no model |
| Manifest integrity | `demonstration-manifest.json` | SHA-256 binds every curated capability, result, event log, screenshot, and snapshot |
| Typed model-free replay | `runtime/046ca658c36443ab9e1b43387f558f01.result.json` and matching event log | Redacted typed output plus independently verified exact-output SHA-256 commitment, type/click/read actions, and completed status |
| Navigation to review | `runtime/a6bc4b49d5c9449f97c387b1a8d3c6cb.result.json` and matching event log | Stable-ID click and completed checkpoint |
| Reversible interaction | `runtime/b524623270a94804afb41f7c34473745.result.json` and matching event log | Two reversible writes and completed checkpoint |
| Recoverable condition | `runtime/d6641853110a4a0995bd06b2424dd3d3.result.json` and matching event log | Delayed read fails twice, succeeds on attempt three |
| Business outcome | `runtime/4f2b3da6e4da41a2a35cb02ff70a2c14.result.json` and matching event log | Four bounded attempts, then `businessOutcome` |
| Hard failure | `runtime/0b6b97a7ce034969b6ae7249778ede4d.result.json` and matching event log | Structured `checkpoint-not-satisfied` result and terminal failed event |
| Failure inspection | `runtime/surface-20260912170037235.png` and `runtime/surface-20260912170037481.txt` | Masked fields/session identifier and omitted DOM values |
| Same-session handoff | `runtime/3d3b230e0793481e8dec0d99c0eb36f8.result.json` and matching event log | Denial, human ownership, authoritative approval, one human action, browser-session commitment equality, trusted resume, completion |

The hard-failure and handoff results reference screenshots and snapshots by exact relative names. Snapshots record only origin, title, fingerprint, bounded semantic elements, and redacted visible text. `DemonstrationManifestTests` verifies hashes, run IDs, statuses, discovery provenance, exact event contracts, independently derived output commitments, referenced evidence, and absence of supplied inputs from event logs.

Run the commands in `docs/setup-and-demo.md` to regenerate local evidence. New runtime files remain ignored unless explicitly reviewed and allowlisted.
