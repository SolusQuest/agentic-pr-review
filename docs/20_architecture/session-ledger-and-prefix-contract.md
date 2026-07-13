# M4 Session Ledger And Provider Request Prefix Contract

Status: design contract for M4, owned by issue #29.

This document defines the cross-issue semantics for the project-owned live
provider path. It does not implement the ledger, provider adapter, artifact
transport, or cost evaluation harness.

## Decision Summary

- The C# runtime core owns the provider-neutral canonical session ledger,
  canonical logical projection, deterministic prefix construction, append and
  ledger validation. It never owns GitHub side effects.
- Provider adapters own provider-specific request envelopes, cache markers,
  capability detection, telemetry mapping, and provider invocation. The core
  ledger schema remains provider-neutral. M4 uses one reference adapter
  conforming to the selected Anthropic Messages-style capability profile;
  exact SDK, model snapshot, and request API choices belong to the adapter
  issue.
- TypeScript remains the GitHub Action host. It owns workflow facts, target and
  provenance validation, secret/trust-mode selection, state artifact transport,
  publishing, and the final fail-closed side-effect barrier.
- M4 implementation must prove one explicit, default-off,
  trusted-workflow-only live path. Its deterministic and trusted live gates
  each use two separate workflow runs. It remains sticky-only, does not
  replace `claude-code-cli`, does not implement the full tool loop, and does
  not require production Native AOT release packaging.
- M4 introduces `StateManifestV2`, which references an independent ledger file
  in the same state artifact bundle. The M4 live path writes and fully
  validates v2. Existing v1 paths remain outside this ledger contract until a
  separately documented breaking cutover.
- A pre-v2 manifest is not migrated or used to construct a ledger. In the M4
  live path, v1 is recognized as `unsupported_legacy_state` and follows the
  safe bootstrap policy. A v1 rejection fixture prevents accidental reuse.

## Scope And Non-Goals

M4 covers the minimum project-owned live-provider foundation: ledger state,
stable prefix construction, provider capability and usage normalization,
trusted live invocation, safe state restore/persist, and representative
resumed-session cost evaluation.

M4 does not cover:

- replacing or removing `claude-code-cli`;
- a general state migration framework;
- full state/publisher compatibility, finding identity, or publisher
  idempotency contracts owned by Phase 5;
- full repo-local read/grep/glob or provider/tool-loop orchestration;
- production Native AOT release packaging, download, checksum, or platform
  distribution policy;
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
- transport of optional ledger input and candidate ledger output across the
  process boundary;
- manifest v2 descriptor validation and state artifact upload/restore;
- host-owned result assembly, budget enforcement, publishing, and side-effect
  barriers.

The runtime never receives `GITHUB_TOKEN`, never calls GitHub APIs, and never
publishes comments or artifacts.

## M4 Runnable Surface And Trust Mode

The first M4 live path is experimental and explicit:

- it is default-off and selected by a distinct live-provider runtime mode;
- it runs only from a trusted workflow and trusted/immutable runtime source;
- fork-origin PRs and untrusted checkout execution fail closed before secret
  exposure;
- provider credentials enter only through an explicit allowlisted runtime
  secret channel, such as an allowlisted environment or equivalent OS secret
  mechanism;
- credentials never enter CLI arguments, protocol files, ledger, trace,
  manifest, normal artifacts, logs, or structured output;
- the current deterministic path remains provider-secret-free;
- the path produces sticky-only output and leaves the default runtime path
  unchanged.

The exact environment variable names, executable construction, SDK, and
provider model snapshot are implementation decisions under the live adapter
issue. The reference adapter must conform to the selected Messages-style
capability profile; gateway-specific behavior is not implicitly covered. The
trust category and fail-closed rules are design decisions in #29.

## StateManifestV2 And Legacy Policy

The existing state manifest is an active v1 contract for current action paths.
It is not rewritten in place under `version: 1`. M4 adds a v2 shape for the
ledger-aware live path.

The v2 manifest keeps the existing host-owned state metadata and adds ledger
and provider-run-metadata descriptors with at least:

- relative ledger path inside the state bundle;
- exact ledger content hash and byte length;
- ledger schema version and prefix contract version;
- session identity and provider/model/adapter identity;
- generation or commit identity for stale-writer detection;
- provenance binding to repository, PR, head repository, workflow/event, and
  producing run.

