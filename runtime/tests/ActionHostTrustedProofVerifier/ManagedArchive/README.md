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
`codeload.github.com` and `api.deepseek.com`. All three require successful HTTPS;
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
SDK uploads retain their original ZIP bytes and producing run identities.

The external GitHub service serves actual blob/tree hashes and nonempty gzip/tar
source through the authenticated API redirect to anonymous codeload. Three
successive source heads exercise fresh and restored work with grounded findings;
same-head recovery must avoid provider work. Failure, cancellation, stale-head,
wrong-key, reset refusal and hostile loading cases require no sticky writes.
Ambient CLR loading variables must not reach the actual child. Per-invocation
owner cleanup precedes every presentation. Only bounded counts, closed outcomes,
source and archive identities leave the temporary consumer. A failure is a failed
qualification; fixture unit tests alone do not establish this proof.
