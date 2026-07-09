# Architecture Direction

The project should evolve toward a narrow, review-specific architecture:

1. TypeScript GitHub Action host and publisher.
2. C# review runtime core.
3. Schema-first JSON protocol between them.

This direction is a product architecture target, not a requirement to rewrite the current implementation immediately.

The schema-first protocol is defined as JSON Schema files under `protocol/schemas/`; see `docs/20_architecture/runtime-protocol.md` for contract details.

## TypeScript Responsibilities

TypeScript remains the best layer for GitHub Action integration:

- action inputs and outputs;
- GitHub event parsing;
- GitHub API calls;
- PR metadata and changed file loading;
- sticky PR comment state;
- existing comment scanning;
- inline comment publishing;
- line mapping and inline eligibility, at least initially;
- artifact upload;
- step summary and failure reporting;
- runtime binary resolution and invocation when an external runtime exists.

The TypeScript publisher owns side effects.

## Runtime Core Responsibilities

A future C# runtime core should be platform-neutral and review-specific:

- read sanitized review input;
- validate protocol version;
- perform context packing;
- run deterministic and live providers through a project-owned provider interface;
- orchestrate read/grep/glob-style repo-local tools when enabled;
- generate structured findings;
- generate finding fingerprints or fingerprint inputs;
- produce usage and trace data;
- write structured review result output.

- own canonical session ledger management for cross-run resume;
- own deterministic provider request construction;
- preserve cacheable prefix stability across restored sessions;
- expose stable request serialization and prefix-hash diagnostics through provider adapters.

The runtime core proposes findings. The publisher decides what can be posted.

## Runtime Replacement Direction

`claude-code-cli` is the current live provider baseline. It is kept in a compatibility and maintenance role: bug fixes, security fixes, provider-version compatibility, and CI/live-smoke maintenance remain in scope. New runtime product capabilities - including cross-session context recovery and stable provider request construction - target the project-owned runtime path, not the Claude Code CLI integration.

The project-owned runtime is intended to replace `claude-code-cli` as the long-term default live review path. This is a directional target, not an immediate removal; migration, compatibility, and deprecation criteria are deferred to later planning.

The project-owned runtime owns the LLM API call path directly. It does not wrap or depend on a third-party coding-agent CLI for live review execution.

## Session Continuity

The project-owned runtime must resume review context across separate GitHub Actions runs without depending on Claude Code's `--resume` mechanism or `session.jsonl` files.

To achieve this, the runtime maintains a canonical session ledger: a durable, schema-versioned record that can be restored from a state artifact and used to reconstruct the next provider request. The ledger is distinct from `ReviewTraceV1`, which is sanitized execution evidence for validation and replay and carries no conversation content.

See `docs/20_architecture/runtime-protocol.md` for the direction on a future ledger artifact, and `docs/20_architecture/security-boundary.md` for the artifact boundary the ledger must satisfy.

## Provider Request Prefix Contract

The runtime must construct LLM API requests with a strict, stable cacheable prefix so that prefix-cache reuse is possible across resumed sessions. Cache hit is the result of this contract, not the contract itself.

The contract defines four invariants:

1. **Materialization** - given the same canonical session ledger and the same stable runtime version, `materializePrefix(ledger, providerConfig, staticPolicy)` produces the same cacheable prefix.
2. **Append** - after a provider interaction is appended to the ledger, re-materializing the prefix does not drift due to non-semantic fields such as run id, artifact id, timestamp, or runner path.
3. **Round-trip** - for project-owned runtime-generated requests, the runtime can derive a canonical request-prefix projection from the session ledger, and that projection re-materializes to the same cache-relevant prefix. This is not a general parser from arbitrary provider HTTP requests back into full runtime state; it does not promise that provider SDK internals can be losslessly recovered.
4. **Prefix boundary** - requests have an explicit `[stable prefix][dynamic suffix]` split. The stable prefix may include fixed system/developer instructions, protocol or schema identifiers, stable review policy, stable tool definitions, and canonical prior session turns. The dynamic suffix includes current PR delta, changed-file patch subsets, current run metadata, transient diagnostics, fresh tool outputs, timestamps, and provider request ids.

Byte-for-byte stability is defined over the runtime-owned canonical provider request-prefix serialization (deterministic key order, UTF-8, newline normalization, default-value omission, array ordering) - not over raw HTTP bodies, provider SDK internal objects, or tokenized prefixes.

A `prefixSha256` diagnostic carries version and domain separation (contract version, template version, provider, model) to avoid false cross-version or cross-model cache-hit judgments. Provider-specific cache behavior is isolated behind provider adapters; the core contract owns deterministic request construction, not provider cache guarantees.

The ledger must not be raw API request storage. See `docs/20_architecture/security-boundary.md` for the artifact-class constraints the ledger must satisfy.

## Non-Goals

Do not build these in the initial architecture:

- a general coding agent;
- an autonomous code-writing agent;
- a hosted GitHub App;
- a multi-platform tool beyond GitHub PR review;
- a Semantic Kernel or Microsoft Agent Framework dependency;
- deep Roslyn/MSBuild semantic analysis;
- model-callable GitHub write tools;
- runtime-owned GitHub comment posting.

The non-goal of not becoming a general coding agent or IDE-coding-agent replacement refers to product scope. It does not mean `claude-code-cli` is permanent; `claude-code-cli` is the transitional live provider that the project-owned runtime is intended to replace as the long-term default (see Runtime Replacement Direction above).
