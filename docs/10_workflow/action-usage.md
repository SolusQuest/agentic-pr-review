# Managed Experimental Action Usage

This is the operator guide for the planned managed experiment. **No experimental release or supported downstream installation is available yet.** The checked Action map is null, so ordinary acquisition refuses execution. These instructions become usable only after the separately authorized publication supplies an exact version and Action commit. P9 owns inserting that published identity; V4 owns observed installation evidence. Documentation acceptance does not complete either gate.

For ongoing operation, see [state, upgrades and shutdown](r7-operation.md). The [Action interface](../20_architecture/action-interface.md#downstream-workflow-admission) owns admission and configuration; [distribution](../20_architecture/distribution.md) owns acquisition and runtime verification.

## Select The Immutable Release

The future version is `vMAJOR.MINOR.PATCH-internal.N`, with `N >= 1`; this is a placeholder pattern, not an available tag. `internal` describes experimental maturity, not private visibility. There is no stable API, accuracy, compatibility, maintenance-duration or service-level commitment, and no automatic default graduation.

Use the full 40-character released Action commit `T` supplied by the published release. The admitted templates require that SHA in the nested reference:

```text
SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@__ACTION_SOURCE_SHA__
```

Replace only `__ACTION_SOURCE_SHA__` with `T`. Do not paste a version tag, `main`, a development SHA, payload source `S`, builder commit `W`, or a shortened SHA into the template. The consumer version tag resolves to `T`; the distinct transport tag `payload-vMAJOR.MINOR.PATCH-internal.N` identifies payload source `S`. The reviewed map at `T` binds original archive bytes `B`, `S`, `W` and build/dependency identities. Published promotion does not rebuild those bytes. See the [release identity contract](../90_roadmap/r7-plan.md#exact-release-identities-and-trust).

## Prepare The Repository

The initial supported environment is GitHub.com Actions on GitHub-hosted `ubuntu-24.04` x64, with Node 24. The composite acquires and verifies its pinned runtime-only .NET 10 prerequisite and the full managed publish tree. An ambient SDK, a runtime found on `PATH`, an arbitrary download URL or a caller-prepared directory is not its authority. Consumers install no .NET SDK and supply no private package-feed credential. Self-hosted runners, other operating systems, complete local/offline review and other CI systems are outside this scope.

Before enabling review, the repository owner must authorize the repository content sent to the provider and provision the following secrets through their normal secret-management process. This guide performs no secret operation.

| Name                                   | Purpose                                                                                                              |
| -------------------------------------- | -------------------------------------------------------------------------------------------------------------------- |
| `GITHUB_TOKEN`                         | Existing Actions token; the admitted review job grants `contents: read`, `pull-requests: write` and `actions: write` |
| `DEEPSEEK_API_KEY`                     | Credential for the selected provider; pass only through `provider-api-key`                                           |
| `AGENTIC_PR_REVIEW_STATE_KEY`          | Independent base64 encoding of exactly 32 cryptographically random bytes                                             |
| `AGENTIC_PR_REVIEW_PREVIOUS_STATE_KEY` | Optional previous state key during the controlled rotation window                                                    |

The state key must not be derived from the repository, provider credential or GitHub token. Generate it outside the workflow with an appropriate secret-generation tool; for example, `openssl rand -base64 32` produces the required encoding. Store its output directly as the secret, without printing it in workflow logs, committing it, or forwarding it to the model. Losing the key can make retained state unavailable. See [rotation and loss](r7-operation.md#rotate-the-state-key).

On the default branch, create `.github/agentic-pr-review.json` and the referenced instructions file. A minimal complete configuration is:

```json
{
  "schema": "agentic-pr-review.config.v1",
  "instructionsPath": ".github/agentic-pr-review/instructions.md",
  "publication": { "mode": "sticky" }
}
```

Create `.github/agentic-pr-review/instructions.md` with the repository's review rules, for example: “Report concrete correctness defects supported by the changed code. Do not include credentials or unrelated private data.” Keep instructions within the [managed input limits](../../runtime/src/AgenticPrReview.Runtime/Agent/Loop/capacity.md#v3-managed-review-admission). The Host reads configuration and instructions from the immutable authorized workflow commit, not the reviewed checkout. Configuration edits are identity changes, not a way to alter an already authorized run.

## Install Exactly One Template

Choose one of the shipped alternatives and copy it to `.github/workflows/agentic-pr-review.yml` on the repository's default branch:

| Alternative                       | Source                                                                    | Trigger                                                                |
| --------------------------------- | ------------------------------------------------------------------------- | ---------------------------------------------------------------------- |
| Automatic plus manual maintenance | [automatic.yml.template](../../examples/workflows/automatic.yml.template) | Successful completed `pull_request` runs of `CI`, plus manual dispatch |
| Manual only                       | [manual.yml.template](../../examples/workflows/manual.yml.template)       | Manual dispatch only                                                   |

The automatic route requires the named CI workflow at `.github/workflows/ci.yml`. It is not a general listener for arbitrary workflow names or paths. The reviewed PR must be same-repository, open and nondraft; forks, drafts, closed PRs, stale or ambiguous CI associations and untrusted workflow sources are refused before state/provider/publication work. Manual dispatch additionally requires a write/admin actor and the authorized default-branch run identity.

Substitute only the released `T` SHA. Do not combine both alternatives, rename the installed path, add checkout/setup/build/output-logging steps, alter permissions, add an environment or change concurrency. The templates are exact compiled Host contracts, with CRLF normalization allowed. Both retain the repository/PR concurrency group and `cancel-in-progress: false`; the workflow grants no permissions at its root and no `id-token: write`. Custom YAML is rejected rather than treated as a supported variant.

After the separately published release is available and the trusted files are installed, use the installed workflow's manual dispatch with the eligible `pr-number` and `state-mode: auto` for a first controlled review, or let the automatic alternative observe eligible CI. Check the workflow conclusion, job summary, annotations and sticky review comment before considering the run successful. This is a future operator procedure, not a claim that an installation has been observed here.

## Inputs And Defaults

The composite forwards seven raw strings without product defaults. C# owns parsing and admission; an empty known input is absent, not permission to bypass a required trusted-route credential. Nonempty values are not silently trimmed.

| Input                | Ordinary admitted behavior                                                                                             |
| -------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| `github-token`       | The template supplies `GITHUB_TOKEN`; required for the trusted GitHub/state/publication route                          |
| `provider-api-key`   | The template supplies the selected provider secret; no caller-selectable provider or endpoint                          |
| `state-key`          | Current independent state key; malformed or missing required key fails before state/provider admission                 |
| `previous-state-key` | Absent by default; read-only old-key support during authorized rotation                                                |
| `config-path`        | C# default `.github/agentic-pr-review.json`; H1 fixes this exact path and refuses substitutions                        |
| `pr-number`          | Required positive PR number for manual dispatch; absent on the automatic route, which uses its verified CI association |
| `state-mode`         | C# default `auto`; explicit `reset` only through the authorized manual route and matching event input                  |

Omitting the optional configuration `review` object selects the current managed defaults. Smaller explicit integer limits are honored or visibly refused, never clamped upward. See the [configuration field ranges](../20_architecture/action-interface.md#trusted-configuration).

The selected provider/model binding is DeepSeek / `deepseek-flash`; neither is an input or repository configuration override. The selected `cr1-solus-internal4-dev` profile uses five exact Solus `0.1.0-internal.4` packages, context format 2 / compatibility 1, at most six current logical calls, physical attempts and work units, six tool invocations, three tools per batch, concurrency one and 120 seconds of current runtime execution. Independent uncached/cached input allowances are 65,536 each, with a separate total-input ceiling of 65,536; output allowance is 8,192. Retained history has separate 64-attempt/64-record, 32-input and 196,608-variable-byte limits, plus independent logical and HTTP wire bounds. Current counters do not renew retained capacity. Full limits and reservation semantics belong to the [capacity owner](../../runtime/src/AgenticPrReview.Runtime/Agent/Loop/capacity.md#v3-managed-review-admission).

A numerically valid small budget need not permit a provider send: output allowance below 1,024 or combined input allowance below 8,192 cannot fund the first reservation. The 120-second runtime limit is not a promise that acquisition, Host preparation, reconciliation and publication all finish within 120 seconds. Historical 64/128-call native profiles are not this managed profile.

## Observe The Result

Use the existing workflow conclusion and job summary, canonical annotations and sticky publication for operator decisions. A process that never finalized can leave outputs unavailable; a later Host failure can retain accounting and `review_completed` while the overall invocation still fails. Successful model termination alone does not prove state acceptance or publication success.

The `review` step exposes these fourteen fields through the composite metadata. This inventory explains their meaning; adding a step to print or export them changes the admitted template and is not a supported customization.

| Output                                      | Meaning                                                                                   |
| ------------------------------------------- | ----------------------------------------------------------------------------------------- |
| `status`                                    | Validated final Host status, or the wrapper's fixed failure status                        |
| `termination-reason`                        | Closed reason such as `review_completed`, `not_started`, a limit, cancellation or failure |
| `model-calls`                               | Current invocation's observed logical calls                                               |
| `provider-attempts`                         | Observed physical provider sends                                                          |
| `provider-retries`                          | Additional physical sends, not a promise retries are enabled                              |
| `provider-failed-attempts`                  | Sends without successful finalization                                                     |
| `provider-unknown-usage-attempts`           | Sends missing observed usage                                                              |
| `provider-unknown-cache-partition-attempts` | Sends without a known cache partition                                                     |
| `attempt-accounting-completeness`           | Independent `complete`, `partial` or `unavailable` attempt observation                    |
| `usage-completeness`                        | Independent `complete`, `partial` or `unavailable` usage observation                      |
| `input-tokens`                              | Known current input usage                                                                 |
| `input-cache-hit-tokens`                    | Known cached input partition                                                              |
| `input-cache-miss-tokens`                   | Known uncached input partition                                                            |
| `output-tokens`                             | Known current output usage                                                                |

Numerical outputs are exact nonnegative decimal strings when known. Empty/unavailable is not zero; partial observations are lower bounds. A complete zero with `not_started` is different from an unobserved attempt. Never infer cache partitions by subtraction or treat observed counters as billing, quality or cost guarantees. Continuation does not add prior-run usage to current-run outputs. See [accounting semantics](../20_architecture/action-interface.md#current-run-provider-accounting).

## Evidence And Legacy Scope

Source checks and synthetic fixtures establish only their tested contracts. [CR2 acceptance](../20_architecture/cr2-technical-acceptance.md) and [managed archive qualification](../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/README.md) retain exact-source execution evidence; a documentation head does not acquire a new exact-byte receipt by inheriting that evidence. Published identity, public acquisition and actual downstream installation remain separately qualified claims.

The immutable `v0.1.0` tag is historical and unmaintained. The Claude Code CLI path has been removed from current source; legacy Claude/M4 state is not migrated into the managed experiment. Do not use an old tag or a private proof workflow as a workaround for the unbound ordinary map.
