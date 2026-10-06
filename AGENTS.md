# Agent Entry

This repository is public. Agents work from this repository, the current task prompt, and public context.

## Startup Reading Order

1. [APR context](docs/50_ai/agent-context.md), [repository policy](docs/10_workflow/repository-policy.md), and [local collaboration placement](docs/50_ai/collaboration-layers.md).
2. Shared [collaboration](docs/shared/standards/collaboration.md), [source of truth](docs/shared/standards/source-of-truth.md), and [conventions](docs/shared/standards/conventions.md).
3. [Task routing](docs/shared/agents/task-routing.md), the selected shared skill, and its APR additions under `docs/50_ai/skills/`.
4. Relevant project, workflow, architecture, or roadmap docs under `docs/`.

The shared source is the pinned Solus Book submodule at `docs/shared/`. Initialize it with `git submodule update --init --recursive`. [Handbook adoption](docs/50_ai/handbook-adoption.md) records the revision, resource resolution, local requirements, and verification. Follow its loading-failure guidance if shared content is unavailable. Solus Book's own `AGENTS.md` applies to maintaining that submodule; APR uses the shared rules linked above.

## Code Conventions

See [Code conventions](docs/00_project/conventions.md) for TypeScript, module, test, and formatting conventions.

## APR Requirements

[Repository policy](docs/10_workflow/repository-policy.md) owns APR's merge, metadata, CI, and live-provider restrictions.

## Validation

Follow [APR validation](docs/10_workflow/validation.md) for the required commands and environments.
