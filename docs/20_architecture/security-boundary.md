# Security Boundary

The core safety rule is simple: the model and runtime may propose review findings, but deterministic publisher code performs side effects.

## GitHub Token Boundary

GitHub write credentials belong to the GitHub Action host and publisher layer.

The runtime core must not:

- receive `GITHUB_TOKEN`;
- read GitHub token environment variables;
- import GitHub API clients in the initial runtime design;
- post comments;
- update issues;
- apply labels;
- mutate repository settings;
- expose model-callable write tools.

## Provider Secret Boundary

Provider credentials must not appear in:

- repository files;
- PR bodies;
- comments;
- logs;
- normal artifacts;
- structured review output.

If a live provider is used, the workflow must define the trust boundary explicitly and avoid exposing secrets to untrusted code.

## Deterministic C# Host Bridge

The `runtime_backend=deterministic-csharp` path is default-off and receives only sanitized
`ReviewInputV1`. The C# runtime proposes typed review content; the TypeScript host owns phase,
repository facts, fingerprints, state identity, lineage, comments, artifacts, and action outputs.

The runtime command is workflow-owned configuration. `AGENTIC_REVIEW_RUNTIME_EXECUTABLE` and
complete absolute prefix arguments are realpath-checked and must resolve outside `GITHUB_WORKSPACE`;
relative arguments and opaque option forms such as `--option=/path` are not interpreted as paths.
Malformed command configuration fails closed before spawn. The deterministic path does not receive
GitHub or provider credentials, does not use `PATH` search or downloads, and does not publish inline
comments. Its local state bundle is sanitized before the sticky comment or artifact upload barrier.

State artifact restore is provenance-bound. The GitHub artifact store accepts only successful
runs from the same workflow and event, with the same head repository and requested pull request;
the restored manifest must be version 1, match the artifact run head SHA, target PR, and head
repository. Unknown manifest versions and unknown top-level fields fail closed. Explicit
`state_artifact_run_id` selection does not bypass these manifest checks. For pull-request targets,
incremental restore is allowed only on `pull_request` events; explicit selection also cannot bypass
the artifact's association with the requested PR. The state-producing workflow must not execute
untrusted pull-request code: it must check out a trusted/default or immutable ref, or use a separate
trusted workflow before writing state artifacts.

The host rechecks the pull request head immediately before publishing state. Downstream workflows
should also configure per-PR concurrency with cancellation to prevent stale writers from replacing
a newer review; the host-side check is a final stale-head barrier, not a replacement for workflow
concurrency.

Deterministic summaries omit runner-local bundle and result paths. Runtime versions are bounded
single-line metadata, and host warnings, diagnostics, authorization headers, token-shaped values,
and filesystem paths are sanitized before publication. If state upload fails after deterministic
runtime success, the action keeps the success outputs and writes a bounded partial-side-effect
summary identifying the failed state upload.

An identical incremental PR snapshot is a host short-circuit: it does not parse command settings,
construct runtime input, invoke the runtime, or validate a trace. It still constructs and sanitizes
the host-owned structured result and state bundle before upload.

## Artifact Boundary

Artifacts are useful for state, trace, and validation evidence, but they must not contain raw provider request or response bodies, secrets, auth headers, raw prompts, or unbounded tool results.

Restricted diagnostic capture must be opt-in, explicit, and documented.

## Canonical Session Ledger Artifacts

A future project-owned runtime maintains a canonical session ledger to resume review context across GitHub Actions runs (see `docs/20_architecture/architecture.md`). The ledger is a new class of durable artifact, distinct from both `ReviewTraceV1` and restricted raw diagnostics.

Canonical session ledger artifacts:

- are not raw provider request or response captures;
- are bounded, schema-versioned, sanitized logical session records required for runtime resume;
- may contain enough canonical logical content or content-addressed references to reconstruct the cacheable provider request prefix;
- must not contain auth headers, provider secrets, raw HTTP bodies, raw provider request/response bodies, raw prompts, private runner paths, unbounded tool output, or debug captures.
- must represent canonical logical content needed for prefix reconstruction in bounded, sanitized, schema-owned form; the ledger is not an archive of raw prompt text or raw provider messages.

Any content-addressed reference needed for prefix reconstruction must itself resolve to bounded, sanitized, durable content available across GitHub Actions runs.

If a prefix-relevant value cannot be represented safely in the ledger, it cannot be required for cross-action prefix reconstruction. This prevents the prefix-stability goal from forcing unsafe content into normal artifacts.

`ReviewTraceV1` remains evidence-only and does not carry ledger content. Restricted raw diagnostics remain a separate opt-in path and are not part of the ledger.
