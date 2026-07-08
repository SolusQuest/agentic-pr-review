# Runtime Design Refinement

Use this procedure before implementing high-impact runtime or action architecture changes.

## Trigger This Procedure For

- action input or output changes;
- runtime provider selection changes;
- model backend configuration changes;
- JSON schema or protocol changes;
- C# runtime boundary changes;
- GitHub token or side-effect boundary changes;
- sticky state, lineage, or memory model changes;
- inline comment or publisher behavior changes;
- trace, artifact, or debug capture privacy changes;
- release, pinning, or runtime download policy changes.

## Output

Produce a short design decision summary before implementation:

- decision;
- recommended option;
- alternatives considered;
- rationale;
- security/privacy impact;
- migration impact;
- validation plan;
- open questions.

## Current Architectural Defaults

- TypeScript owns GitHub Action integration and deterministic publishing.
- The runtime core should not own GitHub side effects.
- GitHub token stays outside the runtime core.
- The model should produce structured proposed findings, not call write tools.
- Schema/protocol changes require contract tests.
- Deterministic fixtures should exist before live provider-only validation.

## Non-Goals For Early Runtime Work

- hosted GitHub App;
- GitLab or Azure DevOps support;
- Semantic Kernel or Microsoft Agent Framework dependency;
- deep Roslyn/MSBuild semantic analysis;
- dynamic latest-runtime downloads.
