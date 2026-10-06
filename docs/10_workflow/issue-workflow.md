# Issue Workflow

Use the shared [issue workflow](../shared/standards/issue-workflow.md) for content, readiness, tracker hygiene, and readback, and the shared [issue-refinement skill](../shared/skills/issue-refinement/SKILL.md) for the procedure. APR adds the native types and publication requirements below.

## Issue Types

The GitHub native issue type is the single source of truth for the broad work category:

- `Feature`: a new user, maintainer, action, runtime, or system capability.
- `Enhancement`: an improvement to an existing capability.
- `Bug`: broken expected behavior.
- `Task`: planning, docs, research, spike, tooling, release, or maintenance work.

Do not create separate `Spike`, `Chore`, `Docs`, or `Subtask` issue types. Use `Task` plus parent/sub-issue relationships when useful.

An issue without a native type has incomplete metadata even when its title or body names a type.

This rule does not authorize a bulk migration of existing issues. Normalize an existing issue's title, body, and native type together the next time an authorized substantive update is made. Leave closed or historical issues unchanged unless a task explicitly authorizes their migration.

## Publishing And Verification

Set the native issue type when the issue is created. Issue forms must declare the matching `type`; agents and other API clients must write the native field explicitly.

Do not use `gh issue create` without its `--type` option. Prefer the atomic REST recipe in [APR client guidance](../50_ai/skills/issue-publishing.md) when the installed CLI cannot write it at creation. If another creation path is required, set the native type immediately afterward and verify it before reporting publication as complete. The remote `.type.name` must exactly match the selected native type.

Use the shared [issue-publishing skill](../shared/skills/issue-publishing/SKILL.md) with that client guidance and [APR repository policy](repository-policy.md).

## APR Refinement Context

[APR validation](validation.md) supplies actual commands; product contracts supply acceptance. Use [APR refinement context](../50_ai/skills/issue-refinement.md) and [runtime design additions](../50_ai/skills/runtime-design-refinement.md) for local design triggers and architecture constraints.
