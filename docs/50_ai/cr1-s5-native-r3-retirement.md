# CR1-S5 native R3 entrypoint retirement

This inventory describes the current executable boundary. Earlier R3 receipts,
goldens, manifests and design handoffs retain their historical meaning. They do
not qualify current Solus execution. Actual archive qualification remains owned
by the full managed archive harness.

| Previous entry or responsibility                                                                          | Current disposition                                                                                                         | Executable owner or evidence                                                                                                                               |
| --------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Shell `deterministic`                                                                                     | Runs real current component suites on Linux, with complete nonempty test results required                                   | Existing `scripts/run-native-tests.mjs`; current application, credential, quality, capability, receipt, launcher and supervisor tests                      |
| Shell `live --dry-run`                                                                                    | Runs the same credential-free current component suites and policy checks                                                    | Same runner; no provider activation                                                                                                                        |
| Shell `managed`                                                                                           | Full actual archive qualification                                                                                           | Existing `ManagedArchive/verify-archive.mjs`, unchanged delegation                                                                                         |
| Shell `aot`, `live`, `live --prepare`, `live --verify-prepared`                                           | Native SESSION execution retired; fixed nonzero `APR_R3_MANAGED_REQUIRED` before helpers or credential reads                | Actual shell counterfactual tests, exact preamble and whole-source policy pin                                                                              |
| Fixture `historical-direct`, ordinary phase verbs and every `negative-*` verb                             | Native SESSION execution retired; fixed nonzero boundary before argument materialization or credentials                     | `Program.Main` guard; production command rejection and current managed proof retain their own responsibilities                                             |
| Fixture `live-supervise`, `trusted-*`                                                                     | Native provider execution retired at the same early boundary                                                                | No profile activation, provider read, SDK work or passing receipt                                                                                          |
| `RunPhaseAsync`, `RunNegativePhaseAsync`, `RunTrustedPhaseAsync`, `RunReplacementWriteFailureAsync`       | Old execution methods and their private tuple/canary/tree helpers removed                                                   | Historical receipts stay readable; no old SESSION execution replacement is claimed                                                                         |
| Native verifier profiles and `VerifierCommitObserver`                                                     | Private activation, delegation timing and quality-at-commit callback retired                                                | Current managed qualification and postacceptance failure proof retain their separate evidence; no combined callback is invented                            |
| Supervisor old four-phase launcher                                                                        | Old native phase execution removed                                                                                          | Shared cancellation, signal registration, process launcher, bounded capture, inspection, zeroing, cleanup and completion logic remain                      |
| Supervisor SIGTERM lifecycle                                                                              | Actual fixed synthetic self-child and grandchild, through the shared supervisor core                                        | `launcher-supervisor-probe`; Linux test really signals supervisor, checks child termination and sensitive-root removal; cannot pass a provider receipt     |
| Supervisor environment capture and sanitized failure completion                                           | Controlled unit invocation only; fixed synthetic inputs with environment restoration                                        | `SupervisorEmitsOneSanitizedFailureRecord`, both original cases plus credential-clear assertions                                                           |
| Materializer rejection and private failure marker                                                         | Actual materializer rejects invalid corpus; actual marker writer and codec preserve closed category and CreateNew semantics | `MaterializerFailureAndPrivateMarkerRetainClosedCategories`, `PrivateFailureMarkerUsesCreateNewSemantics`; old CLI joint timing retired                    |
| Historical receipt, observation sidecar, ABI, secret launcher, typed chat and continuation-only predicate | Retained component responsibilities                                                                                         | Existing strict protocol/forgery tests, proof-contract capability test, launcher tests; continuation predicate moved byte-for-byte out of removed observer |
| Fixture `aggregate` and `architecture`                                                                    | Readonly historical protocol consumers retained                                                                             | Existing parsers, evidence validation and architecture readers; no current execution qualification implied                                                 |
| Generic AgentLoop AOT proof and protected workflow policy                                                 | Independent responsibilities retained                                                                                       | Owning scripts and workflow definitions unchanged; no event, permission, environment or secret metadata changes                                            |

The complete component route requires Linux. A macOS return from a Linux-only
signal test is not evidence that Linux process termination passed. Current
component success is not live-provider, SDK acceptance or archive qualification.

The supervisor test driver drains stdout and stderr concurrently and separately
bounds process exit and pipe closure. Fixed self-process fixtures cover normal
and delayed closure, a parent that exits while a descendant retains its pipes,
and a parent that remains alive. Timeout observations precede cleanup; cleanup
only reaps processes created by that test and cannot satisfy the original
SIGTERM, child-termination, sensitive-root or single-completion assertions.

The shared sensitive-file scan opens Unix entries with the existing nonblocking,
nofollow descriptor primitive and checks regular-file identity before reading.
It reads at most the original 16 MiB bound from that same handle, rejects a short
read or changed length, and clears its read buffer in `finally`. FIFOs, links and
other unscannable entries remain unsafe; they are never skipped as clean. The
original safety classification and subsequent sensitive-root deletion order
remain. Controlled subprocess tests reproduce the old FIFO wait and require the
new bounded refusal, while physical-file controls preserve canary, size and
original-descriptor checks. Linux SIGTERM success still requires every original
assertion before failure cleanup.