The provider-run-metadata descriptor binds the complete
`ProviderRunMetadataV1` sidecar by path, hash, byte length, schema version, and
producing generation. It is restricted telemetry evidence, not ledger content
and not a raw provider transcript.

The ledger remains a separate file. The manifest binds it; it does not embed
the full ledger content.

For M4 it is a restricted durable state file inside the existing state bundle,
not a normal review artifact and not a raw-diagnostics artifact. It is restored
only through the M4 v2 namespace and is never selected through the current v1
backend namespace. The manifest is authoritative for host facts, provenance,
state key, and generation; the ledger is authoritative only for its bounded
runtime-owned logical records. Duplicate identity fields must match the
manifest or the candidate is rejected.

The logical state key is the canonical tuple
`m4-ledger-v2 / repository / headRepository / pullRequest / workflowIdentity /
trustedExecutionDomain`; artifact names and run ids are not substitutes for
this key. Automatic selection considers only bundles in this namespace.

Compatibility policy:

- v1 is not converted to v2;
- v1 fields such as session id, usage, prompt hash, review input hash, or legacy
  runtime files are not used to synthesize a ledger;
- v1 is classified as `unsupported_legacy_state` for the M4 live path and
  follows the safe bootstrap policy;
- missing, incompatible, corrupt, unsafe, or integrity-mismatched ledger state
  also follows safe bootstrap without unsafe reuse;
- existing v1 legacy/deterministic paths are not silently cut over by #29;
  global removal of v1 is a separate breaking-release decision;
- v2 is the first supported manifest contract for the live ledger path.

The implementation must make the v1 classification observable through a
bounded warning/phase reason. The action may complete a valid bootstrap review;
it must not treat an unsupported restore state as a provider cache miss.

## Ledger Content Contract

The ledger is a bounded, schema-owned, provider-neutral logical record. M4
`ProviderSessionLedgerV1` contains only these record variants:

- session and contract identities;
- cache-relevant policy/configuration identities;
- ordered prior interaction records containing an ordinal, role, source head
  and base provenance, and a bounded canonical review-context projection;
- validated structured review-outcome projections derived from the accepted
  `ReviewResultV1` shape, with no raw model transcript;
- version, hash, ordering, and provenance metadata required for integrity.

`ProviderSessionLedgerV1` does not use external or content-addressed references.
All durable content required to materialize the M4 prefix is inline in the
ledger and is covered by the manifest descriptor hash. Any reference-shaped
field is rejected by the closed schema and is an integrity failure.

The allowed interaction roles are exactly `review_context` and
`review_outcome`. The `review_context` projection contains only the bounded
changed-file metadata (`path`, `previousPath`, `status`, `additions`,
`deletions`, `changes`, and patch `sha256`/`truncated`/`maxChars` metadata), a
subject digest, and a policy/configuration digest. It excludes PR title/body,
policy text, patch text, and host facts. The `review_outcome` projection
contains only `summary`, `findings`, and `limitations` from the accepted
`ReviewResultV1`; it excludes usage, trace, warnings, diagnostics, budget
status, and host facts. The current raw PR delta is never persisted, but its
bounded canonical `review_context` projection is dynamic during the current
call and becomes stable prior history only after the candidate state is
accepted. Untrusted content is data inside framed record fields; it cannot
select a control role, policy, tool definition, or secret channel.

It must not contain:

- rendered provider prompts or raw provider message archives;
- raw provider request/response bodies;
- auth headers, provider secrets, or debug captures;
- private runner paths or environment values;
- unbounded patch, tool, or provider output;
- content that can be reinterpreted as system/developer instruction.

Restored repository and PR content remains untrusted data. Canonicalization
must preserve the data/control boundary and must not allow persisted content to
change policy, tool definitions, provider configuration, or secret handling.

`ProviderSessionLedgerV1` has a finite record and byte bound. Exact numeric
limits are implementation parameters, but the behavior is fixed: an existing
state over the bound is invalid and follows observable safe bootstrap; a
candidate append that would exceed the bound is rejected, is not persisted,
and is a current-run state contract failure. M4 does not perform automatic
rollover, silent truncation, or model-generated lossy compaction. A later
generation/rollover policy is a separate design decision.

