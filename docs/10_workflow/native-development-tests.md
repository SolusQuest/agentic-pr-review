# Native focused development tests

Run relevant Node and .NET regressions on macOS before waiting for hosted Linux CI. This is local development support; the release platform and production qualification remain Ubuntu x64.

Install the repository's Node dependencies with `npm ci` and the .NET SDK selected by `global.json`. The entrypoint uses the current checkout and rebuilds .NET references by default:

```bash
npm run test:native
npm run test:native:node
npm run test:native:runtime
```

`node` selects `src/` and `tests/`. `runtime` selects output/trace application, reviewed snapshot/tools/materialization, restricted state, replay admission, evidence and synthetic rate-bucket tests. Both retain the underlying runners' failures. Select an individual file or .NET filter when working on a narrower change:

```bash
npm run test:native -- node src/action-wrapper/artifact-bridge/staging.test.ts
npm run test:native -- node src/action-wrapper/launcher/bridge-runtime.test.ts
npm run test:native -- runtime --filter 'FullyQualifiedName~LocalRestrictedStateStoreTests'
npm run test:native -- runtime --filter 'FullyQualifiedName~R5ReplayAdmissionTests'
npm run test:native -- runtime --filter 'FullyQualifiedName~NativeFileOperationsTests'
```

Runner arguments follow the suite name. An explicit .NET `--filter` replaces the default focused filter. Use `--no-build --no-restore` only after a successful build of the same source, dependencies and configuration. An empty .NET selection is an error. Runtime runs retain TRX results under ignored `.local/native-tests/` and print their actual runner counts. Vitest prints its own case/file counts and rejects empty selection.

The entrypoint gives test children a physical temporary root. On Darwin it resolves system `/tmp` to `/private/tmp`, avoiding `/var` aliases and the long per-user temporary paths that can exceed Unix socket limits. This changes the child process environment only. Product admission still rejects arbitrary symlinks, noncanonical roots, unsafe entry types and replacement identities. Direct `dotnet test` and `npm test` inherit the caller's original temporary path.

Replay children retain an exact environment key set. Darwin's CoreFoundation adds or normalizes `__CF_USER_TEXT_ENCODING` at startup; the fixture supplies a fixed input and admits that platform key while checking that the caller's value and credential canaries do not leak. This does not add ambient environment inheritance.

VSTest, loopback HTTP fixtures and Unix socket fixtures need local socket permission. In an execution sandbox, `listen`/`bind` returning `EPERM` or VSTest failing before discovery is blocked execution. Run the same synthetic test in an environment allowing those sockets to distinguish permissions from an assertion failure. Do not skip the socket assertion or infer that macOS lacks the mechanism.

## Diagnostic baseline and remaining boundaries

The unchanged baseline `07fe42b2d81eee8d20b401433a21838041ad22d6` was measured on Darwin arm64, .NET SDK 10.0.109, Node 24.21.0 and APFS:

| Selection/environment                                               | Passed | Failed | Skipped | Qualification                                                                   |
| ------------------------------------------------------------------- | -----: | -----: | ------: | ------------------------------------------------------------------------------- |
| Complete .NET project with socket permission                        |  3,599 |  1,041 |       1 | Missing native implementations and dependent failures; 4,641 cases              |
| Complete Node suite in the restricted sandbox                       |  1,427 |    195 |     202 | Also 76 unhandled errors, including socket `EPERM`                              |
| Same Node suite with socket permission                              |  1,506 |    116 |     202 | One unhandled error remained                                                    |
| Same Node suite with socket permission and physical short temp root |  1,607 |     15 |     202 | Remaining failures/error belong to the unsupported enrollment recovery endpoint |

These are baseline runner counts, not current success claims. Existing tests that return before assertions on unsupported platforms can be reported as passed by xUnit; existing Vitest platform guards report skipped cases. Actual Darwin filesystem evidence comes from the platform-positive and hostile tests, together with the relevant producer/consumer paths. The PR records post-change selections, counts and remaining failures for its actual revision.

The unfiltered commands retain unsupported-platform diagnostics:

```bash
npm run test:native -- node-all
npm run test:native -- runtime-all
```

| Boundary                                                                 | Required environment or disposition                                                                                                                                                                                                                                                   |
| ------------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `scripts/execute-r4-trusted-proof-enrollment.test.ts` recovery lease     | Its existing implementation selects Linux abstract sockets or Windows named pipes and rejects Darwin. It remains in unfiltered `npm test` and hosted CI; the focused Node profile selects product/distribution tests. Portable script tests can still be selected explicitly.         |
| Solus invocation exclusion and its composition tests                     | Linux `statx`, guarded invocation directories and production ownership remain Linux-only. Native local storage/replay support does not qualify this launch path.                                                                                                                      |
| `/proc/self/fd` launch, namespaces and `strace` audits                   | Retained Linux qualification; Darwin uses its own descriptor operations for local filesystem work.                                                                                                                                                                                    |
| Native AOT and full framework/AOT proof scripts                          | Retained Linux qualification and parity; focused native tests do not establish archive or loading proof.                                                                                                                                                                              |
| Managed archive execution, loader authority and installed-package proofs | Complete hosted Ubuntu x64 CI remains required.                                                                                                                                                                                                                                       |
| Sanitized apphost launch fixtures                                        | A framework-dependent apphost needs a registered/default .NET installation after its child environment is cleared. A custom SDK visible only through the parent environment can produce exit 131 before fixture assertions; focused filesystem tests do not qualify that launch path. |
| Filesystem behavior                                                      | Darwin atomic no-replace and hostile controls were exercised on APFS. Unsupported exclusive rename fails closed; there is no copy/delete or check-then-move fallback. Other volumes and Intel execution need their own evidence.                                                      |

Do not replace [`npm run check`, `npm run dist:check` or runtime qualification](validation.md) with the focused commands. Full `npm run check` on Darwin still encounters the enrollment endpoint boundary above; record that failure and use the required successful hosted Linux checks. Keep all retained CI aggregates and security negatives. Local success grants no macOS distribution, release, live-provider or deployment authority.
