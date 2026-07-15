# M4 Session Ledger And Provider Request Prefix Contract

Status: design contract for M4, owned by issue #29.

This document defines the cross-issue semantics for the project-owned live provider path. It does not implement the ledger, provider adapter, artifact transport, or cost evaluation harness.

## Decision Summary

- The C# runtime core owns the provider-neutral canonical session ledger, canonical logical projection, deterministic prefix construction, append and ledger validation. It never owns GitHub side effects.
- Provider adapters own provider-specific request envelopes, cache markers, capability detection, telemetry mapping, and provider invocation. The core ledger schema remains provider-neutral. M4 uses one reference adapter conforming to the selected Anthropic Messages-style capability profile; exact SDK, model snapshot, and request API choices belong to the adapter issue.
- TypeScript remains the GitHub Action host. It owns workflow facts, target and provenance validation, secret/trust-mode selection, state artifact transport, publishing, and the final fail-closed side-effect barrier.
- M4 implementation must prove one explicit, default-off, trusted-workflow-only live path. Its deterministic and trusted live gates each use two separate workflow runs. It remains sticky-only, does not replace `claude-code-cli`, does not implement the full tool loop, and does not require production Native AOT release packaging.
- M4 introduces `StateManifestV2`, which references an independent ledger file in the same state artifact bundle. The M4 live path writes and fully validates v2. Existing v1 paths remain outside this ledger contract until a separately documented breaking cutover.
- A pre-v2 manifest is not migrated or used to construct a ledger. In the M4 live path, v1 is recognized as `unsupported_legacy_v1` and follows the safe bootstrap policy. A v1 rejection fixture prevents accidental reuse.

## Scope And Non-Goals

M4 covers the minimum project-owned live-provider foundation: ledger state, stable prefix construction, provider capability and usage normalization, trusted live invocation, safe state restore/persist, and representative resumed-session cost evaluation.

M4 does not cover:

- replacing or removing `claude-code-cli`;
- a general state migration framework;
- full state/publisher compatibility, finding identity, or publisher idempotency contracts owned by Phase 5;
- full repo-local read/grep/glob or provider/tool-loop orchestration;
- production Native AOT release packaging, download, checksum, or platform distribution policy;
- hard-coded provider pricing in protocol or ledger data;
- raw provider request/response capture or unrestricted prompt archives.

## Ownership Boundary

The runtime core owns:

- the provider-neutral ledger model and its schema contract;
- bounded canonical interaction records;
- session identity material used for restore/invalidation;
- canonical logical projection and deterministic prefix materialization;
- append, round-trip, prefix-extension, and ledger integrity invariants;
- runtime-side ledger validation and candidate ledger output.

The provider adapter owns:

- provider capability and cache eligibility observation;
- provider-specific request envelope and cache-control representation;
- mapping provider usage into normalized usage;
- provider errors, retries, and provider request invocation;
- adapter version and provider/model identity in the prefix hash domain.

The TypeScript host owns:

- trusted invocation selection and provider-secret policy;
- GitHub repository, PR, head SHA, workflow event, and artifact provenance;
- transport of optional ledger input and candidate ledger output across the process boundary;
- manifest v2 descriptor validation and state artifact upload/restore;
- host-owned result assembly, budget enforcement, publishing, and side-effect barriers.

The runtime never receives `GITHUB_TOKEN`, never calls GitHub APIs, and never publishes comments or artifacts.

## M4 Runnable Surface And Trust Mode

The first M4 live path is experimental and explicit:

- it is default-off and selected by a distinct live-provider runtime mode;
- it runs only from a trusted workflow and trusted/immutable runtime source;
- fork-origin PRs and untrusted checkout execution fail closed before secret exposure;
- provider credentials enter only through an explicit allowlisted runtime secret channel, such as an allowlisted environment or equivalent OS secret mechanism;
- credentials never enter CLI arguments, protocol files, ledger, trace, manifest, normal artifacts, logs, or structured output;
- the current deterministic path remains provider-secret-free;
- the path produces sticky-only output and leaves the default runtime path unchanged.

The exact environment variable names, executable construction, SDK, and provider model snapshot are implementation decisions under the live adapter issue. The reference adapter must conform to the selected Messages-style capability profile; gateway-specific behavior is not implicitly covered. The trust category and fail-closed rules are design decisions in #29.

## StateManifestV2 And Legacy Policy

The existing state manifest is an active v1 contract for current action paths. It is not rewritten in place under `version: 1`. M4 adds a v2 shape for the ledger-aware live path.

The v2 manifest keeps the existing host-owned state metadata and adds ledger and provider-run-metadata descriptors with at least:

- relative ledger path inside the state bundle;
- exact ledger content hash and byte length;
- ledger schema version and prefix contract version;
- session identity and provider/model/adapter identity;
- generation or commit identity for stale-writer detection;
- provenance binding to repository, PR, head repository, workflow/event, and producing run.

The provider-run-metadata descriptor binds the complete `ProviderRunMetadataV1` sidecar by path, hash, byte length, schema version, and producing generation. It is restricted telemetry evidence, not ledger content and not a raw provider transcript.

The ledger remains a separate file. The manifest binds it; it does not embed the full ledger content.

For M4 it is a restricted durable state file inside the existing state bundle, not a normal review artifact and not a raw-diagnostics artifact. It is restored only through the M4 v2 namespace and is never selected through the current v1 backend namespace. The manifest is authoritative for host facts, provenance, state key, and generation; the ledger is authoritative only for its bounded runtime-owned logical records. Duplicate identity fields must match the manifest or the candidate is rejected.

The logical state key is the canonical tuple `m4-ledger-v2 / repository / headRepository / pullRequest / workflowIdentity / trustedExecutionDomain`; artifact names and run ids are not substitutes for this key. Automatic selection considers only bundles in this namespace.

Compatibility policy:

- v1 is not converted to v2;
- v1 fields such as session id, usage, prompt hash, review input hash, or legacy runtime files are not used to synthesize a ledger;
- v1 is classified as `unsupported_legacy_v1` for the M4 live path and follows the safe bootstrap policy;
- missing, incompatible, corrupt, unsafe, or integrity-mismatched ledger state also follows safe bootstrap without unsafe reuse;
- existing v1 legacy/deterministic paths are not silently cut over by #29; global removal of v1 is a separate breaking-release decision;
- v2 is the first supported manifest contract for the live ledger path.

The implementation must make the v1 classification observable through a bounded warning/phase reason. The action may complete a valid bootstrap review; it must not treat an unsupported restore state as a provider cache miss.

## Ledger Content Contract

The ledger is a bounded, schema-owned, provider-neutral logical record. M4 `ProviderSessionLedgerV1` contains only these record variants:

- session and contract identities;
- cache-relevant policy/configuration identities;
- ordered prior interaction records containing an ordinal, role, source head and base provenance, and a bounded canonical review-context projection;
- validated structured review-outcome projections derived from the accepted `ReviewResultV1` shape, with no raw model transcript;
- version, hash, ordering, and provenance metadata required for integrity.

`ProviderSessionLedgerV1` does not use external or content-addressed references. All durable content required to materialize the M4 prefix is inline in the ledger and is covered by the manifest descriptor hash. Any reference-shaped field is rejected by the closed schema and is an integrity failure.

The allowed interaction roles are exactly `review_context` and `review_outcome`. The `review_context` projection contains only the bounded changed-file metadata (`path`, `previousPath`, `status`, `additions`, `deletions`, `changes`, and patch `sha256`/`truncated`/`maxChars` metadata), a subject digest, and a policy/configuration digest. It excludes PR title/body, policy text, patch text, and host facts. The `review_outcome` projection contains only `summary`, `findings`, and `limitations` from the accepted `ReviewResultV1`; it excludes usage, trace, warnings, diagnostics, budget status, and host facts. The current raw PR delta is never persisted, but its bounded canonical `review_context` projection is dynamic during the current call and becomes stable prior history only after the candidate state is accepted. Untrusted content is data inside framed record fields; it cannot select a control role, policy, tool definition, or secret channel.

It must not contain:

- rendered provider prompts or raw provider message archives;
- raw provider request/response bodies;
- auth headers, provider secrets, or debug captures;
- private runner paths or environment values;
- unbounded patch, tool, or provider output;
- content that can be reinterpreted as system/developer instruction.

Restored repository and PR content remains untrusted data. Canonicalization must preserve the data/control boundary and must not allow persisted content to change policy, tool definitions, provider configuration, or secret handling.

`ProviderSessionLedgerV1` has a finite record and byte bound. Exact numeric limits are implementation parameters, but the behavior is fixed: an existing state over the bound is invalid and follows observable safe bootstrap; a candidate append that would exceed the bound is rejected, is not persisted, and is a current-run state contract failure. M4 does not perform automatic rollover, silent truncation, or model-generated lossy compaction. A later generation/rollover policy is a separate design decision.

## Session Identity And Lifecycle

Session continuity uses three distinct identity layers:

- **session scope**: repository, head repository, PR number, workflow identity, trusted execution domain, and host-owned session epoch;
- **cache-contract identity**: provider, model, adapter, ledger/prefix contract, template, policy, tool-definition, and cache-relevant configuration;
- **generation provenance**: predecessor `stateGeneration`, current `stateGeneration`, `ledgerEpoch`, reviewed head/base SHAs, current head/base SHAs, producing run, and commit identity.

Normal head pushes on the same PR, repository, workflow identity, and trust domain continue the same session and append a new `stateGeneration` record within the same `ledgerEpoch`. A base branch/SHA change, force-push, or cache-contract identity change starts a new `stateGeneration` under the same session scope with a new `ledgerEpoch`, an empty ledger, and an explicit predecessor link; it does not reuse old prefix records. An empty-generation reset is distinct from bootstrap: its header carries the accepted predecessor manifest hash, predecessor ledger hash, predecessor `stateGeneration`, and reset reason, but its logical record stream contains no predecessor records. Its first candidate contains that reset header plus exactly one new context and one new outcome record. The interaction identity uses the actual predecessor ledger hash (not the `bootstrap` sentinel), and its `ledgerEpoch`-local interaction ordinal starts at zero. The host validates this reset form by requiring the header to match the accepted predecessor and by rejecting any copied predecessor records. A repository, head repository, PR, workflow identity, or trust-domain change requires a clean bootstrap under a new session scope with a new `ledgerEpoch`; every new-session bootstrap root has `stateGeneration` zero and interaction ordinal zero. A ledger/prefix contract version change is incompatible and cannot restore the old state. These rules are normative, not implementation choices.

Current head/base identity is generation provenance, not session scope. The workflow event type is provenance, while workflow identity and trust domain determine scope. Host-owned repository and PR facts cannot be replaced by runtime ledger values.

The identity precedence is fixed: a ledger/prefix contract version mismatch is incompatible and is evaluated before any generic cache-contract change; it never creates a new generation from the old artifact. Provider/model/adapter, template, policy, tool, or cache-config changes create an empty `stateGeneration` with a new `ledgerEpoch` under the same session scope. The host classifies a head update as a normal push only when the new head is a descendant of the accepted prior head; a non-descendant or unknown ancestry (including a force-push) creates an empty `stateGeneration` with a new `ledgerEpoch`. A base-ref name change or base-SHA change likewise creates an empty `stateGeneration` with a new `ledgerEpoch`; base advancement is intentionally conservative and is not treated as an ordinary head append.

The lifecycle is:

```text
missing
  -> bootstrap
compatible
  -> restore -> materialize -> invoke -> validate -> append -> persist
invalid/incompatible/unsafe
  -> observable bootstrap
```

Expected invalidation, corruption, and unsafe provenance are distinct outcomes. Expected invalidation follows the generation rules above. Automatic selection of a missing, expired, corrupt, unsafe, incompatible, or mismatched artifact bootstraps with an observable reason. An explicitly selected artifact with any of those conditions fails closed before provider invocation; it never silently bootstraps.

Automatic recovery from an invalid or unavailable accepted artifact is a separate root transition, not a generation derived from that artifact. The host creates a new session epoch under the same state key, sets `stateGeneration` to zero, uses the `bootstrap` predecessor sentinel, and writes a root header with the new epoch and observable recovery reason. The invalid artifact's bytes are never used as a predecessor. Acceptance of that root is guarded by the observed current-selector revision (including an empty revision when no accepted marker exists), so it can supersede the invalid current marker but cannot overwrite a newer valid successor. Old markers remain immutable and non-current. The same recovery-root rule applies to contract-version incompatibility and an over-bound ledger; those cases never create a generation from the incompatible artifact. Explicitly selected invalid state does not enter recovery and remains fail-closed.

