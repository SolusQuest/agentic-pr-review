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

Each isolated archive role retains its original 1,200-second outer hard cap.
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
inside the 1,200-second role budget; it does not qualify the Host's full
28-minute capacity. Failure diagnostics contain only closed operation counts
and process/watchdog outcomes, never payloads, credentials or private paths.

Failure-only operation traces are bounded to 4 KiB and contain per-case start/
elapsed times, closed operation counters, twelve tail observations and the last
completed mutation, SDK finalize and read observation. Object aliases correlate
SDK and HTTPS activity without publishing opaque IDs, names, URLs or payloads.
A row records sequence, elapsed milliseconds, operation, object alias, HTTP
status, service duration milliseconds and `finished`/`closed`. These are external
service observations; they do not assert C# readback or budget semantics.
