# R1 Legacy Removal Handoff

## Status and boundary

Issue #80 completes the R1 deletion boundary from exact start SHA `720d782768be66f0e468e9857e631ff263077e3f`, the merge commit of PR #82 and completed dependency #79.

The development head no longer contains the legacy TypeScript Action coordinator, its input parser, Claude Code CLI installer/invoker/parser, legacy state reader/writer, Action artifact adapter, prompt assembler, or context-path loader. Historical tags retain historical behavior. R1 does not provide a public Action and does not choose the R2 Agent abstraction, state model, tool loop, or R4 artifact transport.

Deleted source families:

- `src/main.ts`, `src/main.test.ts`, and `src/main-sanitizer.test.ts`;
- `src/config.ts` and `src/config.test.ts`;
- `src/runtime.ts` and `src/runtime.test.ts`;
- `src/state.ts` and `src/state.test.ts`;
- `src/artifacts.ts` and `src/artifacts.test.ts`;
- `src/prompt.ts`, `src/prompt.test.ts`, and `src/context-blocks.ts`.

At the R1 handoff, `@actions/core` and `@actions/artifact` were removed because no production importer survived, while `@actions/github` remained for the temporary M4 ledger/state/publisher evidence and `esbuild` remained for fixture and acceptance-test generation. R4 later restored the checked wrapper and artifact bridge dependency set; W8 removes only the superseded TypeScript sticky publisher and does not change current wrapper package ownership.

## Prompt and context disposition

| Prior assertion                                                                    | R1 disposition                                               | Future owner                                |
| ---------------------------------------------------------------------------------- | ------------------------------------------------------------ | ------------------------------------------- |
| Untrusted repository, PR, diff, and context text is data rather than control       | Preserve as a required control/data boundary                 | R2 Agent request materialization            |
| Prompt-injection framing is explicit and deterministic                             | Preserve the security invariant, not the old string template | R2 Agent request materialization            |
| Monolithic Action-era prompt formatting                                            | Obsolete; deleted with `buildReviewPrompt`                   | R2 refines the selected Agent request shape |
| Action input and repository path context loading                                   | Obsolete; deleted with the public Action parser/loader       | R3/R4 refine trusted tool and Host inputs   |
| Incremental prompt-text assembly and prompt-text patch allocation                  | Obsolete; deleted with the old prompt assembler              | None                                        |
| Bounded patch DTO, schema, and runtime validation                                  | Retain as temporary protocol evidence                        | R2 protocol/runtime replacement             |
| Review-scope behavior for changed, removed, unchanged, and patch-unavailable files | Retain in target snapshot/delta and protocol tests           | R2 Agent request materialization            |

## Broad coordinator-test disposition

Coordinator routing, Action outputs, step summaries, artifact upload ordering, legacy manifest restoration, provider skip routing, and sticky-disabled/inline coupling are deleted with the no-public-Action interval. Their surviving product invariants are owned by narrower evidence:

- direct framework and Native AOT runtime integration owns command, output validation, failure taxonomy, cleanup, diagnostics, privacy, and child-environment behavior;
- at the R1 handoff, the production `ledger-csharp` pre-acceptance stage owned runtime/result/trace/cancellation-to-zero-write composition; R4-W7 later removed that TypeScript coordinator after P6/S6/E1 replacement evidence passed;
- state selection owns exact-v1 automatic recovery and explicit fail-closed restore without predecessor replay;
- at the R1 handoff, state acceptance owned candidate, registration, marker, selector, cancellation, stale, conflict, CAS, idempotency, publication, and receipt behavior; R4-W6 later removed that TypeScript family after the encrypted state transaction and recovery evidence passed;
- R4-W8 later removed the TypeScript sticky comment family after its rendering, marker, fingerprint, mutation/readback, and recovery contracts moved to checked C# evidence;
- R4-W9 later removed inline comments and target snapshots after the H2/H5/P3/P4/P6/E1 disposition map passed; remaining protocol suites retain their narrower owners.

Under the historical M4 contract at this handoff, sticky publication occurred after selector acceptance. A failed or unknown sticky publication could therefore coexist with accepted state and a receipt outcome; R4's retained C# transaction instead proves sticky readback before acceptance.

## Artifact provenance vector handoff

`src/artifact-provenance-vectors.ts` is the authoritative provider-independent vector manifest. `src/artifact-provenance-vectors.test.ts` requires the complete stable ID range, unique IDs, structured inputs and outcomes, security invariants, future consumers and gates for ported vectors, and rationales for obsolete vectors.

