# Prefix Materialization (Issue #50)

`prefix-materialization` is the canonical logical projection and append-safe provider-prefix contract implementation for the M4 live path. Its shared framing, hash preimages, domain tags, envelope field sets, and identity domains are owned by the [session ledger and prefix contract](session-ledger-and-prefix-contract.md) (`## Prefix Contract`, `## M4 Batch #1 Frozen Vocabulary`); this document records only the workstream surface owned by issue #50 and does not restate the shared algorithms.

Production C# (`AgenticPrReview.Runtime.Prefix`) owns full materialization. Production TypeScript (`src/prefix-contract/`) owns the host-side digest / interaction-id / identity helpers. Full-stream golden vectors under `protocol/fixtures/prefix-contract/v1/` are produced by the test-only TS oracle (`src/prefix-contract/generate-fixtures.testhelper.ts`, run via `scripts/regenerate-prefix-contract-fixtures.mjs`) and verified read-only by the C# test suite.

## C# API surface

Namespace `AgenticPrReview.Runtime.Prefix`:

- `PrefixMaterializer.Materialize(PrefixMaterializationInput) -> PrefixMaterializationOutcome(Value?, Diagnostics)` — deterministic, fail-fast; a failure carries exactly one `PrefixDiagnostic`.
- `PrefixMaterializationInput` = `MaterializationHistory History`, `ValidatedContextSource CurrentContext` (#49), `InteractionIdentity Interaction` (#49), `ExpectedIdentities ExpectedIdentities` (#49), `string SessionEpoch`, `RawCacheContractEnvelopes Envelopes`.
- `MaterializationHistory` = `BootstrapHistory` (also used for recovery-root materialization) | `ContinuationHistory(ValidatedLedger Prior)` | `ResetHistory(ValidatedLedger AcceptedPredecessor)`.
- `RawCacheContractEnvelopes` = five raw `JsonElement` envelopes (template, policy, tools, cache config, adapter). The materializer validates each in the stage order below; there is no caller path that skips validation.
- `CacheContractDigests.ComputeTemplateId / ComputePolicyId / ComputeToolDefinitionId / ComputeCacheConfigId / ComputeAdapterId(JsonElement) -> DigestOutcome`.
- `InteractionIdDeriver.Derive(PredecessorLedgerReference, consumedInputSha256, currentHeadSha, interactionOrdinal) -> InteractionIdOutcome`; `PredecessorLedgerReference` = `Bootstrap | LedgerHash(sha256Hex)`.
- `PrefixMaterializer.LedgerSchemaVersion = 1`, `PrefixMaterializer.PrefixContractVersion = 1`. The internal hash producers accept a test-only version seam for hash-framing invalidation vectors.

History identity rules: continuation requires every prior-header identity field (session scope, `sessionEpoch`, provider/model, five digests) to equal the supplied values; reset requires session-scope fields and `sessionEpoch` to equal the supplied values while cache-contract fields are free to differ.

The dynamic `review_context` segment is never projected from a caller-fabricated DTO: the materializer runs the supplied `ValidatedContextSource` through #49 `LedgerBuilder.BuildReviewContext`; only a successfully built `ReviewContextRecord` enters the projection.

`PrefixMaterialization` is deeply immutable: stable logical stream, stable provider-block stream, dynamic suffix (both representations), `StableSegmentCount`, stable byte lengths per stream, both prefix hashes, and the five recomputed cache-contract digests. Both prefix hashes cover the stable streams only.

## TypeScript API surface

Module `src/prefix-contract/`:

- `PrefixResult<T> = { ok: true; value: T } | { ok: false; errors: readonly PrefixError[] }`; `PrefixError = { code, path }`, with `path: ""` when no path applies. No throw crosses the public boundary.
- `computeTemplateId / computePolicyId / computeToolDefinitionId / computeCacheConfigId / computeAdapterId(envelope): PrefixResult<string>`.
- `deriveInteractionId(predecessor, consumedInputSha256, currentHeadSha, interactionOrdinal): PrefixResult<string>` with `predecessor = { kind: 'bootstrap' } | { kind: 'ledger'; sha256Hex }`.
- `validateIdentity(value)`, `validateModelSnapshot(modelId)` (rejects `latest`).
- Shared identity strings must be non-empty, well-formed UTF-16, free of C0/DEL controls, and at most 256 UTF-8 bytes; both implementations count bytes with an allocation-bounded code-unit scan and reject unpaired surrogates before framing.
- Envelope validators accept `unknown`, enforce the exact key set on the raw value, then project.
- The public module exports only the D9 entry points and result/predecessor types; validators, constants, validated snapshots, and domain predicates remain internal.

## Segment projection (D3/D5)

Segment JSON is a closed object discriminated by `kind`, framed per the shared Prefix Contract framing. Stable order: `template`, `policy`, `tools` (always present, empty toolset is `definitions: []`), then one segment per prior ledger record in ledger order (continuation only). The dynamic suffix is exactly the current `review_context` segment.

- `template`: `{definition, kind, templateVersion}`; `policy`: `{constraints, instructions, kind, policyVersion}`; `tools`: `{definitions, kind, toolsetVersion}`.
- `review_context`: `{cacheContractDigest, changedFiles, interactionOrdinal, kind, reviewedBaseSha, reviewedHeadSha, subjectDigest}`; `review_outcome`: `{findings, interactionOrdinal, kind, limitations, summary}`. Optional record fields (`previousPath`, `patch`, finding optionals) are omitted when null.
- Segments carry no ledger-header cache-contract IDs, versions, or session/transaction identity fields; record-local `subjectDigest`/`cacheContractDigest` remain as projected from #49. Cache-config envelope content never enters either stream.
- Reference canonical provider-block projection (D6): each segment maps to exactly one block `{"role": R, "content": [{"type": "text", "text": S}]}` with `S` the segment's canonical JSON as a UTF-8 string. Role mapping: template/policy/tools → `system`, review_context → `user`, review_outcome → `assistant`. No merging, splitting, or reordering.

## Envelope field domains (D4)

Envelope field sets and the `digestId` algorithm follow the shared Prefix Contract. Field domains:

- `schemaVersion` and every `*Version`: JSON integer `1..2_147_483_647`; current contract version is `1`.
- template `definition`, policy `constraints`, tools `policyMetadata`: any canonical JSON value (`policyMetadata` optional, omitted when absent); policy `instructions`: string.
- tools `definitions[]`: `{name, description, inputSchema, policyMetadata?}`; `name` = shared identity domain; `description` = string; `inputSchema` = canonical JSON object; duplicate names (exact, case-sensitive) rejected; ≤ 64 definitions.
- cache config `markerPolicy`/`eligibility`: string; `statelessMode`: boolean. adapter `adapterBuildVersion`: shared identity domain.
- Exact-key-set validation applies only to the five envelope roots and each tool-definition wrapper; objects inside `definition`, `constraints`, `inputSchema`, `policyMetadata` are open canonical JSON data.
- Duplicate property names are forbidden at every object level and detected before canonicalization: contract-owned objects → `prefix_envelope_invalid`; open JSON fields → `prefix_canonical_input_rejected`.
- C# property-name ordering, duplicate detection, arbitrary-string canonicalization, and embedded-identity validation consume .NET 10 `JsonMarshal` raw UTF-8 token views directly. They stream decoded UTF-16 code units into bounded state/sinks, retain at most a bounded diagnostic-name prefix, and never allocate a managed string proportional to one caller-controlled token.
- Canonical number domain: finite IEEE-754 binary64 (C# `double`), ECMAScript `Number::toString` layout, `-0` → `0`. `U+0000` in open JSON string content is emitted as `\u0000`; NUL/control characters are rejected only inside the shared identity-string domain.

## Diagnostics (D10)

`PrefixDiagnostic { Code, Message, CauseCode? }`; `Message` is the code plus an optional safe path (inheriting the shared safe-path and schema-position resolver sections; `#50` owns only its envelope key sets). Validation is fail-fast in this stage order:

1. host-declared identities — `prefix_identity_invalid`, `prefix_model_alias_literal`, `prefix_digest_invalid`, `prefix_git_sha_invalid`, `prefix_ordinal_invalid`, `prefix_epoch_invalid`
2. envelope structure — `prefix_envelope_invalid`
3. embedded identities (`definitions[].name`, `adapterBuildVersion`) — `prefix_identity_invalid`
4. canonical JSON — `prefix_canonical_input_rejected`
5. bounds / digest / equality — `prefix_envelope_too_large`, `prefix_cache_contract_id_mismatch`, `prefix_identity_mismatch`, `prefix_current_context_invalid` (`CauseCode` = first #49 diagnostic in its deterministic order), `prefix_segment_too_large`, `prefix_stream_too_large` (`CauseCode` ∈ `logical-stable|logical-dynamic|provider-stable|provider-dynamic`), `prefix_length_overflow`

TS codes are the mechanical kebab-case mirrors. Envelope check order: template → policy → tools → cache config → adapter.

Within envelope structure, both languages use this fixed fail-fast order: closed root keys in unsigned UTF-16 order (invalid UTF-16 keys use the shared invalid-name sentinel position), missing root keys in the published key-list order, `schemaVersion` before the envelope-specific version, remaining contract-owned scalar fields in declaration order, tool definitions by ascending index with wrapper keys using the same closed-key order, then recursive bounds with object keys in unsigned UTF-16 order and array indices ascending. Canonical-domain checks remain a later stage.

## Bounds (D11)

Payload = canonical JSON bytes before framing; framed = length prefix + payload.

| Constant                            | Value     | Notes                                              |
| ----------------------------------- | --------- | -------------------------------------------------- |
| `MAX_LOGICAL_SEGMENT_PAYLOAD_BYTES` | 262 144   | real-content boundary vectors                      |
| `MAX_LOGICAL_STABLE_STREAM_BYTES`   | 1 048 576 | framed total                                       |
| `MAX_LOGICAL_DYNAMIC_STREAM_BYTES`  | 262 144   | framed total                                       |
| `MAX_PROVIDER_BLOCK_WRAPPER_BYTES`  | 64        | conservative guard bound (real wrapper ≤ 58)       |
| `MAX_PROVIDER_BLOCK_PAYLOAD_BYTES`  | 524 352   | = 2 × segment cap + wrapper; seam-tested           |
| `MAX_PROVIDER_STABLE_STREAM_BYTES`  | 2 101 708 | = 2 × stable cap + 67 × (wrapper + 4); seam-tested |
| `MAX_PROVIDER_DYNAMIC_STREAM_BYTES` | 524 356   | = 4 + block payload cap; seam-tested               |
| `MAX_ENVELOPE_CANONICAL_BYTES`      | 262 144   | checked before digest computation                  |
| `MAX_STABLE_SEGMENTS`               | 67        | informational; the #49 `records` bound enforces it |

Structural bounds: tools ≤ 64; envelope JSON depth ≤ 64 (root value = depth 1, counted per object/array nesting); object properties ≤ 256; array items ≤ 1 024 — violations map to `prefix_envelope_invalid`.

Reachability notes (frozen classification):

- `MAX_LOGICAL_SEGMENT_PAYLOAD_BYTES`: reachable — a template envelope at exactly its canonical cap yields a template segment payload of exactly 262 144 (the envelope and segment wrappers are the same length), covered by a real-content test; `cap + 1` is unreachable because the envelope cap binds first, so the over-cap guard is seam-covered.
- `MAX_LOGICAL_DYNAMIC_STREAM_BYTES`: reachable — the dynamic context is produced through #49 `LedgerBuilder.BuildReviewContext`, and the #49 bounds (`changedFiles` ≤ 200 items, paths ≤ 500 chars with multi-byte UTF-8 content up to 3 bytes per BMP char) allow legal contexts well past the cap; real-content tests cover payload 262 140 (framed at cap), 262 141 (over cap), and the over-segment-cap failure.
- `MAX_ENVELOPE_CANONICAL_BYTES`: reachable, covered by real-content at-cap / cap+1 vectors.

## Fixture contract (D12)

`protocol/fixtures/prefix-contract/v1/` holds JSON vector files plus a closed-index `manifest.json` (`{schemaVersion, generatedBy{tool,version}, creationCrossCheck{tool,version,checkedAt}, vectors:[{id, kind, file}]}`). Entry `id`/`kind` equal the vector file's own; `file` is a relative safe path; every file is referenced exactly once; append/invalidation references resolve to materialization vectors; cycles and self-references are rejected. Vector kinds: `framing-vector`, `digest-vector`, `interaction-vector`, `materialization-vector`, `append-vector`, `invalidation-vector` (`mode` ∈ `materializer|hash-framing`), `invalid-vector` (target-sensitive expected: `materialize` asserts only `csharpCode`; shared helpers assert both languages; defensive codes use seam targets). `creationCrossCheck.checkedAt` is fixed creation evidence and is never rewritten by generator reruns.

Both consumers validate the complete recursive `materialization-vector.input` schema, compare materializer mutations using structured property/index segments (never dotted strings), and assert the exact D13 boolean row instead of trusting fixture-declared change flags. Hash-framing invalidation vectors are closed to the two named version mutations, require byte-identical base/mutated streams, fix the D13 row to `false,false,true,true`, and are independently consumed in both languages. Framing vectors include the required `["ab", "c"]` versus `["a", "bc"]` identity-concatenation proof.
