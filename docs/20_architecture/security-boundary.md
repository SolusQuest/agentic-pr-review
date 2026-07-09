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
