# Collaboration Layers

This repository uses a three-layer collaboration model. `AGENTS.md` is the shared root entrypoint that routes agents into the layers below; agent-specific entrypoints (layer 3) point back to it.

## 1. Project Rules

Rules that both humans and agents must follow, such as coding standards, workflow, architecture, and release policy.

Locations:

- `docs/00_project/`
- `docs/10_workflow/`
- `docs/20_architecture/`
- `docs/90_roadmap/`

Use these docs for source-of-truth decisions about project role, workflow, architecture, security boundary, release policy, and roadmap direction.

## 2. Shared Agent Skills And Context

Skills and context shared by all agents, tool-neutral.

Locations:

- `docs/50_ai/agent-context.md`
- `docs/50_ai/collaboration-layers.md`
- `docs/50_ai/skills/`

Examples:

- issue refinement;
- PR publishing;
- runtime design refinement.

These files can reference project rules but should not duplicate entire project-rule documents.

## 3. Agent-Specific Entrypoints

Thin, agent-specific entrypoints. Each points to the shared root entrypoint (`AGENTS.md`) and adds only that agent's specific rules.

Locations:

- `CLAUDE.md` (Claude Code)
- future per-agent directories such as `.codex/` or `.claude/` for agent-only skills

## Placement Rule

- If humans and agents must both follow it, put it in project rules (`docs/`).
- If it is a tool-neutral agent workflow or shared agent context, put it in `docs/50_ai/`.
- If it is specific to one agent tool, keep it in a thin agent-specific entrypoint (`CLAUDE.md`, `.codex/`, `.claude/`) that points to `AGENTS.md`.
