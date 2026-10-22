# CR2 T2 SDK upload capability

[T2 #450](https://github.com/SolusQuest/agentic-pr-review/issues/450) replaces T1's transitional upload request with the closed `apr.sdk.upload.cr2` capability in the [CLI/Action contract](./cr2-cli-action-contract.md). C# owns physical naming, the unchanged six-field S2 carrier, retention-day selection and independent REST verification. Node owns confined copying and one official SDK upload. Neither an SDK receipt nor local process termination establishes remote store success.

## SDK lifetime

The pinned `@actions/artifact` 6.2.1 upload promise can reject while its ZIP source continues reading. Synthetic maximum-carrier probes observed continued reads after rejection on Linux and macOS. Consequently, promise settlement cannot release the operation copy or restore output.

The accepted implementation amendment uses one short-lived Node subprocess per upload. The parent starts its own Node executable with the inventoried, build-bound `dist/sdk-upload.js`, fixed arguments and an explicit environment. Only the Actions runtime token, Results URL and admitted server/proxy settings reach it. Ambient Node injection is rejected; REST, provider and state credentials are excluded. Standard input/output/error are OS-discarded. A bounded private descriptor carries only the closed request and scalar result.

The owner separately tracks possible dispatch, validated receipt, process exit and complete channel closure. A clean exit with a valid receipt may return provisional success. Crash, missing IPC, forced termination or observation timeout remains an unknown mutation. A kill request never releases ownership. Unconfirmed termination retains the copy and blocks admission and presentation; a later confirmed close permits safe cleanup without changing the reported outcome. Confirmed termination permits subsequent requests that C# authorizes through its existing reconciliation journal. Business cancellation alone does not seal the capability.

## Confinement and interoperability

The C# caller sends one length-prefixed frame and shuts down only the write half of its Unix connection. Node waits for EOF before dispatch, rejecting trailing bytes and additional frames. Both sides enforce closed JSON, exact identity and bounded canonical scalar values.

Node walks the held staging root using descriptor-relative no-follow opens. Source directories accept owner-only `0500` sealing or `0700`; the Node-owned root and operation directory require `0700`. Regular source files must have the current owner, one link, private mode, exact size and hash. Node verifies an exclusive copy before launching the SDK and keeps that copy until local termination is confirmed. This preserves the trusted same-UID runner assumption rather than claiming physical immutability against that principal.

The SDK receives exactly `artifact-envelope.json`, its private operation directory, compression level zero and requested retention days. C# verifies the returned archive identity, exact physical name, encrypted digest and size, producer run/attempt and actual expiry through its existing REST owner before reporting committed success. No APR SDK replay, overwrite or new recovery algorithm is introduced.

## Retained fixture inventory

| Surface                                                                                                                              | Disposition and current consumer                                                                                                                                                         |
| ------------------------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `upload-protocol`, `upload-framing`, `upload-server`, `upload-operations`, `upload-staging`, `sdk-process-owner`, `sdk-upload-child` | Current ordinary/protected Node upload closure; no state interpretation or REST client.                                                                                                  |
| `SdkUploadClient`, `ArtifactUploadConnection`, `ArtifactUploadLimits`, `ArtifactTransportFormat`                                     | Current C# wire, transport bounds and carrier producer/reader.                                                                                                                           |
| `historical-upload-*.fixture.ts`                                                                                                     | T1's transitional broad request and provisional reply, retained only for named old protocol/lifecycle tests and the historical fixture runtime.                                          |
| `historical-staging`, `historical-transport-envelope`, `historical-physical-name`                                                    | Explicit historical helpers used by synthetic and capacity fixtures. The actual SDK interoperability fixture also uses the retained S2 reader to independently check original ZIP bytes. |
| `historical-*.fixture.ts` six-operation REST/server/store closure                                                                    | Existing historical framework, capacity and evaluation comparison paths; excluded from ordinary/protected production bundles.                                                            |
| `SharedHostPorts/HistoricalArtifactBridgeContracts`, `HistoricalArtifactBridgeJsonContext`, `HistoricalPrivateArtifactBridgeClient`  | Linked only into the named test/verifier projects alongside the historical store; removed from the production runtime assembly.                                                          |

Frozen historical records retain their original bytes. Finite source-successor maps account for changed imports, the moved client and the three additional linked fixture files. Updating those maps establishes source identity only; their execution checks remain necessary. The production factory has no historical selector or fallback.

## Evidence boundaries

Focused tests cover the actual C# Unix caller, pinned SDK subprocess and synthetic Twirp/blob service, original ZIP bytes at the maximum carrier, independent REST verification, lost acknowledgement, malformed replies, no replay, confinement, process/channel lifetime and cleanup failure. Actual .NET tests also append trailing bytes or a second frame and verify zero SDK dispatch before a subsequent valid request succeeds.

Local emulated Linux runs are development evidence. Native Linux CI, protected supporting execution, original private managed-archive qualification and hosted cross-run interoperability remain distinct gates. T2 does not claim a release, bound public installation, successful original archive qualification or live-provider result from these fixtures.