An interaction is appended only after the provider result and candidate ledger pass runtime validation. The host derives `interactionId = SHA256(UTF8("agentic-pr-review/interaction/v1") || 0x00 || encodeIdentity(predecessorLedgerSha256) || encodeIdentity(inputSha256) || encodeIdentity(currentHeadSha) || encodeIdentity(interactionOrdinal))`. For a new-session bootstrap, `predecessorLedgerSha256` is the literal sentinel `bootstrap` (not a hash). For a same-session empty-generation reset, it is the actual accepted predecessor ledger hash. In both cases the new `ledgerEpoch`'s ordinal starts at zero. `interactionOrdinal` is the zero-based ordinal within the `ledgerEpoch`, encoded as its ASCII decimal string; it increments once per accepted interaction in a compatible successor and resets to zero for a new session, recovery root, or empty-generation reset. The context and outcome records share this id; retries reuse it. `stateGeneration` increments for every accepted successor independently of `interactionOrdinal`. `ProviderRunMetadataV1` contains the interaction id, consumed input hash, result hash, trace hash, predecessor ledger hash, and candidate ledger hash. The host independently requires the trace/result input hashes, exact result/trace bytes, metadata hashes, and candidate final outcome projection to agree before accepting the transaction. A candidate manifest binds the same values and predecessor. The host derives the expected current `review_context` projection from the validated `ReviewInputV1` and host facts and requires exact equality. The For a compatible continuation, the candidate ledger must preserve the predecessor ledger byte-for-byte in logical content and append exactly two new records—one context and one outcome—with the current interaction id. For a same-session empty-generation reset, it must contain the matching reset header and exactly those two new records, with no predecessor records. For a new-session bootstrap, it contains only its bootstrap header and those two records. In all three forms, it may not delete, modify, reorder, or insert any other record. This exact-append/reset check is independent of prefix materialization.

Provider retries do not silently create duplicate logical interactions. M4 requires per-state-key workflow concurrency with cancel-in-progress; without that correctness barrier the host must not persist live state. The host accepts a candidate only when its state key matches, its predecessor manifest hash equals the currently accepted bundle (or is empty for a root), its `stateGeneration` is predecessor plus one for a continuation/reset or zero for a new session/recovery root, its `ledgerEpoch` equals the predecessor epoch for a compatible continuation and is fresh for a reset/root, and its current head/base provenance matches. Artifact upload is not acceptance. Under the per-state-key workflow lock, the host takes an acceptance snapshot scoped to one state key, `sessionEpoch`, observed selector revision, target `stateGeneration`, `ledgerEpoch`, and interaction, validates the selector result for the exact semantic candidate, writes one immutable `accepted-state` marker for that accepted generation, and atomically advances the current-state selector only when its predecessor pointer still matches the snapshot. Only then does it cross the sticky-publishing barrier. The semantic duplicate key is `(sessionEpoch, selectorRevision, interactionId, predecessorLedgerSha256, candidateLedgerSha256, resultSha256, traceSha256, metadataSemanticSha256)` and excludes producing-run provenance. Candidate headers, manifests, accepted markers, and the current selector all bind the same `sessionEpoch`; candidates from an older epoch are stale before duplicate or conflict comparison. `metadataSemanticSha256` is `SHA256(UTF8("agentic-pr-review/provider-run-metadata-semantic/v1") || 0x00 || RFC8785(semanticMetadata))`, where `semanticMetadata` contains exactly `schemaVersion`, `selectedProviderId`, `observedProviderId`, `resolvedModelId`, `adapterId`, `logicalPrefixSha256`, `prefixSha256`, `capability`, `cacheStatus`, `normalizedUsage`, `retryObservations`, `errorCodes`, and `telemetryCompleteness`; it excludes exactly `producingRunId` and `runAttempt` and the transaction-binding fields `interactionId`, `consumedInputSha256`, `resultSha256`, `traceSha256`, `predecessorLedgerSha256`, and `candidateLedgerSha256`, because those are bound by the outer semantic duplicate key. It also excludes all request IDs, raw errors, endpoints, and arbitrary extensions. Byte- identical semantic duplicates use the smallest `(producingRunId, runAttempt)` present in that acceptance snapshot. If that snapshot contains differing semantic candidates for the same session epoch, selector revision, interaction, and predecessor, it is a conflict and none is accepted in that snapshot. A later candidate targeting the same session epoch, selector revision, predecessor/`stateGeneration`/interaction is permanently `stale_candidate` and cannot revoke or modify that generation's immutable marker or sticky result. A candidate whose predecessor is the currently selected accepted generation is a valid successor and may advance the selector through a new acceptance snapshot. Reruns with the same interaction id and semantic hashes are idempotent. A stale run may finish its review, but must not overwrite a newer ledger.

The current dynamic PR context becomes stable prior history only after the current interaction and candidate ledger have passed validation and the host accepts the result for state persistence. If state upload fails, no sticky review is published by the M4 live path; the candidate is discarded and the next run bootstraps. This is a bounded partial failure, not a claim of continuity.

## Runtime Process Transport

The existing M2 process contract carries `ReviewInputV1`, `ReviewResultV1`, and `ReviewTraceV1`. M4 does not add ledger content to those closed V1 protocol objects.

M4 adds separate, explicit ledger and provider-run-metadata sidecar channels to the trusted live invocation contract. `ProviderRunMetadataV1` is a new sidecar contract, not a change to the closed `Review*V1` objects, and carries the current-run prefix hash, selected/observed provider identity, capability, cache status, normalized usage, retry observations, and telemetry completeness.

The invocation contract is:

- an optional validated ledger input is supplied as a host-owned sidecar for restore; its absence represents bootstrap;
- the runtime writes complete candidate ledger and provider-run-metadata sidecars separately from result and trace, including on bootstrap;
- the runtime performs schema/integrity validation; the host independently validates schema, manifest binding, provenance, and secret/privacy rules;
- the sidecar channel is not an arbitrary path supplied by untrusted input; the host owns the bounded invocation directory and exact file names;
- all four outputs must be valid for a live invocation to exit successfully;
- the host commit order is fixed: validate all four staged outputs; write the ledger and provider-run metadata; write the v2 manifest last with descriptors for both; atomically publish a validated local candidate bundle; upload that candidate; under the per-state-key lock take an immutable acceptance snapshot, select and validate the exact candidate, durably write the `accepted-state` marker, and only then cross the sticky-publishing barrier;
- workstream 8 produces a validated local candidate, not an accepted bundle; workstream 6 owns upload, selector validation, the durable acceptance marker, and the sticky barrier. Marker-write failure means no accepted state and no sticky publication. An uploaded candidate not referenced by an accepted marker is permanently non-restorable and is treated as an orphan;
- an upload or validation failure leaves no accepted state and permits no sticky publication; temporary files and orphan bundles are ignored by the v2 state-key selector;
- a missing or invalid candidate ledger cannot be treated as successful resumed-state output.

Cancellation before the manifest-last local commit leaves staged files discardable. A stale writer may finish its review, but its generation/provenance must fail the host acceptance check and cannot replace an accepted bundle. Exact CLI flag spelling or sidecar file names belong to the implementation issue, but the channel set, ownership, optional-input semantics, validation, atomicity, commit order, and privacy boundary are fixed here.

## Prefix Contract

The contract has two explicit layers:

1. The core produces a provider-neutral canonical logical projection.
2. The adapter produces provider-specific cache-relevant prefix bytes from that projection under an explicit adapter/provider/model identity.

The core first emits an append-safe logical segment stream. The reference adapter then maps that stream one-to-one to a canonical provider-block content prefix: ordered role/content blocks with no provider envelope or closing JSON delimiters. The cache marker is adapter control metadata, not part of either append-safe stream. The adapter places one marker at the current stable/dynamic boundary for each request; moving or regenerating that marker is allowed and does not rewrite the content blocks covered by the strict-prefix invariant. The provider adapter conformance fixture must prove that this block projection is exactly the cache-relevant prefix submitted to the provider and that appending a logical segment appends provider blocks without rewriting prior blocks. Raw HTTP serialization is not itself hashed, but the adapter must not let SDK serialization change the canonical block sequence.

Each logical and provider-block segment is encoded as a big-endian uint32 byte length followed by canonical UTF-8 JSON; segments are concatenated without a total-length prefix. Identity values are non-empty, case-sensitive UTF-8 strings of at most 256 bytes, with no normalization; version values use their ASCII decimal form and `cacheConfigId` is a lowercase hex digest. Define `encodeIdentity(x) = uint32be(byteLength(UTF8(x))) || UTF8(x)`. The exact preimages are:

All cache-contract IDs are host-owned lowercase SHA-256 digests. For every ID, `digestId(tag, envelope) = SHA256(UTF8(tag) || 0x00 || RFC8785(envelope))`, where the tag is ASCII exactly as shown below and RFC 8785 emits UTF-8 bytes. The canonical envelopes are fixed as follows: `templateId` uses `{schemaVersion, templateVersion, definition}`; `policyId` uses `{schemaVersion, policyVersion, instructions, constraints}`; `toolDefinitionId` uses `{schemaVersion, toolsetVersion, definitions}` where `definitions` is an ordered array of `{name, description, inputSchema, policyMetadata}`; `cacheConfigId` uses `{schemaVersion, cacheConfigVersion, markerPolicy, eligibility, statelessMode}`; and `adapterId` uses `{schemaVersion, capabilityProfileVersion, adapterBuildVersion}`. The exact domain tags are `agentic-pr-review/cache-contract/template/v1`, `agentic-pr-review/cache-contract/policy/v1`, `agentic-pr-review/cache-contract/tools/v1`, `agentic-pr-review/cache-contract/config/v1`, and `agentic-pr-review/cache-contract/adapter/v1`, respectively. The listed objects are the complete envelopes: absent fields are absent, explicit null is distinct from absence, and schema defaults are omitted before RFC 8785. Any change to those canonical sources must change its digest or explicitly bump the version in the envelope. Provider and resolved model IDs are host-selected canonical snapshot strings; floating aliases are not valid cache-contract identities. These sources and authorities are fixed by host configuration, not by runtime ledger content.

The interaction and prefix domain tags below end with exactly one NUL octet (`0x00`); they do not contain the UTF-8 bytes for a backslash followed by `0`. This byte rule applies to every `digestId` tag as well.

```text
logicalPrefixSha256 = SHA256(
  UTF8("agentic-pr-review/logical-prefix/v1") || 0x00
  || encodeIdentity(ledgerSchemaVersion)
  || encodeIdentity(prefixContractVersion)
  || logicalSegmentStream
)

prefixSha256 = SHA256(
  UTF8("agentic-pr-review/provider-prefix/v1") || 0x00
  || encodeIdentity(ledgerSchemaVersion)
  || encodeIdentity(prefixContractVersion)
  || encodeIdentity(providerId)
  || encodeIdentity(modelId)
  || encodeIdentity(adapterId)
  || encodeIdentity(templateId)
  || encodeIdentity(policyId)
  || encodeIdentity(toolDefinitionId)
  || encodeIdentity(cacheConfigId)
  || providerBlockContentStream
)
```

Neither hash covers an SDK object, cache marker control metadata, or raw HTTP body. Both outputs are lowercase hexadecimal.

`materializePrefix(ledger, providerConfig, staticPolicy)` is deterministic over its declared inputs. Current time, runner path, random ids, process locale, unordered iteration, implicit SDK defaults, and transient diagnostics are not implicit inputs.

The stable/dynamic split is explicit:

- stable prefix: fixed instructions, policy, tool definitions, versioned identities, and canonical prior turns;
- dynamic suffix: current PR delta, current run metadata, transient diagnostics, fresh tool output, timestamps, and provider request ids.

Required invariants:

- same logical inputs produce byte-identical prefix bytes and the same hash;
- restore round-trip preserves logical identity and prefix identity;
- changing only non-semantic run/artifact metadata does not change the stable prefix;
- appending a validated canonical interaction preserves the old stable prefix as a strict byte prefix of the new stable prefix;
- provider/model/adapter/template/policy/tool/cache-relevant changes produce explicit invalidation or a distinct hash domain;
- provider cache miss with a stable hash is an observed provider outcome, whereas hash drift for unchanged logical inputs is a runtime contract regression.

