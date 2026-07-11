# Distribution Direction

The current project is a JavaScript GitHub Action with a selected C# runtime direction.

Runtime work will introduce a separately built C# runtime binary. Native AOT is the intended production distribution form; this document records the constraints before implementation.

## Action Distribution

The action should be pinned by release tag or full commit SHA. The bundled action entrypoint must be reproducible from source.

If `dist/` changes, run:

```bash
npm run dist:check
```

## C# Runtime Binary

Native AOT distribution is selected because it can provide:

- no .NET SDK requirement for downstream runners;
- fast startup;
- self-contained execution;
- exact release assets;
- clear checksum verification.

Both the framework-dependent and `linux-x64` Native AOT deterministic runtime paths are validated continuously by [`.github/workflows/runtime-ci.yml`](../../.github/workflows/runtime-ci.yml), which publishes the AOT binary and executes it against the bootstrap protocol fixture on every `pull_request` and on `push` to `main`. This proves the selected dependencies, JSON handling, and CLI entrypoint keep publishing and executing under AOT; it is not yet a production release commitment.

Production binary support starts later with a pinned, checksummed `linux-x64` asset. Broader platform support can follow once the contract and provider behavior are stable.

## Version Mapping

Do not download an implicit latest runtime. The default runtime version should map to the action version, with explicit override inputs only for advanced use cases.

Potential future release assets:

- `agentic-pr-review-runtime-vX.Y.Z-linux-x64.tar.gz`
- `agentic-pr-review-runtime-vX.Y.Z-linux-arm64.tar.gz`
- `agentic-pr-review-runtime-vX.Y.Z-win-x64.zip`
- `agentic-pr-review-runtime-vX.Y.Z-osx-x64.tar.gz`
- `agentic-pr-review-runtime-vX.Y.Z-osx-arm64.tar.gz`
- `SHA256SUMS`
