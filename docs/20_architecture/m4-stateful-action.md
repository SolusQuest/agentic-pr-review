# M4 stateful review action

`runtime_backend=ledger-csharp` is default-off. It is not a general public action mode: production execution is accepted only from the repository's default-branch `workflow_run` workflow, and the separately named default-branch verification workflow is the only `workflow_dispatch` exception.

Both controlled workflows require `contents: write`, `pull-requests: write`, and `actions: read`. The production workflow serializes a repository/PR state key with `cancel-in-progress: true`; the verification workflow serializes its reserved PR and verification namespace without cancellation. Workflow cancellation reduces wasted work but does not replace the state-ref selector CAS.

The Git-data state ref is `agentic-pr-review-m4-state-v1`. It is a public, durable M4 record: every candidate, registration, marker, probe, and publication receipt committed to it is retained for the M4 lifetime. Do not pass secrets, provider request payloads, tokens, URLs, or unreviewed private content to this path.

The action derives target and state identity from verified GitHub context, checks the final PR tuple before candidate upload, and uses a separate M4 comment marker. Legacy sticky comments are neither read nor rewritten. A known accepted state can remain unpublished if a job is cancelled after selector acceptance; state rollback and crash-safe publication replay are deliberately outside this milestone.
