# Restricted encrypted Agent state

Status: normative R2 current-format security and storage-conformance contract implemented by issue [#87](https://github.com/SolusQuest/agentic-pr-review/issues/87), connected to the bounded R3 live commit by issue [#108](https://github.com/SolusQuest/agentic-pr-review/issues/108), and exercised across a concrete fresh-process Host boundary by issue [#109](https://github.com/SolusQuest/agentic-pr-review/issues/109).

This document is the durable copy of the restricted encrypted state contract refined under [#78](https://github.com/SolusQuest/agentic-pr-review/issues/78). It protects the current SESSION plaintext defined by [`agent-session-format.md`](./agent-session-format.md). It fixes authorization, authenticated framing, Host binding, lineage, retention, transition outcomes, and local conformance behavior. It deliberately does not choose a production GitHub Actions artifact, cache, or object transport.

## Restricted class and authorization

The selected class is exactly `workflow-restricted-completed-review`. State is readable only by the trusted same-repository, non-fork Host principal for the exact workflow, repository, review target, and session scope.

Fork-origin, untrusted, cross-repository, and wrong-scope principals have no enumerate, existence, read, write, replace, decrypt, delete, handoff, or publish capability. Authorization occurs before root inspection, storage enumeration, existence checks, key resolution, cryptography, SESSION admission, Agent/provider construction, deletion, handoff, or publication. Possession of a key, key identifier, artifact name, candidate, staging object, selector, or receipt does not grant authority or reveal existence.

The implementation represents successful authorization with a Host-owned `AuthorizedStateAccess` capability whose constructor is private. Store, key, crypto, and SESSION admission operations require that capability. The Agent SESSION module remains capability-agnostic; the dependency direction is Host STATE to Agent SESSION.

## Scope, retention, and independent lineage

The stable storage scope contains repository ID, workflow identity, review target, session ID, provider ID, model ID, adapter ID, policy SHA-256, limits SHA-256, toolset SHA-256, and build ID. It excludes the dynamic current base/head. Stored producer base/head remain authenticated provenance, while current dynamic reviewed identity is admitted separately through the SESSION `same_head` or `verified_ahead` transition.

The class retains the accepted generation and its immediate predecessor for at most seven days from a Host-trusted acceptance timestamp. The service reads its trusted clock once per operation; prepare fixes accepted-at to that value and expires-at to exactly accepted-at plus 604,800 seconds. Restore enumeration exposes only accepted objects, ordered by generation descending and then envelope SHA-256 ordinal, and at most two candidates. One separate staging slot may hold the next prepared envelope. Staging is never a restore candidate. Candidate metadata is at most 16 KiB, accepted envelope bytes are at most 4 MiB, and the complete accepted-plus-staging logical scope, including the exact bytes emitted by the candidate-metadata codec, is at most 6 MiB. Partial, malformed, duplicate, non-adjacent, out-of-order, or over-limit enumeration fails closed and selects no partial set.

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

Plaintext is the complete current SESSION bytes and is at most 1 MiB. The complete envelope is at most 2 MiB. Encryption uses `AesGcm`, a 256-bit key, a random 96-bit nonce, and a 128-bit tag. Repeated encryption of identical plaintext and Host binding must produce distinct nonces and envelope identities.

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

Issue [#88](https://github.com/SolusQuest/agentic-pr-review/issues/88) consumed the Host state integration interface and proved authorization-before-capability, encrypted completed-session acceptance, exact restore, independent Host lineage, same-head/verified-ahead transition behavior, replay/tamper/header/cross-scope failure, and two-fresh-process framework and Native AOT execution through the real local store. The synthetic transport oracle is now the runtime CI source of truth and the comparison fixture is deleted. R4 still owns production GitHub Actions authorization, artifact/cache/object transport, Node bridge, key provisioning, retention jobs, and publication; the local conformance store does not claim those product guarantees.
