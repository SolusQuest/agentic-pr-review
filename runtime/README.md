# Deterministic Runtime CLI

This directory contains the C# runtime. Its supported deterministic CLI command is:

```text
review --input <path> --output <path> --trace <path>
```

The executable remains one project, with internal directories separated by responsibility:
`Cli` is process entry, `Application` owns orchestration and error mapping, `Protocol` owns
authoritative-schema validation and JSON models, `Execution` owns the deterministic executor
seam, and `Storage` owns staging and no-replace commits. The test project mirrors the
application and protocol areas without creating premature reusable libraries.

The production validator is `JsonSchema.Net` 9.2.2. The three authoritative V1 schemas are
embedded resources; runtime input and generated result/trace bytes are evaluated directly against
those schemas. The package does not mark its assembly `IsAotCompatible`, so the .NET AOT analyzer
emits IL3058. This is individually justified by the published Native AOT binary evaluating all
embedded schemas and completing the deterministic fixture path under recurring CI. Do not replace
this with a hand-written partial validator.

The runtime retains APR request values, readonly logical serialization, domain tools, grounded terminal validation and Host contracts. Generic execution, provider scheduling and runtime context belong to the selected managed Solus composition. APR's old AgentLoop, Minimal adapter and historical runner are retired. Historical reports and goldens keep their original domains; test-only authored response sources cannot act as production execution ports. See [native execution retirement](../docs/20_architecture/cr1-native-runtime-retirement.md).

The supporting proof remains `runtime/tests/AgentLoopAotFixture` plus `runtime/scripts/verify-agent-loop.sh`. It now exercises four fixed operations through actual APR snapshot tools and grounded terminal admission in a closed environment. Old execution-negative and retry CLI routes explicitly refuse with exit 2. Their historical manifests and goldens do not qualify a current runtime. Managed archive qualification separately owns actual Solus execution, context, capacity and provider behavior. The [abstraction decision](../docs/20_architecture/ai-abstraction-decision.md) records the historical comparison.

## Local validation

The public framework bootstrap and invariant regressions use the scripts below.
The full `all` route also requires actual managed archive qualification on the
selected Ubuntu 24.04 x64 runner with an already verified Solus package cache.
Ordinary PR/push CI calls explicit credential-free routes. The trusted exact-head
`solus-agent-package-proof.yml` workflow performs the private build and original
archive/Action qualification. See [validation](../docs/10_workflow/validation.md)
and [the qualification harness](tests/ActionHostTrustedProofVerifier/ManagedArchive/README.md).

```bash
# runtime tests only
bash runtime/scripts/verify-runtime.sh test

# framework-dependent bootstrap smoke only
bash runtime/scripts/verify-runtime.sh framework

# Native AOT bootstrap smoke only (requires clang and zlib1g-dev)
bash runtime/scripts/verify-runtime.sh aot

# tests, framework bootstrap, then actual managed archive qualification
bash runtime/scripts/verify-runtime.sh all
# equivalently, from the repo root:
npm run runtime:verify

# complete R2 proof: tests, full FDD corpus, actual managed archive, and inventory guard
bash runtime/scripts/verify-agent-loop.sh all

# individual R2 Agent proof modes
bash runtime/scripts/verify-agent-loop.sh framework
bash runtime/scripts/verify-agent-loop.sh managed
bash runtime/scripts/verify-agent-loop.sh aot

# Current R3 component regressions (Linux only)
bash runtime/scripts/verify-live-agent.sh deterministic

# Same credential-free component regressions and policy checks (Linux only)
bash runtime/scripts/verify-live-agent.sh live --dry-run
```

The R3 `deterministic` and `live --dry-run` routes execute the retained current
component suites through the complete nonempty native test runner, including
launcher safety, strict receipt protocols and current command rejection cases.
They require Linux so the actual supervisor SIGTERM and process-tree cleanup
check runs. The supervisor probe uses a fixed synthetic child and cannot produce
a passing provider receipt. `managed` executes full actual archive qualification.

Native R3 `aot`, actual `live`, `live --prepare` and `live --verify-prepared`
execution are retired. They exit nonzero with `APR_R3_MANAGED_REQUIRED` before
provider or state-key reads, activation or SDK work. The retained protected
workflow and its policy checks do not make these retired modes available. No
current native R3 live-provider or AOT qualification is claimed.

Current retirement responsibilities are tracked in the [native R3 entrypoint
inventory](../docs/50_ai/cr1-s5-native-r3-retirement.md).

The implementation does not create or configure GitHub environments. Before any future replacement live dispatch, a maintainer must independently read back the pre-existing `r3-live-proof` environment and verify all of the following: a built-in required-reviewer rule; a selected branches and tags deployment policy allowing only branch `main`, with no other branch pattern and no tag pattern; the environment-only `R3_LIVE_PROOF_DEEPSEEK_API_KEY`; no repository- or organization-level secret with that dedicated name; retirement of the former generic provider secret; and the intended DeepSeek account. `Protected branches only` is not an acceptable substitute for the exact `main`-only deployment policy. Until this readback passes and the workflow exists on merged default-branch lineage, real provider execution and issue completion remain blocked.

Result and trace outputs are compared to committed goldens by exact-byte `cmp`. Each smoke
subcommand allocates a fresh temporary work directory; the CLI's no-overwrite contract prevents
stale output from participating in comparisons.

## Continuous integration

Ordinary `pull_request` and `push` checks run the public FDD bootstrap, complete
R2/R3/Host invariant corpora, managed format/loader checks and trusted-live policy
dry-run through `.github/workflows/runtime-ci.yml`. The two current-source receipt
workers perform independent full FDD builds and mark their receipts
`archive_qualified: false`. Retained historical, R5 and R6 native gates keep their
existing ownership. Stable aggregates reject failed, skipped, cancelled and
missing workers.

Actual Solus archive qualification is mandatory in the separate reviewed-source
trusted workflow and cannot be inferred from a public regression result. It
builds two independent original archives and runs the literal generated Action
with isolated synthetic GitHub/provider services, no consumer SDK or feed
credentials, and bounded source/archive receipts. Native R3 provider execution remains retired; the protected workflow policy
is retained for any separately reviewed future replacement.
