# Runtime Protocol

The runtime boundary should be protocol-first and file-based.

## Direction

The TypeScript host writes review input JSON. The runtime reads that input and writes structured result JSON plus optional trace output.

Conceptual files:

- `review-input.json`
- `review-result.json`
- `review-trace.json`

Both sides should validate protocol version and fail closed on incompatible contracts.

## Conceptual Input

Review input should eventually include:

- `protocolVersion`
- `runtimeVersion` or requested runtime version
- review target metadata
- pull request metadata
- changed files and bounded patch context
- previous state snapshot
- existing comment snapshot
- policy documents
- context documents
- runtime options

Input must be sanitized for runtime consumption. GitHub write credentials do not belong in runtime input.

## Conceptual Output

Review result should eventually include:

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

Prefer JSON Schema or equivalent strict contract tests as the shared source of truth. Avoid two independently drifting definitions of business behavior across TypeScript and C#.
