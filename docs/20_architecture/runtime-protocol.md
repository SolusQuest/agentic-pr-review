# Runtime Protocol

The runtime boundary is protocol-first and file-based.

## Direction

The TypeScript host writes review input JSON. The runtime reads that input and writes structured result JSON plus optional trace output. Both sides validate protocol version and fail closed on incompatible contracts. The M2 CLI process contract, including version comparison, exit classes, output commits, and sanitized diagnostics, is defined in [runtime-cli-process-contract.md](./runtime-cli-process-contract.md).

## Protocol Files

The protocol is defined as JSON Schema files under `protocol/schemas/`:

- `review-input.v1.json` - input contract (ReviewInputV1), defined in #14
- `review-result.v1.json` - result contract (ReviewResultV1), defined in #15
- `review-trace.v1.json` - trace contract (ReviewTraceV1), defined in #16

TypeScript hand-writes convenience interfaces that mirror the schemas and uses ajv for runtime validation. JSON Schema is the authoritative source of truth shared with the selected C# runtime. See `src/protocol/` for the TypeScript types and validation wiring.

## Input Contract (ReviewInputV1)

ReviewInputV1 is defined (#14) and includes:

- `protocolVersion` - integer protocol-generation version, shared across input/result/trace; exact match required
- `requestedRuntimeVersion` - opaque runtime version request or null; the M2 CLI uses exact ordinal string equality when it is non-null (see [runtime-cli-process-contract.md](./runtime-cli-process-contract.md))
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

ReviewTraceV1 is defined (#16) and carries sanitized execution evidence for deterministic validation and as one input to a future replay bundle. The trace is runtime-produced and optional - a review can complete without one. The host stores, uploads, and verifies trace files but does not author their content. The M2 `review` CLI is stricter: it requires `--trace` and commits a valid trace before returning success; see [runtime-cli-process-contract.md](./runtime-cli-process-contract.md).

ReviewTraceV1 includes:

- `protocolVersion` - integer protocol-generation version, shared across input/result/trace
- `runtimeVersion` - opaque runtime version supplied by the runtime
- `inputSha256` - required lowercase hex SHA-256 of the consumed input file bytes
- `resultSha256` - optional lowercase hex SHA-256 of the produced result file bytes; it is absent when no valid result exists and on the M2 CLI's non-circular success path
- `mode` - execution context (`deterministic-fixture | live-provider | skipped`); reflects run type, not success/failure
- `fixture` - optional metadata for test fixture detail (expected only for `deterministic-fixture`)
- `provider` - optional sanitized provider metadata (`name`, `model`, `requestCount`)
- `startedAt` / `completedAt` - optional ISO-8601 timestamp strings (no format validation; fixtures may omit)
- `usage` - optional current-run usage (same shape as `ReviewResultV1.usage`; excludes `lineageTotals` and `usageBudgetStatus`)
- `toolCalls` - required array of sanitized tool-call summaries (each entry: `name`, `status`, optional `durationMs`/`errorCode`; no input/output content)
- `warnings` - sanitized non-blocking notes
- `diagnostics` - sanitized, bounded diagnostics (same shape as `ReviewResultV1` diagnostics)

Key trace conventions:

- `inputSha256` is required because a trace always corresponds to a consumed input; `resultSha256` is optional because traces may lack a valid result and because the M2 CLI uses a non-circular success shape
- `mode` does not express failure taxonomy; failure classification and exit-code mapping are defined by [runtime-cli-process-contract.md](./runtime-cli-process-contract.md)
- `toolCalls` is required (empty array allowed); entries carry no content (no `inputSummary`/`outputSummary`), enforcing the sanitized boundary structurally
- `usage` excludes `lineageTotals` and `usageBudgetStatus` - those are host-owned accumulated state, not runtime-produced
- the trace payload contains no path fields; when present, `ReviewResultV1.trace.path` points to the trace artifact file
- timestamps must not be used for deterministic identity

### Hash chain

The schemas expose optional hash links that can point in both directions:

- `ReviewResultV1.inputSha256` = SHA-256 of input file bytes (result echoes input)
- `ReviewResultV1.trace.sha256` = SHA-256 of trace file bytes (result points to trace)
- `ReviewTraceV1.inputSha256` = SHA-256 of input file bytes (trace echoes input)
- `ReviewTraceV1.resultSha256` = SHA-256 of result file bytes (trace points back to result)

`ReviewResultV1.trace.sha256` and `ReviewTraceV1.resultSha256` are distinct fields with distinct hash targets. Exact-byte result and trace files cannot contain both links without a circular dependency. The M2 CLI therefore uses the non-circular shape in [runtime-cli-process-contract.md](./runtime-cli-process-contract.md): the trace omits `resultSha256`, while the result contains `trace.sha256`.

### Privacy

Trace privacy is enforced at the schema level by closed shapes (`additionalProperties: false`) that reject raw/credential-shaped fields such as `apiKey`, `authHeader`, `rawRequest`, `rawResponse`, and `prompt`. All allowed strings are bounded and non-blank. JSON Schema cannot guarantee arbitrary secret-value detection inside allowed strings; producer-side sanitization is the runtime's responsibility.

Restricted raw diagnostics (raw provider request/response bodies) remain a separate opt-in path via `debugCaptureRawApiBodies` and are not part of `ReviewTraceV1`. See `docs/20_architecture/security-boundary.md`.

## Protocol Fixtures

Synthetic fixture files under `protocol/fixtures/v1/` prove the schemas work with realistic payloads and make schema drift visible in CI. Fixtures are reused by #18 (TS builder tests), #19 (runtime CLI), and #21 (CI fixture check).

### Layout

- `valid-<contract>-<scenario>.json` - valid payloads that must pass validation
- `invalid-<contract>-<reason>.json` - invalid payloads that must fail with expected errors
- `cases/<scenario>/` - paired fixtures (input + result + trace) for hash-chain verification
- `manifest.json` - centralized manifest recording expected validation outcomes for every fixture

### Manifest

Each entry is either a single fixture (`type: "fixture"` with `file`, `contract`, `valid`, optional `expectedErrorIncludes`) or a paired case (`type: "case"` with `directory`, `contracts`, `valid`, `verifyHashChain`).

For invalid fixtures, `expectedErrorIncludes` is an array of substrings matched against joined validator error messages. Each invalid fixture includes at least one field-specific or rule-specific token.

### Validator entrypoints

The fixture runner (`src/protocol/fixtures.test.ts`) calls the TS validators (`validateReviewInputV1`/`validateReviewResultV1`/`validateReviewTraceV1`), not raw Ajv. This ensures post-schema semantic validation (e.g., ReviewResultV1 line-range cross-field rules) is exercised.

### Hash-chain verification

Paired cases verify non-circular hash links over exact file bytes (no canonical JSON). `trace.resultSha256` is omitted in paired cases to avoid a circular exact-file-byte hash dependency between result and trace files.

### Update rules

- All fixtures must be synthetic and public-safe (no real PR data, tokens, provider responses, or private paths).
- When adding a fixture, add a corresponding entry to `manifest.json`.
- When modifying a schema, verify all fixtures still produce expected outcomes.
- When adding a paired case, construct files in dependency order: input first, then trace (with `resultSha256` omitted), then result (with `trace.sha256` filled from trace file bytes).

The protocol uses JSON Schema (draft-07) files as the single source of truth, avoiding two independently drifting definitions of business behavior across TypeScript and C#. TypeScript interfaces are developer ergonomics only; the schemas are authoritative.

## TypeScript Builders and Mappers (M1 test-only, wired in M2)

Two pure helpers bridge existing host structures and the protocol contracts. They are added in #18 as M1 test-only functions and are wired into the action execution path in M2 via #33; they are not called by `src/main.ts` in M1.

### `buildReviewInputV1` (`src/protocol/build-review-input.ts`)

Given the existing host structures (`ReviewTarget`, `LoadedBlock[]`, a `Pick<>` subset of `ActionConfig`, optional `RestoredState`, explicit previous-review and existing-comment fingerprint lists, and an authoritative repository identity), produces a schema-valid `ReviewInputV1`.

Notable rules:

- The `config` parameter is a `Pick<ActionConfig, ...>` that excludes credential- and debug-control-shaped fields (`githubToken`, `apiKey`, `debugAcknowledgement`, `debugCaptureRawApiBodies`). The builder receives resolved config values; it does not compute defaults.
- `previousFindingFingerprints` and `existingCommentFingerprints` are independent inputs; the builder must not reuse one for the other. `previousState.findingFingerprints` and `commentEvidence.existingFindingFingerprints` come from these two separate parameters.
- `previousState.lineage` is intentionally omitted: `RestoredState.lineageTotals` does not currently expose a stable review-count source.
- Bounded patch: truncation is strict `>` (equality is not truncated); `patch.sha256` hashes the bounded `patch.text`; a missing patch is omitted entirely (never emitted as `{}`).
- Path safety is fail-closed: unsafe paths in `ChangedFile.filename` propagate as-is and are rejected by `validateReviewInputV1`; the builder does not silently normalize.

### `mapReviewResultV1ToRuntimeContent` (`src/protocol/map-review-result.ts`)

Given a validated `ReviewResultV1`, produces a runtime-owned projection consumed by future host assembly (M2):

- `content`: `summary`, `findings`, `limitations`, optional `usage`, optional `observedTurns`, optional `observedTurnSource`.
- `sideChannel`: `warnings` (always an array), `diagnostics` (always an array), optional `inputSha256`, optional `trace`.

The helper does not produce `StructuredReviewEnvelopeV1`, does not compute fingerprints, does not apply `maxFindings` capping or scope filtering, and does not accept or return host-owned facts (`phase`, `baseSha`, `headSha`, `reviewedRange`, `runtimeProvider`, `sessionId`, `usageBudgetStatus`, `lineageTotals`, `stateKey`, `repository`, `toolMode`). Envelope assembly, fingerprinting, capping, filtering, and publisher inline eligibility remain in the existing host pipeline and its M2 successor.

The helper assumes the caller has already validated the input with `validateReviewResultV1`; it performs no mutation.

## Future: Replay Bundle

`ReviewTraceV1` is evidence-only and cannot independently replay a review. It does not contain review input, patch/context, provider request material, tool input/output, session content, or deterministic provider response fixtures.

A future versioned replay bundle or manifest (for example, `ReplayBundleV1`) will contain or content-address approved durable copies of:

- sanitized `ReviewInputV1`;
- runtime, provider adapter, model, policy, and configuration identity;
- deterministic provider and bounded tool fixture material needed to reproduce the reviewed path;
- an approved sanitized ledger snapshot when a stateful replay scenario requires one;
- `ReviewTraceV1` as execution evidence;
- actual and/or expected `ReviewResultV1`;
- content hashes plus protocol, runtime, template, and fixture versions.

Replay must not require GitHub credentials or live GitHub state. Replay material remains bounded and sanitized and must not contain provider secrets, auth headers, raw HTTP bodies, unrestricted provider transcripts, private runner paths, or unbounded tool output. Phase 6 will define the bundle schema or manifest contract and its fixture lifecycle.

## Future: Session Ledger Artifact

The current protocol defines `ReviewInputV1`, `ReviewResultV1`, and `ReviewTraceV1`. A future project-owned runtime that resumes context across GitHub Actions runs will need an additional artifact type (for example, `ProviderSessionLedgerV1` or `RuntimeSessionV1`) to carry the canonical session ledger.

This ledger artifact is distinct from `ReviewTraceV1`:

- `ReviewTraceV1` is sanitized execution evidence that may be referenced by a replay bundle; it carries no conversation content and is not sufficient for replay by itself.
- The ledger carries enough canonical logical content to reconstruct the cacheable provider request prefix, within the security boundary defined in `docs/20_architecture/security-boundary.md`.

The protocol will also need to partition stable context (system instructions, policy, tool definitions, canonical prior turns) from volatile context (current PR delta, run metadata) to serve prefix-cache stability. See `docs/20_architecture/architecture.md` (Provider Request Prefix Contract) for the invariants.

This is a direction statement only. The existing schemas are not changed; the ledger artifact and stable/volatile partitioning will be designed in a separate issue before project-owned live provider implementation.
