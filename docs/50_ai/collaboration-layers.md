# Collaboration Layers

This repository uses a three-layer collaboration model.

## 1. Project Rules

Project rules are durable rules for humans and agents.

Locations:

- `docs/00_project/`
- `docs/10_workflow/`
- `docs/20_architecture/`
- `docs/90_roadmap/`

Use these docs for source-of-truth decisions about project role, workflow, architecture, security boundary, release policy, and roadmap direction.

## 2. Cross-Agent Procedures

Cross-agent procedures describe how agents perform recurring work without binding to one tool.

Location:

- `docs/50_ai/skills/`

Examples:

- issue refinement;
- PR publishing;
- runtime design refinement.

These files can reference project rules but should not duplicate entire project-rule documents.

## 3. Platform-Specific Entrypoints

Platform-specific entrypoints are thin.

Locations:

- `AGENTS.md`
- `CLAUDE.md`
- future tool-specific skill files, if needed.

Entrypoints should route agents to durable docs and include only platform-specific instructions, validation commands, or safety reminders.

## Placement Rule

- If humans and agents must both follow it, put it in project rules.
- If it is an agent workflow but tool-neutral, put it in `docs/50_ai/skills/`.
- If it is specific to one agent platform, keep it in a thin entrypoint.
