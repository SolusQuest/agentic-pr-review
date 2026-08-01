# Current Agent Session Format

Status: normative current-format contract implemented by issue [#89](https://github.com/SolusQuest/agentic-pr-review/issues/89), extended to the complete R3 read-only observation surface by issue [#102](https://github.com/SolusQuest/agentic-pr-review/issues/102), and bound to the final DeepSeek reasoning-continuation adapter by issue [#106](https://github.com/SolusQuest/agentic-pr-review/issues/106).

This document is the durable copy of the current Agent session contract refined under [#78](https://github.com/SolusQuest/agentic-pr-review/issues/78). It defines the bounded plaintext SESSION artifact and deterministic restore boundary. It does not define encryption, storage, transport, authorization, retention, or accepted-lineage persistence; [#87](https://github.com/SolusQuest/agentic-pr-review/issues/87) owns those responsibilities.

## Current family and framing

The namespace is exactly `agentic-pr-review/agent-session`. The current discriminator is exactly `r2-current-1`. These literals identify the current internal durable format and do not create a public `V1` API.

Family selection is Host-authoritative storage metadata, not a conclusion drawn from mutable plaintext bytes. STATE authenticates and decrypts a Host-classified current envelope before SESSION receives plaintext. A malformed selected-current plaintext fails closed and cannot become a non-current bootstrap.

The plaintext byte sequence is:

1. eight ASCII bytes `APRSES01`;
2. one unsigned UInt32 little-endian canonical JSON byte length;
3. exactly that many canonical UTF-8 JSON bytes;
4. no trailing bytes.

The complete plaintext is at most `AgentLimits.SessionPlaintextBytes` (1 MiB), including the 12 framing bytes. Length and magic are checked before JSON parsing or allocation based on the declared length.

## Canonical JSON

Canonical JSON is UTF-8 without BOM or insignificant whitespace. Objects contain no duplicate, unknown, missing, or reordered properties. Integers use shortest decimal notation; floating-point, exponent, and negative-zero forms are prohibited. Strings retain their normalization and must contain valid Unicode scalar values. The writer escapes `"`, `\`, the five short control escapes, and other U+0000-U+001F controls exactly as defined by the R2 canonical registry; it does not escape `/` or non-ASCII scalars.

Reads use source-generated `System.Text.Json` metadata for a closed DTO graph, explicit record/content kind dispatch, and `JsonUnmappedMemberHandling.Disallow`. Admission reserializes the complete parsed domain through the explicit schema-order writer and requires byte equality. Reflection fallback, dynamic type loading, arbitrary extension dictionaries, and unbounded recursive values are not part of this format.

The root property order is exactly:

`namespace`, `discriminator`, `session_id`, `repository_id`, `review_target`, `workflow_identity`, `provider_id`, `model_id`, `adapter_id`, `policy_sha256`, `build_id`, `toolset_sha256`, `limits_sha256`, `producer_base_sha`, `producer_head_sha`, `generation`, `predecessor_state_sha256`, `prior_session_sha256`, `completed_runs`.

| Field                                                    | Exact domain                                                                                       |
| -------------------------------------------------------- | -------------------------------------------------------------------------------------------------- |
| `namespace`                                              | fixed `agentic-pr-review/agent-session`                                                            |
| `discriminator`                                          | fixed `r2-current-1`                                                                               |
| `session_id`                                             | ASCII `[A-Za-z0-9_-]{1,64}`                                                                        |
| `repository_id`, `provider_id`, `model_id`, `adapter_id` | valid-scalar UTF-8 string, 1-128 bytes                                                             |
| `workflow_identity`, `build_id`                          | valid-scalar UTF-8 string, 1-256 bytes                                                             |
| `review_target`                                          | integer 1 through 9,223,372,036,854,775,807                                                        |
| every `*_sha256`                                         | 64 lowercase hexadecimal characters; predecessor/prior fields are JSON `null` only at generation 0 |
| `producer_base_sha`, `producer_head_sha`                 | 40 lowercase hexadecimal characters                                                                |
| `generation`                                             | integer 0 through 9,223,372,036,854,775,807 and equal to `completed_runs.length - 1`               |

`policy_sha256` is the raw SHA-256 of the exact Host-selected trusted policy bytes. `toolset_sha256`, `limits_sha256`, and each run's `stable_plan_sha256` use the domain-separated canonical registries defined by the Agent core. `session_sha256` is `lowerhex(SHA256(ASCII("apr.session.r2") || 0x00 || complete_plaintext_bytes))`.

## Completed runs and lineage

`completed_runs` contains 1-64 closed run objects. Their property order is exactly `run_id`, `run_ordinal`, `reviewed_identity`, `stable_plan_sha256`, `records`, `continuation`.

Run IDs use the identifier domain. `run_ordinal` is a contiguous zero-based Int32. `reviewed_identity` has exact property order `repository_id`, `review_target`, `base_sha`, `head_sha`; repository and target equal the root stable scope, while base/head remain attached to the run that produced the stored review.

Generation 0 contains exactly run 0 and has null predecessor/prior hashes. Generation N+1 contains every predecessor run object byte-for-byte in the same order and appends exactly one new run. It sets `prior_session_sha256` to the predecessor accepted session hash and `predecessor_state_sha256` to the predecessor accepted encrypted-envelope hash. Root producer base/head identify the newly appended run.

Construction never truncates, compacts, summarizes, normalizes, reorders, or deletes earlier runs. If an append would exceed a count or byte cap, construction returns `session_construction_limit`, emits no session bytes, and leaves the accepted predecessor usable.

## Logical records

Every record property order is `kind`, `id`, `sequence`, the kind-specific fields below, `role`, `framing`, `classification`. Record IDs and provider call IDs use `[A-Za-z0-9_-]{1,64}` and are unique across the cumulative session. Sequence values are contiguous zero-based Int32 values within each run. The cumulative sum of logical records plus continuation items across every completed run is at most 256; this is one session cap, not a per-run allowance. Each canonical record is at most 512 KiB.

| Kind                | Kind-specific fields in exact order                                                      | role        | framing              | classification            |
| ------------------- | ---------------------------------------------------------------------------------------- | ----------- | -------------------- | ------------------------- |
| `review_context`    | `reviewed_identity`, `text`                                                              | `user`      | `text`               | `untrusted_review_data`   |
| `assistant_message` | `message_ordinal`, `contents`                                                            | `assistant` | `provider_message`   | `provider_data`           |
| `tool_result`       | `source_message_id`, `call_id`, `name`, `observation_id`, `result_json`                  | `tool`      | `tool_result`        | `untrusted_tool_data`     |
| `review_outcome`    | `terminal_message_id`, `terminal_call_id`, `terminal_sha256`, `summary`, `findings_json` | `assistant` | `validated_terminal` | `validated_terminal_data` |

`review_context.text` is 1-64 KiB of valid-scalar UTF-8. Construction accepts a current dynamic request message only when it is exactly one `user` message containing exactly one valid `ProjectTextContent`. It does not join, normalize, select, or discard physical text parts. The initial outcome event must match the same role and text. Restore reconstructs exactly one user message with exactly one text content.

`assistant_message.message_ordinal` is contiguous from zero and at most 63. `contents` has 1-32 items with contiguous zero-based `content_position`.

| Assistant content kind | Fields after `kind`, `content_position`                                                                                  |
| ---------------------- | ------------------------------------------------------------------------------------------------------------------------ |
| `text`                 | `text`, 1-64 KiB valid-scalar UTF-8                                                                                      |
| `continuation_slot`    | identifier `continuation_item_id`                                                                                        |
| `tool_call`            | identifier `call_id`, one of the five read-only tool names below, canonical `arguments_json` of 1-8 KiB                  |
| `terminal_call`        | identifier `call_id`, fixed name `finish_review`, canonical `arguments_json` within the terminal cap, `arguments_sha256` |

A non-terminal assistant message contains zero or more text/continuation slots and one through eight tool calls, with no terminal call. It is followed immediately by one ordered `tool_result` record per physical call. Each result binds the same assistant message ID and call ID. The five admitted names are exactly `list_files`, `list_changed_files`, `read_diff`, `search_text`, and `read_file`; aliases, alternate spellings, and additional names are rejected. `arguments_json` must equal the normalized canonical bytes returned by its closed parser; provider-input spellings with omitted/defaulted properties are never retained as a second durable form. Durable `list_files` arguments always include `prefix` and `after`, durable `list_changed_files` arguments always include `after`, and durable `read_diff` arguments always include `path`, `start_hunk`, and `hunk_count`, including explicit JSON `null` and numeric defaults. `result_json` is the exact canonical admitted tool result and is at most 32 KiB.

Canonical bytes and observation hashes are necessary but not sufficient for a stored result. SESSION re-admits every `read_file` result under the complete tool contract: status, requested/returned range, contiguous line numbering, UTF-8 byte accounting, control-free physical line text, count cap, and the exact truncation reason must agree. It similarly re-admits every `search_text` result under status, selected path, control-free query-matching line text, deterministic path/line order, duplicate exclusion, per-path raw-byte identity, mutually reachable file/raw-byte/skip/match counters, scan/skip/match caps, and the exact truncation reason. The internal canonical form records an absent search path as `path: null`; provider-facing input still omits the absent optional property. A self-consistent result that could not have been produced by the approved tool semantics is rejected before its observation can ground a finding.

SESSION re-admits `list_files` results under exact reviewed identity, echoed prefix/cursor, canonical repository paths, strict ordinal order and uniqueness, entry caps, visible cursor relation, truncation/`next_after` shape, canonical result bytes, and the `apr.observation.list-files.r3` preimage; its reconstructed observation has no evidence lines. `list_changed_files` additionally re-admits each change's lifecycle/previous-path matrix, additions/deletions/changes counters, patch status/hash/nullability, `source_truncated`, strict path order, visible cursor/truncation, and the `apr.observation.list-changed-files.r3` preimage; it likewise reconstructs no evidence lines.

SESSION re-admits `read_diff` results under exact reviewed identity and requested path/range, canonical patch-hash preservation, status/nullability, source-truncation shape, hunk count and ordinal page bounds, complete old/new line progression within every hunk, strict hunk order, visible truncation/next-hunk relation, canonical result bytes, and the `apr.observation.read-diff.r3` preimage. `eof` is legal only after hunk 1. An `ok` truncated page must end before `AgentLimits.DiffHunksPerFile` and name exactly the following hunk. Its evidence map is derived only from returned `context.new_line` and `addition.new_line` values; deletion lines, no-newline markers, omitted hunks, and unreturned gaps gain no authority. Evidence ranges remain set-based, so returned lines 10 and 12 do not ground range 10 through 12.

Validation remains explicitly two-stage. The live immutable `ReviewedSnapshot`, `SnapshotToolExecutor`, and initial `AgentToolResultAdmission` own cursor membership, complete eligible pages, tracked/change/diff membership and collisions, summary/full-source coherence, and the full canonical diff-source hash before `AgentLoop` emits canonical result events. SESSION construction stores those already admitted generic call/result records and re-runs the durable rules above. Fresh-process restore has no snapshot, filesystem, or repository capability; it preserves Host-derived fields byte-for-byte and revalidates only facts present in the durable record. Authenticated STATE integrity does not replace this semantic re-admission.

A terminal assistant message contains zero or more text/continuation slots plus exactly one terminal call and no non-terminal call. It is followed by exactly one `review_outcome`, bound one-to-one to the terminal message/call. Terminal arguments are re-read through the closed `finish_review` reader. SESSION reconstructs that run's observations from its admitted tool results and reruns `TerminalReviewValidator`; a hash-self-consistent terminal that cites an unknown, prior-run, wrong-identity, wrong-path, out-of-range, truncated-away, duplicate, or otherwise ungrounded observation is rejected. Observation IDs are content-addressed and may repeat for equivalent results within or across generations; record/call IDs and one-to-one associations remain unique, and no later run may borrow evidence authority from an earlier run.

Provider-facing reconstruction retains that physical terminal assistant message and every admitted continuation item anchored to it. It then projects the bound `review_outcome` as one immediately following `tool` message containing exactly one `ProjectToolResultContent` with the terminal call ID and fixed result JSON `{}`. This synthetic closure carries no review authority or outcome data and is not a durable logical record; the validated terminal call and `review_outcome` remain the only durable terminal authority. Its sole purpose is to close the historical `finish_review` call under provider and `AgentLoop` call/result grammar without rewriting, compacting, or discarding terminal text or continuation. Every historical call, including `finish_review`, must therefore have its immediately following result in a reconstructed request.

`AgentLimits.SessionRecords` applies only to the cumulative durable `records` plus continuation `items` admitted by SESSION construction and validation. `AgentLoop` logical events also contain the stable plan, reconstructed control/current messages, and synthetic terminal closures, so event-stream length is not a SESSION record count and cannot preempt the SESSION builder's authoritative capacity decision. Event memory remains bounded by the independent message, total-part, model-call, tool-call, continuation-item, continuation-byte, and request-byte authorities.

The legal grammar is:

```text
review_context
  -> zero or more (assistant_message with non-terminal calls -> ordered tool_result records)
  -> exactly one terminal assistant_message
  -> exactly one review_outcome
```

Direct first-response `finish_review`, a textless tool-call message, multiple calls grouped in one physical assistant message, and mixed text/continuation/call contents are legal. Nothing follows the outcome.

`findings_json` is exactly the canonical `findings` array from the terminal arguments. Outcome `summary` equals terminal `summary`. Terminal call `arguments_sha256`, outcome `terminal_sha256`, and the recomputed validated review digest are identical under `apr.terminal.r2`.

## Provider continuation

Every run has one continuation object with exact property order `codec_id`, `codec_discriminator`, `items`, including a zero-item current-run continuation. Codec strings use the identifier domain.

Each item property order is exactly `item_id`, `encoding`, `payload`, `payload_sha256`, `message_id`, `content_position`, `associated_call_id`.

| Field                | Exact domain                                             |
| -------------------- | -------------------------------------------------------- |
| `item_id`            | unique identifier                                        |
| `encoding`           | `utf8` or canonical padded RFC 4648 `base64`             |
| `payload`            | exact codec payload string; decoded bytes at most 64 KiB |
| `payload_sha256`     | continuation registry digest                             |
| `message_id`         | assistant record ID in the same run                      |
| `content_position`   | Int32 0-31 naming the matching continuation slot         |
| `associated_call_id` | same-message call identifier or JSON `null`              |

The continuation digest is domain `apr.continuation.r2` over the exact ordered canonical object `codec_id`, `codec_discriminator`, `item_id`, `encoding`, `payload_bytes`, where `payload_bytes` is canonical padded base64 of the decoded exact codec bytes.

Generic SESSION code validates bytes, encoding, hash, array order, slot, same-message association, and size. It does not parse, concatenate, trim, normalize, summarize, reinterpret, or synthesize provider continuation. The selected adapter codec owns its approved field set and converts between exact bounded payload bytes and the project-minimal continuation value. A codec may additionally implement the provider-neutral whole-structure policy seam; SESSION supplies only ordered assistant-message ordinals, continuation-slot positions, call counts, ordered item ordinals, message ordinals, content positions, associations, and decoded values. Both construction and restore invoke this seam after the generic checks, while SESSION never references a concrete provider. Missing-property and other closed codec-domain exceptions are contained at the adapter boundary and become `session_continuation_invalid`; the selected codec must also return false for malformed required-property shapes rather than exposing partial values.

The DeepSeek thinking adapter uses exactly `codec_id: "deepseek-reasoning-content"`, `codec_discriminator: "deepseek-v4-flash-thinking-v1"`, `encoding: "utf8"`, and framing `deepseek.reasoning_content.utf8.v1`. Its payload bytes are the exact non-empty UTF-8 bytes of the readable `reasoning_content`, without BOM, normalization, wrapper, compression, or trailing newline; opaque is exactly empty. Every assistant message containing admitted calls has exactly one continuation item at content position 0, before optional non-empty text and ordered calls. Items are chronological by assistant-message ordinal, `associated_call_id` is JSON `null`, and multi-call order does not create additional items. Every later request reinserts each complete reasoning value at its original assistant message before provider projection.

The final DeepSeek adapter scope is provider `deepseek`, model `deepseek-v4-flash`, and adapter ID `0c585a37957e31b864e137bde2fbfd7c14005d03c42fd1a6983171d54e8977e0`. That adapter ID is the lowercase SHA-256 of the frozen 531-byte no-BOM/no-newline descriptor owned by the backend; build identity remains the existing independent root field. These provider rules add no SESSION property, namespace, discriminator, public schema, compatibility reader, or provider framework.

Durable item-array order is significant and never changed. Adapter materialization independently groups by absolute assistant-message position and inserts by content position. Consequently a durable array may intentionally differ from physical insertion order; tests assert both orders separately through the exact `MinimalChatClient.Materialize` path.

The R2 public-safe synthetic codec uses `codec_id: "r2-synthetic"` and `codec_discriminator: "current-1"`. Its readable payload property order is `kind`, `text`; opaque order is `kind`, `opaque`; structured order is `kind`, `framing`, `readable`, `opaque`, `signature`, `fields`. Structured `fields` is an ordered array of exact `{ "name": ..., "value": ... }` objects, with 0-32 fields, ASCII names `[A-Za-z0-9_.-]{1,64}`, and values up to 8 KiB. This codec is fixture evidence, not a production provider contract.

The total decoded continuation payload retained across all completed runs is at most 256 KiB. Generation N+1 reconstructs the predecessor cumulative continuation, requires the actual `AgentRunRequest.Continuation` to equal it exactly, and requires the successful outcome's cumulative candidate to start with that unchanged prefix. Only the remaining current-run suffix is persisted in the appended run. A successor with no new continuation therefore appends a zero-item run container rather than duplicating predecessor items.

## Stable and dynamic request identity

Stable scope is repository, review target, workflow authorization/source, selected policy bytes/hash, provider, model, adapter, toolset, limits, build, and session identity. Stored producer identity is the prior base/head, generation, predecessor envelope hash, and prior session hash. Current dynamic identity is the new reviewed base/head. A Host transition fact is exactly `same_head` or `verified_ahead`; `unknown`, `diverged`, and `unrelated` fail.

One Host-authoritative stable-request materializer is shared by construction and restore. It consumes the exact trusted policy bytes, workflow/source identity, fixed ordered tool definitions, central limits, required-thinking `true`, provider/model/adapter identity, build identity, repository, review target, and prior session hash. It strictly decodes the policy bytes as UTF-8 and derives exactly one `system` message containing exactly one text content. There is no independent system/control-message input that can vary while retaining the same policy hash. The materializer recomputes policy/toolset/limits/stable-plan hashes.

Construction requires the actual initial request to be exactly:

```text
the single Host-materialized policy system message
+ all deterministically reconstructed predecessor logical history and terminal-closure projections, when generation N+1
+ exactly one current review-context user message
```

The complete initial `AgentPlanEvent` and `AgentMessageEvent` prefix must match that request. Missing, extra, altered, or reordered stable messages are rejected even if a caller supplies the correct claimed policy hash. A correct predecessor hash with altered history or continuation is rejected. Restore uses the same materializer, adds validated historical logical records, appends the new dynamic review context, and creates a stable plan whose `prior_session_sha256` is the restored session hash.

Provider-neutral request equality is proven with `AgentRequestWriter.Write`. Physical provider placement is a separate proof through `MinimalChatClient.Materialize`; the provider-neutral writer is not treated as a placement oracle.

Before publishing an appended session, construction parses the newly encoded artifact through the production history-reconstruction path, materializes its new stable plan, and proves that the reconstructed request can still accept a minimal valid next user context within the message-count, total-content-part, and request-byte limits. Each reconstructed terminal closure counts as one message and one content part in this proof. Failure returns `session_construction_limit`, publishes no artifact, and leaves the accepted predecessor usable.

## Restore decisions and failure precedence

The Host classifies locator family before SESSION sees candidate bytes.

| Candidate/request                                                           | Automatic                                                      | Explicit                                                      |
| --------------------------------------------------------------------------- | -------------------------------------------------------------- | ------------------------------------------------------------- |
| no candidate and no accepted lineage                                        | `session_bootstrap_absent`                                     | `session_explicit_missing`                                    |
| Host-classified non-current family                                          | `session_bootstrap_incompatible`; current parser is not called | `session_explicit_incompatible`; current parser is not called |
| selected-current malformed, oversized, truncated, tampered, or incompatible | fail closed                                                    | fail closed                                                   |
| Host-authorized explicit reset                                              | `session_reset_explicit`                                       | `session_reset_explicit`                                      |

There is no scope-mismatch reset or best-effort replay. A selected-current artifact cannot become non-current because its inner magic, namespace, or discriminator was altered.

When defects conflict, evaluation order is Host locator/reset, current framing/size/canonical bytes, stable scope, accepted producer/predecessor facts, Host transition, record grammar/classification/association and semantic terminal grounding, continuation codec admission, then request reconstruction. Once an assistant content object declares `kind: "continuation_slot"`, a missing/invalid slot identifier or slot-specific position/shape defect becomes `session_continuation_invalid`; framing or otherwise noncanonical JSON retains the earlier current-format code. Closed continuation-codec domain failures, including RFC 8785 canonicalization failures, become `session_continuation_invalid`; malformed terminal findings shapes remain bounded record failures. No failure returns an `AgentRunRequest` or invokes provider code.

Stable SESSION outcomes are:

- `session_bootstrap_absent`
- `session_bootstrap_incompatible`
- `session_reset_explicit`
- `session_explicit_missing`
- `session_explicit_incompatible`
- `session_current_malformed`
- `session_current_oversized`
- `session_scope_mismatch`
- `session_transition_rejected`
- `session_record_invalid`
- `session_classification_invalid`
- `session_association_invalid`
- `session_continuation_invalid`
- `session_construction_limit`

Diagnostics expose only one of these bounded codes and public-safe identity classes or hashes. They never include logical text, tool results, findings JSON, continuation payload, provider reasoning, raw session bytes, credentials, authorization, or ambient paths. Continuation-bearing project, minimal-chat, Agent, SESSION, and DeepSeek carrier `ToString()` surfaces return fixed non-sensitive type labels rather than compiler-generated field dumps.

## Golden vectors

The byte-exact minimal record is:

```json
{
  "kind": "review_context",
  "id": "r0",
  "sequence": 0,
  "reviewed_identity": {
    "repository_id": "repo",
    "review_target": 1,
    "base_sha": "0000000000000000000000000000000000000000",
    "head_sha": "1111111111111111111111111111111111111111"
  },
  "text": "x",
  "role": "user",
  "framing": "text",
  "classification": "untrusted_review_data"
}
```

The byte-exact minimal continuation item is:

```json
{
  "item_id": "c0",
  "encoding": "utf8",
  "payload": "x",
  "payload_sha256": "e9a9e4ff63897f3b91af639ccf3973c54b62c2c56875d4426578ee70b0e73612",
  "message_id": "m0",
  "content_position": 0,
  "associated_call_id": null
}
```

For codec `r2-synthetic`, discriminator `current-1`, item `c0`, encoding `utf8`, and decoded payload byte `78`, the continuation payload digest is `e9a9e4ff63897f3b91af639ccf3973c54b62c2c56875d4426578ee70b0e73612`.

The deterministic generation-zero fixture with one synthetic opaque continuation item is 2,627 plaintext bytes and has session digest `c0236fe3c10bfedbced813778507d8c9cca7e0a84f0b4a749535bb01c2e95b6a` under `apr.session.r2`.

The cumulative generation golden uses review texts `g0`, `g1`, and `g2`, terminal call IDs `finish0`, `finish1`, and `finish2`, and synthetic continuation in generations 0 and 2. Its complete plaintext vectors are:

| Generation | Plaintext bytes | `session_sha256`                                                   |
| ---------- | --------------: | ------------------------------------------------------------------ |
| 0          |           2,607 | `40fb1abbcb7b9c96f2e27c6520eeb1144822c37ba6b6082e8013c97f9d77d31d` |
| 1          |           4,162 | `e289e6fe6c095c10924761e8fdf85f0ffddd2409af9974d8b01fc84824f1a2d6` |
| 2          |           6,015 | `44e434665aff7155f6f92db085427c15d22576545b3c4c723271f0bbd39596a1` |

Focused tests pin these vectors, complete framing/root property order, canonical read/write equality, generation 0→1→2 predecessor run bytes, both predecessor hashes, generation-2-only reconstruction after the oldest envelope is unavailable, direct terminal and tool-round grammars, provider-valid terminal closure, replayable multi-item continuation placement across generations, exact cumulative 256/257 durable-record validation, builder-owned append-capacity failure without predecessor mutation, and semantic terminal re-admission.

## Security exclusions and downstream handoff

SESSION is a pure bounded in-memory plaintext transformation. It has no filesystem, environment, process, network, GitHub, Actions, publisher, endpoint, HTTP, header, credential, key, encryption, storage, retention, enumeration, deletion, CAS, or authorization capability. Review and tool text stays in immutable untrusted-data record classes. Provider payload bytes exist only inside the bounded in-memory continuation layer and transient reconstructed request. The plaintext SESSION artifact is persisted only as the authenticated encrypted STATE payload; an authenticated current envelope whose decrypted SESSION fails semantic admission collapses to STATE's existing `state_envelope_invalid`, while raw AEAD/tag corruption remains the existing authentication failure.

SESSION does not reference, deserialize, reinterpret, or convert `ProviderSessionLedgerV1`, `StateManifestV2`, `ReviewInputV1`, or any other M4 state payload. Host-classified non-current artifacts bypass the current parser.

Issue [#87](https://github.com/SolusQuest/agentic-pr-review/issues/87) consumes SESSION through the narrow `AgentSessionStateBoundary`: STATE re-admits Agent-produced plaintext before encryption and re-admits authenticated plaintext after decryption through the complete current parser and semantic validator. STATE does not reinterpret records or continuation semantics; authenticated SESSION rejection maps to its closed state outcome. The exact encrypted wrapper, authorization, independent lineage, retention, and local conformance store are defined by [`restricted-state-format.md`](./restricted-state-format.md). Issue [#88](https://github.com/SolusQuest/agentic-pr-review/issues/88) now proves this integration across two fresh framework and Native AOT processes, including exact provider-continuation reconstruction, generation 0→1 lineage, same-head/verified-ahead admission, rejected head transitions, and construction/capacity failure. R3 owns live-provider continuation semantics; R4 owns production workflow transport.
