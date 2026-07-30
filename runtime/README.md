# Deterministic Runtime CLI

This directory contains the M2 C# runtime implemented by issue #19. Its only command is:

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

The runtime now contains the selected project-minimal Agent product path under `Agent/Chat`, `Agent/Core`, `Agent/Loop`, `Agent/Session`, `Agent/Tools`, and `Host/State`. Issue #88 verifies that path through two fresh executable invocations in framework-dependent Release and Linux x64 Native AOT modes. The first invocation requires a thinking-enabled tool round and grounded terminal result before accepting encrypted generation 0 state; the second authorizes, restores exact logical and provider continuation state, uses a prior-only fact, and accepts generation 1 with verified-ahead lineage.

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
```

Result and trace outputs are compared to committed goldens by exact-byte `cmp`. Each smoke
subcommand allocates a fresh temporary work directory; the CLI's no-overwrite contract prevents
stale output from participating in comparisons.

## Continuous integration

The direct runtime framework and `linux-x64` Native AOT paths and the real R2 Agent product proof run on every `pull_request` and on `push` to `main` via `.github/workflows/runtime-ci.yml`. The workflow installs the SDK from `global.json` and calls the same scripts used locally, so CI and contributor validation cannot drift.
