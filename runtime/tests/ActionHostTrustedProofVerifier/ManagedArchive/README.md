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
