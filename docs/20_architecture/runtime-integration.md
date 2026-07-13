# Runtime integration validation

`npm run runtime:integration` is the repository-owned cross-language integration entry point. It publishes the framework-dependent C# runtime and the test-only integration fixture into temporary directories outside the repository workspace, then runs the source-host integration suite through `src/main.ts:run()` and the real `invokeRuntime()` process boundary.

On Linux the command also publishes the `linux-x64` Native AOT runtime and runs the success/no-findings host smoke through the same protocol boundary. On non-Linux it runs the portable framework subset and prints explicit `NON_PORTABLE_CASES_SKIPPED` and `AOT_SKIPPED_NON_LINUX` notices. Linux CI is the release-gating proof for unsafe-file process semantics and AOT.

The integration fixture lives under `runtime/tests/IntegrationFixtures/` and is a standalone test-only .NET executable. It is not referenced by the production runtime project, is not included in production publish output, and is selected only by integration tests through the existing trusted command/prefix-argument boundary. It produces deterministic malformed output, exit, timeout, file-safety, privacy, and environment-probe scenarios; it does not define or replace protocol schemas.

The checked-in action smoke uses `action.yml` and `dist/index.js` with a real framework-dependent C# runtime and a local artifact store. It verifies public runtime outputs, the manifest review-input hash, trace hash, deterministic artifact layout, and trace non-persistence. No provider secret or real GitHub write is required. The child process receives only the runtime environment allowlist, which is checked separately by the integration fixture environment probe.

The normal state bundle contains host-owned manifest, structured result, and rendered review files. Deterministic runtime input, result, and trace files are not persisted in that bundle.
