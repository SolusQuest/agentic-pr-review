# Managed archive qualification prerequisites

The `network-prerequisite` mode of `solus-agent-package-proof.yml` runs only the
supporting Ubuntu 24.04 x64 network/TLS and package-independent transaction
selection checks. It does not restore private packages, execute a Solus archive,
or establish archive qualification. The reviewed-source guard still binds the
workflow, checkout and requested commit. Only the complete `verify` route is
eligible for qualification evidence.

`node runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs`
builds a public .NET probe, verifies the same pinned .NET 10.0.13 runtime used by
the managed launcher, then runs it without an SDK in private Linux mount and
network namespaces. The unprivileged child has no capabilities, external routes
or inherited credentials. Private hosts and CA mounts cover `api.github.com`,
`codeload.github.com` `api.deepseek.com` and the synthetic archive host `apr-d5.blob.core.windows.net`. All four require successful HTTPS;
wrong-host and untrusted-root certificates must fail TLS validation. The test
also checks that the caller's hosts, trust bundle and namespaces remain intact.

The bounded `APR_D5_NETWORK_PREREQUISITE` receipt identifies this supporting
proof, source, probe and runtime. It cannot substitute for original archive
identity, generated Action execution, useful fresh/restored work, failure and
loading scenarios, or the independently built archive receipts. The transaction
selection tests use synthetic records; installed-package execution and capture
require their separate actual-archive evidence.

The separate `generated-action-tail` mode uses the same reviewed-source and
private-cache guards, verifies startup/lock metadata with the existing `verify`
preparation, then runs the current generated Action and all remaining proof
entries. It deliberately skips this dual-original-archive harness and the already
separate inventory/staging/loader steps. Its receipts retain their own source
identities; earlier original archive evidence is never renamed for a new head.
The other three workflow modes keep their existing selections.

`verify-archive.mjs` constructs two independent original D1 archives from the
selected source and verified build cache. For each role it runs the literal
checked Action bundle, adjacent synthetic release map, unchanged D4 observer,
ordinary managed acquisition and framework-dependent launcher in the isolated
consumer. Neither the entry assembly nor launch frame is substituted. Official
SDK uploads retain their original ZIP bytes and producing run identities. The current C# artifact REST client uses the private HTTPS API service with Bearer authorization and redirects to the separate anonymous synthetic blob host. Both services use the original SDK archive store. The HTTP/token route remains only for the pinned historical producer; no production auth or redirect rule is relaxed.

The external GitHub service serves actual blob/tree hashes and nonempty gzip/tar
source through the authenticated API redirect to anonymous codeload. Three
successive source heads exercise fresh and restored work with grounded findings;
same-head recovery must avoid provider work. Failure, cancellation, stale-head,
wrong-key, reset refusal and hostile loading cases require no sticky writes.
Missing usage, malformed final JSON and truncated tool-call responses execute
through the same original archive and must refuse acceptance. The known-commit
crash barrier holds readback only after the current invocation has written;
recovery must perform no provider work or duplicate sticky write.
Ambient CLR loading variables must not reach the actual child. Per-invocation
owner cleanup precedes every presentation. Only bounded counts, closed outcomes,
source and archive identities leave the temporary consumer. A failure is a failed
qualification; fixture unit tests alone do not establish this proof.

H1 adds six cases to this same original-archive gate: each exact downstream
installation alternative bootstraps, continues usefully at a new PR head and
performs an explicit manual reset. The automatic alternative starts through
`workflow_run`, then continues through `workflow_dispatch` under the same
immutable workflow source. The service supplies the CI run and commit/PR
association, default configuration and instructions; the literal generated
Action still provisions its verified runtime with no consumer SDK. Fourteen
outputs and their summary projection retain the existing checks. Each
alternative starts a separate controlled installation scope; its accepted
store is preserved between bootstrap, continuation and reset. Changing the
template does not migrate another workflow identity's state.

The historical V1 workflow and unmodified old SESSION producer retain their
original isolated scope and full negative/reset matrix. The Host's closed path
selector admits each exact template only at its own fixed path; candidate V2
authority remains separately injected. The complete gate now requires 41 cases
per independently built archive. Source tests exercise the new service routes,
but only a successful exact-source Ubuntu `verify` run establishes actual
managed execution of these cases.

Each bounded receipt binds its workflow run/attempt, original archive size/hash,
inventory digest, entry/deps/runtimeconfig hashes, dependency closure, source,
Action bundle and runner. The source and Action projection identities remain
distinct; no receipt relabels a supporting diagnostic as qualification.

The preparation probe is a separate supporting diagnostic. It compares pure
schema construction with JSON reflection disabled/enabled and, only after a
failed fresh archive case, repeats read-only Host preparation. It cannot run
Agent, state or publication work. Every probe line says `archive_qualified=false`;
it never substitutes its assembly, launch command or settings into the generated
Action. The actual archive retains the production build settings and must still
pass independently.