## Session Identity And Lifecycle

Session continuity uses three distinct identity layers:

- **session scope**: repository, head repository, PR number, workflow identity,
  trusted execution domain, and host-owned session epoch;
- **cache-contract identity**: provider, model, adapter, ledger/prefix contract,
  template, policy, tool-definition, and cache-relevant configuration;
- **generation provenance**: predecessor `stateGeneration`, current
  `stateGeneration`, `ledgerEpoch`, reviewed head/base SHAs, current head/base
  SHAs, producing run, and commit identity.

Normal head pushes on the same PR, repository, workflow identity, and trust
domain continue the same session and append a new `stateGeneration` record
within the same `ledgerEpoch`. A base
branch/SHA change, force-push, or cache-contract identity change starts a new
`stateGeneration` under the same session scope with a new `ledgerEpoch`, an empty ledger, and an explicit
predecessor link; it does not reuse old prefix records. An empty-generation
reset is distinct from bootstrap: its header carries the accepted predecessor
manifest hash, predecessor ledger hash, predecessor `stateGeneration`, and reset
reason, but its logical record stream contains no predecessor records. Its
first candidate contains that reset header plus exactly one new context and one
new outcome record. The interaction identity uses the actual predecessor
ledger hash (not the `bootstrap` sentinel), and its `ledgerEpoch`-local
interaction ordinal starts at zero. The host validates this reset form by requiring the header to
match the accepted predecessor and by rejecting any copied predecessor
records. A repository, head
repository, PR, workflow identity, or trust-domain change requires a clean
bootstrap under a new session scope with a new `ledgerEpoch`; every new-session
bootstrap root has `stateGeneration` zero and interaction ordinal zero. A
ledger/prefix contract version change
is incompatible and cannot restore the old state. These rules are normative,
not implementation choices.

Current head/base identity is generation provenance, not session scope. The
workflow event type is provenance, while workflow identity and trust domain
determine scope. Host-owned repository and PR facts cannot be replaced by
runtime ledger values.

The identity precedence is fixed: a ledger/prefix contract version mismatch is
incompatible and is evaluated before any generic cache-contract change; it
never creates a new generation from the old artifact. Provider/model/adapter,
template, policy, tool, or cache-config changes create an empty
`stateGeneration` with a new `ledgerEpoch` under the same session scope. The
host classifies a head update as a normal
push only when the new head is a descendant of the accepted prior head; a
non-descendant or unknown ancestry (including a force-push) creates an empty
`stateGeneration` with a new `ledgerEpoch`. A base-ref name change or base-SHA
change likewise creates an empty `stateGeneration` with a new `ledgerEpoch`;
base advancement is intentionally conservative and is not
treated as an ordinary head append.

The lifecycle is:

```text
missing
  -> bootstrap
compatible
  -> restore -> materialize -> invoke -> validate -> append -> persist
invalid/incompatible/unsafe
  -> observable bootstrap
```

Expected invalidation, corruption, and unsafe provenance are distinct outcomes.
Expected invalidation follows the generation rules above. Automatic selection
of a missing, expired, corrupt, unsafe, incompatible, or mismatched artifact
bootstraps with an observable reason. An explicitly selected artifact with any
of those conditions fails closed before provider invocation; it never silently
bootstraps.

Automatic recovery from an invalid or unavailable accepted artifact is a
separate root transition, not a generation derived from that artifact. The
host creates a new session epoch under the same state key, sets
`stateGeneration` to zero, uses the `bootstrap` predecessor sentinel, and writes a root header with
the new epoch and observable recovery reason. The invalid artifact's bytes are
never used as a predecessor. Acceptance of that root is guarded by the
observed current-selector revision (including an empty revision when no
accepted marker exists), so it can supersede the invalid current marker but
cannot overwrite a newer valid successor. Old markers remain immutable and
non-current. The same recovery-root rule applies to contract-version
incompatibility and an over-bound ledger; those cases never create a
generation from the incompatible artifact. Explicitly selected invalid state
does not enter recovery and remains fail-closed.

