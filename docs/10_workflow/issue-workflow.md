# Issue Workflow

Issues in this repository must be self-contained and actionable.

## Issue Types

Use GitHub native issue types for the broad work category:

- `Feature`: a new user, maintainer, action, runtime, or system capability.
- `Enhancement`: an improvement to an existing capability.
- `Bug`: broken expected behavior.
- `Task`: planning, docs, research, spike, tooling, release, or maintenance work.

Do not create separate `Spike`, `Chore`, `Docs`, or `Subtask` issue types. Use `Task` plus parent/sub-issue relationships when useful.

## Issue Body

Every issue should include:

- objective or goal;
- context;
- scope (in scope / out of scope);
- acceptance criteria;
- related docs, issues, or code paths.

Keep issue bodies self-contained. Do not paste raw task prompts, raw logs, transcripts, credentials, or secrets.

## Agent Readiness

An issue is agent-ready when all are true:

- objective is clear;
- acceptance criteria are clear;
- relevant docs or code paths are linked;
- no unresolved design questions remain;
- validation method is defined;
- scope is reasonable for one focused PR.

If an issue requires a public API decision, schema decision, security boundary decision, state model decision, or release policy decision, run design refinement first (see `docs/50_ai/skills/runtime-design-refinement.md`).
