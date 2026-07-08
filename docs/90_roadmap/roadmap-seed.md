# Roadmap Seed

This is seed material for future public issue refinement. It is not a finalized milestone plan.

## Phase 0: Public Context Bootstrap

Establish public repository context:

- thin agent entrypoints;
- layered docs;
- issue and PR workflow;
- architecture direction;
- security boundary;
- roadmap seed.

## Phase 1: Contract Spike

Prove the review runtime boundary without replacing the current action:

- define initial review input and review result schemas;
- add contract fixtures;
- add deterministic runtime behavior;
- validate TypeScript-to-runtime invocation shape;
- keep publishing deterministic and side-effect-safe.

## Phase 2: Runtime CLI Spike

Add a review runtime CLI that can:

- read review input JSON;
- validate protocol version;
- run deterministic provider behavior;
- write review result JSON;
- emit trace data;
- return stable exit codes.

## Phase 3: TypeScript Integration

Wire the TypeScript action host to:

- create review input;
- invoke the runtime;
- validate review result;
- render sticky summary in guarded mode.

Keep inline comments disabled until line mapping, duplicate suppression, and fallback behavior are tested.

## Phase 4: Release Distribution

Prepare release mechanics:

- exact action/runtime version mapping;
- checksum verification for external runtime assets if introduced;
- no implicit latest downloads;
- release validation workflow.

## Phase 5: Live Provider

Add a simple OpenAI-compatible or provider-specific implementation behind the project-owned provider interface.

Avoid heavy agent frameworks until the narrow review runtime contract is proven.

## Phase 6: Memory And Incremental Review

Formalize:

- sticky state schema;
- finding fingerprints;
- duplicate suppression;
- previous state loading;
- incremental review fixtures.

## Phase 7: Optional Inline Comments

Enable inline comments only after:

- line mapping is tested;
- duplicate suppression is tested;
- fallback to sticky summary is reliable;
- posting behavior is deterministic.

## Phase 8: Optional Code Intelligence

Explore lightweight syntax-level analysis first. Deep semantic analysis can become an optional helper later if it proves useful and compatible with distribution constraints.