| ID      | Security question and current disposition                                                                                                                                                     | R4 requirement or deletion rationale                        |
| ------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ----------------------------------------------------------- |
| APV-001 | Exact artifact-name mismatch is ineligible                                                                                                                                                    | Exact-name eligibility                                      |
| APV-002 | Expired artifact is ineligible                                                                                                                                                                | Retention-aware eligibility                                 |
| APV-003 | Missing or zero artifact ID is ineligible                                                                                                                                                     | Positive artifact identity                                  |
| APV-004 | Repository lookup requires embedded producer identity; explicit-run lookup falls back from missing/null embedded identity to `explicitRunId`, but present zero/invalid identity is ineligible | Structured producer-ID precedence cases                     |
| APV-005 | Current-run metadata read failure aborts lookup                                                                                                                                               | Fail-closed trust-anchor read                               |
| APV-006 | Candidate-run metadata read failure skips that candidate                                                                                                                                      | Per-candidate fail-closed metadata read                     |
| APV-007 | Malformed current run ID invalidates the trust anchor                                                                                                                                         | Safe-integer current-run identity                           |
| APV-008 | Malformed candidate run ID invalidates the candidate                                                                                                                                          | Safe-integer candidate identity                             |
| APV-009 | Missing, invalid, or mismatched workflow ID is rejected                                                                                                                                       | Complete workflow identity                                  |
| APV-010 | Missing or mismatched workflow path is rejected                                                                                                                                               | Complete workflow definition identity                       |
| APV-011 | Missing or mismatched event is rejected                                                                                                                                                       | Complete event-domain identity                              |
| APV-012 | Missing head SHA is rejected                                                                                                                                                                  | Complete source provenance                                  |
| APV-013 | Missing or mismatched head repository is rejected                                                                                                                                             | Complete repository identity                                |
| APV-014 | Only the candidate producer must already have `conclusion=success`                                                                                                                            | Candidate-only successful-producer rule                     |
| APV-015 | Workflow ID mismatch is rejected                                                                                                                                                              | Current/candidate workflow equality                         |
| APV-016 | Workflow path mismatch is rejected                                                                                                                                                            | Current/candidate path equality                             |
| APV-017 | Event mismatch is rejected                                                                                                                                                                    | Current/candidate event equality                            |
| APV-018 | Head-repository mismatch is rejected                                                                                                                                                          | Current/candidate repository equality                       |
| APV-019 | PR state requires a `pull_request` producer event                                                                                                                                             | PR-domain eligibility                                       |
| APV-020 | Candidate must be associated with the target PR number                                                                                                                                        | Target binding                                              |
| APV-021 | Explicit run selection does not bypass trust checks                                                                                                                                           | Explicit-selection non-bypass                               |
| APV-022 | Trust filtering occurs before recency ordering                                                                                                                                                | Newer untrusted candidates cannot shadow trusted state      |
| APV-023 | Distinct valid timestamps used descending lexical order                                                                                                                                       | Define timestamp semantics                                  |
| APV-024 | Equal timestamps preserved upstream API order without a tie-break                                                                                                                             | Define and test a deterministic total order                 |
| APV-025 | Missing/malformed timestamps were string-coerced and remained eligible                                                                                                                        | Validate or explicitly order malformed timestamps           |
| APV-026 | The retired adapter observed only the first 100 results                                                                                                                                       | Complete enumeration                                        |
| APV-027 | A trusted candidate outside the observed page was indistinguishable from absence                                                                                                              | Pagination completeness                                     |
| APV-028 | Later-page failure was unrepresentable because pagination did not exist                                                                                                                       | Partial-enumeration failure taxonomy                        |
| APV-029 | A fully observed set with no trusted candidate returns no state                                                                                                                               | Never select the best untrusted candidate                   |
| APV-030 | Artifact-list API failure propagates                                                                                                                                                          | Lookup failure cannot masquerade as absence                 |
| APV-031 | Permissive legacy explicit restore is obsolete                                                                                                                                                | Deleted with no reader, converter, or compatibility promise |
| APV-032 | `@actions/artifact` upload/download transport is obsolete                                                                                                                                     | R4 chooses its own artifact bridge                          |

Ported vectors remain until equivalent R4 artifact-bridge conformance coverage exists. They preserve security questions and invariants without freezing the future transport or ordering algorithm.

## Retained family ownership