S5's genuine old-SESSION negative input is seeded by the unmodified historical
producer at commit `eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2`, tree
`484c40c6aae3087428fb5169a18685eb744aa590`. The builder uses an isolated private
temporary source directory and records exact source and assembly identities;
it removes that directory after publishing the separate supporting producer.
A shallow checkout may acquire only that exact public pin into a temporary
object database. No historical writer is compiled into the maintained current
source or installed archive. The original producer must accept the actual
controlled launch and authority unchanged; a failure blocks qualification.
Current-source input generation, current managed archive refusal/reset/retry,
claim checks and state inventory assertions remain mandatory. The historical
producer's receipt explicitly cannot qualify current package execution.

Each isolated archive role has a fixed 4,200-second outer hard cap. The network
prerequisite retains its separate 90-second cap. The complete dual-archive
workflow step has a 180-minute backstop.
Immediately before starting that timeout, the trusted role owner generates one
absolute monotonic deadline and passes its origin/deadline through isolated argv.
The consumer validates it once, before product work; missing, malformed, expired
or future-origin deadlines fail. Setup and every later invocation consume that
same origin. No case, product environment or input can renew it. Each invocation
uses only the remaining role time minus settlement/diagnostic reserve, rather
than imposing a fixed whole-Host limit derived from one logical operation.

The conservative reserve is 52 seconds: the existing 30-second official SDK
quiescence allowance plus the existing 5-second Host post-kill close allowance,
2 seconds for T2 upload-socket drain, then 5 seconds for fixture process-group close, 5 seconds for audit/summary/output
reads, and 5 seconds for fixture services to close. Host close and SDK settlement
and the preceding upload-socket drain are conservatively summed rather than assumed to overlap. The three audit reads
run concurrently within one 5-second bound; both fixture services close
concurrently within one further 5-second bound. The fresh case additionally
reserves 90 seconds for its two existing, sequential 45-second preparation probes
on failure. These sums reserve capacity; they do not certify that bridge drain,
guard release or resource disposal will finish. Unconfirmed release remains a
failure. Insufficient remaining reserve refuses another invocation.

The case watchdog kills the detached process group, then requires an actual
close/stdio completion within 5 seconds. Watchdog expiry is a failed proof even
if close is confirmed. Unconfirmed close, bounded observation failure, or service
close failure cannot produce a successful qualification. The outer PID namespace
hard kill remains the final backstop; it also fails qualification. No product
Host deadline, cancellation/reconciliation window, REST pacing, quota, reserve
or success assertion changes. This suite measures only its actual scenarios
inside the 4,200-second role budget; it does not qualify the Host's full
28-minute capacity. Failure diagnostics contain only closed operation counts
and process/watchdog outcomes, never payloads, credentials or private paths.

Failure-only operation traces are bounded to 4 KiB and contain per-case start/
elapsed times, closed operation counters, twelve tail observations and the last
completed mutation, SDK finalize and read observation. Object aliases correlate
SDK and HTTPS activity without publishing opaque IDs, names, URLs or payloads.
A row records sequence, elapsed milliseconds, operation, object alias, HTTP
status, service duration milliseconds and `finished`/`closed`. These are external
service observations; they do not assert C# readback or budget semantics.

Supporting subprocesses retain their existing individual limits and also consume
the remaining role allowance minus the same 52-second reserve. No supporting
invocation renews the role. Each ordinary case emits one bounded, closed timing
record on success or failure: role, ordinal, elapsed milliseconds, total role
elapsed milliseconds, observed close and watchdog outcome. These records contain
no paths, case inputs or acceptance claim. Each original role retains all 23
ordinary cases and their assertions, then adds 12 CR2 qualification cases and
six H1 downstream cases against the same original archive bytes. The timing parser retains a closed 41-case
ceiling; counts alone confer no acceptance.

## CR2 original-archive assertion disposition

The first V1 execution at source `9f52bc365b148bc05f6ec259bf5d616be17c503a`
(run `39963403079`) failed the existing presentation/Host-close assertion during
acceptance cancellation. A real Node 24 Unix-socket reproduction exposed an
unhandled `EPIPE`: the upload writer removed its error listener from the `end`
callback before the queued error event. Product source
`e96876af96255e54f1097fe19d4fd51ef23ddb55` observes writable completion through
`finished`, preserving the read half and propagating a fixed write failure.
Its real subprocess regressions cover peer disconnect, write error, close before
finish and success. No global exception handler, protocol, deadline or state
outcome rule changes. Both affected generated bundles have new identities;
the prior `23d5`/`b47` Runner evidence remains historical. New original-archive
and actual Runner proof must execute the corrected installed bytes.

