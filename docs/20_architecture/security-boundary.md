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

## Artifact Boundary

Artifacts are useful for state, trace, and validation evidence, but they must not contain raw provider request or response bodies, secrets, auth headers, raw prompts, or unbounded tool results.

Restricted diagnostic capture must be opt-in, explicit, and documented.

## Canonical Session Ledger Artifacts

A future project-owned runtime maintains a canonical session ledger to resume review context across GitHub Actions runs (see `docs/20_architecture/architecture.md`). The ledger is a new class of durable artifact, distinct from both `ReviewTraceV1` and restricted raw diagnostics.

Canonical session ledger artifacts:

- are not raw provider request or response captures;
- are bounded, schema-versioned, sanitized logical session records required for runtime resume;
- may contain enough canonical logical content or content-addressed references to reconstruct the cacheable provider request prefix;
- must not contain auth headers, provider secrets, raw HTTP bodies, private runner paths, unbounded tool output, or debug captures.

Any content-addressed reference needed for prefix reconstruction must itself resolve to bounded, sanitized, durable content available across GitHub Actions runs.

If a prefix-relevant value cannot be represented safely in the ledger, it cannot be required for cross-action prefix reconstruction. This prevents the prefix-stability goal from forcing unsafe content into normal artifacts.

`ReviewTraceV1` remains evidence-only and does not carry ledger content. Restricted raw diagnostics remain a separate opt-in path and are not part of the ledger.
