# Issue Publishing

Use this procedure when creating an issue or publishing an approved issue refinement.

Follow `docs/10_workflow/issue-workflow.md` for the normative type, title, body, and readiness rules.

## Preflight

Before writing to GitHub:

- confirm the repository and target issue, if updating;
- select exactly one native type: `Feature`, `Enhancement`, `Bug`, or `Task`;
- confirm the title has no type prefix;
- confirm the body has no parallel `Type:` metadata field;
- confirm any milestone, parent/sub-issue, dependency, or assignee changes are authorized.

## Create

Use a creation path that writes the native issue type in the initial request. Do not use `gh issue create` without its `--type` option.

If the installed GitHub CLI does not support `gh issue create --type`, use the REST endpoint:

```bash
gh api --method POST 'repos/{owner}/{repo}/issues' -f title='Describe the work' -F body=@issue-body.md -f type='Task'
```

Use the selected type's exact display name in the API request.

If another creation path is required, set the native type immediately afterward and do not report the issue as published until the update succeeds:

```bash
gh api --method PATCH 'repos/{owner}/{repo}/issues/NUMBER' -f type='Task'
```

## Additional Metadata

Apply requested milestone, parent/sub-issue, dependency, project, or assignee metadata through the appropriate GitHub fields or endpoints. These writes are independent of the native type and must be verified separately.

Do not create or mutate labels, milestones, Projects, or repository settings unless the task explicitly authorizes that operation.

## Verify

Read the remote issue after all writes:

```bash
gh api 'repos/{owner}/{repo}/issues/NUMBER' --jq '{number, title, type: .type.name, milestone: .milestone.title, body}'
```

Before reporting completion, verify:

- `.type.name` exactly matches the selected native type and is not `null`;
- the remote title exactly matches the approved title and has no type prefix;
- the remote body exactly matches the approved body and has no `Type:` field;
- requested milestone and relationship metadata is present;
- the rendered issue contains no raw prompts, logs, transcripts, credentials, or secrets.

If any check fails, repair the remote metadata and verify again.
