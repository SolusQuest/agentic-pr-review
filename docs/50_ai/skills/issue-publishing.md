# Issue Publishing

Use this procedure when creating an issue or publishing an approved issue refinement.

Follow `docs/10_workflow/issue-workflow.md` for the normative type, title, body, and readiness rules.

## Preflight

Before writing to GitHub:

- confirm the repository and read the current target issue, if updating;
- select exactly one native type: `Feature`, `Enhancement`, `Bug`, or `Task`;
- confirm the title has no type prefix;
- confirm the body has no parallel `Type:` metadata field;
- list every requested metadata write and confirm that milestone, parent/sub-issue, dependency, project, or assignee changes are authorized.

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

## Update

Update the existing issue in place; do not create a replacement issue for an approved refinement.

Read the target first and confirm its repository, number, title, body, native type, milestone, and assignees:

```bash
gh api 'repos/{owner}/{repo}/issues/NUMBER' --jq '{number, title, type: .type.name, milestone: .milestone.title, assignees: [.assignees[].login], body}'
```

Write the approved title, body, and native type to that same issue in one request:

```bash
gh api --method PATCH 'repos/{owner}/{repo}/issues/NUMBER' -f title='Describe the work' -F body=@issue-body.md -f type='Task'
```

Do not include milestone, assignees, labels, or other fields in this PATCH unless the task explicitly authorizes those changes. Apply authorized additional metadata through its dedicated field or endpoint.

## Additional Metadata

Apply requested milestone, parent/sub-issue, dependency, project, or assignee metadata through the appropriate GitHub fields or endpoints. These writes are independent of the native type and must be verified separately.

Do not create or mutate labels, milestones, Projects, or repository settings unless the task explicitly authorizes that operation.

## Verify

With a GitHub CLI version that supports the fields, read the complete issue metadata after all writes:

```bash
gh issue view NUMBER --repo OWNER/REPO --json number,title,body,issueType,milestone,parent,subIssues,blockedBy,blocking,assignees,projectItems
```

If the installed CLI does not expose one of these fields, read it through the corresponding REST or GraphQL path instead. Do not omit a requested field from verification.

| Metadata                                       | Preferred JSON field                                   | Older-CLI fallback                                                                                                                  |
| ---------------------------------------------- | ------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------- |
| title, body, native type, milestone, assignees | `title`, `body`, `issueType`, `milestone`, `assignees` | `GET repos/{owner}/{repo}/issues/NUMBER`                                                                                            |
| parent                                         | `parent`                                               | `GET repos/{owner}/{repo}/issues/NUMBER/parent`                                                                                     |
| sub-issues                                     | `subIssues`                                            | `GET repos/{owner}/{repo}/issues/NUMBER/sub_issues`                                                                                 |
| dependencies                                   | `blockedBy`, `blocking`                                | `GET repos/{owner}/{repo}/issues/NUMBER/dependencies/blocked_by` and `GET repos/{owner}/{repo}/issues/NUMBER/dependencies/blocking` |
| Projects                                       | `projectItems`                                         | GraphQL `Issue.projectItems` query                                                                                                  |

Before reporting completion, verify:

- the remote native type exactly matches the selected type and is not `null`;
- the remote title exactly matches the approved title and has no type prefix;
- the remote body exactly matches the approved body and has no `Type:` field;
- every requested and authorized milestone, parent/sub-issue, dependency, project, and assignee write exactly matches the remote value;
- the rendered issue contains no raw prompts, logs, transcripts, credentials, or secrets.

If any requested field cannot be read back, remains `null`, is silently dropped, or otherwise does not match, stop and report publication as incomplete. Name the missing permission, unsupported verification path, or mismatched remote state. Retry only after authorization or external state changes; do not loop on the same failed write.
