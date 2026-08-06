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

The runtime contains the selected project-minimal Agent product path under `Agent/Chat`, `Agent/Core`, `Agent/Loop`, `Agent/Session`, `Agent/Tools`, and `Host/State`. Issue #88 verifies the base path through two fresh executable invocations, and the R3 verifier extends it through the real DeepSeek request writer, response parser, and transport seam in framework-dependent Release and Linux x64 Native AOT modes. The first invocation requires a thinking-enabled tool round and grounded terminal result before accepting encrypted generation 0 state; the second authorizes, restores exact logical and provider continuation state, uses a prior-only fact, and accepts generation 1 with verified-ahead lineage. The superseded `review-live` single-request application and executor family are deleted; `review-live-agent-r3` remains a verification command, not a public Action surface.

The replacement proof is `runtime/tests/AgentLoopAotFixture` plus `runtime/scripts/verify-agent-loop.sh`. It uses the real runtime modules, a synthetic loopback provider oracle, closed child environments, exact public-safe goldens, and fail-closed transport, authorization, session, state, cancellation, and limit cases. It is not live-provider quality evidence or production GitHub Actions transport evidence. The earlier two-candidate comparison fixture and its test-only `Microsoft.Extensions.AI.Abstractions` dependency were deleted only after this replacement passed in both modes; the historical selection evidence remains recorded in [`docs/20_architecture/ai-abstraction-decision.md`](../docs/20_architecture/ai-abstraction-decision.md).

## Local validation

The framework-dependent and Native AOT bootstrap paths are exercised by a single Bash script
that CI also calls. It targets Linux (WSL and Linux containers are acceptable); native Windows
and macOS are not promised at M2.

```bash
# runtime tests only
bash runtime/scripts/verify-runtime.sh test

# framework-dependent bootstrap smoke only
bash runtime/scripts/verify-runtime.sh framework

# Native AOT bootstrap smoke only (requires clang and zlib1g-dev)
bash runtime/scripts/verify-runtime.sh aot

# all three, in order
bash runtime/scripts/verify-runtime.sh all
# equivalently, from the repo root:
npm run runtime:verify

# complete R2 Agent proof: focused tests, framework, Native AOT, and retired-surface guard
bash runtime/scripts/verify-agent-loop.sh all

# individual R2 Agent proof modes
bash runtime/scripts/verify-agent-loop.sh framework
bash runtime/scripts/verify-agent-loop.sh aot

# R3 live-Agent replacement proof modes
bash runtime/scripts/verify-live-agent.sh deterministic
bash runtime/scripts/verify-live-agent.sh aot

# secret-free trusted-live build, policy, and launcher dry-run (Linux only)
bash runtime/scripts/verify-live-agent.sh live --dry-run
```

The R3 `aot` mode is a secret-free Linux x64 replacement and retirement proof. It publishes the verifier once, binds the exact managed compiler input to the resulting native executable, and executes the same two-run corpus, continuation, negative, canary, grounding, freshness, retired-definition absence, and exact route/composition expectations as `deterministic`. It is not a released payload, production Host, trusted live-provider run, or public Action.

The `live` mode is not a contributor-facing provider command. `.github/workflows/r3-live-proof.yml` is its only credential-bearing caller: a no-input manual workflow on an exact merged `main` commit. The workflow proves repository, workflow, checkout, ancestry, clean-tree, and prepared-build identity before mapping the dedicated environment secret only to the final supervisor step. That step replaces its shell with one admitted Native AOT executable, so no adjacent managed assembly can replace the Agent, tools, SESSION, STATE, verifier, or provider composition after admission. The supervisor uses a dedicated private trusted profile over the existing production `R3LiveAgentTransportFactory`, clears the ambient credential, creates one in-memory 32-byte STATE key, and launches that same native executable in four serial fresh processes for must-find, must-not-find, continuation seed, and continuation restore. It emits one bounded JSON completion line only after child termination, unconditional sensitive-tree inspection and cleanup, and owned-key zeroing. The retired direct DeepSeek compatibility probe is not an alternate route.

The implementation does not create or configure GitHub environments. Before the first live dispatch, a maintainer must independently read back the pre-existing `r3-live-proof` environment and verify all of the following: a built-in required-reviewer rule; a selected branches and tags deployment policy allowing only branch `main`, with no other branch pattern and no tag pattern; the environment-only `R3_LIVE_PROOF_DEEPSEEK_API_KEY`; no repository- or organization-level secret with that dedicated name; retirement of the former generic provider secret; and the intended DeepSeek account. `Protected branches only` is not an acceptable substitute for the exact `main`-only deployment policy. Until this readback passes and the workflow exists on merged default-branch lineage, real provider execution and issue completion remain blocked.

Result and trace outputs are compared to committed goldens by exact-byte `cmp`. Each smoke
subcommand allocates a fresh temporary work directory; the CLI's no-overwrite contract prevents
stale output from participating in comparisons.

## Continuous integration

The direct runtime framework and `linux-x64` Native AOT paths, the real R2 Agent product proof, both R3 live-Agent replacement modes, and the secret-free trusted-live dry-run run on every `pull_request` and on `push` to `main` via `.github/workflows/runtime-ci.yml`. The workflow installs the SDK from `global.json` and calls the same scripts used locally, so CI and contributor validation cannot drift. Actual provider traffic exists only in the separately protected `R3 Trusted Live Proof` workflow after merge and maintainer authorization.
