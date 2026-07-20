# M4 v2 state acceptance

This document is the implementation handoff for issue #67 and the contract consumed by issue #53. The TypeScript surface is isolated under `src/state-acceptance/`; the default action path does not import it.

## Durable records

`CandidateRegistrationV1`, `AcceptedStateMarkerV1`, and `StateSelectorV1` are closed canonical JSON records with `schemaVersion: 1`. The schemas in `protocol/schemas/` define the wire field sets; the validators additionally enforce cross-record bindings and identity preimages. All record bytes are capped at 32 KiB and use the parser order raw-byte cap, BOM rejection, fatal UTF-8, duplicate-key rejection, version routing, Unicode/NUL safety, closed validation, canonical-byte equality, and semantic validation.

The candidate ID is the domain-separated SHA-256 of the complete seven-field content envelope (manifest, ledger, provider metadata, metadata semantic, consumed input, result, and trace hashes). Registration, marker, and selector IDs use their corresponding domain tags and exclude only the fields documented by the issue contract. Timestamps, artifact locators, registration sequences, and selector revisions excluded from an identity preimage never affect identity or winner selection. The selector revision is a closed `bootstrap`, `sha256:<digest>`, or `invalid:<digest>` token. Marker construction precedes selector construction, so marker IDs never contain a successor selector revision.

The candidate locator is `{kind: "store-object", namespace: "m4-state-v1", objectId: "candidate-<64 lowercase hex>"}`. It is exactly 74 ASCII bytes and contains no path, URL, provider payload, runner path, or secret. Candidate bundles remain the exact three #55 files: `manifest.json`, `ledger.json`, and `provider-run-metadata.json`.

## Snapshots and races

Selection occurs before the #55 invocation. `StateSelectionSnapshot` retains the exact observed selector bytes and exact predecessor bytes in memory; its ID hashes only the branch semantic envelope and specified byte hashes. It is never upgraded by rereading the selector during acceptance. Missing automatic state selects bootstrap. Conclusively corrupt or unavailable referenced state selects a recovery root with closed recovery evidence; transport or transaction failures return infrastructure failure and never synthesize bootstrap or recovery. Explicit invalid state fails closed.

Acceptance creates an immutable snapshot while holding the per-state-key transaction. It verifies the observed selector revision, captures a decimal sequence cutoff, freezes every matching registration at or below that cutoff, and computes the candidate-set digest from the compact ordered projection. The hard limits are exactly 64 matching registrations and 2,097,152 aggregate registration bytes. Exceeding either limit returns `candidate_snapshot_limit_exceeded` with no partial snapshot or digest. Duplicate IDs, duplicate sequences, incomplete enumeration, invalid sequence fields, and sequence overflow fail closed; numeric gaps are allowed.

The competing scope excludes the candidate-owned ledger epoch and includes state key, session epoch, observed selector revision, predecessor marker/manifest/ledger identities, target generation, and interaction ID. Same-snapshot semantic duplicates use exact decimal producing-run ordering; semantic conflicts, stale candidates, CAS rejection, infrastructure failure, and accepted-but-unpublished are separate typed outcomes.

## Reference store

`ReferenceStateStore` is a Linux-only synthetic oracle. It stores canonical records and exact candidate bytes below a private 0700 root, uses 0600 files and atomic same-filesystem rename, rejects symlinks/non-regular files/path traversal, and can reopen from disk without shared in-memory state. It makes no fsync or power-loss durability claim.

The transaction boundary is a kernel-owned abstract Unix-domain socket named `\0agentic-pr-review-m4-` followed by `SHA256(RFC8785(stateKey))`. Acquisition retries `EADDRINUSE` for five seconds; other errors and unsupported platforms return `store_transaction_failed`/`store_capability_unsupported`. There is no stale pathname, owner record, timeout eviction, recovery unlink, or filesystem-lock fallback.

Candidate upload uses the deterministic locator and exact three-file read-back. Unknown writes are reconciled by exact read-back: equal validated bytes continue, absence is upload failure, different/invalid bytes are read-back mismatch, and inconclusive reads remain unknown. Registration and marker writes are immutable and idempotent. Selector CAS compares the complete revision token and reports applied, already-applied, rejected, or unknown without allowing sticky publication on an uncertain acceptance.

## Handoff boundary

Issue #67 defines the contract, validators, pure classification, synthetic reference store, and #55 lease handoff tests. Issue #53 remains responsible for a production CAS-capable transport, action wiring, permissions/security review, sticky integration, durable publication receipts, and the two-independent-workflow closure proof.
