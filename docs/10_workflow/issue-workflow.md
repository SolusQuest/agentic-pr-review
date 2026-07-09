# Issue Workflow

Issues in this repository must be self-contained and actionable.

## Issue Forms And Types

Use GitHub native issue types for the broad work category:

- `Feature`: a new user, maintainer, action, runtime, or system capability.
- `Bug`: broken expected behavior.
- `Task`: planning, docs, research, spike, tooling, release, or maintenance work.

`Enhancement` is an issue form for improving an existing capability. It maps to the
`Feature` issue type unless the repository later creates a dedicated custom issue type.

Do not create separate `Spike`, `Chore`, `Docs`, or `Subtask` issue types. Use `Task` plus labels and parent/sub-issue relationships when useful.

## Labels

Use labels as stackable metadata. Recommended taxonomy:

- `area:action`: GitHub Action wrapper, inputs, outputs, event handling.
- `area:runtime`: runtime execution, provider orchestration, CLI bridge.
- `area:schema`: structured contracts, JSON schema, type validation.
- `area:publisher`: sticky comments, inline comments, duplicate suppression.
- `area:memory`: state artifacts, lineage, cross-workflow memory.
- `area:provider`: live model provider integration and provider config.
- `area:docs`: documentation and examples.
- `area:ci`: build, tests, packaging, workflows.
- `area:security`: token boundaries, secret handling, artifact privacy.
- `needs-design`: scope, architecture, or acceptance criteria need clarification.
- `needs-test`: validation coverage must be added before completion.
- `blocked`: work cannot proceed until a dependency or decision is resolved.
- `spike`: short research task to reduce a specific unknown.
- `agent:ready`: an agent can execute the issue without additional product or architecture decisions.
- `agent:needs-human`: a human decision is needed before execution.

Labels are recommendations until created in repository metadata. Do not mutate labels unless a task explicitly authorizes it.

## Agent Readiness

Apply `agent:ready` only when all are true:

- objective is clear;
- acceptance criteria are clear;
- relevant public docs or code paths are linked;
- no unresolved design questions remain;
- validation method is defined;
- scope is reasonable for one focused PR.

If an issue requires a public API decision, schema decision, security boundary decision, state model decision, or release policy decision, run design refinement first.