The `apr-cr2-v1-managed-archive-v2` receipt is the current harness observation. The predecessor `apr-d5-managed-archive-v1` assertions are inventoried at source `f45b6c09a166eb0f4c1683d93d1766c607c3f4b9`; naming that harness baseline does not assert that an archive was qualified at that source. Historical passing receipts keep their original source/run/archive identities. A new exact-source run is required for the changed observer and failure cases.

| Obligation                                                                       | Retained assertion or demonstrated successor                                                                                                                                                                                                                                                                                                                                                                                             |
| -------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Fresh, same-head recovery, second/third restored heads                           | All original status, grounded source/tools, complete prefix, accounting and no duplicate sticky-write assertions remain. New authenticated chronology checks cover fresh and both restored heads.                                                                                                                                                                                                                                        |
| Selected-current incompatibility and reset                                       | The immutable historical SESSION producer, auto refusal, unauthorized reset, authorized fresh reset and same-reset recovery remain. `ResetInventoryProof` and its negative mutations are unchanged.                                                                                                                                                                                                                                      |
| Provider failure, missing usage, malformed Final, truncated tools and stale head | Original no-publication and incurred-work checks remain; the current C# output contract and authenticated absence of successor acceptance add specific refusal evidence. Unknown provider usage is distinct from the supporting CLI command-usage negatives.                                                                                                                                                                             |
| Provider cancellation                                                            | The real Node owner PID is now signalled, replacing the outer-fixture signal target. Actual Host forwarding/close, every SDK child close, cleanup and canary checks remain required.                                                                                                                                                                                                                                                     |
| Known publication commit/crash recovery                                          | Original after-write/before-readback SIGKILL and zero-provider/no-duplicate recovery remain. New before-write refusal and wrong publication-readback cases separately require reached barriers and no successor acceptance.                                                                                                                                                                                                              |
| Acceptance interruption and SDK ambiguity                                        | New acceptance-family refusal, exact current-head acceptance finalize/response-loss crash and cancellation cases use original SDK ZIPs. Current-head selection uses authenticated production codecs, so a predecessor retention copy cannot satisfy the fault. Response close and SDK process close precede fixture reset. Independent recovery requires zero provider work, no repeated sticky write and unchanged committed ZIP bytes. |
| Whole-tree and candidate handoff integrity                                       | Original non-entry substitution, unsupported runtime, hostile CLR environment, wrong key/corrupt encrypted artifact, bad redirect and wrong source archive checks remain. New between-step payload/runtime/record/map/asset mutations must be applied and must reject before actual Host launch; accepted state and publication remain unchanged.                                                                                        |
| Identity and confidentiality                                                     | Original member/closure/build/runtime/source/environment identities remain. All eight checked assets, the separate synthetic map digest and the `cr1-solus-internal4-dev`/context `2/1` identity are explicit. New private observations never become raw public evidence.                                                                                                                                                                |

`TransactionInventoryProof` is a separately compiled supporting reader. After the original Host closes, its `derive` operation performs only current trusted-source preparation and derives names through production authorization/scope/locator code. `decode` authenticates original carrier ZIPs and encrypted records using production codecs; it performs no Host preparation, provider or storage work and can select an exact receipt while its SDK response is held. Neither operation executes a product review or substitutes bytes in the archive. Original ZIPs are retained privately when finalized so later legitimate deletion cannot erase the phase evidence. Missing/unreached barriers, wrong classes, changed source state and wrong ordering fail the oracle's negative controls.

HTTP body delivery alone is an external observation. The original C# Host's subsequent transaction progress, authenticated candidate/claim/intent/acceptance bindings and the negative barriers establish the scoped order. A missing SDK reply never establishes rollback. Unconfirmed child/stream close fails proof; retaining uncertain resources is not reported as successful cleanup.

The literal checked code assets and the adjacent synthetic release map have separate hashes. The ordinary checked map remains null. Synthetic Action, workflow and builder projections are not public release provenance. A1's historical actual Runner evidence applies only to byte-identical installed assets and its recorded Runner scenarios, never as a new-source archive receipt. H1/H2 real-service interoperability and remaining R7 qualification remain separate. `verify-runtime.sh all` and `runtime:integration` execute in the owning guarded hosted `verify` route; `generated-action-tail` still omits the dual original archive. R5/R6/R7 managed consumers retain their separate proof results.

After the isolated PID namespace has observably exited, the trusted owner runs fixture teardown in a detached child with a 30-second limit and a further 5-second close bound. It requires the exact successful teardown response before publishing a qualification receipt or starting the second archive role. Unknown namespace close, teardown expiry, unknown teardown close or cleanup failure blocks qualification and the next role. Teardown success alone does not qualify the archive.
