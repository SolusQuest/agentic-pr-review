# APR Repository Policy

Use the shared [collaboration rules](../shared/standards/collaboration.md) for scope, authority, task identity, and preservation of unrelated work. APR adds the following restrictions:

- Agents must not merge PRs.
- Repository settings, labels, milestones, Projects, branch protection, and secrets require explicit task authorization for that metadata operation.
- `pull_request` and `push` CI must run without provider secrets.
- Do not use `pull_request_target` without explicit security review.
- Use synthetic fixtures or test-only modes unless the task explicitly defines live provider validation. Authorized live validation also follows the owning phase's procedure.

This repository is public; apply the shared [information and destination rules](../shared/standards/conventions.md#information-and-destination) to its maintained content and publications. [Release policy](release-policy.md) owns APR's additional release, deployment, and closeout boundaries. [Validation](validation.md) owns commands and qualification requirements.
