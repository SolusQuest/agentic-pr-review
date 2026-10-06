# APR Issue Publishing

Use shared [issue publishing](../../shared/skills/issue-publishing/SKILL.md) with [APR issue rules](../../10_workflow/issue-workflow.md). The shared skill owns preparation, scoped writes, reconciliation, and completion reporting; this supplement supplies GitHub client recipes. Substitute the verified repository, issue number, approved title/body file, and selected type in the examples.

## Typed Creation

When the installed CLI cannot create with `--type`, write the native field atomically through REST:

```bash
gh api --method POST 'repos/{owner}/{repo}/issues' -f title='Describe the work' -F body=@issue-body.md -f type='Task'
```

For the alternate creation path permitted by APR's issue rules, set the native field on the observed issue:

```bash
gh api --method PATCH 'repos/{owner}/{repo}/issues/NUMBER' -f type='Task'
```

## Scoped Updates

Read the target's current text and native metadata:

```bash
gh api 'repos/{owner}/{repo}/issues/NUMBER' --jq '{number, title, type: .type.name, milestone: .milestone.title, assignees: [.assignees[].login], body}'
```

For authorized substantive normalization under APR's issue rules, write the approved title, body, and native type together:

```bash
gh api --method PATCH 'repos/{owner}/{repo}/issues/NUMBER' -f title='Describe the work' -F body=@issue-body.md -f type='Task'
```

For a body-only correction, use a narrower PATCH that preserves the native type and all other fields:

```bash
gh api --method PATCH 'repos/{owner}/{repo}/issues/NUMBER' -F body=@issue-body.md
```

## Readback Capabilities

The REST representation exposes the native type as `.type.name`:

```bash
gh api 'repos/{owner}/{repo}/issues/NUMBER' --jq '{number, title, body, type: .type.name}'
```

For authorized relationship or metadata writes, use the corresponding supported fields or endpoints. A CLI exposing all these fields can read them together:

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

Apply the shared skill's readback and incomplete-result rules to every intended write, using [APR repository policy](../../10_workflow/repository-policy.md) for local operation restrictions.
