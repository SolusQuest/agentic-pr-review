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

The runtime now also contains the selected internal project-minimal Agent chat seam under
`Agent/Chat`. It is not yet the production Agent loop or provider transport. The seam was selected
by the reproducible two-candidate decision in
[`docs/20_architecture/ai-abstraction-decision.md`](../docs/20_architecture/ai-abstraction-decision.md);
the comparison fixture compiles the exact same selected source rather than a copied implementation.
The comparison gate also verifies checked-in neutral adapter/API manifests, parses isolated
`project.assets.json` files, rejects non-zero candidate warnings, and classifies every byte
comparison failure without printing raw build logs or private absolute paths.

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

# reproduce both AI-abstraction candidates
bash runtime/scripts/verify-ai-abstraction.sh all

# execute the exact selected production source in framework and Native AOT modes
bash runtime/scripts/verify-ai-abstraction.sh selected-production
```

Result and trace outputs are compared to committed goldens by exact-byte `cmp`. Each smoke
subcommand allocates a fresh temporary work directory; the CLI's no-overwrite contract prevents
stale output from participating in comparisons.

## Continuous integration

The direct runtime framework and `linux-x64` Native AOT paths, the two-candidate AI-abstraction
comparison, and the selected-production parity path run on every `pull_request` and on `push` to
`main` via `.github/workflows/runtime-ci.yml`. The workflow installs the SDK from `global.json`
and calls the same scripts used locally, so CI and contributor validation cannot drift.