An interaction is appended only after the provider result and candidate ledger
pass runtime validation. The host derives
`interactionId = SHA256(UTF8("agentic-pr-review/interaction/v1") || 0x00 ||
encodeIdentity(predecessorLedgerSha256) || encodeIdentity(inputSha256) ||
encodeIdentity(currentHeadSha) || encodeIdentity(interactionOrdinal))`. For a new-session
bootstrap, `predecessorLedgerSha256` is the literal sentinel `bootstrap` (not a
hash). For a same-session empty-generation reset, it is the actual accepted
predecessor ledger hash. In both cases the new `ledgerEpoch`'s ordinal starts
at zero. `interactionOrdinal` is the zero-based ordinal within the
`ledgerEpoch`, encoded as its ASCII decimal string; it increments once per
accepted interaction in a compatible successor and resets to zero for a new
session, recovery root, or empty-generation reset. The context and outcome
records share this id; retries reuse it. `stateGeneration` increments for every
accepted successor independently of `interactionOrdinal`.
`ProviderRunMetadataV1`
contains the interaction id, consumed input hash, result hash, trace hash,
predecessor ledger hash, and candidate ledger hash. The host independently
requires the trace/result input hashes, exact result/trace bytes, metadata
hashes, and candidate final outcome projection to agree before accepting the
transaction. A candidate manifest binds the same values and predecessor. The
host derives the expected current `review_context` projection from the
validated `ReviewInputV1` and host facts and requires exact equality. The
For a compatible continuation, the candidate ledger must preserve the
predecessor ledger byte-for-byte in logical content and append exactly two new
records—one context and one outcome—with the current interaction id. For a
same-session empty-generation reset, it must contain the matching reset header
and exactly those two new records, with no predecessor records. For a new-session
bootstrap, it contains only its bootstrap header and those two records. In all
three forms, it may not delete, modify, reorder, or insert any other record.
This exact-append/reset check is independent of prefix materialization.

Provider retries do not silently create duplicate logical interactions. M4
requires per-state-key workflow concurrency with cancel-in-progress; without
that correctness barrier the host must not persist live state. The host
accepts a candidate only when its state key matches, its predecessor manifest
hash equals the currently accepted bundle (or is empty for a root), its
`stateGeneration` is predecessor plus one for a continuation/reset or zero for
a new session/recovery root, its `ledgerEpoch` equals the predecessor epoch for
a compatible continuation and is fresh for a reset/root, and its current
head/base provenance matches. Artifact
upload is not acceptance. Under the per-state-key workflow lock, the host
takes an acceptance snapshot scoped to one state key, `sessionEpoch`, observed
selector revision, target `stateGeneration`, `ledgerEpoch`, and interaction,
validates the selector result for the exact semantic candidate, writes one
immutable `accepted-state` marker for that accepted generation, and atomically
advances the current-state selector only when its predecessor pointer still
matches the snapshot. Only then does it cross the sticky-publishing barrier.
The semantic duplicate key is `(sessionEpoch, selectorRevision, interactionId,
predecessorLedgerSha256, candidateLedgerSha256, resultSha256, traceSha256,
metadataSemanticSha256)` and excludes producing-run provenance. Candidate
headers, manifests, accepted markers, and the current selector all bind the
same `sessionEpoch`; candidates from an older epoch are stale before duplicate
or conflict comparison. `metadataSemanticSha256` is
`SHA256(UTF8("agentic-pr-review/provider-run-metadata-semantic/v1") || 0x00 ||
RFC8785(semanticMetadata))`, where `semanticMetadata` contains exactly
`schemaVersion`, `selectedProviderId`, `observedProviderId`, `resolvedModelId`,
`adapterId`, `logicalPrefixSha256`, `prefixSha256`, `capability`,
`cacheStatus`, `normalizedUsage`, `retryObservations`, `errorCodes`, and
`telemetryCompleteness`; it excludes exactly `producingRunId` and `runAttempt`
and the transaction-binding fields `interactionId`, `consumedInputSha256`,
`resultSha256`, `traceSha256`, `predecessorLedgerSha256`, and
`candidateLedgerSha256`, because those are bound by the outer semantic
duplicate key. It also excludes all request IDs, raw errors, endpoints, and
arbitrary extensions. Byte-
identical semantic duplicates use the smallest `(producingRunId, runAttempt)`
present in that acceptance snapshot. If that snapshot contains differing
semantic candidates for the same session epoch, selector revision, interaction,
and predecessor, it is a conflict and
none is accepted in that snapshot. A later candidate targeting the same
session epoch, selector revision, predecessor/`stateGeneration`/interaction is
permanently `stale_candidate` and cannot revoke or modify that generation's
immutable marker or sticky result. A
candidate whose predecessor is the currently selected accepted generation is a
valid successor and may advance the selector through a new acceptance snapshot.
Reruns with the same interaction id and semantic hashes are idempotent. A
stale run may finish its review, but must not overwrite a newer ledger.