Canonical JSON uses the RFC 8785 JSON Canonicalization Scheme over UTF-8, without Unicode normalization; lone surrogates and non-finite numbers are rejected, unknown fields are rejected, schema-declared defaults are omitted, and null-versus-absent semantics are explicit. The hash framing and the logical-to-provider-block conformance fixture above are normative; the implementation library and writer are not.

## Capability, Usage, And Cost

The reference capability profile requires messages with the fixed role/content block mapping above, an explicit cache marker at the stable-prefix boundary, per-request cache-read/cache-write/uncached/output usage or an explicit unsupported result, resolved model identity, a cache-disabled/stateless mode, and a reported minimum cacheable-prefix eligibility. Capability and cache status are per-request observations. The run aggregate is unsupported if any request is unsupported or ineligible, unknown if any request is telemetry-unavailable or unknown, partial for a mixture of hits and misses, or if any request is partial, hit for all hits, and miss for all misses. The stateless mode is an adapter-owned request-construction mode that must also carry a provider-advertised or synthetic proof of no cache read/write. Adapters report capability and observed usage without pretending that provider cache behavior is deterministic. Capability distinguishes unsupported, eligible, ineligible, telemetry-unavailable, and unknown cases. Cache status distinguishes hit, partial, miss, unsupported, and unknown.

Every run returns the normalized usage vector through `ProviderRunMetadataV1` when available:

- uncached input;
- cache-write input;
- cache-read input;
- output;
- request/retry observations and telemetry completeness.

The evaluation configuration defines versioned weights outside the protocol. The primary cache metric is normalized input cost:

```text
normalizedInputCost =
  uncachedInput * uncachedWeight
  + cacheWriteInput * cacheWriteWeight
  + cacheReadInput * cacheReadWeight
```

For each provider request, uncached, cache-write, and cache-read input are mutually exclusive partitions of total input tokens when telemetry is complete; retries are summed by attempt. Missing values are unknown, not zero. Inconsistent counters produce incomplete telemetry and cannot pass a cost graduation gate. This precedence is normative. The host-selected provider/model/adapter identity is authoritative; the adapter-reported resolved identity must match it or the current run fails, unless a future explicitly versioned alias map is added. The resolved identity is persisted in `ProviderRunMetadataV1` and the v2 manifest descriptor. The metadata sidecar contains only bounded identities, hashes, counts, statuses, retry/error codes, and completeness flags; it excludes provider request ids, raw error text, raw provider fields, endpoints, and arbitrary extension objects.

A secondary normalized total cost may add output cost. Resumed-session evaluation compares equivalent multi-run sequences with the same provider, model, policy, tools, current PR deltas, request count, and retry policy, including cache-write warm-up:

```text
resumedSessionInputCostRatio =
  sum(resumed normalized input cost)
  / sum(stateless normalized input cost)
```

The stateless comparator serializes the same canonical prior logical context, current PR deltas, provider/model/policy/tools, request count, and retry policy with cache markers disabled and the provider's cache-disabled/stateless mode explicitly selected; only the resume/cache strategy differs. If the provider cannot prove cache-disabled behavior, that live observation is inconclusive and cannot pass the gate. The resumed sequence includes cache-write warm-up. Ratios use checked non-negative decimal arithmetic, sum all valid run costs before one division, and reject a zero denominator. The evaluation profile versions the rounding epsilon (M4 default: 0.01 ratio units) and defines mutually exclusive outcomes: pass is `ratio <= 1.01`, inconclusive is `1.01 < ratio <= 1.05`, and regression is `ratio > 1.05`. Every complete suite must include all seven mandatory scenario classes: large prior context/small delta, multiple normal head pushes, repeated no-finding/finding outcomes, cache-write warm-up, partial hit, isolated miss, and multi-request/retry. Each of three consecutive complete suites must pass for cost graduation; a suite-level regression in each of three consecutive complete suites blocks graduation. Inconclusive or telemetry-incomplete suites cannot pass and do not count toward either window. Prefix hash drift is a contract regression and is not covered by provider tolerance.

M4 implementation closure requires the two deterministic and two trusted live workflow proofs plus an executable cost harness and at least one complete synthetic suite. Three-suite cost graduation is a separate runtime-graduation gate: M4 may remain experimental when live cost is inconclusive or regresses, but it cannot be promoted to a default path; three consecutive regression suites are an explicit graduation blocker.

## Failure And Partial-Success Semantics

- Missing, expired, incompatible, corrupt, unsafe, or untrusted prior ledger: observable safe bootstrap; no partial ledger reuse.
- Current-run ledger validation, hash, or process-contract failure: runtime contract failure; do not publish candidate runtime output as valid resumed state.
- Provider success followed by append or candidate-ledger validation failure: runtime/state contract failure; no invalid ledger or sticky review is published.
- Valid staged state followed by ledger artifact upload failure: bounded partial state failure; the candidate is not accepted, no sticky review is published, and the next run bootstraps.
- State upload success followed by sticky publication failure: the v2 state is valid and may be restored; publication failure is reported separately and does not make the ledger corrupt.
- Same prefix hash with provider miss: observed provider/cache outcome, not a deterministic contract failure.
- Different prefix hash after a legal provider/model/policy/version change: expected invalidation, not corruption.

## Implementation Follow-Ups

The following workstreams are created or explicitly identified before #29 closes. They may begin as refinement-ready outlines; each must be refined to agent-ready before implementation and must not redefine this document:

1. `StateManifestV2` descriptor, v2 namespace, local bundle schema, host-fact authority, and v1 unsupported-state handling. It does not own artifact selection/upload or concurrency orchestration.
2. `ProviderSessionLedgerV1` schema, bounded content validation, integrity, unsafe/corrupt fixtures, over-bound restore bootstrap, and append-rejection behavior. It does not design rollover.
3. Canonical logical projection, append-safe provider-prefix segment stream, `prefixSha256`, and golden byte/hash fixtures. It does not own the provider envelope.
4. Provider capability, normalized usage, cache status, and `ProviderRunMetadataV1` sidecar schema.
5. Trusted live provider adapter and live-mode configuration, including the selected capability profile, secret injection, trust gates, provider selection, and bounded provider failures. It consumes the existing process and metadata contracts and does not own sidecar I/O or artifact persistence.
6. Cross-workflow artifact selection/upload, v2 state persistence, stale-writer/concurrency barriers, selector validation, durable acceptance marker, sticky-after-upload behavior, and partial-success handling, starting from a validated local candidate produced by workstream 8. It must never restore an uploaded bundle that lacks the accepted marker.
7. Resumed-session cache/cost evaluation with deterministic synthetic provider fixtures and representative multi-run sequences.
8. Process-boundary sidecar I/O through a validated local candidate bundle: ledger input/output, provider-run metadata, result/trace staging, independent host validation, manifest-last local commit, exit semantics, cancellation cleanup, and local no-overwrite behavior. It ends before GitHub artifact upload and sticky publication.

Each follow-up owns its implementation API, files, test layout, exact bounds, and diagnostic names while inheriting the contract above.

## Proof Gates And Validation Plan

M4 implementation closure has two separate proof gates. The deterministic contract gate is required and must use two real, isolated workflow runs with a secret-free synthetic provider to prove restore, append, hash, and fallback; it is suitable for normal CI but is not two in-process test cases. The live observation gate is also required for M4 implementation closure and must run the trusted reference adapter across two controlled workflow runs using the allowlisted secret channel. It is opt-in and never a per-PR requirement; its cache/usage observations are graduation evidence, not deterministic contract oracles. Issue #29 itself is only the design gate and does not execute either workflow proof.

The design is validated with `npm run check` and documentation link/format checks. Follow-up fixtures must cover:

- same-ledger restore and exact prefix/hash stability;
- append and strict prefix extension;
- stable/dynamic boundary changes;
- provider/model/template/policy/tool/config invalidation;
- v1 unsupported legacy state and v2 validation;
- missing, expired, corrupt, incompatible, unsafe, and hash-mismatched ledger;
- provenance mismatch, stale writer, rerun, and duplicate append;
- provider telemetry unavailable, unsupported cache, hit, partial, miss, and inconsistent counters;
- stateless/resumed cost comparison including warm-up and output reporting;
- secret, raw prompt, path, auth-header, and prompt-injection-like content non-disclosure.

## Migration Impact

This is an explicit state-artifact format boundary, not a general migration framework. The v1-to-v2 policy is no conversion, no backfill, no ledger reuse, and safe observable bootstrap for the M4 live path. If a later release removes the existing v1 legacy/deterministic writer and reader globally, that is a separate breaking release with migration notes and a retention-window plan.

## Open Implementation Questions

These do not reopen the cross-issue contract:

- exact v2 JSON property names and `$id`;
- exact sidecar filenames and CLI flags;
- exact environment variable names and provider SDK;
- exact ledger byte/turn limits;
- C# namespaces and file layout;
- test framework, helper layout, and diagnostic code strings;
- exact provider model snapshot used by the reference adapter.

## M4 Batch #1 Frozen Vocabulary

### Single normative source

This entire section, including every current subsection under it, is the single normative source for the shared M4 Batch #1 machinery it defines. Workstream issues #48 / #49 / #51 (and later #50, #52..#55) MUST reference these subsections by heading anchor and MUST NOT re-state the algorithms, tables, pseudocode, byte-exact per-sidecar deep-path diagnostic literals, or conformance vectors defined here. Workstream issues MAY publish their own workstream-specific tables (error-code mappings, API surface, fixture matrices, workstream-specific acceptance criteria) that reference this section by anchor. Non-exhaustive examples of shared items owned by this section: epoch identity encoding and lifecycle, generator authority, transition vocabulary, reset and recovery-root reasons and observed-condition mapping, numeric bounds intersection, identity domain, repository syntax, Git SHA domain, floating-alias rejection, duplicate-identity equality tables, sidecar byte caps, canonical JSON helper ownership and vendor-and-replace policy, `unsupported_legacy_*` naming, `interactionId` scope, `producingRunId` regex, aggregate token overflow contract (safe-integer boundary, pre-addition check, stage/code ownership, precedence), root-header sentinel disparity, safe-path sanitizer, schema-position resolver, traversal pseudocode, terminal-safety invariant, truncation formula, dual message caps, per-sidecar deep-path oracle table, and language-agnostic conformance vectors.

**Regulation vs. implementation.** For contracts split between shared regulation and per-workstream implementation, this section owns the regulation (boundaries, wire vocabulary, algorithm shape, precedence, byte-exact expected outputs) and each workstream owns its own implementation (language-specific types, API surface, tests, fixtures under its schema). Example: aggregate token overflow — this section owns the safe-integer boundary, the pre-addition check rule, and the stage/code ownership; #51 owns the TypeScript implementation, fixtures, and workstream-specific acceptance criteria.

Any change to a shared item requires a docs PR to this file first, followed by coordinated updates to the referencing workstream issues before any dependent implementation PR may be re-approved.