| Retained family                                                   | Current role                                                                  | Lifecycle owner                    |
| ----------------------------------------------------------------- | ----------------------------------------------------------------------------- | ---------------------------------- |
| `src/runtime-invocation/` and direct integration                  | Runtime process, file, validation, cleanup, privacy, and capability evidence  | R2 runtime and R4 Host             |
| `src/live-runtime-invocation/` and `src/live-provider/`           | Temporary M4 live-provider evidence                                           | R2/R4 replacement                  |
| removed `src/state-v2/`                                           | R4-W5 retired StateV2; no current reader or compatibility surface.            | Replaced by R4 Host W5             |
| removed `src/state-acceptance/`                                   | Historical candidate-to-receipt transaction evidence; replacement is checked  | Replaced by R4 Host W6             |
| removed sticky root files/tests                                   | Historical sticky evidence; replacement is checked in C#                      | Replaced by R4 Host W8             |
| removed inline-comment and target root files/tests                | Historical mapped inline/target evidence; replacement is checked in C#        | Replaced by R4 Host W9             |
| removed `src/ledger-csharp.ts` and root tests                     | Historical internal ledger composition evidence; no public Action caller      | Replaced by R4 Host W7             |
| embedded review schemas and `protocol/fixtures/v1/**`             | Live internal C# direct-runtime conformance and mixed ledger fixture evidence | C# Protocol/Ledger owners          |
| removed protocol TypeScript DTOs/adapters and structured assembly | Historical W10 disposition and base-inventory evidence                        | Replaced by R4 Host W10            |
| removed root `src/types.ts` and `src/utils.ts`                    | W15 52-symbol disposition and exact W7-W10 historical import evidence         | Direct C# protocol and Host owners |
| Artifact provenance vectors                                       | R4 security input manifest                                                    | R4 artifact bridge                 |

## Residual-reference ownership

`src/residual-reference-allowlist.ts` and `src/residual-reference-guard.test.ts` are lifecycle machinery and are excluded from match discovery to avoid recursive self-matches. The guard enumerates every Git-tracked file, skips only binary or invalid-UTF-8 content plus those two machinery files, and scans all remaining text. It enforces both directions: every executable, contract, or documentary match has exactly one narrow rule, and every rule still matches at least one owned line.

| Rules       | Owned residual                                       | Consumer and deletion gate                                                                |
| ----------- | ---------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| RR-001..002 | Exact direct-runtime selector vocabulary             | Permanent narrow C# schema and synthetic-fixture conformance                              |
| RR-005..006 | Historical provider credential names                 | Generic child-environment canaries; delete with the R2/R4 invocation replacement          |
| RR-009      | S2 artifact-provenance and provider vocabulary       | Permanent executable negative and transport evidence; preserve equivalent coverage        |
| RR-010      | Removed TypeScript ledger vocabulary                 | Removed with R4-W7 after P6/S6/E1 replacement evidence passed                             |
| RR-011..012 | Unsupported-v1 state selection/classifier vocabulary | Rejection and non-replay evidence; delete with R4 state replacement                       |
| RR-013      | Temporary live-provider vocabulary                   | Internal M4 contract and retained non-executing fixture; delete with the R4 Host boundary |
| RR-014..030 | Governing or historical documentation                | Permanent path-specific status, owner, interpretation, and supersession records           |
| RR-031      | Claude provider/model identity fixtures              | Permanent narrow synthetic C# protocol/ledger conformance                                 |
| RR-034      | Root `CLAUDE.md` entrypoint                          | Supported thin agent instruction entrypoint governed by collaboration policy              |
| RR-035      | C# integration canary                                | Child-environment privacy evidence; delete with the R4 Host boundary                      |
| RR-037      | R3 single-shot removal handoff                       | Permanent checked deletion, retained-consumer, and later-owner record                     |

W13 closes the residual lifecycle with zero temporary rules. The exact selector and provider/model spellings remain only in the narrow embedded direct-runtime schema and synthetic fixture corpus owned by C# conformance tests. RR-009 permanently owns the S2 negative and transport evidence. Permanent documentation matches describe current governing boundaries or are explicitly historical, with their controlling supersession rule in the allowlist.

R4-W14 retired the TypeScript canonical-json family; C# Canonical remains current and the prefix corpus remains immutable evidence.

## Validation ownership

Ordinary CI runs unconditional TypeScript unit, composition, state, publisher, vector, and residual-guard evidence. Runtime CI owns `npm run runtime:integration`, which publishes the framework fixture and executes the coordinator-free framework and Linux Native AOT matrix. Listing the conditionally enabled integration suite in a raw Vitest command would only skip it and is not accepted as evidence.

R1 rollback is source rollback to the exact pre-removal SHA. It is not an in-tree compatibility flag or a legacy reader.