The current dynamic PR context becomes stable prior history only after the
current interaction and candidate ledger have passed validation and the host
accepts the result for state persistence. If state upload fails, no sticky
review is published by the M4 live path; the candidate is discarded and the
next run bootstraps. This is a bounded partial failure, not a claim of
continuity.

## Runtime Process Transport

The existing M2 process contract carries `ReviewInputV1`, `ReviewResultV1`,
and `ReviewTraceV1`. M4 does not add ledger content to those closed V1
protocol objects.

M4 adds separate, explicit ledger and provider-run-metadata sidecar channels
to the trusted live invocation contract. `ProviderRunMetadataV1` is a new
sidecar contract, not a change to the closed `Review*V1` objects, and carries
the current-run prefix hash, selected/observed provider identity, capability,
cache status, normalized usage, retry observations, and telemetry completeness.

The invocation contract is:

- an optional validated ledger input is supplied as a host-owned sidecar for
  restore; its absence represents bootstrap;
- the runtime writes complete candidate ledger and provider-run-metadata
  sidecars separately from result and trace, including on bootstrap;
- the runtime performs schema/integrity validation; the host independently
  validates schema, manifest binding, provenance, and secret/privacy rules;
- the sidecar channel is not an arbitrary path supplied by untrusted input;
  the host owns the bounded invocation directory and exact file names;
- all four outputs must be valid for a live invocation to exit successfully;
- the host commit order is fixed: validate all four staged outputs; write the
  ledger and provider-run metadata; write the v2 manifest last with descriptors
  for both; atomically publish a validated local candidate bundle; upload that
  candidate; under the per-state-key lock take an immutable acceptance snapshot,
  select and validate the exact candidate, durably write the `accepted-state`
  marker, and only then cross the sticky-publishing barrier;
- workstream 8 produces a validated local candidate, not an accepted bundle;
  workstream 6 owns upload, selector validation, the durable acceptance marker,
  and the sticky barrier. Marker-write failure means no accepted state and no
  sticky publication. An uploaded candidate not referenced by an accepted
  marker is permanently non-restorable and is treated as an orphan;
- an upload or validation failure leaves no accepted state and permits no
  sticky publication; temporary files and orphan bundles are ignored by the
  v2 state-key selector;
- a missing or invalid candidate ledger cannot be treated as successful
  resumed-state output.

Cancellation before the manifest-last local commit leaves staged files
discardable. A stale writer may finish its review, but its generation/provenance
must fail the host acceptance check and cannot replace an accepted bundle.
Exact CLI flag spelling or sidecar file names belong to the implementation
issue, but the channel set, ownership, optional-input semantics, validation,
atomicity, commit order, and privacy boundary are fixed here.

## Prefix Contract

The contract has two explicit layers:

1. The core produces a provider-neutral canonical logical projection.
2. The adapter produces provider-specific cache-relevant prefix bytes from that
   projection under an explicit adapter/provider/model identity.

The core first emits an append-safe logical segment stream. The reference
adapter then maps that stream one-to-one to a canonical provider-block
content prefix: ordered role/content blocks with no provider envelope or
closing JSON delimiters. The cache marker is adapter control metadata, not part
of either append-safe stream. The adapter places one marker at the current
stable/dynamic boundary for each request; moving or regenerating that marker is
allowed and does not rewrite the content blocks covered by the strict-prefix
invariant.
The provider adapter conformance fixture must prove that this block projection
is exactly the cache-relevant prefix submitted to the provider and that
appending a logical segment appends provider blocks without rewriting prior
blocks. Raw HTTP serialization is not itself hashed, but the adapter must not
let SDK serialization change the canonical block sequence.

