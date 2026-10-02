# Opaque transport capacity

R7-C2 admits opaque synthetic objects independently of SESSION. `OpaqueStoreCapacity` and the Node artifact bridge derive the same carrier bounds. R7-C3 owns larger SESSION construction, encryption, transaction composition, projection, and restore; these allowances do not activate those semantics.

## Derivation

All values below are bytes. E through C are future sizing allowances; O, J, and A are active transport admission limits.

| Member                | Equation                                                             |      Bytes |
| --------------------- | -------------------------------------------------------------------- | ---------: |
| Inner envelope E      | 32 MiB                                                               | 33,554,432 |
| Generation G          | E + 256 KiB publication + 16 KiB metadata                            | 33,832,960 |
| Physical copy P       | G + 16 KiB metadata                                                  | 33,849,344 |
| Control wrapper W     | 16 KiB header + 132 framing                                          |     16,516 |
| Acceptance recovery R | (64 KiB receipt + W) + (P + W) + 1 KiB metadata                      | 33,948,936 |
| Recovery record T     | R + 64 KiB surrounding record/readback metadata                      | 34,014,472 |
| Target envelope Q     | T + W                                                                | 34,030,988 |
| Cleanup anchor C      | Q + 2 KiB anchor metadata                                            | 34,033,036 |
| Opaque object O       | C + W                                                                | 34,049,552 |
| Local record          | O + 2 KiB metadata                                                   | 34,051,600 |
| Base64 B              | 4 × ceil(O / 3)                                                      | 45,399,404 |
| JSON entry J          | B + 290 metadata                                                     | 45,399,694 |
| ZIP archive A         | J + ceil(J / 4096) + ceil(J / 16384) + ceil(J / 33554432) + 13 + 158 | 45,413,722 |

The control framing is two plaintext length prefixes (8), outer magic (12), version/algorithm (4), key ID (68), nonce (16), ciphertext length (4), and tag (20). The cleanup anchor contains the complete target envelope and is not recursively anchored. Tests exercise actual binary writer widths and the anchor codec with bounded identities.

The six-string JSON schema needs at most 290 ASCII metadata bytes with safe 16-digit run/attempt IDs, a 64-character digest, and the eight-digit object length. The encoder admits its exact expanded length before constructing base64. ZIP framing allows one fixed 22-byte name, local and central headers, EOCD, and a 16-byte descriptor; extras and comments are rejected. The archive allowance includes conservative deflate expansion. The pinned `@actions/artifact` 6.2.1 writer with compression level zero produces STORE with J + 158 bytes, verified locally without uploading.

## Semantic and aggregate boundaries

SESSION plaintext remains 1 MiB and its inner encrypted envelope remains 2 MiB. Accepted generation payload remains 1,400,000 bytes, physical copy 1,500,000, and acceptance receipt 64 KiB. Lineage Candidate and Acceptance payloads retain the existing 1,500,000-byte reader limit; other classes retain 1 MiB. The global lineage envelope ceiling remains 1,517,408 bytes. Nested codecs can impose smaller effective limits. Metadata can describe an O-sized opaque carrier, but a known lineage class is checked against its existing payload plus header/framing bound before download; authenticated class admission occurs before payload copying.

The unchanged 72-object scope bound implies at most 108,000,000 retained payload bytes and 109,253,376 downloaded envelope bytes by class/count multiplication. These are structural upper bounds, not newly introduced aggregate policies. Snapshot persistence uploads candidates separately; its aggregate logical-version encoding is not one opaque upload.

Current retained transaction buffers still use those old semantic caps. The owned byte-array sums are generation + envelope for `RetainedStatePreparedCandidate`, payload + envelope + recovery payload for `RetainedStateOpaqueWriteAttempt`, receipt + envelope for `RetainedStateAcceptanceAttempt`, and physical-copy payload + envelope for `RetainedStatePredecessorCopyAttempt`. The other retained record, extraction, preparation, and observed-candidate holders own one payload/recovery/generation array each. Their referenced records, immutable copies, encryption temporaries, and object sets are additional allocations; these sums are not process RSS limits. C3 must reconcile per-phase aggregate ownership when it enlarges those component limits. C2 does not enlarge them.

The Node shared cache remains 64 MiB across conditional representations and verified records, with the existing 32 verified-entry limit. One maximum carrier fits; admitting a second evicts and wipes the oldest before cloning. Raw staging reads allocate only the admitted stat size, read fixed chunks, and recheck identity/size. The local record reader bounds the record before allocation and wipes its temporary record; C# staging wipes failed reads.

Archive bytes, extracted JSON, decoded ciphertext, caller copies, and cache copies can coexist transiently. Each is individually bounded; the cache ledger is not an operation-wide memory cap. The flat JSON guard rejects nested graphs and duplicate keys before full parsing, scans scalar strings without accumulating character copies, and checks deadlines. Base64 length and alphabet/padding are checked before decoding. Inflation stops at the admitted declared entry size, wipes chunks on failure/cancellation, and verifies CRC and actual length. Commands/results remain 256 KiB; request, count, correlation, and deadline policies are unchanged.

## Verification

The shared C# store conformance runs maximum-size byte-exact upload, metadata, download, readback, and deletion against local, synthetic, and C#-to-Node adapters, plus cap-plus-one rejection. Node tests cover the official-operation lifecycle with synthetic services at O, the pinned SDK archive at J, malformed sizes/ZIP/JSON, fixed reads, cache eviction, and cleanup. `OpaqueTransportCapacityTests` pins C#/Node-facing bounds and unchanged semantic limits, exercises existing class boundaries, and checks rejection before download/payload allocation.

Run `npm run check`, Release .NET tests, `npm run runtime:integration`, and `npm run dist:check` after regenerating the action bundle. All fixtures remain synthetic; this capacity work makes no provider call or release-readiness claim.
