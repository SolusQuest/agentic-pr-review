# Collaboration Layers

Use Solus Book's [context model](../shared/agents/context-model.md) for the three layers and canonical placement. APR's root [AGENTS.md](../../AGENTS.md) composes shared guidance with the local context below.

| Layer                                       | APR-owned content                                                                                                                                                                                                                         |
| ------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Human-and-agent project rules               | `docs/00_project/`, `docs/10_workflow/`, `docs/20_architecture/`, and `docs/90_roadmap/` own product role, toolchain, commands, architecture, safety, release policy, and sequencing.                                                     |
| Harness-neutral agent context and additions | [Agent context](agent-context.md), [handbook adoption](handbook-adoption.md), and `docs/50_ai/skills/` supply APR routing, design constraints, and client-specific issue operations. Common skill bodies remain in `docs/shared/skills/`. |
| Harness-specific entrypoints                | None maintained. Root [AGENTS.md](../../AGENTS.md) is the repository entrypoint.                                                                                                                                                          |

Shared versus local ownership is separate from these layers. Put APR requirements in their owning project document and link the applicable shared rule or skill. Resolve shared resources from the pinned source tree as described in [handbook adoption](handbook-adoption.md).

[Repository policy](../10_workflow/repository-policy.md) is the local safety owner. Workflow documents and agent additions reference it instead of maintaining separate rule lists.