Each logical and provider-block segment is encoded as a big-endian uint32 byte
length followed by canonical UTF-8 JSON; segments are concatenated without a
total-length prefix. Identity values are non-empty, case-sensitive UTF-8
strings of at most 256 bytes, with no normalization; version values use their
ASCII decimal form and `cacheConfigId` is a lowercase hex digest. Define
`encodeIdentity(x) = uint32be(byteLength(UTF8(x))) || UTF8(x)`. The exact
preimages are:

All cache-contract IDs are host-owned lowercase SHA-256 digests. For every ID,
`digestId(tag, envelope) = SHA256(UTF8(tag) || 0x00 || RFC8785(envelope))`,
where the tag is ASCII exactly as shown below and RFC 8785 emits UTF-8 bytes.
The canonical envelopes are fixed as follows: `templateId` uses
`{schemaVersion, templateVersion, definition}`; `policyId` uses
`{schemaVersion, policyVersion, instructions, constraints}`;
`toolDefinitionId` uses `{schemaVersion, toolsetVersion, definitions}` where
`definitions` is an ordered array of `{name, description, inputSchema,
policyMetadata}`; `cacheConfigId` uses `{schemaVersion, cacheConfigVersion,
markerPolicy, eligibility, statelessMode}`; and `adapterId` uses
`{schemaVersion, capabilityProfileVersion, adapterBuildVersion}`. The exact
domain tags are `agentic-pr-review/cache-contract/template/v1`,
`agentic-pr-review/cache-contract/policy/v1`,
`agentic-pr-review/cache-contract/tools/v1`,
`agentic-pr-review/cache-contract/config/v1`, and
`agentic-pr-review/cache-contract/adapter/v1`, respectively. The listed
objects are the complete envelopes: absent fields are absent, explicit null is
distinct from absence, and schema defaults are omitted before RFC 8785. Any
change to those canonical sources must change its digest or explicitly bump the
version in the envelope. Provider and resolved model IDs are host-selected
canonical snapshot strings; floating aliases are not valid cache-contract
identities. These sources and authorities are fixed by host configuration, not
by runtime ledger content.

The interaction and prefix domain tags below end with exactly one NUL octet
(`0x00`); they do not contain the UTF-8 bytes for a backslash followed by `0`.
This byte rule applies to every `digestId` tag as well.

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

Neither hash covers an SDK object, cache marker control metadata, or raw HTTP
body. Both outputs are lowercase hexadecimal.

`materializePrefix(ledger, providerConfig, staticPolicy)` is deterministic over
its declared inputs. Current time, runner path, random ids, process locale,
unordered iteration, implicit SDK defaults, and transient diagnostics are not
implicit inputs.

The stable/dynamic split is explicit:

- stable prefix: fixed instructions, policy, tool definitions, versioned
  identities, and canonical prior turns;
- dynamic suffix: current PR delta, current run metadata, transient
  diagnostics, fresh tool output, timestamps, and provider request ids.

Required invariants:

- same logical inputs produce byte-identical prefix bytes and the same hash;
- restore round-trip preserves logical identity and prefix identity;
- changing only non-semantic run/artifact metadata does not change the stable
  prefix;
- appending a validated canonical interaction preserves the old stable prefix
  as a strict byte prefix of the new stable prefix;
- provider/model/adapter/template/policy/tool/cache-relevant changes produce
  explicit invalidation or a distinct hash domain;
- provider cache miss with a stable hash is an observed provider outcome,
  whereas hash drift for unchanged logical inputs is a runtime contract
  regression.

Canonical JSON uses the RFC 8785 JSON Canonicalization Scheme over UTF-8,
without Unicode normalization; lone surrogates and non-finite numbers are
rejected, unknown fields are rejected, schema-declared defaults are omitted,
and null-versus-absent semantics are explicit. The hash framing and the
logical-to-provider-block conformance fixture above are normative; the
implementation library and writer are not.

## Capability, Usage, And Cost

The reference capability profile requires messages with the fixed role/content
block mapping above, an explicit cache marker at the stable-prefix boundary,
per-request cache-read/cache-write/uncached/output usage or an explicit
unsupported result, resolved model identity, a cache-disabled/stateless mode,
and a reported minimum cacheable-prefix eligibility. Capability and cache
status are per-request observations. The run aggregate is unsupported if any
request is unsupported or ineligible, unknown if any request is
telemetry-unavailable or unknown, partial for a mixture of hits and misses,
or if any request is partial, hit for all hits, and miss for all misses. The stateless mode is an
adapter-owned request-construction mode that must also carry a
provider-advertised or synthetic proof of no cache read/write. Adapters report
capability and observed usage without pretending that provider cache behavior
is deterministic. Capability distinguishes unsupported, eligible, ineligible,
telemetry-unavailable, and unknown cases. Cache status distinguishes hit,
partial, miss, unsupported, and unknown.

