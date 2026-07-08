# Distribution Direction

The current project is a JavaScript GitHub Action.

Future runtime work may introduce a separately built C# runtime binary. This document records the intended distribution constraints before implementation.

## Action Distribution

The action should be pinned by release tag or full commit SHA. The bundled action entrypoint must be reproducible from source.

If `dist/` changes, run:

```bash
npm run dist:check
```

## Future Runtime Binary

If a C# runtime CLI is introduced, Native AOT distribution is attractive because it can provide:

- no .NET SDK requirement for downstream runners;
- fast startup;
- self-contained execution;
- exact release assets;
- clear checksum verification.

Initial binary support may start with `linux-x64` if needed. Broader platform support can follow once the contract is stable.

## Version Mapping

Do not download an implicit latest runtime. The default runtime version should map to the action version, with explicit override inputs only for advanced use cases.

Potential future release assets:

- `agentic-pr-review-runtime-vX.Y.Z-linux-x64.tar.gz`
- `agentic-pr-review-runtime-vX.Y.Z-linux-arm64.tar.gz`
- `agentic-pr-review-runtime-vX.Y.Z-win-x64.zip`
- `agentic-pr-review-runtime-vX.Y.Z-osx-x64.tar.gz`
- `agentic-pr-review-runtime-vX.Y.Z-osx-arm64.tar.gz`
- `SHA256SUMS`