This section is normative for the first foundational M4 implementation batch (issues #48, #49, #51) and inherited by later follow-ups (#50, #52-#55). It freezes exactly those cross-workstream contract values that must not diverge between the host (TypeScript) and the runtime (C#). Fields not explicitly listed here remain workstream-scoped. For any field that is duplicated across manifest / ledger / metadata boundaries, the accepted domain, pattern, and equality semantics default to shared: sibling workstreams may only diverge with an explicit note in this section.

### Epoch identity encoding and lifecycle

- `sessionEpoch`, `ledgerEpoch`, `transition.predecessorLedgerEpoch`, and every `producingGeneration.{sessionEpoch,ledgerEpoch}` are opaque host-generated strings of exactly 22 characters over the base64url alphabet, matching the regex `^[A-Za-z0-9_-]{22}$`. #48 exports the constant as `EPOCH_ID_REGEX` and the branded TypeScript alias `EpochId`.
- The literal string `"bootstrap"` is not an `EpochId`. It is the fixed sentinel used only in `transition.predecessorLedgerSha256` (and in the ledger root header equivalent), and in `transition.predecessorManifestSha256` on the manifest side, for `bootstrap` and `recovery_root` kinds. It never appears in any epoch field.
- **Root-header sentinel disparity.** The manifest root transition (`bootstrap`, `recovery_root`) carries both `predecessorManifestSha256` and `predecessorLedgerSha256` with the `"bootstrap"` sentinel. The ledger root header carries only `predecessorLedgerSha256` with that sentinel; `predecessorManifestSha256` is absent from `bootstrap` and `recovery_root` ledger headers.
- **Lifecycle matrix.** The `sessionEpoch` / `ledgerEpoch` / `predecessorLedgerEpoch` freshness relationship is normative:

  | kind            | `sessionEpoch`              | `ledgerEpoch`                        | `predecessorLedgerEpoch`                  |
  | --------------- | --------------------------- | ------------------------------------ | ----------------------------------------- |
  | `bootstrap`     | fresh                       | fresh                                | absent                                    |
  | `continuation`  | equals accepted predecessor | equals accepted predecessor          | equals `generation.ledgerEpoch`           |
  | `reset`         | equals accepted predecessor | fresh and different from predecessor | equals accepted predecessor `ledgerEpoch` |
  | `recovery_root` | fresh                       | fresh                                | absent                                    |

  #48 verifies candidate-internal relationships only (`predecessorLedgerEpoch` presence per kind, continuation vs reset epoch equality/inequality, `bootstrap` and `recovery_root` requiring `stateGeneration == 0`). Comparison against the actual accepted predecessor is #53's and #55's responsibility.

- **Generator authority (#55).** The token validator (#48) accepts any string matching `EPOCH_ID_REGEX`. The token producer (#55) uses this per-kind matrix:

  | kind            | producer action                                                                  |
  | --------------- | -------------------------------------------------------------------------------- |
  | `bootstrap`     | generate fresh `sessionEpoch`; generate fresh `ledgerEpoch`                      |
  | `continuation`  | reuse both epochs from the accepted predecessor                                  |
  | `reset`         | reuse `sessionEpoch` from the accepted predecessor; generate fresh `ledgerEpoch` |
  | `recovery_root` | generate fresh `sessionEpoch`; generate fresh `ledgerEpoch`                      |

  A fresh token MUST be produced from 16 cryptographically random bytes encoded as unpadded base64url. Freshness is a correctness condition, not a recommendation. `reset` is not a root transition; it stays within the same session scope.

### Transition kinds

Both the manifest (`transition.kind`) and the ledger header (`header.kind`) use the same four values as the wire vocabulary:

- `bootstrap`
- `continuation`
- `reset`
- `recovery_root`

The legacy `"recovery"` spelling is not used on the wire.

**Public API naming.** C# / TypeScript public types, method names, and diagnostic codes track the wire vocabulary: `RecoveryRootTransition`, `CreateRecoveryRoot`, `ValidateRecoveryRoot`, `ledger_recovery_root_shape_violation`, `ledger_recovery_root_reason_missing`, `ledger_recovery_root_reason_mismatch`. Internal test file names and local variable names are not constrained.

### Reset reasons (closed enum)

`transition.reason` under `kind == "reset"` and the ledger header `resetReason` under `kind == "reset"` use the same three values:

- `base_change`
- `head_history_discontinuity`
- `cache_contract_change`

`head_history_discontinuity` covers both force-push and non-descendant / unknown-ancestry advances of the head SHA. The legacy `force_push` / `base_changed` / `cache_contract_changed` spellings are not used.

### Recovery-root reasons (closed enum) and observed-condition mapping

`transition.reason` under `kind == "recovery_root"` and the ledger header `recoveryReason` under `kind == "recovery_root"` use the same seven values. Their normative mapping to observed conditions:

| Observed condition                                                                                                                                                                                                                                                                                                    | Transition                                           |
| --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------- |
| No accepted selector / no current state exists                                                                                                                                                                                                                                                                        | `bootstrap` (not a recovery root)                    |
| Accepted artifact expired, deleted, or otherwise cannot be downloaded within the transport contract                                                                                                                                                                                                                   | `recovery_root` with `unavailable_accepted_artifact` |
| Selected artifact is v1 or an unknown contract version                                                                                                                                                                                                                                                                | `recovery_root` with `contract_version_incompatible` |
| Malformed JSON, schema-invalid or internally semantic-invalid manifest / ledger / metadata payload (semantic-invalid includes cross-field / ordering / partition / aggregate / Unicode-well-formedness failures), non-canonical ledger bytes (per #49), duplicate JSON keys, or over-bound manifest or metadata bytes | `recovery_root` with `corrupt_accepted_artifact`     |
| Descriptor length / hash mismatch, ledger digest mismatch, transaction-binding mismatch, host-authoritative identity mismatch between manifest / ledger / metadata sidecars                                                                                                                                           | `recovery_root` with `integrity_mismatch`            |
| Provenance / trust failure                                                                                                                                                                                                                                                                                            | `recovery_root` with `unsafe_provenance`             |
| Selected manifest disagrees with expected state key                                                                                                                                                                                                                                                                   | `recovery_root` with `state_key_mismatch`            |
| Ledger raw bytes exceed 512 KiB, or an otherwise-decoded ledger exceeds the 256 KiB canonical-byte cap                                                                                                                                                                                                                | `recovery_root` with `over_bound_ledger`             |

The legacy `predecessor_*` spellings are not used. Note that `unavailable_accepted_artifact` includes artifact expiry.

**String safety applies to every persisted sidecar.** #48 (manifest), #49 (ledger), and #51 (metadata) each recursively reject both **unpaired UTF-16 surrogates** and the character **`U+0000` (NUL)** in every string value and every property name of their persisted sidecar payload, before their canonical serializers or hash producers run. This is a correctness requirement — a persisted sidecar with a lone surrogate would fail canonical serialization / hash reproduction downstream. #48 emits `manifest_shape_invalid` with message code `x_invalid_unicode`; #49 emits `ledger_invalid_unicode`; #51 emits `invalid-metadata-unicode`. #48's Unicode scan runs after JSON parse and after the legacy-v1 short-circuit, and before any Ajv or cross-field evaluation. A v1 legacy manifest that also contains a lone surrogate is still classified as `unsupported_legacy_v1` (legacy short-circuit wins); a v2 manifest that would otherwise be valid but carries a lone surrogate anywhere is `manifest_shape_invalid` with message code `x_invalid_unicode`.

### Numeric bounds intersection

- `stateGeneration`, `predecessorStateGeneration`, `interactionOrdinal`: integer `0..1_000_000`.
- `runAttempt` / `producingRunAttempt`: integer `1..2_147_483_647`.
- `pullRequest`: integer `1..2_147_483_647`.
- `producingRunId`: canonical decimal string matching `^[1-9][0-9]{0,18}$`. This applies to every occurrence (manifest `provenance.producingRunId`, metadata `producingRunId`).
- `stateKey.repository`, `stateKey.headRepository`: JSON Schema draft-07 constraints `minLength: 3`, `maxLength: 200` (character count), `pattern: ^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$`. The semantic validator additionally enforces `<= 256 UTF-8 bytes` (byte length cannot be expressed by draft-07).
- Identity strings (`workflowIdentity`, `trustedExecutionDomain`, `providerId`, `modelId`, and the metadata `selectedProviderId` / `observedProviderId` / `resolvedModelId`): non-empty, `minLength: 1`, `maxLength: 256` characters, `<= 256 UTF-8 bytes`, case-sensitive, no Unicode normalization, no control characters (`U+0000..U+001F` and `U+007F` rejected). The metadata 128-ASCII-only rule is superseded by this shared domain.
- Git SHA fields (`reviewedHeadSha`, `reviewedBaseSha`, `currentHeadSha`, `currentBaseSha`, `producingActionSourceSha`): every occurrence matches `/^([a-f0-9]{40}|[a-f0-9]{64})$/`. Ledger `review_context.reviewedHeadSha` / `reviewedBaseSha` follows the same pattern; the previously issue-scoped "SHA-256 support requires a schema-version bump" note is superseded.

### Floating alias rejection

`modelId` and `resolvedModelId` must be host-selected canonical snapshot strings. The exact literal `latest` is rejected in every contract:

- `#48` semantic validator maps it to `manifest_shape_invalid` with message code `x_model_alias_literal`.
- `#49` semantic validator maps it to `ledger_model_alias_literal`.
- `#51` semantic validator maps it to `invalid-metadata-model-alias-literal`.

Broader floating-alias policy remains #52's host configuration responsibility.

### Duplicate identity equality (host-authoritative)

The manifest is authoritative for host / cache-contract identity. Runtime validators only compare equality against caller-supplied expected values; they never translate between vocabularies. Sibling workstreams must accept the following equalities in the accepted domain intersection given above:

- `metadata.selectedProviderId == manifest.cacheContractIdentity.providerId`.
- `metadata.observedProviderId == manifest.cacheContractIdentity.providerId`.
- `metadata.resolvedModelId == manifest.cacheContractIdentity.modelId`.
- `metadata.adapterId == manifest.cacheContractIdentity.adapterId`.
- `metadata.interactionId == manifest.transaction.interactionId`.
- `metadata.{consumedInputSha256,resultSha256,traceSha256,candidateLedgerSha256} == manifest.transaction.{consumedInputSha256,resultSha256,traceSha256,candidateLedgerSha256}`.
- `metadata.predecessorLedgerSha256 == manifest.transition.predecessorLedgerSha256` (both may be the `"bootstrap"` sentinel).
- `ledger.header.{sessionEpoch,ledgerEpoch,repository,headRepository,pullRequest,workflowIdentity,trustedExecutionDomain,providerId,modelId,adapterId,templateId,policyId,toolDefinitionId,cacheConfigId} == manifest.{sessionEpoch,generation.ledgerEpoch,stateKey.*,cacheContractIdentity.*}` verbatim.

The runtime and metadata validators are self-checking and per-contract; end-to-end host-side equality across all three sidecars is enforced by #53 (selection / acceptance) and #55 (sidecar transport / manifest-last commit).

### Sidecar byte caps (parser is authoritative, descriptor is outer bound)

- `ledger.json` raw bytes: `<= 524_288` (512 KiB). Canonical bytes: `<= 262_144` (256 KiB). Parser (#49) is authoritative for both.
- `provider-run-metadata.json` raw bytes: `<= 32_768` (32 KiB). Parser (#51) is authoritative.
- The v2 manifest descriptor JSON Schema `maximum` constraints (`ledger.bytes.maximum`, `providerRunMetadata.bytes.maximum`) must equal the parser raw caps: `524288` and `32768` respectively.
- **Layering.** #48 `valid` means the manifest schema / cross-field is valid and every sidecar descriptor / hash / length matches the caller-supplied bytes. It does _not_ mean the ledger or metadata payloads have passed their own authoritative validation. #55 must run #49 and #51 validation before completing the manifest-last local-candidate commit or returning local-candidate success; a payload within the descriptor raw cap but exceeding the parser canonical cap is a #49 semantic rejection.
- **Producer responsibility.** #49 ledger builder / #52 metadata producer must fail closed before writing a sidecar whose bytes would exceed the parser cap. The manifest descriptor `bytes` value is the exact byte count of the sidecar bytes bound into the manifest; producers may not overstate size.

**#51 raw-byte cap API.** The 32 KiB cap is a raw-bytes property, not a JS-string property. `parseProviderRunMetadata` receives `Uint8Array` bytes and produces one of the following outcomes before schema evaluation:

- `bytes.byteLength > 32_768` -> `invalid-metadata-bounds` (raw-byte cap violation).
- Bytes begin with UTF-8 BOM `0xEF 0xBB 0xBF` -> `invalid-metadata-bom`.
- UTF-8 decoding rejects illegal byte sequences -> `invalid-metadata-utf8`. (JSON-escaped `\uXXXX` sequences that decode to unpaired UTF-16 surrogates are legal UTF-8; they are rejected by the Unicode/string-safety stage, after JSON parse and before JSON Schema evaluation, as `invalid-metadata-unicode`.)
- JSON parse fails on the decoded string -> `invalid-metadata-json`.
- JSON parse succeeds but the parser detects a duplicate JSON property name at any object level -> `invalid-metadata-duplicate-json-property`.

`invalid-metadata-duplicate-json-property` is enforced by a duplicate-key-aware JSON parser (or an equivalent stream-level check) because JSON Schema draft-07 cannot detect duplicate keys after `JSON.parse`. Only after all five raw-transport checks succeed does the value enter the Unicode/string-safety stage. Only after that stage succeeds does the value enter JSON Schema validation.

### Canonical JSON helper (per-language)

- **#48 (TypeScript)** owns the reusable RFC 8785 canonical-JSON helper under `src/canonical-json/`, exported as `canonicalJsonBytes(value): Uint8Array` and `CANONICAL_JSON_VERSION = 1`.
- **#51 (TypeScript)** consumes the helper once #48 has landed. Until then it may vendor a metadata-envelope-scoped RFC 8785 producer whose golden bytes must remain unchanged; a follow-up PR replaces the vendored implementation with the shared helper in place.
- **#49 (C#) and #50 (C#)** own runtime-scoped canonical writers / primitives; they do not import the TypeScript helper. Cross-language equivalence is enforced through shared golden byte / hash vectors, not through source-level sharing.

### `unsupported_legacy_*` naming (global)

The classifier discriminator kind is `unsupported_legacy_v1`; the diagnostic code is `state_unsupported_legacy_v1`. The legacy `unsupported_legacy_state` spelling is not used. This update propagates to #29's body, the earlier legacy-policy paragraphs of this design document, #48, and any related fixtures.

### `interactionId` scope (clarification)

`interactionId` is not globally unique across session epochs. Its acceptance and semantic-duplicate scope includes `sessionEpoch`; retries within one accepted interaction reuse the same id. `bootstrap` and `recovery_root` use the literal `"bootstrap"` sentinel as the `predecessorLedgerSha256` component of the interaction-id preimage; `reset` uses the actual accepted predecessor ledger hash.

### Safe diagnostic path for Unicode / additional-property rejections

Because the Unicode/string-safety stage runs before JSON Schema, `additionalProperties: false`, and any cross-field checks, a naive `JSON Pointer of the offending element` would echo attacker-controlled ancestor property names into the diagnostic `message` before those names have been validated against a schema, alphabet, or byte cap. #48 (`x_invalid_unicode:...`), #49 (`ledger_invalid_unicode:...`), and #51 (`invalid-metadata-unicode:...`) therefore share one **safe diagnostic-path encoding**:

- Path segments come from the RFC 6901 JSON Pointer of the offending element's location, but each **property-name segment** is sanitized by the deterministic table below **before** it is spliced into the message. Numeric array-index segments are passed through as ASCII decimal.
- Sanitization table (deterministic, first match wins):
  1. Empty property name (JSON allows `""`) → literal marker `<empty-name>`.
  2. Property name is exactly one of the schema-known top-level or nested property names for the sidecar (i.e. appears in the closed JSON Schema for that sidecar at that depth): passed through with RFC 6901 escaping of `~` (as `~0`) and `/` (as `~1`), no further transformation. This is the **only** case in which the property-name segment is echoed verbatim.
  3. Property name contains an unpaired UTF-16 surrogate → literal marker `<invalid-utf16>`.
  4. Property name contains `U+0000` (NUL) → literal marker `<invalid-nul>`.
  5. Property name contains any other control character (`U+0001..U+001F` or `U+007F`) → literal marker `<invalid-control>`.
  6. Otherwise (property name is not schema-known but is otherwise well-formed, including plain ASCII identifiers, non-ASCII well-formed Unicode, or oversize) → literal marker `<untrusted-property>`.

This is a **closed rule set**: an unknown ASCII property such as `secretToken`, `apiKey`, `AWS_SECRET_ACCESS_KEY`, or `sk-proj-abc123` is not schema-known at any depth and therefore always resolves to `<untrusted-property>`. A well-formed non-ASCII property name is also always `<untrusted-property>` (never echoed) unless it is a schema-known property at that depth. This eliminates any avenue for an attacker to route content through the diagnostic message.

The offending **leaf value** is _not_ reported with an additional path segment. The safe path points at the leaf value's own JSON Pointer (sanitized). The diagnostic template is:

- **String value with unpaired surrogate:** safe path = `<safe-pointer-to-value>` (no trailing marker segment).
- **String value with NUL character:** safe path = `<safe-pointer-to-value>` (no trailing marker segment).
- **Property name with unpaired surrogate:** safe path = `<safe-pointer-to-parent>/<invalid-utf16>`.
- **Property name with NUL character:** safe path = `<safe-pointer-to-parent>/<invalid-nul>`.
- **Property name with other control character (`U+0001..U+001F` or `U+007F`, excluding NUL):** _not_ a string-safety-stage rejection on its own — see the shared traversal pseudocode. The `<invalid-control>` marker only appears in these two situations:
  1. As an **ancestor segment** in a descendant's diagnostic path when a lone surrogate or NUL is found deeper in the tree (e.g. G2).
  2. As the **final segment** of a schema-stage additional-property / unknown-field diagnostic when the offending property name itself is unknown at that schema position.
     In particular, a property name containing only `U+0001..U+001F` or `U+007F` (no NUL, no surrogate) whose value is a normal string does _not_ produce a `<code>:/<invalid-control>` Unicode-stage diagnostic. Sibling issue bodies MUST NOT describe `<invalid-control>` as an independent Unicode-stage rejection.

Each sidecar carries the workstream-specific code (`x_invalid_unicode` for #48, `ledger_invalid_unicode` for #49, `invalid-metadata-unicode` for #51) alongside the safe path. Concrete representation:

- **#48 and #49** emit a single `message: string` whose value is `<code>:<safe-path>` (colon-joined). The complete `message` (including the code prefix and colon) is capped at each sidecar's already-frozen per-diagnostic byte and character caps.
- **#51** emits a structured `MetadataError` with `code: <code>` and `path: <safe-path>` as separate fields; the safe path never repeats the code prefix.

**Path truncation algorithm.** When the encoded safe path would exceed the effective per-sidecar path budget, apply the following deterministic algorithm. The effective budgets are:

- **#48 and #49** operate on a single `message` field. The path budget is the message cap minus the length of the code prefix `<code>` plus the `:` separator. Specifically: `pathCharBudget = MAX_DIAGNOSTIC_MESSAGE_CHARS - length(code) - 1`, `pathByteBudget = MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES - utf8Length(code) - 1`. If either cap would be exceeded after truncation, use whichever binds first.
- **#51** operates on a dedicated `MetadataError.path` field with its own frozen caps (`MAX_METADATA_PATH_CHARS`, `MAX_METADATA_PATH_UTF8_BYTES`); truncation applies directly to `path`.

Invariant: every possible **final segment** (the fixed markers `<empty-name>` / `<invalid-utf16>` / `<invalid-nul>` / `<invalid-control>` / `<untrusted-property>` and the fixed placeholder `<path-truncated>`, plus any single schema-known property-name segment, plus any bounded ASCII-decimal array-index segment produced by the sidecar's array size caps) is shorter than every sidecar's effective path budget. Producer-side unit tests assert this invariant against every sidecar's actual cap.

**Pre-check.** Before applying the greedy algorithm, compute the fully-sanitized path `/s0/s1/.../sN` and its char/byte length. If both budgets are already satisfied, the untruncated path is emitted unchanged and `<path-truncated>` never appears.

**Root scalar violation.** If the offending element is the JSON document's root value (a top-level scalar), there is no ancestor and no property-name segment. The emitted safe path is the empty string `""` (a valid RFC 6901 pointer at the document root); no leading `/`, no marker segment. Sibling tests exercise this via a `root-scalar-lone-surrogate.json` fixture and, for sidecars where NUL is also a value-level rejection, a `root-scalar-nul.json` fixture.

**Schema-stage final segment: truly-undeclared vs. variant-forbidden.** Schema-stage diagnostics on a property name split into two disjoint categories. (a) A **truly-undeclared** additional-property / unknown-field vector (G5 and similar) is one where the resolver's union of all matching branches does not declare the offending key at that position; the finalSegment is one of `<empty-name>`, `<invalid-control>`, or `<untrusted-property>` per the six-rule sanitizer table applied at the schema stage. (b) A **variant-forbidden** vector is one where the resolver reports `schemaKnown = true` at the parent position (some branch of a `oneOf` / `anyOf` / `allOf` declares the key), but the branch selected by runtime schema validation forbids it or a sibling cross-field rule rejects it; because the union-based safe-path resolver has already established that the key is schema-known, the finalSegment is the RFC 6901-escaped schema-known name itself. The two property-name string-safety markers (`<invalid-utf16>`, `<invalid-nul>`) cannot appear as a schema-stage finalSegment because the string-safety stage runs earlier and terminates the scan before the schema stage can inspect the key. The truncation algorithm operates the same way on both schema-stage categories as it does on the string-safety stage.

Algorithm (deterministic; produces the same bytes on every implementation):

1. Compute the fully-sanitized ordered segment list `[s0, s1, ..., sN]` per the six-rule table, and the **final segment** by originating stage and category:
   - **Property-name string-safety violation**: `<invalid-utf16>` (unpaired UTF-16 surrogate) or `<invalid-nul>` (`U+0000`). These are the only two markers produced by the earlier string-safety stage; the schema stage never sees such property names.
   - **Schema-stage truly-undeclared additional-property / unknown-field**: `<empty-name>`, `<invalid-control>`, or `<untrusted-property>` per the six-rule sanitizer table applied at the schema stage. A schema-known name never arises here by definition (the key is not schema-known under the union-based resolver).
   - **Schema-stage variant-forbidden field**: the RFC 6901-escaped schema-known property name itself. This arises when the safe-path resolver's union across `oneOf` / `anyOf` / `allOf` branches reports `schemaKnown = true`, but the branch selected by runtime schema validation forbids the key or a sibling cross-field rule rejects it.
   - **String-value violation**: if the value is an object member, `finalSegment` is that member's already-sanitized property-name segment (produced by the six-rule table for the leaf's key); if the value is an array item, `finalSegment` is the item's ASCII-decimal array-index segment (numeric array-index segments pass through as ASCII decimal per the safe-path sanitizer table and are not further transformed by the six-rule table). A root scalar is handled exclusively by the **Root scalar violation** rule above and does not enter the truncation algorithm.

   In all cases the resulting finalSegment length is bounded by the invariant above; the truncation reference table below enumerates the specific lengths for the marker cases.

2. Compute the reserved budget: `reserved = length("/" + finalSegment) + length("/<path-truncated>")`, and its UTF-8 counterpart.
3. Greedily join `s0`, `s1`, ..., `sN-1` with leading `/`. Stop as soon as adding the next segment would push the joined prefix past `budget - reserved` (character or byte). If all N leading segments fit within `budget - reserved`, no placeholder is inserted and the untruncated path is `/s0/s1/.../sN`.
4. If a truncation was applied, emit `/prefix/<path-truncated>/<finalSegment>`, where `prefix` is the greedily-accepted concatenation from step 3 (which may be empty, giving `/<path-truncated>/<finalSegment>`). By the invariant, `<path-truncated>/<finalSegment>` alone always fits within `budget`, so the result never exceeds `budget`.

`<path-truncated>` never appears in an untruncated path. Sibling test suites include **two deep-path** golden vectors per sidecar (a no-truncation vector verifying the pre-check branch, and a truncation vector verifying the greedy-truncation branch), defined by a specific nested chain of `<untrusted-property>` segments long enough to force truncation. Each sidecar's deep-path expected output is byte-exact and includes the code prefix (or, for #51, the exact `MetadataError.path`). Deep-path expected outputs for the three sidecars follow (schema-known ancestors elided as `...`):

**Diagnostic-text counting unit and dual-cap discipline.** All `length` values in the truncation formula are UTF-16 code-unit counts, matching TypeScript's `String.length` and C#'s `String.Length`. UTF-8 byte length is computed separately. The bounded diagnostic text field — `message` for #48/#49 and `path` for #51 — is capped at 256 UTF-16 code units AND 1024 UTF-8 bytes, whichever binds first. For every well-formed UTF-16 string, `utf8Length(s) <= 3 * s.length` (a single non-surrogate BMP code unit encodes to at most 3 UTF-8 bytes; a surrogate pair is 2 UTF-16 code units and encodes to 4 UTF-8 bytes, still `<= 3 * 2`). So 256 UTF-16 code units yield at most 768 UTF-8 bytes, well below 1024, and the character cap therefore always binds first under the current caps. The safe-path contract further ensures that only schema-known ASCII property names pass through the sanitizer (every other name becomes one of a small set of ASCII markers). All property names declared by the current three sidecar schemas are ASCII, so the emitted bounded diagnostic text (the `message` for #48/#49 or the `path` for #51) is in practice pure ASCII and its UTF-8 byte length equals its UTF-16 code-unit length. Nevertheless, implementations MUST still compute both budgets and the emitted result MUST satisfy both; there is no secondary rejection. If the byte budget were ever to bind first, the truncation algorithm's identical structure (replace `char` with `byte` in the formulas below) produces the same shape of result.

**Parametric truncation formulas (final-segment-aware).** The `reserved` budget depends on the actual final segment (never hard-code `38`):

```
reserved_chars  = length("/<path-truncated>") + length("/" + finalSegment)
reserved_bytes  = utf8Length("/<path-truncated>") + utf8Length("/" + finalSegment)
allowance_chars = charBudget - reserved_chars
leadingCount    = floor(allowance_chars / length("/" + leadingSegment))
total_chars     = codePrefixChars
                + leadingCount * length("/" + leadingSegment)
                + length("/<path-truncated>")
                + length("/" + finalSegment)
```

The `leadingCount = floor(...)` closed form applies only when every leading segment has the same length (the case exercised by the deep-path oracle below, where every unknown ancestor is sanitized to `<untrusted-property>`). For paths mixing different sanitized ancestor lengths (schema-known names, `<empty-name>`, `<invalid-control>`, etc.), the general truncation algorithm above continues to be the authoritative behavior: iterate over segments, accumulate greedily, stop when adding the next one would exceed `charBudget - reserved_chars` or its byte counterpart.

For the `x_invalid_unicode:` producer (`codePrefixChars = 18`, `charBudget = 256 - 18 = 238`), with `leadingSegment = "<untrusted-property>"` (marker length 20, slash-plus-marker length 21), the reference table below shows how the formula depends on the final segment (all values in UTF-16 code units):

| finalSegment           | marker_chars | slash_plus_marker_chars | reserved_chars | allowance_chars | leadingCount | total_chars |
| ---------------------- | ------------ | ----------------------- | -------------- | --------------- | ------------ | ----------- |
| `<untrusted-property>` | 20           | 21                      | 38             | 200             | 9            | 245         |
| `<invalid-utf16>`      | 15           | 16                      | 33             | 205             | 9            | 240         |
| `<invalid-nul>`        | 13           | 14                      | 31             | 207             | 9            | 238         |
| `<invalid-control>`    | 17           | 18                      | 35             | 203             | 9            | 242         |
| `<empty-name>`         | 12           | 13                      | 30             | 208             | 9            | 237         |

The same formulas apply for `ledger_invalid_unicode:` (`codePrefixChars = 23`) and for #51's independent `path` field (`fieldPrefixChars = 0` because #51 emits `path` and `code` as separate fields; `path` alone gets the full 256-code-unit budget).

**Concrete deep-path golden vectors — frozen oracle.** Each sidecar has TWO named vectors (a no-truncation case for the pre-check branch and a truncation case for the greedy-truncation branch). All input shapes and expected outputs below are frozen literals; producer implementations MUST reproduce these exact bytes.

Each vector is characterized by `fullSanitizedSegmentCount` — the number of `<untrusted-property>` segments in the fully sanitized path BEFORE any truncation is applied. For a no-truncation vector, this equals the number of segments in the emitted path. For a truncation vector, this equals the number of segments in the notional pre-truncation path (unknown ancestors + terminal leaf property), which the truncation algorithm then trims to `leadingCount + 2` emitted segments.

All six vectors share the following input shape rules:

- The parsed root JSON value is an object whose UTF-16-sorted keys
  contain exactly one unknown top-level key that begins the diagnostic path; every other required top-level field for the sidecar's schema is present and valid so the unknown key is the first violation the traversal reaches.
- The unknown chain is a nested chain of objects, each with a
  single property whose name is a well-formed ASCII string not declared in the sidecar's schema at that depth (so every sanitized segment is `<untrusted-property>` under the six-rule table).
- The terminal value is a single-code-unit string containing the
  unpaired UTF-16 high surrogate `U+D800`; the string-safety stage rejects it as a value-level violation and emits the safe path pointing at the leaf value's location.
- No earlier UTF-16-sorted property, and no earlier sibling under
  any ancestor, contains a violation that would fire before the unknown chain.

The `code`/`path` shape below is: `<code>:<path>` in a single `message` string for #48 and #49; separate `code` and `path` fields on `MetadataError` for #51 (the `code` value is `invalid-metadata-unicode`; the diagnostic-text column below is the full value of `path`).

- `manifest-deep-path-no-truncation` — `fullSanitizedSegmentCount
= 9` (8 unknown ancestors above the terminal `U+D800` value, plus the terminal leaf property; the entire chain is top-level in a v2 manifest object). `codePrefixChars = 18`, `charBudget = 238`, no truncation, `total_chars = 207`. Expected `message`:

  ```
  x_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>
  ```

- `manifest-deep-path-truncation` — `fullSanitizedSegmentCount =
13` (12 unknown ancestors above the terminal `U+D800` value, plus the terminal leaf property; top-level in a v2 manifest object). `codePrefixChars = 18`, `charBudget = 238`, `finalSegment = <untrusted-property>`, `reserved_chars = 38`, `allowance_chars = 200`, `leadingCount = 9`, `total_chars = 245`. Expected `message`:

  ```
  x_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>
  ```

- `ledger-deep-path-no-truncation` — `fullSanitizedSegmentCount
= 9` (8 unknown ancestors above the terminal `U+D800` value, plus the terminal leaf property; the entire chain is top-level in a ledger object; the unknown chain starts at the ledger root, so no `review_context / changedFiles / patch` prefix appears in the path). `codePrefixChars = 23`, `charBudget = 233`, no truncation, `total_chars = 212`. Expected `message`:

  ```
  ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>
  ```

- `ledger-deep-path-truncation` — `fullSanitizedSegmentCount = 13`
  (12 unknown ancestors above the terminal `U+D800` value, plus the terminal leaf property; top-level in a ledger object). `codePrefixChars = 23`, `charBudget = 233`, `finalSegment = <untrusted-property>`, `reserved_chars = 38`, `allowance_chars = 195`, `leadingCount = 9`, `total_chars = 250`. Expected `message`:

  ```
  ledger_invalid_unicode:/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>
  ```

- `metadata-deep-path-no-truncation` — `fullSanitizedSegmentCount
= 10` (9 unknown ancestors above the terminal `U+D800` value, plus the terminal leaf property; top-level in a metadata object). `codePrefixChars = 0` (path field only), `charBudget = 256`, no truncation, `total_chars = 210`. Expected `path`:

  ```
  /<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>
  ```

- `metadata-deep-path-truncation` — `fullSanitizedSegmentCount =
14` (13 unknown ancestors above the terminal `U+D800` value, plus the terminal leaf property; top-level in a metadata object). `codePrefixChars = 0`, `charBudget = 256`, `finalSegment = <untrusted-property>`, `reserved_chars = 38`, `allowance_chars = 218`, `leadingCount = 10`, `total_chars = 248`. Expected `path`:

  ```
  /<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>
  ```

All expected diagnostic-text strings are ASCII (byte length equals UTF-16 code-unit length; the six lengths are 207 / 245 / 212 / 250 / 210 / 248 respectively). The code-fenced expected text is the entire single-line ASCII string between the opening and closing fences; neither the fence delimiters nor a trailing LF is part of the literal.

Producer-side tests assert the full expected `message` / `path` byte-exact against these frozen literals (test-lookup by vector ID). `<code>:` prefixes and delimiter characters are counted per the parametric formulas above. Neither branch's expected output is re-derived from the production implementation; both are computed once and stored as constants in the test source. These six byte-exact strings are owned by this section (single normative source, see "Single normative source"); workstream issues MUST reference them by vector ID and MUST NOT re-state them.

**No untrusted content is ever placed in the diagnostic message.** Every sidecar's Acceptance Criteria includes the following **shared golden vectors** (each sidecar realises them with sidecar-specific code prefix and MetadataError / BundleClassification / LedgerDiagnostic shape):

**Path-sensitive schema-known-property rule.** Rule 2 (schema-known passthrough) applies **only when the entire ancestor chain up to and including that segment is schema-known**. Once traversal enters an unknown property, all its descendants are treated as unknown (rule 6). Global name vocabularies are not used; depth-indexed vocabularies are not used.

**Unicode-stage golden vectors.** These vectors are produced by the Unicode/string-safety stage of each sidecar (both unpaired surrogate rejection and NUL rejection are universal across #48 / #49 / #51). `<code>` is `x_invalid_unicode` / `ledger_invalid_unicode` / `invalid-metadata-unicode` respectively.

| #   | Input scenario                                                                                                                                                          | Expected `code`    | Expected safe path                           | Note                                                                                                                              |
| --- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------------------ | -------------------------------------------- | --------------------------------------------------------------------------------------------------------------------------------- |
| G1  | Attacker-controlled ancestor property name `secretToken` (well-formed ASCII, not schema-known) with a descendant lone-surrogate **value** at any property name below it | `<code>` (Unicode) | `/<untrusted-property>/<untrusted-property>` | Path-sensitive: once traversal enters an unknown ancestor, every descendant property-name segment is also `<untrusted-property>`. |
| G2  | Attacker-controlled ancestor property name `attacker\ncontrolled` (contains U+000A) with a descendant lone-surrogate **value** at any property below it                 | `<code>` (Unicode) | `/<invalid-control>/<untrusted-property>`    | Ancestor sanitized as control; descendants under an unknown ancestor stay unknown.                                                |
| G3  | Lone-surrogate **property name** at the top level of the persisted sidecar                                                                                              | `<code>` (Unicode) | `/<invalid-utf16>`                           | Parent pointer empty.                                                                                                             |
| G6  | Schema-known top-level property (e.g. `stateKey` for #48) with a lone-surrogate **value** at a schema-known descendant (e.g. `workflowIdentity`)                        | `<code>` (Unicode) | `/stateKey/workflowIdentity`                 | Only fully schema-known ancestor chains echo the real names.                                                                      |
| G7  | A well-formed surrogate pair in a schema-valid string value field                                                                                                       | _accepted_         | _n/a_                                        | Unicode stage passes it; canonical serialization preserves it byte-exact.                                                         |

**Property-existence and NUL vectors.** NUL rejection is a **uniform** contract across the three sidecars (see the String-safety paragraph above): G4 always produces the Unicode-stage code. G5 (empty property name) is a well-formed UTF-16 name and therefore reaches the schema stage. The frozen expected outcomes:

| #   | Input scenario                                                                 | #48 outcome                                                                                                                                                                                                                                                                                                                                                                                                                               | #49 outcome                                                                                   | #51 outcome                                                                                    |
| --- | ------------------------------------------------------------------------------ | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------- |
| G4  | NUL character in a **property name** at the top level of the persisted sidecar | `manifest_shape_invalid` with `message = x_invalid_unicode:/<invalid-nul>` (Unicode stage rejects NUL in any string; the stage covers property names because NUL rejection is a uniform sidecar-wide safety and privacy policy (allowing NUL in string content would let attacker-controlled bytes flow into diagnostics, canonical serializer inputs, and any downstream log stream even though RFC 8785 could technically escape them)) | `ledger_invalid_unicode:/<invalid-nul>` (Section 5 rejects NUL globally at the Unicode stage) | `invalid-metadata-unicode` with `path = /<invalid-nul>` (matching the Section 3 Unicode stage) |
| G5  | Empty property name `""` at the top level of the persisted sidecar             | `manifest_unknown_field` with `message` referencing `/<empty-name>` (a well-formed empty name reaches the schema stage; `additionalProperties: false` rejects it)                                                                                                                                                                                                                                                                         | `ledger_unknown_field` with the same path shape                                               | `invalid-metadata-additional-property` with `path = /<empty-name>`                             |

All three sidecars therefore adopt a uniform Unicode stage that rejects **NUL in string values and in property names**, in addition to unpaired surrogates. The corresponding stage description in each issue body has been updated accordingly. G5 stays a schema-stage vector because an empty property name is well-formed UTF-16; each sidecar's schema-stage diagnostic uses its own additional-property / unknown-field code with the safe empty-name marker.

Each sibling test suite implements these seven vectors with the workstream-appropriate code and error object, and every AC lists them as named tests (not merely "a fixture that exceeds the cap"). Path truncation, if it applies, must occur only at the RFC 6901 segment boundary and preserve the final marker segment; each sidecar's message cap already provides the numeric byte budget.

### Schema-position resolver

The safe diagnostic path stage (see "Safe diagnostic path for Unicode / additional-property rejections") and the shared traversal below both depend on a deterministic way to decide, at every position in the parsed sidecar value, whether a property or array item is declared by the sidecar's closed JSON Schema. That decision MUST be identical across languages so the emitted safe paths byte-equal each other. This subsection defines that resolver.

**Types.**

- `ResolveResult = { schemaKnown: boolean, childSchemaPosition: SchemaPosition }`.
- `SchemaPosition` is one of:
  - `UnknownPosition` — a sentinel; the resolver treats every key
    or item on this position as not schema-known.
  - `ObjectPosition(node, activeSchemaNodes)` — an object-shape
    position carrying the immutable per-call-chain `activeSchemaNodes` snapshot from the `normalizePosition` invocation that produced it.
  - `ArrayPosition(node, activeSchemaNodes)` — an array-shape
    position carrying the same kind of snapshot; `node.items` is required to be a single JSON Schema (M4 sidecar schemas MUST NOT use tuple-form `items` or `additionalItems`; a build-time schema-conformance test rejects both).
  - `CompositePosition(children)` — a structural representation
    for positions produced by schema composition; recursive resolution semantics below.

`resolveProperty(UnknownPosition, _)` and `resolveArrayItem(UnknownPosition)` always return `{ schemaKnown: false, childSchemaPosition: UnknownPosition }`.

**Ordinary object position.**

```
resolveProperty(ObjectPosition(node, activeSchemaNodes), key):
  if key is a declared own key of node.properties:
    return {
      schemaKnown: true,
      childSchemaPosition:
        normalizePosition(node.properties[key], activeSchemaNodes)
    }
  return { schemaKnown: false,
           childSchemaPosition: UnknownPosition }

resolveArrayItem(ObjectPosition(node, activeSchemaNodes)):
  return { schemaKnown: false,
           childSchemaPosition: UnknownPosition }
```

**Ordinary array position.**

```
resolveProperty(ArrayPosition(node, activeSchemaNodes), _):
  return { schemaKnown: false,
           childSchemaPosition: UnknownPosition }

resolveArrayItem(ArrayPosition(node, activeSchemaNodes)):
  if node.items is present as a single schema:
    return {
      schemaKnown: true,
      childSchemaPosition:
        normalizePosition(node.items, activeSchemaNodes)
    }
  return { schemaKnown: false,
           childSchemaPosition: UnknownPosition }
```

**Composite position.**

```
resolveProperty(CompositePosition(children), key):
  matches = []
  for child in children:
    r = resolveProperty(child, key)
    if r.schemaKnown:
      matches.append(r.childSchemaPosition)
  if matches is empty:
    return { schemaKnown: false,
             childSchemaPosition: UnknownPosition }
  return { schemaKnown: true,
           childSchemaPosition:
             CompositePosition(deduplicate(matches)) }

resolveArrayItem(CompositePosition(children)):
  matches = []
  for child in children:
    r = resolveArrayItem(child)
    if r.schemaKnown:
      matches.append(r.childSchemaPosition)
  if matches is empty:
    return { schemaKnown: false,
             childSchemaPosition: UnknownPosition }
  return { schemaKnown: true,
           childSchemaPosition:
             CompositePosition(deduplicate(matches)) }
```

`deduplicate` treats two positions as identical only when their source schema node, their `activeSchemaNodes` snapshot, AND their recursive composite shape all match. Positions produced from the same schema node under different `activeSchemaNodes` snapshots are NOT merged (their cycle behavior may differ). When a `CompositePosition` reduces to a single child (after deduplication), implementations MAY replace it with that single child; observable behavior on the conformance vectors below is unchanged either way. The internal data structure used by any language binding may differ; its externally observable resolution behavior on the conformance vectors below MUST match byte-exactly.

**Normalization.**

```
normalizePosition(node, activeSchemaNodes = empty set):
  nodeId = canonicalSchemaNodeIdentity(node)
  if nodeId is in activeSchemaNodes:
    return UnknownPosition   // cycle
  childActive = activeSchemaNodes union { nodeId }

  if node contains $ref:
    if node contains any structural or validation sibling of $ref
       (properties, items, oneOf, anyOf, allOf, or any other
       keyword besides $ref itself):
      return UnknownPosition
    target, ok = dereference(node.$ref)
    if not ok:
      return UnknownPosition
    return normalizePosition(target, childActive)

  positions = []
  if node declares object-shape keywords supported by this
     resolver (currently just `properties`):
    positions.append(ObjectPosition(node, childActive))
  if node declares array-shape keywords supported by this
     resolver (currently just single-schema `items`):
    positions.append(ArrayPosition(node, childActive))
  for branch in (node.oneOf or []) ++ (node.anyOf or []) ++
                (node.allOf or []):
    positions.append(normalizePosition(branch, childActive))

  if positions is empty:
    return UnknownPosition
  if positions has exactly one element:
    return that element
  return CompositePosition(positions)
```

`activeSchemaNodes` is the set of schema-node identities currently on the normalization call stack (root, inline object/array schemas, composition branch subschemas, and `$ref` targets alike). It is a per-call chain, not a global visited-set. A schema that legitimately visits the same target twice from two independent branches (each with its own descent) is normalized correctly: only when a schema node reappears in its own descent chain is it treated as a cycle and returned as `UnknownPosition`. This closes both `$ref`-driven cycles and cycles that arise via inline structural back-edges (e.g. a child's `$ref` pointing to an ancestor object that was itself entered without a `$ref`). The three sidecar schemas MUST form an acyclic `$ref` graph; a build-time conformance test (owned by whichever workstream ships the schema file) rejects every direct or indirect cycle. The runtime cycle guard above is defense-in-depth so a cycle introduced by a future schema change or a test regression degrades to `UnknownPosition` rather than infinite recursion. Implementations MUST NOT rely on an implementation-defined recursion or depth limit.

`canonicalSchemaNodeIdentity(node)` is a stable identity of the schema node itself (e.g. its canonical URI + JSON Pointer within its schema document, or an equivalent normalized form). When the resolver dereferences a `$ref`, the identity added to `activeSchemaNodes` is the identity of the dereferenced target node, not the literal `$ref` string, so two syntactically different references to the same target share one identity.

**Union / composition summary.** `oneOf` / `anyOf` / `allOf` compositions are handled by `normalizePosition` above: each branch normalizes recursively (with the current descent chain) and the whole node becomes a `CompositePosition` whose children include any direct object/array declaration on the same node plus each branch's normalization. `oneOf` and `anyOf` behave identically for safe-path purposes (a key is `schemaKnown` iff any branch declares it); `allOf` behaves like a union of declared-property maps at the safe-path level (runtime schema validation may impose a stricter intersection, but that is a schema-stage concern and not a safe-path concern). Discriminated header variants are treated as `oneOf`; the resolver does NOT pre-select a branch by discriminator, because pre-selection would give the discriminator field's own value a schema-driving role before its Unicode/string safety is verified.

**Undeclared-property behavior.** A position that either permits arbitrary property names (open positions with `additionalProperties: true` or an omitted `properties` list) or forbids property names outside the declared list (closed positions with `additionalProperties: false` and the requested key not in the `properties` list) yields `schemaKnown = false` for the requested key with `childSchemaPosition = UnknownPosition`. Only keys explicitly declared under `properties` at that position produce `schemaKnown = true`. This applies to `ObjectPosition`, `ArrayPosition`, and every `CompositePosition` whose recursion base cases hit `UnknownPosition`.

**Unsupported schema keywords.** Keywords other than the ones supported above (in particular `if` / `then` / `else`, `not`, `dependencies`, `patternProperties`, tuple-form `items`, `additionalItems`) contribute no schema-known keys, but their presence at a node does NOT erase knowledge produced by supported siblings at the same node. Concretely, if a node declares both `properties.known` (supported) and `patternProperties.^x` (unsupported), a caller that has an `ObjectPosition(node, activeSchemaNodes)` for that node observes `resolveProperty(objectPos, "known")` return `schemaKnown = true` with the declared child's normalized position, and `resolveProperty(objectPos, "other")` return `schemaKnown = false` with `UnknownPosition`.

**Root-call convention.** The traversal is invoked as `scan(rootValue, "", rootSchemaPosition, /*trustedChain=*/ true)` where `rootSchemaPosition = normalizePosition(rootSidecarSchema, {})`.

**Fail-closed rule.** There are two distinct failure surfaces:

- `normalizePosition` failures (missing schema node, malformed
  supported-keyword shape, malformed `$ref`, prohibited `$ref` sibling, dereference failure, or schema-node cycle detected by the `activeSchemaNodes` guard) return `UnknownPosition`.
- `resolveProperty` / `resolveArrayItem` return
  `{ schemaKnown: false, childSchemaPosition: UnknownPosition }` only when the requested key/item is not explicitly declared at the current position, or when the current position itself cannot be inspected sufficiently to determine that declaration.

Once a property is confirmed as explicitly declared at an `ObjectPosition`, `resolveProperty` returns `schemaKnown = true` even if `normalizePosition` of that property's schema returns `UnknownPosition` (for example because the child triggers the `activeSchemaNodes` cycle guard); the returned `childSchemaPosition` is that `UnknownPosition`, and subsequent descent stops being schema-known from that point. The same applies to `resolveArrayItem` on an `ArrayPosition` whose `items` schema normalizes to `UnknownPosition`. A child-side normalization failure never retroactively downgrades the parent's `schemaKnown` verdict for an explicitly declared key/item.

For nodes that do NOT contain `$ref`, mere presence of an unsupported schema keyword does NOT trigger fail-closed on the whole node; the unsupported keyword simply contributes no schema-known keys. Nodes that DO contain `$ref` follow the exclusive-`$ref` rule above: any sibling keyword at all (supported or unsupported) causes `normalizePosition` to return `UnknownPosition`.

**Concrete observable consequences** (any conformant implementation MUST produce these results):

- **Root back-edge via `$ref`.** Given a root schema
  `{"type":"object","properties":{"next":{"$ref":"#"}}}`, let `rootPos = normalizePosition(root, {})`. Then `resolveProperty(rootPos, "next")` returns `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (the `next` child normalizes into a cycle at the first back-edge hop, so the declared key stays `schemaKnown = true` but no further schema-known descent follows).

- **Inline ancestor back-edge via `$ref`.** Given a root schema
  `{"type":"object","properties":{"payload":{"type":"object","properties":{"next":{"$ref":"#/properties/payload"}}}}}`, let `rootPos = normalizePosition(root, {})` and `P_payload = resolveProperty(rootPos, "payload").childSchemaPosition`. Then `resolveProperty(P_payload, "next")` returns `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (the `next` child's `$ref` back-edge to `#/properties/payload` is detected because `activeSchemaNodes` includes the ancestor `payload` schema node from the surrounding descent chain).

- **Independent multi-branch reference is not a cycle.** Given a
  root schema whose two `oneOf` branches each declare a property `payload` whose value is `{"$ref":"#/$defs/leaf"}` where the shared target `#/$defs/leaf` is `{"type":"object","properties":{"value":{"type":"string"}}}`, let `rootPos = normalizePosition(root, {})`, `rootBranchAPos` and `rootBranchBPos` denote the normalized positions of branch A's and branch B's roots respectively, `P_A_payload = resolveProperty(rootBranchAPos, "payload").childSchemaPosition`, `P_B_payload = resolveProperty(rootBranchBPos, "payload").childSchemaPosition`, and `P_payload = resolveProperty(rootPos, "payload").childSchemaPosition`. Then BOTH of the following per-branch assertions MUST hold in addition to the aggregate assertion:

  - `resolveProperty(P_A_payload, "value")` returns
    `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (branch A normalizes `leaf` successfully; the `value` scalar has no supported structural keywords).
  - `resolveProperty(P_B_payload, "value")` returns
    `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (branch B independently normalizes the SAME shared `leaf` target; since neither branch's descent chain contains the leaf node when the other branch is being normalized, the per-call `activeSchemaNodes` guard does NOT treat this as a cycle).
  - `resolveProperty(P_payload, "value")` returns
    `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (aggregate; follows from either per-branch match under the composite union rule).

  The two per-branch assertions are what distinguish a correct per-call `activeSchemaNodes` implementation from an incorrect global visited-set implementation: under a global visited-set, branch B's second visit of `leaf` would degrade to `UnknownPosition` at the `leaf` normalization step, so `resolveProperty(P_B_payload, "value")` would return `schemaKnown = false` (since `P_B_payload` would itself be `UnknownPosition`). The aggregate assertion alone does NOT distinguish the two implementations because a still-correct branch A would keep the aggregate `schemaKnown = true` under the composite union rule.

### Shared traversal order and stage precedence

All three sidecars implement the following deterministic traversal so multi-violation inputs produce byte-identical diagnostics within each sidecar (only the `<code>` and error object shape differ across sidecars).

```text
// Root invocation:
//   rootSchemaPosition = normalizePosition(rootSidecarSchema, {})
//   scan(rootValue, "", rootSchemaPosition, /*trustedChain=*/ true)
//
// `schemaPosition` is a SchemaPosition (see "Schema-position
// resolver") — an ObjectPosition, ArrayPosition, CompositePosition,
// or UnknownPosition. `trustedChain` is true only when every
// ancestor property name is schema-known at its exact position.

function scan(node, path, schemaPosition, trustedChain):
  if node is a string:
    // Scan UTF-16 code units left-to-right; first violating code
    // unit terminates the scan and produces the diagnostic.
    for i in 0 .. node.length - 1:
      c = node[i]
      if c == U+0000 (NUL):
        return {code: <code>, path: path}  // value-level; leaf
      if c is a high surrogate (U+D800..U+DBFF):
        if i + 1 >= node.length or node[i+1] not in U+DC00..U+DFFF:
          return {code: <code>, path: path}
        i += 1
        continue
      if c is a low surrogate (U+DC00..U+DFFF):
        return {code: <code>, path: path}
      // Any other code unit is accepted at this stage.
  if node is an array:
    resolved = resolveArrayItem(schemaPosition)
    itemTrusted = trustedChain && resolved.schemaKnown
    itemSchemaPosition = itemTrusted
                         ? resolved.childSchemaPosition
                         : UnknownPosition
    for i in 0 .. node.length - 1:
      result = scan(node[i], path.append(i),
                    itemSchemaPosition, itemTrusted)
      if result != null:
        return result
    return null
  if node is an object:
    // RFC 8785 canonical order: keys sorted by unsigned UTF-16 code units.
    for key in sortByUtf16CodeUnits(Object.keys(node)):
      // (1) Property-name terminal safety FIRST — before any
      //     resolver call. Only two rules terminate the scan at
      //     the property-name level:
      if key contains an unpaired UTF-16 surrogate:
        return {code: <code>, path: path.append("<invalid-utf16>")}
      if key contains U+0000 (NUL):
        return {code: <code>, path: path.append("<invalid-nul>")}
      // A property name that only contains non-NUL C0/DEL controls
      // does NOT terminate the string-safety stage; those are
      // sanitized to "<invalid-control>" when the ancestor
      // segment is emitted as part of a descendant's path.
      //
      // (2) Consult the schema-position resolver.
      resolved = resolveProperty(schemaPosition, key)
      keyIsSchemaKnown = trustedChain && resolved.schemaKnown
      segment = sanitize(key, keyIsSchemaKnown)
      childTrusted = keyIsSchemaKnown
      childSchemaPosition = childTrusted
                            ? resolved.childSchemaPosition
                            : UnknownPosition
      // (3) Recurse. Once traversal enters an unknown-ancestor
      //     subtree (trustedChain == false), every subsequent
      //     recursive call is made with
      //     schemaPosition = UnknownPosition and
      //     trustedChain = false; the resolver behavior on
      //     UnknownPosition then guarantees schemaKnown = false
      //     for every descendant key/item.
      result = scan(node[key], path.append(segment),
                    childSchemaPosition, childTrusted)
      if result != null:
        return result
    return null
  return null    // primitive node with no string content
```

**Terminal-safety invariant.** Unknown ancestry (`trustedChain == false` at or above the current node) disables schema-known passthrough (rule 2 of the six-rule sanitizer table) below the first unknown property. It does NOT replace every ancestor label with `<untrusted-property>` and it does NOT suppress property-name safety checks. Every ancestor segment is still sanitized independently through the six-rule table on the way down:

- schema-known-and-trusted ancestors above the first unknown
  property retain their RFC 6901-escaped schema names;
- an unknown ancestor whose property name contains an unpaired
  UTF-16 surrogate terminates the scan with `<invalid-utf16>` as the final segment;
- an unknown ancestor whose property name contains `U+0000`
  (NUL) terminates the scan with `<invalid-nul>` as the final segment;
- an unknown ancestor whose property name contains only non-NUL
  C0/DEL control characters is emitted as `<invalid-control>` (baseline vector G2 relies on this);
- an unknown ancestor whose property name is the empty string
  is emitted as `<empty-name>`;
- every other unknown ancestor is emitted as
  `<untrusted-property>`.

A descendant property name containing an unpaired UTF-16 surrogate or NUL still terminates the scan with the corresponding terminal marker (`<invalid-utf16>` / `<invalid-nul>`) regardless of the trust of its ancestors.

`<invalid-control>` is NOT a terminal string-safety marker. Its two permitted uses remain exactly those defined by the sanitizer table: (a) as an ancestor segment on a descendant's diagnostic path when the ancestor property name contains a non-NUL C0/DEL control character (see baseline G2); (b) as the final segment of a schema-stage additional-property / unknown-field diagnostic when the offending property name itself contains such a character. Property names containing only non-NUL C0/DEL control characters do NOT terminate the string-safety stage on their own. `<invalid-utf16>` and `<invalid-nul>` are the ONLY two markers described as terminal string-safety markers anywhere in this section.

**Property-name marker precedence** (when a property name contains more than one violating character) is:

1. Unpaired UTF-16 surrogate at any code-unit position → `<invalid-utf16>`.
2. Otherwise, `U+0000` (NUL) at any code-unit position → `<invalid-nul>`.

This is a category precedence, not a code-unit-position precedence, so implementations can decide category first and then find the first offending code unit within that category. The result is deterministic across implementations.

**Value-level scan** (a plain string node) uses code-unit-position precedence: the first violating code unit (by index) wins, whether it is a NUL or a surrogate.

The initial invocation is `scan(rootValue, "", normalizePosition(rootSidecarSchema, {}), /*trustedChain=*/ true)`. This shared pseudocode is authoritative for `validateStateManifestV2` / `classifyStateBundleV2` step 5 / `buildStateBundleV2` step 3 (#48), for `LedgerParser.ParseAndValidate` and the builder pipeline (#49), and for `parseProviderRunMetadata` stage 2 (#51).

### Shared conformance vectors

The three language-agnostic vectors below cover the traversal / resolver / safe-path machinery. Each vector states input JSON, expected sanitized segment list, expected untruncated safe path, and owning stage. No byte-exact per-sidecar diagnostic literal, no code prefix, and no cap-truncation appears next to any vector — those live in "Concrete deep-path golden vectors — frozen oracle" above. Each `\uXXXX` in the vector inputs appears as a literal 6-character ASCII escape in the Markdown source, not as an actual UTF-16 surrogate; producers process the parsed JSON value whose corresponding string is a single UTF-16 code unit.

**Vector V1 — `shared-unknown-ancestor-with-value-level-surrogate`.**

- Hypothetical schema: none of `a` / `b` / `c` is declared at its
  position; every position is `UnknownPosition`.
- Input JSON: `{"a": {"b": {"c": "\uD800"}}}`.
- Expected sanitized segment list: `[<untrusted-property>,
<untrusted-property>, <untrusted-property>]`.
- Expected untruncated safe path:
  `/<untrusted-property>/<untrusted-property>/<untrusted-property>` (value-level violation; safe path points at the leaf; no terminal marker segment).
- Owning stage: `string-safety`.

**Vector V2 — `shared-terminal-invalid-utf16-in-unknown-property-name`.**

- Hypothetical schema: `a` is not declared; the position under
  `a` is `UnknownPosition`.
- Input JSON: `{"a": {"\uD800": 1}}`.
- Expected sanitized segment list: `[<untrusted-property>,
<invalid-utf16>]`.
- Expected untruncated safe path:
  `/<untrusted-property>/<invalid-utf16>`.
- Owning stage: `string-safety`.

**Vector V3 — `shared-resolver-union-child-position`.** This vector fails if a resolver picks only the first matching branch, if it collapses composites to `UnknownPosition`, if it fails to recurse into composite children, or if it returns `schemaKnown = true` for a key that no branch declares.

- Hypothetical schema at root: `oneOf` with two branches.
  - Branch A: `{ properties: { payload: { properties:
{ alpha: { type: "string" } } } } }`.
  - Branch B: `{ properties: { payload: { properties:
{ beta: { type: "string" } } } } }`. Both branches declare `payload`; branch A declares only `alpha` under `payload`; branch B declares only `beta` under `payload`.
- Input JSON: `{"payload": {"beta": "\u0000"}}` (value-level NUL
  at `beta`).
- Notation: let
  `rootPos = normalizePosition(root, {})`. Let `rootBranchAPos` and `rootBranchBPos` denote the normalized positions of branch A's and branch B's roots respectively (each is an object with a single declared property `payload`). Let `P_A_payload = resolveProperty(rootBranchAPos, "payload").childSchemaPosition` and `P_B_payload = resolveProperty(rootBranchBPos, "payload").childSchemaPosition`. These are the payload-level positions of each branch.
- Expected resolver behavior (all assertions are observational;
  the internal representation of intermediate positions is not fixed):
  - `resolveProperty(rootBranchAPos, "payload")` returns
    `{ schemaKnown: true, childSchemaPosition: P_A_payload }`.
  - `resolveProperty(rootBranchBPos, "payload")` returns
    `{ schemaKnown: true, childSchemaPosition: P_B_payload }`.
  - `resolveProperty(rootPos, "payload")` returns
    `{ schemaKnown: true, childSchemaPosition: P_payload }` for some `P_payload` whose observable behavior is "union of `P_A_payload` and `P_B_payload`" (formalized below).
  - `resolveProperty(P_A_payload, "beta")` returns
    `schemaKnown = false` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (branch A's payload declares `alpha` only).
  - `resolveProperty(P_B_payload, "beta")` returns
    `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (branch B's payload declares `beta`; its scalar schema has no supported structural keywords, so `normalizePosition` yields `UnknownPosition` for the child).
  - `resolveProperty(P_payload, "beta")` returns
    `schemaKnown = true` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (`P_B_payload`'s contribution has `schemaKnown = true` with an `UnknownPosition`-equivalent child; `P_A_payload`'s contribution has `schemaKnown = false` and is discarded).
  - `resolveProperty(P_payload, "extraneous")` returns
    `schemaKnown = false` with a `childSchemaPosition` observationally equivalent to `UnknownPosition` (neither `P_A_payload` nor `P_B_payload` declares `extraneous`).
- Expected sanitized segment list on the terminal NUL scan:
  `[payload, beta]` (both segments retain their schema-known names because the ancestor chain is fully trusted through the union + branch descent; the terminal NUL is at a value, so the safe path points at that leaf value).
- Expected untruncated safe path: `/payload/beta` (value-level
  NUL violation; safe path points at the leaf; no terminal marker segment).
- Owning stage: `string-safety`.

For the purposes of V3, "observationally equivalent to `UnknownPosition`" means: all subsequent `resolveProperty` and `resolveArrayItem` calls on that position return `schemaKnown = false` and a child position that is itself observationally equivalent to `UnknownPosition`. This representation-agnostic wording lets both `CompositePosition([UnknownPosition])` and the collapsed `UnknownPosition` satisfy the assertion, as long as their external behavior matches.

### Aggregate token overflow

`invalid-metadata-token-out-of-range` is produced by two distinct stages:

- **Stage 3 (JSON Schema)** rejects any single-value token count that exceeds `2^53 - 1`.
- **Stage 4 (semantic validator)** rejects an aggregate token sum that would overflow `Number.MAX_SAFE_INTEGER` during attempt → request or request → run reduction. Overflow detection uses the pre-addition test `if aggregate + operand > Number.MAX_SAFE_INTEGER: return invalid-metadata-token-out-of-range` before performing the addition.

Precedence: because stage 3 runs before stage 4, single-value overflow always wins over sum overflow. When both a single value and an aggregate would exceed, the diagnostic reports the single value's JSON path. Named fixtures cover both cases (`invalid-token-out-of-range-single-value.json`, `invalid-token-out-of-range-aggregate-sum.json`).

This is a documented exception to the "each code is produced by exactly one stage" rule.
