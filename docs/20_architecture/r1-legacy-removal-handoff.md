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

`@actions/core` and `@actions/artifact` are removed because no production importer survives. `@actions/github` remains for the temporary M4 ledger/state/publisher evidence. `esbuild` remains for fixture and acceptance-test generation.

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
- the production `ledger-csharp` pre-acceptance stage owns runtime/result/trace/cancellation-to-zero-write composition;
- state selection owns exact-v1 automatic recovery and explicit fail-closed restore without predecessor replay;
- state acceptance owns candidate, registration, marker, selector, cancellation, stale, conflict, CAS, idempotency, publication, and receipt behavior;
- comments, inline comments, fingerprints, target snapshots, ledger planning, and protocol suites retain their narrow contracts.

Under the retained M4 contract, sticky publication occurs after selector acceptance. A failed or unknown sticky publication can therefore coexist with accepted state and a receipt outcome; the deleted Action artifact-ordering assertion is not carried forward.

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

| Retained family                                         | Current role                                                                 | Lifecycle owner         |
| ------------------------------------------------------- | ---------------------------------------------------------------------------- | ----------------------- |
| `src/runtime-invocation/` and direct integration        | Runtime process, file, validation, cleanup, privacy, and capability evidence | R2 runtime and R4 Host  |
| `src/live-runtime-invocation/` and `src/live-provider/` | Temporary M4 live-provider evidence                                          | R2/R4 replacement       |
| `src/state-v2/`                                         | Unsupported-v1 classifier and StateManifestV2 conformance                    | R4 state bridge         |
| `src/state-acceptance/`                                 | Temporary candidate-to-receipt state transaction evidence                    | R4 state bridge         |
| `src/comments.ts`, inline comments, and fingerprints    | Temporary deterministic publisher and lineage evidence                       | R4 Host/publisher       |
| `src/ledger-csharp.ts`                                  | Temporary internal ledger composition evidence; no public Action caller      | R4 Host                 |
| `protocol/` and protocol TypeScript DTOs                | Frozen migration/conformance evidence                                        | R2 protocol replacement |
| Artifact provenance vectors                             | R4 security input manifest                                                   | R4 artifact bridge      |

## Residual-reference ownership

`src/residual-reference-allowlist.ts` and `src/residual-reference-guard.test.ts` are lifecycle machinery and are excluded from match discovery to avoid recursive self-matches. The guard enumerates every Git-tracked file, skips only binary or invalid-UTF-8 content plus those two machinery files, and scans all remaining text. It enforces both directions: every executable, contract, or documentary match has exactly one narrow rule, and every rule still matches at least one owned line.

| Rules       | Owned residual                                                     | Consumer and deletion gate                                                                                             |
| ----------- | ------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------- |
| RR-001..004 | Exact retired runtime-selector vocabulary                          | Frozen protocol DTO/schema/fixtures and a retained non-executing rejection fixture; delete with its R4 migration owner |
| RR-005..006 | Historical provider credential names                               | Generic child-environment canaries; delete with the R2/R4 invocation replacement                                       |
| RR-007..010 | M4 marker, state, artifact-vector, ledger, and provider vocabulary | Internal transition evidence only; delete with R4 owners                                                               |
| RR-011..012 | Unsupported-v1 state selection/classifier vocabulary               | Rejection and non-replay evidence; delete with R4 state replacement                                                    |
| RR-013      | Temporary live-provider vocabulary                                 | Internal M4 contract and retained non-executing fixture; delete with the R4 Host boundary                              |
| RR-014..030 | Governing or historical documentation                              | Permanent path-specific status, owner, interpretation, and supersession records                                        |
| RR-031..033 | Claude provider/model identity fixtures                            | Opaque protocol/state fixture evidence; delete with the R2/R4 owners                                                   |
| RR-034      | Root `CLAUDE.md` entrypoint                                        | Supported thin agent instruction entrypoint governed by collaboration policy                                           |
| RR-035      | C# integration canary                                              | Child-environment privacy evidence; delete with the R4 Host boundary                                                   |

Temporary matches are not supported public runtime selectors. The exact retired runtime selector is retained only in frozen protocol/conformance inputs and a negative runtime regression; unsupported legacy state is retained only to prove rejection; credential names are canaries whose values must not reach the generic child. Permanent documentation matches describe current governing boundaries or are explicitly marked historical, with their controlling supersession rule in the allowlist.

## Validation ownership

Ordinary CI runs unconditional TypeScript unit, composition, state, publisher, vector, and residual-guard evidence. Runtime CI owns `npm run runtime:integration`, which publishes the framework fixture and executes the coordinator-free framework and Linux Native AOT matrix. Listing the conditionally enabled integration suite in a raw Vitest command would only skip it and is not accepted as evidence.

R1 rollback is source rollback to the exact pre-removal SHA. It is not an in-tree compatibility flag or a legacy reader.
