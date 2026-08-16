# Runtime integration validation

`npm run runtime:integration` is the repository-owned retained CLI integration entry point. It publishes the framework-dependent C# runtime into a temporary directory outside the repository workspace, runs the deterministic `review` command directly, and verifies schema-valid Input/Result/Trace artifacts without recreating the removed TypeScript launcher.

On Linux the command also publishes the `linux-x64` Native AOT runtime and runs the same direct CLI smoke. On non-Linux it runs the portable framework subset and prints explicit `NON_PORTABLE_CASES_SKIPPED` and `AOT_SKIPPED_NON_LINUX` notices. Linux CI is the release-gating proof for Native AOT. The separate ActionHost framework verifier owns the real W2 wrapper, process lifecycle, cancellation, closed-environment, and privacy proof.

W3 removed the old `src/runtime-integration/runtime-integration.test.ts` harness and its dedicated `runtime/tests/IntegrationFixtures/` executable together with the two TypeScript launcher families. Their process and privacy assertions are now owned by W1/W2/E1 replacement evidence; no compatibility launcher or alternate ActionHost route remains.

R1 retired the checked-in old Action smoke together with the mixed public Action. Retained C# Host tests and E1 framework scenarios verify manifest review-input and target/review binding, sanitized artifact contents, trace and runtime-session non-persistence, failure publication barriers, and the child environment allowlist. R4 distribution tests separately validate the exact nested Action contract, generated-wrapper reproducibility, isolated no-`node_modules` execution, and private prepared-payload mismatch barrier; `runtime:integration` itself claims only the direct retained CLI smoke described above.

The normal state bundle contains host-owned manifest, structured result, and rendered review files. Deterministic runtime input, result, and trace files are not persisted in that bundle.