Every run returns the normalized usage vector through `ProviderRunMetadataV1`
when available:

- uncached input;
- cache-write input;
- cache-read input;
- output;
- request/retry observations and telemetry completeness.

The evaluation configuration defines versioned weights outside the protocol.
The primary cache metric is normalized input cost:

```text
normalizedInputCost =
  uncachedInput * uncachedWeight
  + cacheWriteInput * cacheWriteWeight
  + cacheReadInput * cacheReadWeight
```

For each provider request, uncached, cache-write, and cache-read input are
mutually exclusive partitions of total input tokens when telemetry is
complete; retries are summed by attempt. Missing values are unknown, not zero.
Inconsistent counters produce incomplete telemetry and cannot pass a cost
graduation gate. This precedence is normative. The host-selected
provider/model/adapter identity is authoritative; the adapter-reported
resolved identity must match it or the current run fails, unless a future
explicitly versioned alias map is added. The resolved identity is persisted in
`ProviderRunMetadataV1` and the v2 manifest descriptor. The metadata sidecar
contains only bounded identities, hashes, counts, statuses, retry/error codes,
and completeness flags; it excludes provider request ids, raw error text, raw
provider fields, endpoints, and arbitrary extension objects.

A secondary normalized total cost may add output cost. Resumed-session
evaluation compares equivalent multi-run sequences with the same provider,
model, policy, tools, current PR deltas, request count, and retry policy,
including cache-write warm-up:

```text
resumedSessionInputCostRatio =
  sum(resumed normalized input cost)
  / sum(stateless normalized input cost)
```

The stateless comparator serializes the same canonical prior logical context,
current PR deltas, provider/model/policy/tools, request count, and retry policy
with cache markers disabled and the provider's cache-disabled/stateless mode
explicitly selected; only the resume/cache strategy differs. If the provider
cannot prove cache-disabled behavior, that live observation is inconclusive and
cannot pass the gate. The resumed sequence includes cache-write warm-up.
Ratios use checked non-negative decimal arithmetic, sum all valid run costs
before one division, and reject a zero denominator. The evaluation profile
versions the rounding epsilon (M4 default: 0.01 ratio units) and defines
mutually exclusive outcomes: pass is `ratio <= 1.01`, inconclusive is
`1.01 < ratio <= 1.05`, and regression is `ratio > 1.05`. Every complete suite
must include all seven mandatory scenario classes: large prior context/small
delta, multiple normal head pushes, repeated no-finding/finding outcomes,
cache-write warm-up, partial hit, isolated miss, and multi-request/retry.
Each of three consecutive complete suites must pass for cost graduation; a
suite-level regression in each of three consecutive complete suites blocks
graduation. Inconclusive or telemetry-incomplete suites cannot pass and do not
count toward either window. Prefix hash drift is a contract regression and is
not covered by provider tolerance.

M4 implementation closure requires the two deterministic and two trusted live
workflow proofs plus an executable cost harness and at least one complete
synthetic suite. Three-suite cost graduation is a separate runtime-graduation
gate: M4 may remain experimental when live cost is inconclusive or regresses,
but it cannot be promoted to a default path; three consecutive regression
suites are an explicit graduation blocker.

## Failure And Partial-Success Semantics

- Missing, expired, incompatible, corrupt, unsafe, or untrusted prior ledger:
  observable safe bootstrap; no partial ledger reuse.
- Current-run ledger validation, hash, or process-contract failure: runtime
  contract failure; do not publish candidate runtime output as valid resumed
  state.
- Provider success followed by append or candidate-ledger validation failure:
  runtime/state contract failure; no invalid ledger or sticky review is
  published.
- Valid staged state followed by ledger artifact upload failure: bounded
  partial state failure; the candidate is not accepted, no sticky review is
  published, and the next run bootstraps.
