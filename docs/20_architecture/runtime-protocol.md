# Runtime Protocol

The runtime boundary is protocol-first and file-based.

## Direction

The TypeScript host writes review input JSON. The runtime reads that input and writes structured result JSON plus optional trace output. Both sides validate protocol version and fail closed on incompatible contracts.

## Protocol Files

The protocol is defined as JSON Schema files under `protocol/schemas/`:

- `review-input.v1.json` - input contract (ReviewInputV1), defined in #14
- `review-result.v1.json` - result contract (ReviewResultV1), defined in #15
- `review-trace.v1.json` - trace contract (ReviewTraceV1), defined in #16

TypeScript hand-writes convenience interfaces that mirror the schemas and uses ajv for runtime validation. JSON Schema is the authoritative source of truth shared with the future C# runtime. See `src/protocol/` for the TypeScript types and validation wiring.

## Input Contract (ReviewInputV1)

ReviewInputV1 is defined (#14) and includes:

- `protocolVersion` - integer protocol-generation version, shared across input/result/trace; exact match required
- `requestedRuntimeVersion` - opaque runtime version request or null
- `host` - trusted host-owned metadata (repository, review facts, runtime options)
- `subject` - untrusted review data (pull request metadata, changed files with bounded patch context, context documents, policy text)
- `previousState` - minimal previous review state summary
- `commentEvidence` - minimal existing comment/duplicate-evidence summary

Key conventions established by ReviewInputV1 and reused by result/trace:

- `protocolVersion` is the integer `1`, protocol-wide, exact-match fail-closed
- trusted host metadata and untrusted review subject data are structurally partitioned (`host`/`subject`)
- paths use a reusable `repoRelativePath` definition (normalized POSIX, rejecting absolute, drive-qualified, protocol-looking, current-dir-only, backslash, and `..` paths)
- patch context is a bounded object with a lowercase-hex sha256 of the bounded text
- closed object shapes (`additionalProperties: false`) reject credential-shaped fields

Input is sanitized for runtime consumption. GitHub write credentials do not belong in runtime input.

## Output Contract (ReviewResultV1)

ReviewResultV1 is defined (#15) and carries runtime-proposed content only. The host assembles the full `StructuredReviewEnvelopeV1` by combining the result with host-owned metadata (phase, SHAs, reviewedRange, runtimeProvider, sessionId, usageBudgetStatus, lineageTotals). Host-owned workflow facts are excluded from the result by closed object shapes.

ReviewResultV1 includes:

- `protocolVersion` - integer protocol-generation version, shared across input/result/trace
- `runtimeVersion` - opaque runtime version supplied by the runtime
- `inputSha256` - optional non-authoritative echo of the input hash
- `summary` - review summary
- `findings` - structured findings (severity, confidence, category, title, body, evidence, path, startLine/endLine, suggestedAction, inlinePreference)
- `limitations` - runtime-stated limitations
- `usage` / `observedTurns` / `observedTurnSource` - runtime telemetry (no secrets)
- `warnings` - sanitized non-blocking notes
- `diagnostics` - sanitized, bounded error/contract info (no raw provider bodies, prompts, or auth headers)
- `trace` - optional lightweight reference to the trace output (full `ReviewTraceV1` is #16)

Key result conventions:

- `confidence` is `medium | high` only; low-confidence observations are structurally unrepresentable (omitted by design)
- findings do not carry `fingerprint`; the host computes fingerprints for duplicate suppression
- finding locations use `startLine`/`endLine` (both-null for pathless, both-present with `startLine <= endLine`, line values require a non-null `path`); cross-field rules are enforced by post-schema semantic validation
- `inlinePreference` (`allowed | preferred | avoid`) is a runtime preference; the publisher owns the final inline vs sticky decision

## Trace Contract (ReviewTraceV1)

ReviewTraceV1 is defined (#16) and carries sanitized execution evidence for deterministic validation and future replay. The trace is runtime-produced and optional - a review can complete without one. The host stores, uploads, and verifies trace files but does not author their content.

ReviewTraceV1 includes:

- `protocolVersion` - integer protocol-generation version, shared across input/result/trace
- `runtimeVersion` - opaque runtime version supplied by the runtime
- `inputSha256` - required lowercase hex SHA-256 of the consumed input file bytes
- `resultSha256` - optional lowercase hex SHA-256 of the produced result file bytes (absent on failure path)
- `mode` - execution context (`deterministic-fixture | live-provider | skipped`); reflects run type, not success/failure
- `fixture` - optional metadata for test fixture detail (expected only for `deterministic-fixture`)
- `provider` - optional sanitized provider metadata (`name`, `model`, `requestCount`)
- `startedAt` / `completedAt` - optional ISO-8601 timestamp strings (no format validation; fixtures may omit)
- `usage` - optional current-run usage (same shape as `ReviewResultV1.usage`; excludes `lineageTotals` and `usageBudgetStatus`)
- `toolCalls` - required array of sanitized tool-call summaries (each entry: `name`, `status`, optional `durationMs`/`errorCode`; no input/output content)
- `warnings` - sanitized non-blocking notes
- `diagnostics` - sanitized, bounded diagnostics (same shape as `ReviewResultV1` diagnostics)

Key trace conventions:

- `inputSha256` is required because a trace always corresponds to a consumed input; `resultSha256` is optional because failure paths may produce a trace without a valid result
- `mode` does not express failure taxonomy; failure classification and exit-code mapping are deferred to #20
- `toolCalls` is required (empty array allowed); entries carry no content (no `inputSummary`/`outputSummary`), enforcing the sanitized boundary structurally
- `usage` excludes `lineageTotals` and `usageBudgetStatus` - those are host-owned accumulated state, not runtime-produced
- the trace payload contains no path fields; `ReviewResultV1.trace.path` already points to the trace artifact file
- timestamps must not be used for deterministic identity

### Hash chain

The three contracts form a bidirectional hash chain:

- `ReviewResultV1.inputSha256` = SHA-256 of input file bytes (result echoes input)
- `ReviewResultV1.trace.sha256` = SHA-256 of trace file bytes (result points to trace)
- `ReviewTraceV1.inputSha256` = SHA-256 of input file bytes (trace echoes input)
- `ReviewTraceV1.resultSha256` = SHA-256 of result file bytes (trace points back to result)

`ReviewResultV1.trace.sha256` and `ReviewTraceV1.resultSha256` are distinct fields with distinct hash targets.

### Privacy

Trace privacy is enforced at the schema level by closed shapes (`additionalProperties: false`) that reject raw/credential-shaped fields such as `apiKey`, `authHeader`, `rawRequest`, `rawResponse`, and `prompt`. All allowed strings are bounded and non-blank. JSON Schema cannot guarantee arbitrary secret-value detection inside allowed strings; producer-side sanitization is the runtime's responsibility.

Restricted raw diagnostics (raw provider request/response bodies) remain a separate opt-in path via `debugCaptureRawApiBodies` and are not part of `ReviewTraceV1`. See `docs/20_architecture/security-boundary.md`.

## Contract Strategy

The protocol uses JSON Schema (draft-07) files as the single source of truth, avoiding two independently drifting definitions of business behavior across TypeScript and C#. TypeScript interfaces are developer ergonomics only; the schemas are authoritative.

## Future: Session Ledger Artifact

The current protocol defines `ReviewInputV1`, `ReviewResultV1`, and `ReviewTraceV1`. A future project-owned runtime that resumes context across GitHub Actions runs will need an additional artifact type (for example, `ProviderSessionLedgerV1` or `RuntimeSessionV1`) to carry the canonical session ledger.

This ledger artifact is distinct from `ReviewTraceV1`:

- `ReviewTraceV1` is sanitized execution evidence for validation and replay; it carries no conversation content.
- The ledger carries enough canonical logical content to reconstruct the cacheable provider request prefix, within the security boundary defined in `docs/20_architecture/security-boundary.md`.

The protocol will also need to partition stable context (system instructions, policy, tool definitions, canonical prior turns) from volatile context (current PR delta, run metadata) to serve prefix-cache stability. See `docs/20_architecture/architecture.md` (Provider Request Prefix Contract) for the invariants.

This is a direction statement only. The existing schemas are not changed; the ledger artifact and stable/volatile partitioning will be designed in a separate issue before project-owned live provider implementation.
