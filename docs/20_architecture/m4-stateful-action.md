# M4 stateful review action (historical)

> **Retirement note (R4-W6):** The Git-data state route, including
> `agentic-pr-review-m4-state-v1`, is retired. The detailed state/ref mechanics
> below are historical evidence, not a current state, selector, or receipt route.
>
> **Rebaseline note (2026-07-24):** This document records the completed M4
> implementation baseline. It is useful migration evidence, but its current
> TypeScript orchestration and one-shot provider runtime are not the selected
> end state. The replacement sequence and ownership boundaries are defined in
> [Agent Runtime Architecture Rebaseline](./agent-runtime-rebaseline.md).
> The former public Git-data state ref was suitable only for sanitized M4
> records. It must not carry plaintext Agent reasoning, repository tool
> results, or provider continuation artifacts under the target architecture.
> R1 issue #79 retired the checked-in Action and M4 workflow entrypoints; all
> invocation and workflow details below describe the pre-removal M4 baseline.

`runtime_backend=ledger-csharp` is default-off. It is not a general public action mode: production execution is accepted only from the repository's default-branch `workflow_run` workflow, and the separately named default-branch verification workflow is the only `workflow_dispatch` exception.

## Invocation and authority boundary

The controlled workflow must provide `GITHUB_TOKEN` to the checked-in action. The action rejects missing configuration before it attempts target resolution, but it initializes the complete M4 output surface first so callers never need to interpret absent outputs.

Production accepts only a successful `pull_request` run of `.github/workflows/m4-untrusted-analysis.yml`. It validates the default-branch workflow ref and Git ref of the trusted control-plane job, then binds the triggering run to exactly one current, initially-open pull request: repository, head repository, pull-request number, head SHA, base SHA, and base ref must all match. The run's workflow ID is resolved through the Actions API and must resolve to that same untrusted workflow path. A trusted workflow is therefore a control-plane consumer of untrusted analysis output; it never treats an arbitrary `workflow_run` payload as authority.

`workflow_dispatch` is verification-only. It requires a repository administrator and the exact pull request number configured in `AGENTIC_REVIEW_M4_RESERVED_VERIFICATION_PR`; it writes to a distinct verification namespace and cannot read or mutate production state.

Both controlled workflows require `contents: write`, `pull-requests: write`, and `actions: read`. The production workflow serializes a repository/PR state key with `cancel-in-progress: true`; the verification workflow serializes its reserved PR and verification namespace without cancellation. Workflow cancellation reduces wasted work but does not replace the state-ref selector CAS.

## Durable state and acceptance

The former Git-data state ref was `agentic-pr-review-m4-state-v1`. It was a public, durable M4 record: every candidate, registration, marker, probe, and publication receipt committed to it was retained for the M4 lifetime. Do not pass secrets, provider request payloads, tokens, URLs, or unreviewed private content to any current state route.

Initialization writes and rereads a sentinel through the same retry budget as selector updates. A pre-existing partial M4 namespace is rejected rather than silently repaired. Recursive Git trees are accepted only for explicit tree entries; blobs are validated by their exact M4 path and registrations cannot hide transport failures as malformed optional data. Candidate upload is successful only after an exact readback of all three candidate files.

Selection snapshots the selector revision before the ancestry comparison and proves the same revision is still current during selection. It validates the counter and index control-plane files before a runtime is invoked. A changed selector is retried from a fresh snapshot; a normal default-branch advance is not a provenance failure for an otherwise valid accepted state.

Acceptance uses selector CAS, then derives publication from the accepted selector revision. Unknown CAS or comment outcomes are reported as unknown, not converted to success. A publication receipt is never written while publication is pending. If a receipt write is ambiguous, the durable acceptance result remains visible in outputs and the job fails for human investigation instead of claiming a completed receipt.

## Runtime, cancellation, outputs, and observability

The action derives target and state identity from verified GitHub context, checks the final PR tuple before candidate upload, and uses a separate M4 comment marker. It passes host termination through an `AbortSignal` to runtime execution and acceptance. If work fails before ownership is transferred to the acceptance transaction, the runtime lease is released. Legacy sticky comments are neither read nor rewritten.

All M4 job results expose frozen fields for the state key, phase, transition, candidate and marker IDs, selector revision, acceptance, publication, receipt, cleanup warnings, and an enumerated `state_error_kind`. After a durable acceptance, later publication or receipt failure preserves those fields rather than replacing them with arbitrary error text. The action also writes a dedicated M4 ledger job summary. A known accepted state can remain unpublished if a job is cancelled after selector acceptance; state rollback and crash-safe publication replay are deliberately outside this milestone.
