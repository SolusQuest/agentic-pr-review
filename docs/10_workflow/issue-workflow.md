# Issue Workflow

Issues in this repository must be self-contained and actionable.

## Issue Types

The GitHub native issue type is the single source of truth for the broad work category:

- `Feature`: a new user, maintainer, action, runtime, or system capability.
- `Enhancement`: an improvement to an existing capability.
- `Bug`: broken expected behavior.
- `Task`: planning, docs, research, spike, tooling, release, or maintenance work.

Do not create separate `Spike`, `Chore`, `Docs`, or `Subtask` issue types. Use `Task` plus parent/sub-issue relationships when useful.

Do not repeat the type in the issue title or body:

- titles must not use `Feature:`, `Enhancement:`, `Bug:`, or `Task:` prefixes;
- bodies must not contain a parallel `Type:` metadata field.

An issue without a native type has incomplete metadata even when its title or body names a type.

This rule does not authorize a bulk migration of existing issues. Normalize an existing issue's title, body, and native type together the next time an authorized substantive update is made. Leave closed or historical issues unchanged unless a task explicitly authorizes their migration.

## Issue Body

Every issue should include:

- objective or goal;
- context;
- scope (in scope / out of scope);
- acceptance criteria;
- related docs, issues, or code paths.

Keep issue bodies self-contained. Do not paste raw task prompts, raw logs, transcripts, credentials, or secrets.

## Publishing And Verification

Set the native issue type when the issue is created. Issue forms must declare the matching `type`; agents and other API clients must write the native field explicitly.

Do not use `gh issue create` without its `--type` option. If the installed GitHub CLI does not support that option, use the REST endpoint so the issue is typed atomically:

```bash
gh api --method POST 'repos/{owner}/{repo}/issues' -f title='Describe the work' -F body=@issue-body.md -f type='Task'
```

If an issue is created through another path, set its type before reporting publication as complete:

```bash
gh api --method PATCH 'repos/{owner}/{repo}/issues/NUMBER' -f type='Task'
```

Read the published issue back and verify the native field explicitly:

```bash
gh api 'repos/{owner}/{repo}/issues/NUMBER' --jq '{title, type: .type.name}'
```

Publication is complete only when:

- `.type.name` exactly matches the selected native type;
- the title has no type prefix;
- the body has no parallel `Type:` field and matches the approved content;
- every requested and authorized milestone, parent/sub-issue, dependency, project, and assignee write exactly matches its remote readback.

If any requested field cannot be read back or does not match, publication is incomplete. Stop and report the missing permission, unsupported verification path, or mismatched remote state.

Agents should follow `docs/50_ai/skills/issue-publishing.md` for the complete publishing procedure.

## Agent Readiness

An issue is agent-ready when all are true:

- objective is clear;
- acceptance criteria are clear;
- relevant docs or code paths are linked;
- no unresolved design questions remain;
- validation method is defined;
- scope is reasonable for one focused PR.

If an issue requires a public API decision, schema decision, security boundary decision, state model decision, or release policy decision, run design refinement first (see `docs/50_ai/skills/runtime-design-refinement.md`).