- State upload success followed by sticky publication failure: the v2 state is
  valid and may be restored; publication failure is reported separately and
  does not make the ledger corrupt.
- Same prefix hash with provider miss: observed provider/cache outcome, not a
  deterministic contract failure.
- Different prefix hash after a legal provider/model/policy/version change:
  expected invalidation, not corruption.

## Implementation Follow-Ups

The following workstreams are created or explicitly identified before #29
closes. They may begin as refinement-ready outlines; each must be refined to
agent-ready before implementation and must not redefine this document:

1. `StateManifestV2` descriptor, v2 namespace, local bundle schema, host-fact
   authority, and v1 unsupported-state handling. It does not own artifact
   selection/upload or concurrency orchestration.
2. `ProviderSessionLedgerV1` schema, bounded content validation, integrity,
   unsafe/corrupt fixtures, over-bound restore bootstrap, and append-rejection
   behavior. It does not design rollover.
3. Canonical logical projection, append-safe provider-prefix segment stream,
   `prefixSha256`, and golden byte/hash fixtures. It does not own the provider
   envelope.
4. Provider capability, normalized usage, cache status, and
   `ProviderRunMetadataV1` sidecar schema.
5. Trusted live provider adapter and live-mode configuration, including the
   selected capability profile, secret injection, trust gates, provider
   selection, and bounded provider failures. It consumes the existing process
   and metadata contracts and does not own sidecar I/O or artifact persistence.
6. Cross-workflow artifact selection/upload, v2 state persistence,
   stale-writer/concurrency barriers, selector validation, durable acceptance
   marker, sticky-after-upload behavior, and partial-success handling, starting
   from a validated local candidate produced by workstream 8. It must never
   restore an uploaded bundle that lacks the accepted marker.
7. Resumed-session cache/cost evaluation with deterministic synthetic provider
   fixtures and representative multi-run sequences.
8. Process-boundary sidecar I/O through a validated local candidate bundle:
   ledger input/output, provider-run metadata, result/trace staging, independent host
   validation, manifest-last local commit, exit semantics, cancellation
   cleanup, and local no-overwrite behavior. It ends before GitHub artifact
   upload and sticky publication.

Each follow-up owns its implementation API, files, test layout, exact bounds,
and diagnostic names while inheriting the contract above.

## Proof Gates And Validation Plan

M4 implementation closure has two separate proof gates. The deterministic
contract gate is required and must use two real, isolated workflow runs with a
secret-free synthetic provider to prove restore, append, hash, and fallback;
it is suitable for normal CI but is not two in-process test cases. The live
observation gate is also required for M4 implementation closure and must run
the trusted reference adapter across two controlled workflow runs using the
allowlisted secret channel. It is opt-in and never a per-PR requirement; its
cache/usage observations are graduation evidence, not deterministic contract
oracles. Issue #29 itself is only the design gate and does not execute either
workflow proof.

The design is validated with `npm run check` and documentation link/format
checks. Follow-up fixtures must cover:

- same-ledger restore and exact prefix/hash stability;
- append and strict prefix extension;
- stable/dynamic boundary changes;
- provider/model/template/policy/tool/config invalidation;
- v1 unsupported legacy state and v2 validation;
- missing, expired, corrupt, incompatible, unsafe, and hash-mismatched ledger;
- provenance mismatch, stale writer, rerun, and duplicate append;
- provider telemetry unavailable, unsupported cache, hit, partial, miss, and
  inconsistent counters;
- stateless/resumed cost comparison including warm-up and output reporting;
- secret, raw prompt, path, auth-header, and prompt-injection-like content
  non-disclosure.

## Migration Impact

This is an explicit state-artifact format boundary, not a general migration
framework. The v1-to-v2 policy is no conversion, no backfill, no ledger reuse,
and safe observable bootstrap for the M4 live path. If a later release removes
the existing v1 legacy/deterministic writer and reader globally, that is a
separate breaking release with migration notes and a retention-window plan.

## Open Implementation Questions

These do not reopen the cross-issue contract:

- exact v2 JSON property names and `$id`;
- exact sidecar filenames and CLI flags;
- exact environment variable names and provider SDK;
- exact ledger byte/turn limits;
- C# namespaces and file layout;
- test framework, helper layout, and diagnostic code strings;
- exact provider model snapshot used by the reference adapter.
