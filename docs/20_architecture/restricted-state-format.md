# Restricted encrypted Agent state

Status: normative R2 current-format security and local storage-conformance contract implemented by issue [#87](https://github.com/SolusQuest/agentic-pr-review/issues/87), connected to the bounded R3 live commit by issue [#108](https://github.com/SolusQuest/agentic-pr-review/issues/108), and exercised across a concrete fresh-process Host boundary by issue [#109](https://github.com/SolusQuest/agentic-pr-review/issues/109). The activated R4 production overlay is defined by [`r4-actionhost-wrapper-plan.md`](./r4-actionhost-wrapper-plan.md) and its migration closure by [`r4-migration-cutover-handoff.md`](./r4-migration-cutover-handoff.md).

This document is the durable copy of the restricted encrypted state contract refined under [#78](https://github.com/SolusQuest/agentic-pr-review/issues/78). It protects the current SESSION plaintext defined by [`agent-session-format.md`](./agent-session-format.md). It fixes authorization, authenticated framing, Host binding, lineage, retention, transition outcomes, and local conformance behavior. R4 retains those properties while selecting downstream-owned GitHub Actions artifacts as the first production transport and explicitly allowing platform-visible metadata and ciphertext.

## Restricted class and authorization

The implemented R2 local-store class is exactly `workflow-restricted-completed-review`. State is readable only by the trusted same-repository, non-fork Host principal for the exact workflow, repository, review target, and session scope.

Fork-origin, untrusted, cross-repository, and wrong-scope principals have no enumerate, existence, read, write, replace, decrypt, delete, handoff, or publish capability. Authorization occurs before root inspection, storage enumeration, existence checks, key resolution, cryptography, SESSION admission, Agent/provider construction, deletion, handoff, or publication. Possession of a key, key identifier, artifact name, candidate, staging object, selector, or receipt does not grant authority or reveal existence.

The implementation represents successful authorization with a Host-owned `AuthorizedStateAccess` capability whose constructor is private. Store, key, crypto, and SESSION admission operations require that capability. The Agent SESSION module remains capability-agnostic; the dependency direction is Host STATE to Agent SESSION.

### R4 production visibility overlay

R4 names the production class `workflow-encrypted-completed-review`. Each downstream repository stores authenticated encrypted state in that repository's GitHub Actions artifacts. In a public repository, an observer may discover opaque artifact names, sizes, digests, timestamps, run provenance, and ciphertext under GitHub's repository visibility rules. Existence and encrypted-byte visibility are therefore not protected capabilities.

The protected capabilities are state-key resolution, authenticated decryption, SESSION admission, scope-sensitive logical identity, trusted state creation, replacement, deletion, acceptance, lineage advancement, provider admission, handoff, and publication. The trusted Host still authorizes before asking its adapter to list or mutate state and before key resolution or cryptography. This ordering constrains product behavior; it does not claim that GitHub prevents an external public observer from calling a public artifact read endpoint.

Artifact names are domain-separated keyed opaque values and contain no raw repository, workflow, pull-request, session, provider, model, adapter, policy, or lineage fields. Artifact contents remain the bounded authenticated envelope or an authenticated encrypted transport receipt. The state key is stored separately as downstream-owned secret material and never accompanies the artifact.

The internal store seam becomes asynchronous and transport-neutral in R4. `RestrictedStateService` continues to own every authorization, encryption, SESSION, lineage, retention, and transition rule in this document. The GitHub artifact adapter owns only bounded opaque list/metadata/download/immutable-upload/readback/delete outcomes and shares one conformance suite with the local store. R4 exposes no public adapter ABI; future maintained adapters and any external wire contract require a later design.

R4 adds a repository-level encrypted locator-root sentinel and derives scoped artifact names from the stable root rather than directly from the rotatable encryption key. The fixed generic sentinel name reveals product use but no scope mapping. A maximum of eight live sentinel artifacts form one authenticated generation/successor chain; semantically equivalent same-root duplicates are idempotent, distinct roots or incomparable successors conflict, and each selected head authenticates the exact identities and digests it supersedes. Rotation and refresh append/read back one current-key successor, prune exact superseded artifacts, and preserve the root without another configured secret. The previous key remains read-only for every unexpired state or transaction record needed for recovery and is removed only after none remains. Logical state/transaction retention remains bounded to seven days, while platform retention requests include transport margin: scoped artifacts request at least eight days and the locator sentinel at least ten. Returned `expires_at`, rather than the requested day count, must prove that every scoped artifact expires at least 24 hours before the selected sentinel; if metadata cannot prove that invariant, absence is `state_locator_unavailable`, not bootstrap.

R4 also derives an epoch-independent lineage-head locator from the locator root, base scope, and object class. Candidate, intent, acceptance, reset, and cleanup locators likewise omit the not-yet-known epoch and session ID, while their encrypted contents bind both. Complete head enumeration selects zero initial claims, one active epoch, or conflict before provider work. The initial epoch is keyed-deterministic for the base scope, and an authorized reset is the unique authenticated successor from the former head; concurrent distinct claims or reset successors cannot each publish or accept.

For the R4 production adapter, pending candidate `prepared_at` is distinct from `accepted_at`. The Host fixes `accepted_at` only after exact sticky-publication readback, then sets logical expiry to exactly seven days later. Candidate and active lineage-head metadata must prove platform coverage through that logical expiry before acceptance; the Host also performs a pre-publication coverage check using the complete bounded sticky request/readback budget. Insufficient repository retention or unverifiable `expires_at` fails without accepting a shortened generation. This production overlay replaces any interpretation that a pre-publication prepare timestamp starts the accepted-state window.

The R2 table below remains the implemented local-store contract. The R4 production overlay narrows automatic selection: only true absence or a Host-classified historical non-current family may bootstrap. Once a current family is selected, malformed, unauthenticated, missing-current, scope-incompatible, continuation-invalid, ambiguous, diverged, unrelated, force-pushed, or unknown-ancestry state fails closed unless an authorized explicit reset fully completes with a new lineage epoch and session identity.

## Scope, retention, and independent lineage

The stable storage scope contains repository ID, workflow identity, review target, session ID, provider ID, model ID, adapter ID, policy SHA-256, limits SHA-256, toolset SHA-256, and build ID. It excludes the dynamic current base/head. Stored producer base/head remain authenticated provenance, while current dynamic reviewed identity is admitted separately through the SESSION `same_head` or `verified_ahead` transition.

The class retains the accepted generation and its immediate predecessor for at most seven days from a Host-trusted acceptance timestamp. The service reads its trusted clock once per operation; prepare fixes accepted-at to that value and expires-at to exactly accepted-at plus 604,800 seconds. Restore enumeration exposes only accepted objects, ordered by generation descending and then envelope SHA-256 ordinal, and at most two candidates. One separate staging slot may hold the next prepared envelope. Staging is never a restore candidate. Candidate metadata is at most 16 KiB, accepted envelope bytes are at most 64 MiB, and the complete accepted-plus-staging logical scope, including the exact bytes emitted by the candidate-metadata codec, is at most 96 MiB plus 16 KiB of metadata. Partial, malformed, duplicate, non-adjacent, out-of-order, or over-limit enumeration fails closed and selects no partial set.

The independent Host input `AcceptedLineage` contains the stable scope, accepted generation, accepted `session_sha256`, accepted `envelope_sha256`, expected predecessor envelope SHA-256, accepted timestamp, expiry timestamp, and transition authorization. Candidate bytes, staging, local snapshots, selectors, and receipts cannot create or change lineage.

With no lineage, only generation 0 with a null predecessor may be prepared and accepted. With lineage generation N and envelope H, restore considers only exact envelope H. Hiding or deleting H never makes an older candidate current. Generation N+1 must name H as its predecessor and pass exact compare-and-swap admission. Generation arithmetic is checked and cannot overflow.

`PreparedStateReceipt` contains generation, session SHA-256, envelope SHA-256, and exact prepared object identity. The object identity is a domain-separated digest over the complete canonical Host binding followed by the decoded session and envelope hashes; changing any scope, producer, generation, predecessor, timestamp, session, or envelope field changes the identity. Encryption occurs once per prepare. The receipt is created before the store write, so an outcome-unknown failure can be reconciled against the exact persisted object. Reconciliation decrypts and re-admits the matching object before reporting it idempotent; it never re-encrypts the same generation with a fresh nonce. A same-generation operation is idempotent only when session hash, envelope hash, and object identity match. The same semantic plaintext encrypted with another nonce is a conflict, not another accepted object.

### R3 live commit boundary

Issue [#108](https://github.com/SolusQuest/agentic-pr-review/issues/108) connects only a fully grounded live-Agent completion to this existing transaction. The Host consumes the exact non-serializable candidate and its separately created `AuthorizedStateAccess` while the original key resolver remains alive. It calls the SESSION builder and Prepare once. An outcome-unknown Prepare may Reconcile once by the exact receipt; an outcome-unknown Accept may Reconcile once and retry Accept once with that same receipt. It never re-prepares, resets, enumerates, cleans up, or invokes the local handoff operation as part of this commit.

Caller cancellation is honored before the first Accept. Once an Accept outcome may have crossed the atomic commit boundary, the bounded receipt reconciliation runs independently so later cancellation cannot disguise an accepted generation. After receipt-matching accepted or idempotent success, the commit-known state is absorbing: lineage validation, atomic publication, cancellation, or cleanup failure preserves the accepted generation and hashes. Only confirmed independent-lineage publication is handoff-ready; every unavailable, unknown, cancelled, exceptional, or cleanup-failed publication outcome is handoff-unavailable and cannot authorize a later process launch.

The independent-lineage sink introduced by #108 is typed and Host-only. It receives no key, SESSION plaintext, candidate, provider outcome, state root, store, or process capability. Issue [#109](https://github.com/SolusQuest/agentic-pr-review/issues/109) supplies its first concrete consumer: a fixed-layout, no-follow local Host boundary admits canonical authorization, reviewed-input, snapshot-manifest, and accepted-lineage bytes; exposes bootstrap and continuation entrypoints on the same runtime executable; and publishes canonical lineage and sanitized result bytes atomically. The test Host launches those entrypoints in separate processes. The next process accepts lineage only against the independent raw-byte digest returned by the preceding Host write receipt, and selected-current failure never falls back to bootstrap. This remains a deterministic conformance path, not the production GitHub workflow transport or trusted live-provider run.

`session_sha256` is the SESSION digest over complete plaintext under domain `apr.session.r2`. `envelope_sha256` is the digest over the complete raw `APRAST01` envelope under domain `apr.state-envelope.r2`. Locator, predecessor, replay, and CAS use envelope identity. Semantic reconciliation checks both hashes.

## Stable outcomes

Every operation returns exactly one `StateAction` (`authorized`, `enumerated`, `prepared`, `restored`, `bootstrap`, `reset`, `accepted`, `idempotent`, `handoff_ready`, `denied`, or `failed`) and one stable `state_*` code.

| Operation or condition                       | Exact action/code                   | Mutation                                                  |
| -------------------------------------------- | ----------------------------------- | --------------------------------------------------------- |
| authorize trusted exact scope                | `authorized/state_authorized`       | none                                                      |
| authorize fork, untrusted, or wrong scope    | `denied/state_access_denied`        | none; before existence, storage, or key work              |
| enumerate trusted and bounded                | `enumerated/state_enumerated`       | none                                                      |
| enumerate malformed, partial, or over-limit  | `failed/state_enumeration_invalid`  | none                                                      |
| prepare valid next envelope                  | `prepared/state_prepared`           | one separate staging object                               |
| prepare conflicting or full staging          | `failed/state_conflict`             | none                                                      |
| reconcile exact prepared or accepted receipt | `idempotent/state_idempotent`       | no duplicate and no re-encryption                         |
| explicit trusted reset/delete                | `reset/state_reset`                 | physically remove the complete scope snapshot             |
| trusted current expiry cleanup               | `reset/state_expired`               | physically remove the complete scope snapshot             |
| trusted predecessor expiry cleanup           | `reset/state_expired`               | retain live current; remove only the expired predecessor  |
| required cleanup/prune fails before commit   | `failed/state_cleanup_failed`       | old accepted lineage unchanged                            |
| prepare trusted local handoff receipt        | `handoff_ready/state_handoff_ready` | opaque validated receipt; no external publication         |
| cancellation before commit                   | `failed/state_cancelled`            | none                                                      |
| I/O failure before commit                    | `failed/state_io_failed`            | none; outcome-unknown prepare reconciles by exact receipt |

Cancellation observed after the atomic commit returns the committed operation success, not a false cancellation.

### Restore and acceptance transitions

| Host lineage | Stored logical objects                                       | Request                  | Automatic result                       | Explicit result                 | Mutation                                                      |
| ------------ | ------------------------------------------------------------ | ------------------------ | -------------------------------------- | ------------------------------- | ------------------------------------------------------------- |
| absent       | none                                                         | restore                  | `bootstrap/state_absent`               | `failed/state_explicit_missing` | none                                                          |
| absent       | unaccepted staging generation 0                              | restore                  | `bootstrap/state_absent`               | `failed/state_explicit_missing` | staging ignored                                               |
| absent       | candidate-like object without Host lineage                   | restore                  | `bootstrap/state_absent`               | `failed/state_explicit_missing` | object cannot self-promote                                    |
| current H    | H present                                                    | restore                  | `restored/state_restored`              | `restored/state_restored`       | none                                                          |
| current H    | H missing, predecessor present                               | restore                  | `bootstrap/state_current_missing`      | `failed/state_current_missing`  | never fall back                                               |
| current H    | H expired by Host metadata                                   | restore                  | `bootstrap/state_expired` then cleanup | `failed/state_expired`          | no admission                                                  |
| current H    | H plus staging N+1                                           | restore                  | `restored/state_restored`              | `restored/state_restored`       | staging ignored                                               |
| absent       | prepared generation 0                                        | accept                   | `accepted/state_accepted`              | `accepted/state_accepted`       | accepted set becomes generation 0                             |
| current H    | prepared N+1 with predecessor H                              | accept/CAS success       | `accepted/state_accepted`              | `accepted/state_accepted`       | accepted set becomes N+1 plus H; older accepted object pruned |
| current H    | exact accepted/prepared receipt retried                      | reconcile                | `idempotent/state_idempotent`          | `idempotent/state_idempotent`   | no duplicate or re-encryption                                 |
| current H    | candidate generation lower than H                            | accept/restore candidate | `failed/state_replay_rejected`         | `failed/state_replay_rejected`  | none                                                          |
| current H    | N+1 has wrong or forged predecessor                          | accept                   | `failed/state_lineage_mismatch`        | `failed/state_lineage_mismatch` | none                                                          |
| current H    | same generation with another envelope or conflicting staging | accept                   | `failed/state_conflict`                | `failed/state_conflict`         | none                                                          |

Before acceptance commit, required prune or cleanup failure returns `failed/state_cleanup_failed`, leaves H current and restorable, and permits no partial lineage advance. An unremovable staging orphan is never enumerated or restored and blocks later prepare until trusted cleanup succeeds. Reset or current-expiry deletion is a compare-and-delete operation that leaves the scope version absent rather than writing an empty snapshot file. An exact expired predecessor may be pruned while retaining its live current successor. Forged lineage cannot delete either object.

## Exact encrypted envelope

The envelope is binary and contains, in order:

1. ASCII magic `APRAST01` (8 bytes);
2. envelope format UInt16 little-endian `1`;
3. algorithm UInt16 little-endian `1` for AES-256-GCM;
4. namespace length UInt16 little-endian plus exact UTF-8 `agentic-pr-review/agent-session`;
5. discriminator length UInt16 little-endian plus exact UTF-8 `r2-current-1`;
6. key-ID length UInt16 little-endian plus any 1-64-byte ASCII key ID (`00`-`7f`, including space and control bytes);
7. nonce length UInt16 little-endian `12` plus 12 cryptographically random bytes;
8. ciphertext length UInt32 little-endian plus ciphertext;
9. tag length UInt16 little-endian `16` plus a 16-byte tag;
10. no trailing bytes.

Plaintext is the complete current SESSION bytes and is at most 16 MiB. The complete envelope is at most 32 MiB. Encryption uses `AesGcm`, a 256-bit key, a random 96-bit nonce, and a 128-bit tag. Repeated encryption of identical plaintext and Host binding must produce distinct nonces and envelope identities.

AAD is reconstructed and is not stored separately:

`ASCII("APR-STATE-AAD-1\0") || serialized envelope header bytes from magic through the ciphertext-length field, excluding ciphertext and tag || canonical Host binding`

The canonical Host binding is at most 2,048 bytes and has this exact order:

| Field                        | Exact wire type and domain                                                             |
| ---------------------------- | -------------------------------------------------------------------------------------- |
| repository ID                | UInt16 LE byte length plus UTF-8, 1-128 bytes                                          |
| workflow identity            | UInt16 LE byte length plus UTF-8, 1-256 bytes                                          |
| review target                | UInt64 LE, 1 through 9,223,372,036,854,775,807                                         |
| session ID                   | UInt16 LE length plus ASCII `[A-Za-z0-9_-]{1,64}`                                      |
| provider ID                  | UInt16 LE length plus UTF-8, 1-128 bytes                                               |
| model ID                     | UInt16 LE length plus UTF-8, 1-128 bytes                                               |
| adapter ID                   | UInt16 LE length plus UTF-8, 1-128 bytes                                               |
| policy SHA-256               | exactly 32 decoded hash bytes                                                          |
| limits SHA-256               | exactly 32 decoded hash bytes                                                          |
| toolset SHA-256              | exactly 32 decoded hash bytes                                                          |
| build ID                     | UInt16 LE length plus UTF-8, 1-256 bytes                                               |
| producer base SHA            | exactly 20 decoded Git SHA bytes                                                       |
| producer head SHA            | exactly 20 decoded Git SHA bytes                                                       |
| generation                   | UInt64 LE, 0 through 9,223,372,036,854,775,807                                         |
| predecessor envelope SHA-256 | presence byte `0` for generation 0 or `1` plus exactly 32 hash bytes                   |
| accepted-at                  | Int64 LE Unix seconds, 0 through 253,402,300,799                                       |
| expires-at                   | Int64 LE Unix seconds, greater than accepted-at and no more than accepted-at + 604,800 |

Overflow, width extension or truncation, alternate endianness or length, swapped timestamps, single-field mutation, and total binding overflow fail closed. A Host locator classifies the family before current bytes are parsed, so altered magic, format, algorithm, namespace, discriminator, key ID, nonce, or ciphertext length cannot select a non-current bootstrap.

### AAD golden

The complete golden uses format and algorithm 1, the current namespace and discriminator, key ID `test-key`, nonce bytes `00..0b`, ciphertext length 3, repository `repo`, workflow `workflow`, review target 1, session `session_0`, provider/model/adapter literals, policy/limits/toolset bytes `11`/`22`/`33` repeated 32 times, build `build`, producer base/head bytes `44`/`55` repeated 20 times, generation 0, absent predecessor, accepted-at 1,700,000,000, and expires-at 1,700,604,800.

The reconstructed AAD is exactly 332 bytes. Its SHA-256 is `799f5bc81cd564fec6d781d540dc4b940461161b69efc4c7b8bfd20c1ac3ce7b`. Its canonical base64 is:

`QVBSLVNUQVRFLUFBRC0xAEFQUkFTVDAxAQABAB8AYWdlbnRpYy1wci1yZXZpZXcvYWdlbnQtc2Vzc2lvbgwAcjItY3VycmVudC0xCAB0ZXN0LWtleQwAAAECAwQFBgcICQoLAwAAAAQAcmVwbwgAd29ya2Zsb3cBAAAAAAAAAAkAc2Vzc2lvbl8wCABwcm92aWRlcgUAbW9kZWwHAGFkYXB0ZXIRERERERERERERERERERERERERERERERERERERERERESIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMFAGJ1aWxkRERERERERERERERERERERERERERVVVVVVVVVVVVVVVVVVVVVVVVVVQAAAAAAAAAAAADxU2UAAAAAgCtdZQAAAAA=`

## Key policy and SESSION admission

The key resolver is Host-only. New prepares use the current write key. An explicitly Host-approved previous key may read unexpired state. Unknown or unapproved key ID returns `failed/state_key_unavailable`; an approved key ID with incorrect material returns `failed/state_authentication_failed`. Rotation does not convert formats or rewrite accepted state in place. The next accepted generation uses the current write key. Temporary key copies are zeroed.

Before encryption, STATE passes Agent-produced plaintext through the production SESSION parser and complete root, scope, producer, transition, record grammar, classification, association, continuation, terminal grounding, and reconstructed-request validators. It recomputes the authoritative session digest and producer projection. Every lineage-selected accepted current or predecessor is authenticated and passed through the same complete SESSION boundary before it can influence prepare, acceptance, reconciliation, compare-and-swap, or handoff success. After authenticated decryption, the same boundary runs again before returning a typed admitted SESSION value to Agent/provider integration. Authenticated plaintext that violates SESSION returns the closed `failed/state_envelope_invalid` outcome; STATE does not leak `session_*` detail.

Error precedence is authorization; cancellation; Host locator/family; accepted-current presence; expiry; envelope size/framing; key policy; AEAD; plaintext validation; then transition. In transition handling, an older generation is replay, a wrong N+1 predecessor is lineage mismatch, and same-generation/different-envelope or staging collision is conflict. Current missing wins before predecessor inspection, expiry wins before key lookup, framing wins before key lookup, AEAD wins before plaintext validation, and plaintext validation wins before replay/CAS classification.

The stable codes are `state_authorized`, `state_access_denied`, `state_enumerated`, `state_enumeration_invalid`, `state_prepared`, `state_absent`, `state_explicit_missing`, `state_restored`, `state_accepted`, `state_idempotent`, `state_reset`, `state_handoff_ready`, `state_cancelled`, `state_current_missing`, `state_expired`, `state_envelope_invalid`, `state_key_unavailable`, `state_authentication_failed`, `state_lineage_mismatch`, `state_replay_rejected`, `state_conflict`, `state_cleanup_failed`, and `state_io_failed`.

## Trusted local conformance store

R2 implements one transport-independent local store for tests and the later executable proof. The caller supplies an existing explicit test-owned root. Root and ancestors must be directories and must not be symlinks, junctions, or reparse points. Candidate snapshots are regular files opened with Linux nonblocking/no-follow semantics where applicable. The implementation reads length before allocation, rechecks opened-handle identity and length after reading, and proves that the path still names the opened identity.

One opaque filename is derived from a domain-separated hash of stable non-secret scope identity. The file contains a closed, bounded local snapshot with zero to two accepted immutable envelope objects and zero or one separate staging object. This local snapshot is transaction machinery, not the encrypted envelope, independent lineage authority, or a production transport.

Compare-and-swap is serialized per scope across local store instances. The store opens every root ancestor without following links, captures its filesystem identity, and revalidates the complete proof around reads and commits. Absence is distinguished from directories, dangling links, special files, access failures, and other unsafe or I/O states. While holding commit exclusion, the store reads and validates the expected snapshot version, writes a same-directory unique temporary file, flushes data to disk, and atomically creates or replaces the complete scope snapshot. The replacement already contains the new accepted set, retained predecessor, pruning, and staging removal. Stale versions conflict. Cancellation or failure before replacement leaves the previous snapshot visible, and temporary cleanup failure is explicit. A root-identity loss after the final pre-replacement proof rolls the replacement or deletion back through the held root handle and is not reported as committed success. Cancellation observed after replacement returns committed success. Directory metadata synchronization failure reports an outcome with `Committed: true`, allowing the service to preserve the committed transition. Reset uses a no-follow raw regular-file identity and length version, so a trusted reset can compare-delete malformed, truncated, trailing, or oversized snapshot bytes without parsing them; unsafe filesystem objects still fail closed. Current-expiry uses parsed version-checked physical deletion. Both deletion paths synchronize directory metadata.

Normal results and diagnostics contain only action/code plus bounded generation, hashes, and opaque receipt identity. They never contain plaintext SESSION content, logical records, tool results, findings, continuation or reasoning, ciphertext, nonce, tag, AAD bytes, key material, authorization values, raw object names, ambient roots, or raw exceptions.

## Conformance and downstream handoff

Deterministic tests pin authorization denial, the exact action/code taxonomy, the 332-byte AAD golden, framing and exact bounds, nonce uniqueness, every envelope-byte and Host-binding mutation, the complete ASCII key-ID domain, unknown and wrong keys, approved-key rotation, all stable-scope substitutions at SESSION admission, SESSION semantic re-admission, independent lineage, staging isolation, missing-current no-fallback, exact expiry boundaries, forged-expiry resistance, replay/conflict distinction, exact outcome-unknown receipt reconciliation, cancellation/I/O/cleanup outcomes, physical deletion, opaque naming, restart, corrupt/partial/duplicate/non-adjacent/oversized enumeration, unsafe scope entries, root-swap rejection, directory-sync outcome reporting, and stale-writer CAS.

Checked-in state fixtures and test values are synthetic and public-safe. The state-encryption canary may appear only at the dedicated Host cryptographic boundary and never in persisted state, Agent/provider inputs, tool data, model-visible content, diagnostics, results, handoff receipts, outputs, summaries, annotations, or publications.

Issue [#88](https://github.com/SolusQuest/agentic-pr-review/issues/88) consumed the Host state integration interface and proved authorization-before-capability, encrypted completed-session acceptance, exact restore, independent Host lineage, same-head/verified-ahead transition behavior, replay/tamper/header/cross-scope failure, and two-fresh-process framework and Native AOT execution through the real local store. The synthetic transport oracle is now the runtime CI source of truth and the comparison fixture is deleted. R4 owns the downstream GitHub Actions artifact adapter, public metadata/ciphertext model, private Node bridge, key provisioning, immutable candidate/acceptance records, retention cleanup, and publication transaction; the local conformance store does not claim those product guarantees.

## CR1-S1 authenticated Solus record

[S1 #405](https://github.com/SolusQuest/agentic-pr-review/issues/405) adds an independently testable Solus record boundary. Current production continues selecting the native SESSION reader above. S1 does not activate production restore, select artifact families, implement a transaction/reset, claim a runtime grant, or accept APR findings. S2 owns exclusive runtime authority, S3 owns persistence/restore orchestration, and S4 owns an actual subsequent review. A successful codec or capture result does not satisfy those gates.

`SolusRestrictedStateSnapshotCodec` reuses the unchanged APRAST01 AES-256-GCM wrapper and full `RestrictedStateBinding` AAD. The new plaintext is a separate `APRSOL01` record; the native SESSION reader never reinterprets it. The outer namespace/discriminator, original R2 AAD vectors and frozen native action/code taxonomy remain unchanged. Metadata and complete opaque payload are authenticated together; snapshot metadata and unkeyed hashes cannot authorize restoration.

The exact inner layout is:

| Field                                                            | Wire representation                                                                                   |
| ---------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------- |
| Magic / record version                                           | ASCII `APRSOL01`; uint16 little endian `1`                                                            |
| Package pin                                                      | uint16 byte length; exact ASCII `0.1.0-internal.1`                                                    |
| Profile                                                          | uint16 byte length; exact ASCII `cr1-solus-internal1-dev`                                             |
| Mode                                                             | uint8 `1`, completed candidate context                                                                |
| Solus implementation                                             | 16 RFC 4122/network-order bytes for `3f520f02-3b59-431e-8acf-a62544f28760`                            |
| Solus format / compatibility                                     | two uint32 little endian values, both `1`                                                             |
| Package closure / runtime configuration / provider configuration | three SHA-256 digests, 32 decoded bytes each, in this order                                           |
| Checkpoint correlation                                           | four nonempty GUIDs, 16 RFC 4122/network-order bytes each: checkpoint, logical work, round, execution |
| Opaque context                                                   | uint32 little endian length; exactly that many unchanged bytes; EOF                                   |

There are no optional fields or alternate readers. Fixed framing is 242 bytes. Payload length is 1–196,608 bytes, the selected profile's retained ceiling rather than Solus's internal 2 MiB codec constant. Complete plaintext is at most 196,850 bytes; the existing wrapper's maximum 64-byte key ID gives a complete encrypted maximum of 197,011 bytes. Record and encrypted framing, declared lengths, metadata and EOF are checked before the corresponding allocation/copy/decode. Payload bytes are never text-decoded, parsed as runtime JSON, rewritten, truncated, converted or compacted.

The selected package-closure fingerprint is SHA-256 of the exact checked `packages.managed.lock.json` bytes: `954b5183b4160f833827c410114e1031a5194dfeea31e21b2a05e7266e2098ee`. R2's trusted package proof establishes installed package/source/hash equality with that lock. A saved fingerprint cannot select a pin or establish an installation. Any selected pin/closure change requires explicit consumer update and saved-context requalification; incompatible selected-current state remains a visible failure unless an authorized reset establishes a new epoch/session.

Current trusted compatibility inputs additionally supply runtime and provider configuration fingerprints. The consuming Host must derive them from actual selected tool descriptors/recovery, provider scope, runtime bounds/options, acknowledgement/guarantees, and provider endpoint/model/token/body/time limits, excluding credentials. S1 authenticates and compares the exact supplied fingerprints; it does not implement the future materializers or prove that an arbitrary caller computed them correctly. No saved/default value chooses the expected configuration.

### Capture correlation and accepted source

The managed-only `SolusRestrictedStateCapture` is a call-scoped restricted sink bound to one Host-selected execution ID and current compatibility. The trusted Host gives it only to that invocation and completes it once with that invocation's actual result. It accepts exactly one size/metadata-checked capture, then requires Delivered capture, matching nonempty checkpoint/execution identity, a completed outcome and candidate stop, and one **current** acknowledged Accept/End receipt. Historical accepted receipts do not substitute current completion. Supported intent is Fresh or NewRunFromContext; unfinished ContinueRun recovery is excluded. Missing, duplicate, late, malformed, failed or incomplete capture cannot yield record material. The sink has no key, store, provider, claim or runtime invocation capability.

The tuple is safe correlation, not cryptographic attestation of opaque internal grammar or a durable claim. Solus independently validates its own context representation/source at subsequent runtime admission. Capture delivery, runtime completion, APR findings acceptance, durable successor acceptance and exclusive claim remain separate.

Restore receives `AuthorizedStateAccess`, trusted binding, independent `AcceptedLineage`, current compatibility and a trusted clock. It requires exact scope, generation, predecessor and acceptance/expiry agreement, authorized transition and unexpired lifetime (`now < expiresAt`). The selected encrypted digest must equal independent `EnvelopeSha256`. After AEAD authentication, the complete inner record digest is:

```text
SHA256(ASCII("apr.solus-state.cr1") || 0x00 || complete APRSOL01 record)
```

It must equal independent `AcceptedLineage.SessionSha256`. Candidate paths additionally require candidate binding, both candidate digests and the binding-dependent object identity to agree; recomputing unkeyed candidate fields does not repair independent-lineage disagreement. The authenticated selected record supplies its checkpoint tuple to the fresh process. No extra checkpoint expectation, invented registry or record-derived expected compatibility is required or permitted. S2 later checks a source-bound grant against that authenticated source and independent Host authority.

Structural encode/decode alone grants no authority. The authenticated restore operation exposes only a restricted record on success and a closed `SolusRestrictedStateFailure` on failure. Failures distinguish malformed/oversized input, incompatible selected state, access/lineage failure, expiry, unavailable key and failed authentication. They contain no restricted content and never report bootstrap or reset. This codec cannot enumerate or try an older generation. Native SESSION chosen as Solus current, wrong pin/format/provider/configuration and corrupted or missing selected-current state fail visibly. Only a separately authorized Host reset may establish a new epoch/session; it cannot convert these bytes or serve as an admission bypass.

### Independent conformance evidence

`Cr1SolusStateEnvelopeTests` pins an independently authored Node Buffer binary oracle, including RFC 4122 checkpoint `00112233-4455-6677-8899-aabbccddeeff` represented by `00 11 22 33 44 55 66 77 88 99 aa bb cc dd ee ff`. With the complete selected metadata, non-symmetric remaining GUIDs and binary payload `00 ff 80 c0 41 0a 7b 7d`, its 250-byte literal record has domain digest `4246a4165adb62db2cbc0e2c9c35d58191a40fa536d6e17113dda1d30701788b`. Writer and reader must match that literal independently; their mutual round-trip alone is insufficient.

The independent managed `SolusStateEnvelopeFixture` uses actual installed `.1` types and a real controlled candidate-End capture, then seals, opens and reconstructs the exact carrier. Its synthetic provider makes no HTTP request. Negative cases cover execution/capture/completion association, runtime metadata, checkpoint constructor rules, unsupported/incomplete capture and byte bounds. It proves carrier integration, not an actual restored APR review or durable acceptance. The trusted exact-head package workflow runs it on Ubuntu 24.04 x64 after verified package restore, with an explicit credential-free child environment. Ordinary source/runtime checks remain separate; Mac platform failures or skips do not qualify that runner.

## CR1-S2 source and target authority

`SolusRuntimeContextAuthority` is the managed callback for one prepared, durable context consumption. The Host first obtains a fresh authorized invocation, trusted policy and bounded reviewed snapshot. `CreateForAuthorizedReview` accepts that live Host context and a trusted compiled construction callback, constructs the actual provider/tool/capability/hook bindings once, and owns the resulting runtime configuration. Matching saved descriptors or configuration hashes cannot issue this capability. The constructor snapshots the actual references; disposal or replacement of the snapshot root invalidates admission. The callback is trusted Host code, not a sandbox for hostile extensions.

The source is independently selected by the existing authenticated lineage and accepted-state selectors. The source projection checks current and immediate-predecessor publication policy and trusted same-head/verified-ahead ancestry before S1 authenticates the exact selected ciphertext and record. Its expiry is the immutable generation's original prepared expiry. Extending the requested retention window does not extend eligibility. Selection never falls back to older context, resets the epoch, imports native SESSION, or publishes acceptance.

Source selection and serialized preparation both require authenticated read-only confirmation that no pending Host transaction owns work. The checks reuse lineage pending-intent/successor-cleanup classification and live publication candidate-family selection. Pending successors, reset/expiry intents, active publication/failure/abandonment records, cleanup debt, unknown records or under-retained inventory refuse admission before claim creation. Terminal publication records still requiring cleanup also refuse admission; ordinary accepted candidate/receipt history remains selectable. S2 performs no transaction recovery or reset orchestration.

The supported ActionHost topology retains the authorized workflow's per-PR, non-cancelling cross-run concurrency rule. Processes within one authorized invocation additionally hold a canonical private Linux directory lock keyed by repository, PR, workflow, run and attempt. The native state-writing entrypoint holds the same lease across its state lifetime. The local fresh-process entrypoint locks its existing authorized directory handle. No-follow inode, owner and mode checks fail closed; random staging paths are not exclusion identities. This is not distributed locking across independent hosts or support for unscheduled callers.

A lease-owned asynchronous gate also serializes all preparations borrowing the same already-held OS exclusion, covering current-state reobservation, validation and durable commit. Waiting preparations recheck lifetime after entry; cancellation or disposal cannot authorize storage work. Under that exclusion, asynchronous preparation reobserves the selected head, receipt and logical generation, then scans the complete bounded claim inventory. An authenticated `ContextClaim` conflicts on either source logical-work/round or grant identity in the active scope/epoch/session. Checkpoint changes do not grant a second consumption of the same source round. The Host derives grant identity from a versioned canonical digest of the independent scope, epoch, session, generation, source work/round and new execution/round. This prevents rebinding a grant after source expiry; the digest itself grants no authority.

The new `context_claim` object class is appended without renumbering existing classes. Its strict `APRCCL01` record is at most 4,096 bytes. It binds selected head and receipt, generation, ciphertext and record digests, complete source/new-round identities and immutable source expiry. Existing authenticated storage and exact post-upload reconciliation establish consumption before runtime import. The per-class limit remains eight and the aggregate scope limit remains 72; admission never silently prunes receipts. Unknown or under-retained claim evidence denies admission. Under-retained objects remain visible to the existing transition/key-dependency inventory. Ordinary candidate/acceptance cleanup does not delete claims; authorized epoch transitions retain their existing evidence requirements.

Only known durable consumption produces a one-shot live capability. The synchronous `.1` callback compares exact payload bytes, runtime metadata, every grant/checkpoint identity and current actual targets, then consumes that capability once. It performs no storage, network, provider exchange, tool invocation or new acceptance. Runtime decode/import failure after preparation conservatively leaves the source consumed. Cancellation, unknown persistence and disposal do not refund it. This mechanism does not promise arbitrary-process-kill recovery.

`SolusContextAuthorityFixture` exercises the real Host authorization and snapshot construction, accepted-state codecs/selection, local immutable transport, nonempty installed `.1` generic tool bindings and runtime callback. Independent children test same-grant and competing-grant races, exactly one winner, failed provider import and refusal by a third process. A deterministic paused-upload case constructs two genuine authorities sharing one lease, checks waiting cancellation/disposal, requires one winner and verifies spent state in a fresh process. Accepted G0 plus authenticated pending G1/reset/expiry cases require refusal both at selection and after prior selection, without claim or runtime effects. Separate cases test fresh legitimate reconstruction and payload/grant/lifetime rejection. Synthetic authorization ports and a controlled provider perform no HTTP requests. Local source compilation is diagnostic only; qualification requires the exact-head Ubuntu 24.04 x64 package workflow's credential-free S2 step after actual locked package restore. Neither this fixture nor S2 enables the production Solus review route or replaces the later S3/R3/R4 responsibilities.

The S2 class census adds one empty `context_claim` listing per completed scoped census on the existing native ActionHost route. Independent before/after synthetic traces account for 704 additional listings: bootstrap 56, continuation 88, cross-head conflict 1, delayed visibility 58, upload/delete unknown 56 each, workflow/delete-exact/inline/inline-warning 56 each, stale/provider-malformed/public-result/credentials-missing 4 each, crash-mutation 21, crash-recovery 37, cancel-before-dispatch 7, cancel-known-commit 33, and cancel-outcome-unknown 51. The bootstrap, continuation and delayed-visibility inventories contain 565 background artifacts, so their listings each traverse six 100-item pages; other affected cases use one page. Thus the bridge adds exactly `704 + 5 * (56 + 88 + 58) = 1714` conditional GETs, not-modified responses and secondary points. Primary requests, mutations and all other evidence fields stay equal.

For the continuation trace, first-seen canonical identity normalization independently yields SHA-256 `cd65ca1767ee6134cbcf0be82b06923b7a19f64f4988e88762e3698075677370` before S2 and `7cf174b62656c3b08fff62ddc74c5d6a1c7af8a9eeffd30a19b5ef5f36871573` afterward. Removing exactly the 88 empty tenth-class listing rows and repeating normalization reproduces the complete historical canonical trace byte for byte. The same removal check holds for every affected scenario, preserving the existing selection, object metadata, producer/key identity relations, mutations and recovery operations. The historical golden test applies only these explicit finite increments and the derived digest; every remaining field is still compared strictly. This diagnostic derivation does not qualify the installed Solus S2 package proof.
