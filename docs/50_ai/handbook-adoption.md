# Solus Book Adoption

APR consumes [Solus Book](https://github.com/SolusQuest/solus-book) as the Git submodule at `docs/shared/`, pinned to `c3718d7d194e2c42b2c95ead4e8aab7f3ec1ff67`. The gitlink is the source identity used by this adoption. Shared text remains owned by Solus Book; APR owns the integration, local requirements, and timing of updates.

## Acquisition And Updates

For an APR revision containing this adoption, obtain the selected source during clone with `git clone --recurse-submodules https://github.com/SolusQuest/agentic-pr-review.git`, or initialize an existing checkout:

```bash
git submodule update --init --recursive
git submodule status
git -C docs/shared rev-parse HEAD
```

The resolved handbook commit must match the APR gitlink and adopted revision above. Initialization uses that selected commit, not the latest branch head. No sibling checkout, machine-specific path, or native symlink is required.

To adopt another revision, inspect its semantic and path changes, check out the selected full commit in `docs/shared/`, and update this record and the gitlink together. Verify affected local exceptions, resource paths, and workflows. Ordinary APR tasks do not edit or reformat the submodule. If a shared rule needs changing, handle it in its owning repository under the corresponding task authority.

## Entrypoints And Resource Resolution

Root [AGENTS.md](../../AGENTS.md) is APR's sole maintained repository entrypoint. It loads [APR context](agent-context.md), shared rules, and [task routing](../shared/agents/task-routing.md). APR uses document-based skill selection: read the selected canonical `SKILL.md` and the local additions listed below. This adoption does not install skills into a native automatic discovery directory.

| Work                          | Canonical shared procedure                                       | APR additions                                                                                                           |
| ----------------------------- | ---------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| Design refinement             | [Design refinement](../shared/skills/design-refinement/SKILL.md) | [Runtime design constraints](skills/runtime-design-refinement.md) and affected product contracts.                       |
| Issue refinement              | [Issue refinement](../shared/skills/issue-refinement/SKILL.md)   | [APR refinement context](skills/issue-refinement.md) and [issue rules](../10_workflow/issue-workflow.md).               |
| Issue publication             | [Issue publishing](../shared/skills/issue-publishing/SKILL.md)   | [APR client guidance](skills/issue-publishing.md) and native-field requirements.                                        |
| PR preparation or publication | [PR publishing](../shared/skills/pr-publishing/SKILL.md)         | [APR PR requirements](skills/pr-publishing.md), [workflow](../10_workflow/pr-workflow.md), and the repository template. |
| Validation                    | [Test validation](../shared/skills/test-validation/SKILL.md)     | [APR commands and environments](../10_workflow/validation.md).                                                          |

Resolve relative references from each file's location inside `docs/shared/`. Preserve access to its standards, templates, and other resources. A readable skill alone does not prove that its dependencies load. If the source or a required resource is missing or has a conflicting revision, use [loading failures](../shared/agents/context-model.md#loading-failures): reconcile the selected source within task authority, report the affected operation, and continue independent work covered by available applicable guidance. Do not silently substitute a sibling copy or another revision.

## Local Requirements And Exceptions

Compose the linked shared guidance with APR's owning documents. These local supplements contain project values and restrictions; they do not copy shared procedures or modify the handbook tree. This table is an ownership map, not another copy of the rules.

| Local owner                                                                                                        | APR supplement                                                                                                                                             |
| ------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [Repository policy](../10_workflow/repository-policy.md)                                                           | Merge, metadata, CI, event security, live-provider restrictions, and public-repository context.                                                            |
| [Code conventions](../00_project/conventions.md)                                                                   | Toolchain choices and text-marker restriction.                                                                                                             |
| [Validation](../10_workflow/validation.md)                                                                         | Actual commands, platform qualifications, and handbook formatting boundary.                                                                                |
| [Issue rules](../10_workflow/issue-workflow.md)                                                                    | Required native types, substantive normalization, and typed publication. [Client guidance](skills/issue-publishing.md) supplies executable GitHub recipes. |
| [PR rules](../10_workflow/pr-workflow.md)                                                                          | Repository template and links to local policy and validation.                                                                                              |
| [Release policy](../10_workflow/release-policy.md)                                                                 | Product release, deployment, compatibility, and closeout boundaries.                                                                                       |
| [Runtime design additions](skills/runtime-design-refinement.md)                                                    | APR design triggers, architecture constraints, and early-runtime non-goals.                                                                                |
| [Project context](../00_project/project-context.md) and [source-of-truth owners](../00_project/source-of-truth.md) | Product role, current/selected contracts, roadmap, and historical evidence.                                                                                |

## Semantic Deduplication

| Existing material                                | Disposition                                                                                                                                                                                                                                                |
| ------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `docs/00_project/source-of-truth.md`             | Shared durable-knowledge and tracker-context rules are referenced; APR current/target document owners remain local.                                                                                                                                        |
| `docs/00_project/conventions.md`                 | Shared content conventions are referenced; all existing code/toolchain choices remain local.                                                                                                                                                               |
| `docs/50_ai/collaboration-layers.md`             | Shared layer definitions are referenced; APR placement and harness routing remain local.                                                                                                                                                                   |
| Harness-specific root adapter                    | Removed in favor of the harness-neutral `AGENTS.md` entrypoint. Its active residual rules and preservation test are retired; historical runtime-removal scope remains recorded.                                                                            |
| `docs/50_ai/agent-context.md`                    | Shared routing and validation procedure are referenced; repository role, current-position statements, and R4 retirement marker are retained.                                                                                                               |
| Issue and PR workflow/skill documents            | Common content, readiness, preparation, reconciliation, and readback rules are referenced. APR native-type requirements stay in the issue workflow; GitHub recipes stay in its agent supplement; the PR workflow owns the repository-template requirement. |
| Repeated safety and validation instructions      | Local safety rules are consolidated in `docs/10_workflow/repository-policy.md`; entrypoints and workflow additions reference it. Shared document-consumer and evidence rules are referenced instead of copied.                                             |
| `docs/50_ai/skills/runtime-design-refinement.md` | Shared decision/output procedure is referenced; APR's trigger, architecture-default, and non-goal sections remain local.                                                                                                                                   |
| Validation instructions                          | Actual commands are consolidated in `docs/10_workflow/validation.md`. Obsolete R1-R3 distribution-check descriptions are replaced with the current check's meaning.                                                                                        |

The shared draft-PR default and bounded no-issue path are now explicit defaults when the current task leaves them open. No product contract, runtime support commitment, release authority, or historical acceptance changes. Existing local procedure paths with current consumers are retained as APR additions. Immutable R4 base inventory and replacement records are unchanged. The parent repository's residual-reference guard reads regular files rather than trying to read the submodule gitlink as a file.

## Verification

The following checks were completed on 2026-10-06 for this local working-tree integration, against APR baseline `8064da4a638fbd48a7b846a5b6f4acf89d932457` plus the proposed changes and the pinned handbook commit above.

| Check                             | Observed result                                                                                                                                                                                                                                                                                 |
| --------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Source acquisition                | The published handbook commit and local gitlink agree. A temporary checkout of the proposed APR tree cloned the submodule from its public URL and resolved the exact selected commit.                                                                                                           |
| Entrypoint and selected resources | The current Codex desktop session explicitly read APR's root entrypoint, shared task routing, test-validation skill, and its validation standard. All five skills' linked resources were readable. This verifies documented file-based selection; native automatic discovery is not configured. |
| Links and metadata                | All 305 internal Markdown links and anchors across the 45 affected APR/shared documents resolved. Five shared skills retained matching folder/name identity and nonempty metadata.                                                                                                              |
| Local semantics                   | The runtime-design trigger, architecture-default, and non-goal sections are unchanged from the baseline. The immutable R4 base inventory and replacement record remain byte-identical. Existing document paths required by the migration checks remain available.                               |
| Representative workflow           | The adopted test-validation route selected APR's actual commands and used an isolated current-content checkout after original-directory discovery was blocked. The six residual-reference tests, including the submodule boundary case, and six closed-migration inventory tests passed.        |
| Source pipeline                   | Locked dependencies were installed with `npm ci`. `npm run check -- -- --maxWorkers=2 --minWorkers=1` passed in the isolated checkout: formatting and TypeScript checks passed, with 1170 tests passed and 101 skipped in the Windows configuration.                                            |
| Distribution                      | `npm run dist:check` passed in both the original working directory and the isolated checkout. The checked generated bundle was unchanged.                                                                                                                                                       |
| Diff hygiene                      | `git diff --check` passed for the APR changes and staged submodule metadata. The handbook checkout remained clean.                                                                                                                                                                              |

The source pipeline used Node.js `v23.8.0` and npm `10.9.2`. Original-directory `npm run check` was blocked at formatting discovery by permissions on pre-existing local scratch content. Validation used a temporary Git fixture containing the proposed parent-owned files, exact working-tree bytes, the selected submodule, locked dependencies installed with `npm ci`, and APR's baseline history. It did not alter the original scratch directories or substitute a different implementation tree.

After the adapter retirement, an initial default-concurrency run hit one Git fixture test's five-second timeout. That file passed all 12 tests on an isolated retry; the full pipeline then passed with the worker limits recorded above. No timeout threshold or test selection was relaxed.

This record captures local pre-publication validation. Remote CI and independent-review results belong to the associated PR. These local checks do not establish new-session automatic instruction loading, native automatic skill discovery, Linux runtime/AOT qualification, live provider validation, or a release result.
