# Runtime Protocol

The runtime boundary is protocol-first and file-based.

## Direction

The TypeScript host writes review input JSON. The runtime reads that input and writes structured result JSON plus optional trace output. Both sides validate protocol version and fail closed on incompatible contracts.

## Protocol Files

The protocol is defined as JSON Schema files under `protocol/schemas/`:

- `review-input.v1.json` - input contract (ReviewInputV1), defined in #14
- `review-result.v1.json` - result contract (ReviewResultV1), pending #15
- `review-trace.v1.json` - trace contract (ReviewTraceV1), pending #16

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

Review result (pending #15) should include:

- `protocolVersion`
- `runtimeVersion`
- summary
- structured findings
- usage data
- warnings
- diagnostics
- trace reference or trace payload

Findings should include enough stable information for duplicate suppression and safe publishing:

- severity;
- confidence;
- file path;
- proposed line or range when available;
- title;
- body;
- evidence;
- fingerprint input or fingerprint;
- inline suggestion preference;
- fallback behavior when inline target is invalid.

## Contract Strategy

The protocol uses JSON Schema (draft-07) files as the single source of truth, avoiding two independently drifting definitions of business behavior across TypeScript and C#. TypeScript interfaces are developer ergonomics only; the schemas are authoritative.
