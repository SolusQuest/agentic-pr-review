# R6 prospective quality rubric decision (#311)

## Decision

Future, explicitly selected five-case R6 quality runs use `r6-v5-semantic-v1` beside the unchanged R5 exact scorer and historical R6 V5 gate. This decision does not rescore #295, authorize a paid run, or enter #296. The first future population must be newly predeclared over the frozen #294 corpus and evaluated as one complete run.

The selector is an optional, null-omitted final property of the existing `apr.r5.live-plan.v1` input and normalized journal plan. Omitting it must reproduce the old canonical plan bytes, plan digest, usage-journal bytes, summary shape, and archived #295 JSONL hashes. A separate plan format was considered; it would avoid extending a legacy DTO but duplicate the existing bounded admission and journal contracts. Exact golden bytes and hashes, not an assumption about serializer defaults, decide compatibility.

The private R6 annotation is a separate strict JSON type bound to rubric ID and digest, corpus/case/configuration/execution hashes and each finding ordinal. The invoked reviewer path supplies provenance; an editable annotation cannot assert human origin. One authenticated in-process `EvaluationSubject` is required per completed case. Each case emits one public-safe receipt, including zero-finding cases. Finding verdict, global/run-local defect group, safe-line role and citation class remain independent dimensions. A reviewer confirms causal truth; code checks evidence authority, returned observation grounding, frozen anchor, severity and the compact span. A known authored defect always uses its frozen global group, including when it is found off focus. `run-*` groups are reserved for independently confirmed other defects, with one ID per causal defect throughout the run.

Any accusation of a frozen safe line takes precedence over comparison. Comparison applies only when every use of the safe line is non-accusatory; `none` applies only when no frozen safe line participates. Cross-case repeats of a true defect and independently confirmed off-focus findings are counted and reported, not automatic failures. Within-case duplicate groups, unsafe accusations, false/unresolved findings and missing focal findings block.

The legacy outcome is retained byte-for-byte. Its `ExpectedFindingMissing` can be superseded only by bounded-anchor focal credit. Its mechanical `ProhibitedFinding` can be superseded only when every corresponding safe-line overlap is independently classified comparison-only. Other scenario codes, failed evidence/execution/source authority, privacy, accounting and cleanup cannot be overridden.

One recovery per five-case population may qualify when the rejected tool call was never dispatched or returned, its canonical error reached the model, a later ordinary tool call completed, and the authenticated subject completed. A public attestation binds rubric, schedule index, case/configuration/execution hashes, observed recovery count and fixed status/reason. The strict reader reconciles every attestation with the canonical diagnostics; missing, extra, stale and nonqualifying recovery blocks. Raw call IDs, arguments and paths remain private.

## Privacy, migration and validation

Public receipts expose closed enums, IDs, hashes and counts only. Private review packets and annotations are deleted after each case and their directory cleanup is a hard gate. The strict reader recomputes the candidate from admitted outcomes and receipts; the summary's claimed status is never authority. It cannot independently rejudge prose truth and reports the attestation origin explicitly.

Keyless tests cover exact old-plan bytes/digests and journal roundtrip, the three archived #295 JSONL hashes, stale/missing rubric and annotation bindings, compact citation positives and negatives, safe-line precedence, known and run-local group consistency, zero-finding and origin receipts, only the two permitted legacy overrides, and recovery absence/staleness/unsafe execution. Distinct high-entropy canaries in title/message, terminal summary, source, evidence path, local absolute path, reasoning continuation, private packet/annotation-only fields, exception text and fake credential must be absent from the complete public JSONL and strict-verifier output on both pass and failure paths. Only closed public values may appear. Run `npm run check`, focused framework tests and Linux x64 Native AOT validation. No live provider call is part of #311.

Open question for a future population: independent operator calibration of semantic judgments. This does not weaken deterministic authority checks or justify retroactive #295 changes.

## Historical evidence inventory

The retained public #295 JSONL files are immutable historical evidence. Each contains five case rows, an aggregate report and a summary. Their SHA-256 values are:

| Report | SHA-256 | Completed cases |
| --- | --- | ---: |
| `transport-blocked-370cfbd.jsonl` | `19ed7d1edda26c0760214a8b8a90a427db3bd9574c6d45401c59cae55ed5599e` | 0/5 |
| `live-868775.jsonl` | `760616d13f1bcc4ecb65c5797a91dc6af81983a1ee15c659c69e34cf49a4c24c` | 3/5 |
| `live-64k-04f72fd.jsonl` | `f7926cb69a462e07f26331c76ee582a5c018f7d4a63d73bcdf545fec448e391d` | 4/5 |

Their public rows retain case identities, counts and prior verdicts, but omit finding prose, citations and returned tool results. The separately retained #310 one-case display packet was available locally during this design. It shows a high-severity token logging finding citing `src/Upload.cs:5-7`, `rules/review.md:3-3` and `src/Upload.cs:1-7`. Frozen Upload line 5 forwards the access token to `Log`, line 6 writes it to stderr, and line 7 closes the class. The old exact-span scorer required `src/Upload.cs:5-5`, so it reported `ExpectedFindingMissing` even after the bounded recovered tool call completed. That display packet lacks authenticated returned tool results. It cannot construct an `EvaluationSubject`, and it was never a five-case population or independently semantically adjudicated. If the private packet is unavailable to another reviewer, its status is `unavailable`, not reconstructed from public data. For all these archived sources, `authenticated_subject_available=false` and prospective quality credit is refused.
