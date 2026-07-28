# Agent Loop Contract

Status: R2 LOOP implementation contract introduced by issue [#86](https://github.com/SolusQuest/agentic-pr-review/issues/86).

Last revised: 2026-07-28.

This document records the first executable product Agent core. It applies the normative R2 annex in parent issue [#78](https://github.com/SolusQuest/agentic-pr-review/issues/78) without creating a public package or compatibility contract. Durable session/state, encrypted storage, live provider wire behavior, final two-process Native AOT proof, CI replacement, and comparison-fixture deletion remain with #89, #87, R3, and #88.

## Product boundary

The runtime uses the project-minimal `IProjectChatClient` seam selected by [`ai-abstraction-decision.md`](./ai-abstraction-decision.md). The seam exposes one asynchronous response operation and project-owned logical messages, tools, continuation values, per-call usage, required-thinking selection, and the actual captured backend response-body byte count.

The Agent owns:

- the explicit serial model/tool loop;
- logical message, continuation-reference, call, result, failure, and terminal events;
- the complete immutable R2 limits authority;
- canonical request, argument, result, terminal, and digest behavior;
- the closed `read_file`, `search_text`, and `finish_review` toolset;
- budget, cancellation, deadline, failure-precedence, and terminal decisions.

The selected chat adapter owns only projection to and from its private backend types. Provider endpoints, credentials, headers, HTTP/wire DTOs, and provider roles do not enter Agent core. Provider-returned continuation remains a separate value channel and is never interpreted as hidden reasoning or promoted to control-plane content.

## Execution sequence

For each model turn, the loop:

1. checks caller cancellation, then the monotonic 300-second run deadline, then the model-call and canonical request limits;
2. constructs one project request containing the admitted logical history, the exact ordered tool definitions, continuation values, and `ThinkingRequired = true`;
3. consumes the model-call count immediately before invoking chat;
4. after the await, rechecks caller cancellation and deadline, then maps chat failure;
5. admits captured response bytes and generic message/content-kind shape, then exact non-negative per-call usage and checked cumulative input/output/combined token totals before content counts, tool sequence/name semantics, canonical arguments, or path/allowlist preflight;
6. dispatches admitted nonterminal calls one at a time in provider order only after the complete response has passed lexical, schema, canonical, call-ID, toolset, and snapshot-allowlist admission;
7. after each tool await, rechecks cancellation and deadline, maps only the frozen tool-failure taxonomy, charges the complete actual canonical result against individual and aggregate result-byte limits, then admits the result only when its strict UTF-8 model text, registered canonical writer, reviewed identity, domain-separated observation ID, and returned-line evidence map all agree before permitting the next call;
8. invokes chat again with one physical assistant message followed by its ordered tool-result messages; or
9. succeeds only when the response contains one valid terminal `finish_review` call and no other tool call.

There is no retry, parallel tool execution, partial success, automatic framework dispatch, hidden compaction, or publishable candidate on a failure, limit, deadline, or cancellation outcome.

## Limits and canonical identity

`runtime/src/AgenticPrReview.Runtime/Agent/AgentLimits.cs` is the only R2 authority. It contains all 41 rows from #78 in exact ordinal, registry-name, base-unit value, and unit order, including fields first consumed by later SESSION and STATE work. Later children consume this authority rather than introducing parallel limits families.

Canonical JSON is UTF-8 without BOM or insignificant whitespace. Each owning schema fixes property and array order. Strings use the frozen lowercase control escapes and direct non-ASCII scalars without normalization. Strict input admission rejects duplicate, unknown, missing, reordered, alternate-escape, alternate-number, and trailing representations. The one permitted optional-argument variance is normalized immediately:

- `read_file` stores `path`, defaulted `start_line`, then defaulted `line_count`;
- `search_text` stores `query`, then a string or JSON `null` path;
- `finish_review` has no optional fields.

The normalized bytes are the single identity used for the admitted assistant message, later replay, logical message reference, call event, and tool dispatch. Domain-separated SHA-256 identities use the exact tags and envelopes in #78. File hashes cover the complete raw file, including a leading UTF-8 BOM. Read/search observation identities omit only their own `observation_id` field. The terminal call reference, call event, and terminal review digest all cover the complete validated canonical `finish_review` arguments under the terminal domain.

## Logical history and continuation

The loop accepts an in-memory handoff consisting of the exact stable plan, an identifier-domain session ID, canonical logical history, and an optional provider-scoped continuation envelope. Initial history may contain system/user text, canonical assistant tool calls, and ordered tool-result messages. Logical text content is strict UTF-8 from 1 byte through 64 KiB; an assistant with only tool calls omits a text part instead of emitting an empty one. History is validated before chat, and every prior call ID is seeded into the run-wide reuse registry.

An initial continuation is a cumulative envelope for that history. Each item must match provider, model, adapter, and exact session scope; target an assistant message and valid content slot; retain its exact readable, opaque, and framing values; and reference a valid same-message tool call when associated. Readable and opaque values may be empty while framing is non-empty; all three remain independently bounded. A response continuation is a per-response delta. It must account one-for-one for the response reasoning parts at their exact message/content positions. Accepted deltas are appended once, in returned array order, to the cumulative envelope.

Reasoning payload values stay only in the provider continuation channel. The logical assistant history stores canonical text and tool calls, while message and continuation events retain hashes, placement, and association metadata. A successful outcome exposes the exact cumulative continuation candidate for #89's in-memory session handoff; failures expose no candidate.

## Reviewed-snapshot capability

The Host prepares a capability containing one validated reviewed identity, one absolute snapshot root, and an immutable ordinal allowlist of canonical tracked-file paths. Model arguments cannot choose another root, revision, allowlist, URL, process, service, or ambient filesystem capability.

Paths must be nonempty `/`-separated repository-relative strings within the 1,024-byte cap. Control characters, DEL, backslash, URI/drive/query metacharacters, roots, empty/`.`/`..` segments, and segments ending in ASCII dot or space are rejected before filesystem access. A valid path must also match one allowlist entry exactly.

The production file-access seam performs conservative component checks and fails closed on symlink, junction, reparse, directory/device/special-file, root-containment, or identity ambiguity. Linux opens with read-only, nonblocking, no-follow, and close-on-exec flags before `fstat` proves the opened object is a regular file, so a FIFO or Unix socket cannot block admission. The seam records a platform file identity and length, requires the read handle to match that identity, then reopens and verifies the path identity after the bounded read. Windows identity is volume plus file index; Linux identity is device plus inode. If confinement or identity proof is unavailable, the result is `tool_path_unsafe`. I/O failure after a safe object is established is `tool_io_failed`.

`read_file` and `search_text` share strict UTF-8, leading-BOM, NUL/binary, LF/CRLF, lone-CR, empty-file, final-unterminated-line, and raw-hash behavior. Search is ordinal and literal, scans paths in ordinal order, and applies the exact skip and truncation precedence from #78. All result writers preserve the normative property order and byte caps.

## Terminal and evidence

`finish_review` is terminal and must be the only tool call in its response. Its schema is closed; summary/title/message reject empty or fixed-whitespace-only values, including LF, CR, and CRLF combinations, while allowing multiline text containing a non-whitespace scalar. Their exact UTF-8 byte limits, severities, counts, line domains, duplicate findings, and duplicate evidence are validated locally.

Every evidence reference must match an admitted current-run observation with the same reviewed identity, exact observation ID, normalized path, and an entirely returned line range. A forged ID, wrong path, uncaptured or truncated-away line, prior-run observation, duplicate evidence, or invalid terminal sequence fails without a publishable review or completed-session-eligible outcome.

## Stable outcomes and precedence

The implementation uses only the stable outcome taxonomy in #78. Before starting an operation the first outcome is caller cancellation, elapsed deadline, then the applicable limit. After an await the first outcome is caller cancellation, elapsed deadline, operation/chat failure, response shape/size, usage, then result limits. A backend or transport exception is `agent_chat_failed`; a response received by the selected adapter but not normalizable into the project shape is `agent_response_invalid`.

Diagnostics contain only the stable code and bounded counters. They never contain repository or tool text, prompts, reasoning/continuation, raw arguments/results, credentials, authorization values, absolute roots, or exception messages/objects.

## Executable capability enforcement

Architecture tests cover all production types in `Agent.Chat`, `Agent.Core`, `Agent.Loop`, and `Agent.Tools` with three layers:

1. signature/type-graph inspection over base types, interfaces, constructors, fields, properties, methods, arrays, and nested generic arguments;
2. compiled method-body inspection, including ordinary and static constructor bodies, that resolves call, construction, field, method-handle, and type-token references and rejects indirect unmanaged calls;
3. metadata inspection of every native import declaration, including unused and body-free generated methods.

Chat/Core/Loop cannot acquire filesystem, environment, networking, process/shell, GitHub/Actions, publisher, Host-secret, service-locator, MEAI production, provider-wire, or native capabilities. Tools may use only the reviewed managed file/handle members and three exact native file-access declarations: Windows `GetFileInformationByHandle`, plus Linux `open` and `fstat`, each pinned by module, entry point, owner, and managed signature. Dynamic native loading/export lookup remains forbidden.

Scanner self-tests contain method-body-only forbidden calls, forbidden ordinary- and static-constructor bodies, dynamic native loading, an unused forbidden native process import, and an explicitly allowed file-identity import so a broken resolver cannot silently approve production.

## Validation and handoff

The LOOP change requires Release runtime build/test, runtime script validation, both retained AI-abstraction comparison modes, complete runtime verification, repository checks, and distribution checks. The #81 comparison fixture remains rollback evidence and compiles the exact production Chat sources until #88 owns its deletion.

On merge, #89 receives the next edit right for the Chat seam, limits authority, logical records/request interfaces, runtime/test projects, and related architecture sections. No state, storage, provider, publisher, workflow, or public Action ownership is implied by this contract.
