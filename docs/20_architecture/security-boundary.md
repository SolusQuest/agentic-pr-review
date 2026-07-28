# Security Boundary

The core safety rule is:

> The model and review Agent may propose findings; deterministic `ActionHost` code validates and performs GitHub side effects.

The target runtime architecture and migration sequence are defined in [`agent-runtime-rebaseline.md`](./agent-runtime-rebaseline.md). Existing deterministic and M4 sections in older documents continue to describe current implementation behavior until their replacement lands.

## Trust And Capability Domains

The selected initial target has two executable domains:

1. **Thin Node.js wrapper**: Actions toolkit, secret masking, payload launch, cancellation, artifact bridge, outputs, annotations, and summary.
2. **Single .NET application**: Host, Agent, tools, provider adapters, state, and deterministic publisher.

Inside the C# process, narrow capability domains remain explicit:

- **Host**: GitHub authority, PR snapshot, state acceptance, candidate validation, publisher policy, and side effects;
- **Agent**: sanitized review context, bounded loop, read-only tools, logical session state, and candidate findings;
- **provider adapter**: provider-only credential, allowlisted endpoint, request construction, and bounded response parsing.

The model cannot inspect process memory. It observes bytes sent in provider requests and bounded tool results returned to the conversation. The initial security objective is therefore to prevent GitHub or unrelated secret-derived bytes and capabilities from entering model-visible, provider-visible, durable, or published channels.

A project reference, namespace, `internal` type, or separate `csproj` is not a strong security boundary. A second process provides defense in depth and fault isolation but is not required for the initial trusted tool set and is not a complete sandbox by itself.

## Credential Boundary

| Credential or authority    | Node wrapper                       | C# composition root / Host | Agent module             | Provider adapter | Model-visible      |
| -------------------------- | ---------------------------------- | -------------------------- | ------------------------ | ---------------- | ------------------ |
| `GITHUB_TOKEN`             | may receive for masking/forwarding | allowed                    | no object or byte access | forbidden        | forbidden          |
| Actions artifact authority | allowed                            | only if transport moves    | forbidden                | forbidden        | forbidden          |
| provider API key           | may receive for masking            | binds once                 | no object or byte access | allowed          | forbidden          |
| state encryption key       | may receive for masking/forwarding | Host crypto boundary only  | forbidden                | forbidden        | forbidden          |
| repository read root       | launch path only                   | allowed                    | bounded tools only       | forbidden        | only through tools |
| GitHub write operations    | no business policy                 | allowed                    | forbidden                | forbidden        | forbidden          |
| arbitrary service lookup   | forbidden                          | composition root only      | forbidden                | forbidden        | forbidden          |

The C# composition root reads secrets once and binds them into narrow credential-bearing clients. It should then remove unnecessary secret values from its ambient environment. This reduces accidental access; it is not memory erasure.

Do not pass `ActionConfig`, `HostSecrets`, a GitHub or Actions client or credential, an environment abstraction, a generic `HttpClient`, `IServiceProvider`, or a shell/process capability to Agent orchestration. The provider credential is a necessary transport secret, but it remains encapsulated by the narrow provider adapter and never enters orchestration, tools, the logical conversation, durable state, normal diagnostics, or publication.

## GitHub Authority

GitHub reads and writes belong to `ActionHost`.

The Agent must not:

- receive GitHub credentials or credential-bearing objects;
- construct or send GitHub API requests;
- post comments or reviews;
- update issues, labels, milestones, Projects, or repository settings;
- upload or select artifacts;
- expose GitHub operations as model-callable tools.

The model has no GitHub write tool.

GitHub reads needed for review are converted into an immutable host-authoritative snapshot or performed by a host-owned read service with an explicit bounded contract. The R2 spike and R3 initial live profile use snapshots rather than model-directed GitHub requests.

## Provider Secret Boundary

Provider credentials must not appear in:

- repository files;
- PR bodies or comments;
- workflow summaries;
- normal logs;
- Action outputs;
- state or evaluation artifacts;
- structured review results;
- tool arguments or tool results;
- raw exception messages.

Provider adapters own secret injection into outbound requests. Provider request objects and headers are not exposed to the model or persisted in normal diagnostics.

GitHub and provider traffic use separate `HttpClient` instances, handlers, base addresses, and explicit authorization headers. The provider client:

- accepts only allowlisted provider endpoints;
- rejects cross-host redirects;
- does not accept arbitrary URLs from model or repository content;
- does not share GitHub handlers or default headers;
- never logs authorization headers or raw request objects.

## Secret-Canary Verification

Tests must use unique canaries for GitHub, Actions, provider, state-encryption, and unrelated workflow credentials.

A fake outbound provider transport captures the complete URI, headers, and body and verifies:

- the target endpoint is allowlisted;
- authorization contains only the expected provider credential;
- GitHub, Actions, runner, package, cloud, and signing canaries are absent;
- no unapproved header is present;
- redirects cannot move an authorized request to another host.

GitHub or Actions canaries must also be absent from:

- tool definitions, arguments, and results;
- logical conversation history;
- ledger, state, trace, result, replay, and normal artifacts;
- logs, diagnostics, annotations, summaries, outputs, and comments;
- any future child-process environment.

Provider credential canaries may appear only in the intended provider authorization location. They are forbidden from all model-visible, durable, logged, and published channels.

When encrypted state is selected, the state-encryption canary may appear only at the Host-owned cryptographic boundary. It must not enter state bytes, Agent inputs, provider requests, model-visible content, logs, diagnostics, or publications.

R4 additionally runs an integrated Action canary with GitHub/Actions-shaped Host credentials in the same C# process as the Agent and provider adapter. Authorization denial must occur before state decryption, provider construction, provider network activity, or publication, and all Host credential canaries remain absent from provider, model, tool, state, diagnostic, log, output, summary, annotation, and child-environment channels.

## Reviewed Repository Boundary

The agent reviews one immutable logical snapshot identified by:

- repository;
- head repository;
- pull request number;
- base SHA;
- head SHA;
- tracked-file allowlist;
- host-created changed-file and diff snapshot.

The Agent receives a read-only review root or an equivalent no-write view enforced outside model control.

The first executable R2 implementation and its fail-closed path/opened-object identity checks are specified by [`agent-loop-contract.md`](./agent-loop-contract.md).

Tool implementations must:

- accept only repository-relative paths;
- reject absolute, drive-qualified, protocol-looking, backslash-ambiguous, empty-segment, `.` and `..` paths;
- normalize lexical paths;
- resolve the final filesystem target;
- reject symlinks or reparse points that escape the review root;
- reject untracked files unless a future explicit snapshot contract includes them;
- exclude `.git`, runtime temp files, artifact staging, credentials, and runner-private paths;
- cap files scanned, bytes read, lines returned, and matches returned;
- avoid executing reviewed code.

The R2 implementation validates response usage before tool semantics, then validates every tool call in the received assistant response, including lexical path and tracked-file allowlist preflight, before dispatching the first call. A synchronous preflight exception maps to the frozen I/O failure without dispatch. Linux file access opens with nonblocking and no-follow flags and proves a regular file through the opened descriptor before any seek or read, so FIFO and Unix-socket paths fail closed without hanging the run.

Successful tool execution has one authoritative canonical UTF-8 byte sequence. The model-visible result string is derived from those bytes, not supplied independently. Before the result becomes an event, observation, or later provider message, the Agent revalidates the registered result writer, reviewed identity, domain-separated observation ID, and exact returned-line evidence map. Null or inconsistent success values and non-taxonomy failure codes fail closed as `tool_io_failed`.

The model cannot ask a tool to change the root, revision, allowlist, or head identity.

## Untrusted Content Classification Boundary

Repository, pull-request title/body, diff, file, search-result, and tool-result content is always untrusted data. Text in those sources may resemble system or developer instructions, policy, tool definitions, provider configuration, endpoint selection, or credential-handling directions; resemblance does not grant control-plane authority.

Host/Agent code fixes the project-owned durable record kind, provider-facing message role or tool-result framing, tool-call association, and control/data classification for this content. Persistence, restoration, adapter projection, and provider request materialization must preserve that classification. They must not promote or reinterpret repository-derived or tool-result content as a system or developer message, policy, tool definition, provider configuration, endpoint selection, secret channel, or any other control record.

The authenticated project-owned session structure binds those classification fields. A restored record whose kind, role or framing, association, or control/data classification does not match the authenticated current-format structure is rejected before Agent or provider admission.

R2 negative fixtures must prove both directions:

- prompt injection that looks like a system or developer instruction round-trips inside a bounded tool result and remains tool-result/data content in the reconstructed provider request;
- otherwise authenticated state whose repository-derived or tool-result content is relabeled or reframed as a control record is rejected before provider invocation.

## Trusted Policy Boundary

Configuration and referenced review instructions are control-plane data. The Host resolves them at an immutable workflow-authorized commit SHA, normally from the repository default-branch lineage.

The Host must not load policy from:

- the reviewed-head checkout;
- a model-controlled path or ref;
- an unverified mutable ref;
- an ambient workspace file whose source identity is unknown.

The selected policy bytes and trusted source identity participate in the session and cache identity. A pull request base branch name alone is not a trust decision.

## Tool Boundary

Initial model-callable tools are limited to:

- `list_changed_files`;
- `read_diff`;
- `list_files`;
- `search_text`;
- `read_file`;
- terminal `finish_review`.

All tools are implemented behind project-owned C# validation and dispatch.

Model-produced tool arguments are untrusted:

- validate valid JSON;
- require a known tool name;
- require a closed argument object;
- reject unknown fields;
- reject values outside the tool-specific domain and bounds;
- do not silently normalize or coerce invalid values;
- return bounded stable failure codes;
- do not include raw exception messages.

Provider strict-schema or function-calling modes are quality aids, not security boundaries.

The R2 spike and R3 initial live profile do not expose:

- shell or arbitrary process execution;
- file write or patch operations;
- Git mutation;
- package managers;
- build or test execution;
- arbitrary network access;
- MCP;
- GitHub API operations;
- Roslyn/MSBuild project loading.

Adding any of these requires a new threat review and explicit architecture decision.

## In-Process Host And Agent Boundary

`ActionHost` invokes the Agent through a narrow typed interface. The request contains only sanitized review context, policy, reviewed snapshot identity, approved limits, and explicit read-only tool capabilities.

The Host must:

- construct Agent inputs explicitly rather than passing a host configuration object;
- keep GitHub and Actions clients outside the Agent dependency graph;
- provide cancellation and total deadlines;
- treat Agent results as untrusted candidate data;
- validate candidate result and state before any side effect;
- never serialize exception, credential, HTTP, or DI-container objects into Agent-visible inputs.

The Agent must:

- validate restored state, tool arguments, provider content, identities, paths, and bounds;
- use only injected review capabilities;
- return a complete self-validating candidate result;
- avoid ambient environment, service locator, arbitrary filesystem, process, and network access;
- propagate cancellation through provider and tool operations;
- keep provider transport objects outside logical messages and durable state.

Architecture tests reject signature, ordinary/static-constructor body, other method-body, dynamic-native-loading, and native-import references that would give Agent or tool modules Host secrets, GitHub clients, publisher code, Actions integration, ambient environment, arbitrary filesystem/network, or process capability. The exact R2 enforcement is recorded in [`agent-loop-contract.md`](./agent-loop-contract.md).

## Future Child-Process Boundary

If a later tool or Agent mode starts a child process, it must receive a freshly constructed allowlisted environment and controlled working directory. Do not inherit the complete Action environment.

Explicitly exclude:

- `GITHUB_TOKEN` and `GH_TOKEN`;
- Actions runtime and OIDC credentials;
- provider keys unless the child is the reviewed provider worker;
- unrelated workflow, cloud, package-registry, and signing secrets.

A same-binary worker mode or separate executable requires a new decision when the project needs non-cooperative kill behavior, resource or native-crash isolation, third-party extensions, MCP, shell/compiler/language-server processes, or independently trusted distribution.

Process separation alone is insufficient when the worker shares the runner user, filesystem, handles, and unrestricted network. Stronger threat models require corresponding OS or container isolation.

## Side-Effect Barrier

No model or Agent output is publishable until `ActionHost`:

1. validates the candidate result and session state;
2. validates evidence references;
3. applies finding caps and publisher policy;
4. scans existing comments and computes duplicate suppression;
5. resolves inline eligibility and current diff mapping;
6. rechecks the pull request head and state-selection assumptions;
7. confirms the run still owns the applicable concurrency/state transaction.

If the reviewed head changed, the host does not publish the stale review or accept its state.

Workflow-level per-PR concurrency remains defense in depth. It does not replace the final host recheck.

## Session State Artifact Boundary

The project-owned durable session state is a restricted logical artifact, not a raw provider transcript or HTTP capture. New Agent state uses a namespace and current-format discriminator distinct from old M4 state. Automatic selection never interprets old bytes as current; non-current candidates bootstrap observably, while an explicitly supplied incompatible artifact fails closed.

It may contain:

- project-owned canonical instruction, policy, toolset, provider, model, adapter, and cache identities;
- project-owned admitted review-context and assistant-visible messages;
- project-owned validated tool names and arguments;
- project-owned exact bounded tool results required for continuation;
- project-owned terminal review results;
- bounded provider continuation records preserving value-exact opaque payloads and validated structured-field/order/placement semantics required for thinking-enabled replay;
- repository, pull request, base, and head identities;
- predecessor and integrity hashes;
- bounded normalized usage and operational metadata.

It must not contain:

- GitHub or provider credentials;
- auth headers;
- raw HTTP request or response bodies;
- unrestricted provider request or response captures;
- private runner paths;
- unbounded file or tool content;
- reasoning content outside validated provider continuation records;
- debug captures;
- model-callable write authorities.

Project-owned logical records and provider-owned continuation artifacts are separate layers. Logical message/tool/outcome records may use canonical project-owned representations. Opaque provider byte/string payloads remain value-exact. Structured provider items preserve validated fields, array order, association, and adapter-required placement under an adapter-defined serialization contract. Neither form is normalized into provider-neutral content, interpreted, synthesized, or reused across providers.

Within the R2 in-memory seam, prior history contains only admitted logical roles, canonical tool arguments, and ordered tool results. The cumulative continuation envelope is independently bound to provider, model, adapter, identifier-domain session, assistant message/content positions, and same-message tool-call associations. Each received response contributes a validated delta that is appended exactly once. A successful outcome may hand the exact cumulative candidate to session code; a failure never returns one.

Repository-derived and tool-result logical records remain untrusted data under the classification boundary above. Their record kind, provider-facing role or tool-result framing, tool-call association, and control/data classification are authenticated session structure, not mutable content fields.

State artifacts must be:

- bound to repository and workflow trust domain;
- bound to the reviewed pull request and commit identities;
- bounded before allocation and parsing;
- integrity-checked;
- authenticated and bound to the current-format discriminator, Host-authoritative state identity, provider/adapter scope, session identity, and required generation/provenance;
- authenticated and bound to every logical record's kind, provider-facing role or framing, tool-call association, and control/data classification;
- retained only for the documented period;
- deletable according to the documented lifecycle;
- readable only by workflow-authorized principals;
- safely bootstrapped or failed when missing, incompatible, corrupt, oversized, or unsafe.

A content-addressed reference needed for session reconstruction must resolve to durable, bounded, access-controlled content. A dangling reference cannot be treated as a successful continuation.

Plaintext reasoning, repository tool results, and provider continuation material must not be committed to repository Git objects, Git history, repository-visible state refs, caches, or normal public artifacts. If the selected transport cannot provide confidentiality from ordinary repository visibility, the payload requires workflow-authorized Host-side encryption; the key remains a Host-only credential and is never stored with the payload or exposed to the Agent or model. If no such key path exists, readable continuation content is not persisted.

Encryption provides authenticated confidentiality or equivalent independent integrity protection. The key or key identifier is not payload authority. Authentication failure, identity mismatch, cross-scope substitution, stale replay, or decryption failure is rejected before any state content reaches the Agent or provider. Automatic restore follows the observable bootstrap policy; explicit restore fails closed.

R2 defines the storage conformance interface and negative matrix before R3 begins. R4 proves the selected production transport prevents untrusted and fork-origin workflows from enumerating, restoring, overwriting, decrypting, deleting, or causing publication of restricted continuation state.

## Thinking And Reasoning Content

The first resumable review path enables provider thinking. A quality-first review agent should validate the production reasoning mode from its first live vertical slice.

Provider-returned reasoning state is part of the continuation protocol when the selected API requires or recommends returning it. It may be readable `reasoning_content`, provider-produced summarized or redacted thinking, a signed block, an encrypted reasoning item, or a positional thought signature. Persisting this state in a restricted session artifact is permitted and, for some thinking-mode tool loops, required for correctness.

The project does not request, reconstruct, infer, or synthesize hidden chain-of-thought. Only continuation artifacts explicitly returned by the provider API under a documented adapter contract may enter restricted state.

The security rule is data-flow separation, not blanket deletion:

- the provider adapter preserves opaque byte/string payloads value-exactly and preserves structured validated fields, array order, adapter-required placement, signatures, and tool-call association;
- the session validator binds it to the provider, resolved model, adapter behavior, reviewed session, byte limits, and integrity chain;
- readable reasoning is treated as sensitive repository-session data even when the provider exposes it as ordinary JSON text;
- opaque, signed, encrypted, or redacted payloads remain opaque and are never parsed, normalized, concatenated, summarized, or rewritten by generic session code;
- among retained, logged, diagnostic, artifact, and published channels, only the restricted durable session artifact may contain the payload;
- validated in-memory session state and the outbound provider request required for replay may contain the payload transiently;
- logs, normal traces, annotations, summaries, outputs, comments, replay reports, and diagnostics must not contain the payload;
- bounded metadata such as kind, byte count, content hash, and validation outcome may be recorded;
- credentials, authorization headers, ambient environment data, raw HTTP bodies, headers, unrelated provider fields, and transport framing remain prohibited;
- incompatible, missing, altered, oversized, or structurally misplaced continuation state causes explicit failure or safe bootstrap rather than best-effort replay.

If a provider documents that a prior reasoning item is ignored or unnecessary after a particular boundary, its adapter may omit that item only under provider-specific tested policy. A generic sanitizer must not guess which reasoning state is disposable.

## Evidence Boundary

Findings should cite structured evidence references to content observed through the current Agent execution.

Evidence references may include:

- tool call id;
- tool name;
- reviewed head SHA;
- repository-relative path;
- line range;
- content or diff-hunk hash;
- bounded excerpt already present in an admitted tool result.

The host validates evidence before publication. A model-authored statement that lacks valid evidence may be rejected, downgraded, or retained only as a pathless/sticky limitation according to publisher policy.

## Diagnostics And Telemetry

Normal diagnostics and telemetry may contain:

- stable error codes;
- provider/model/adapter identity;
- model and tool call counts;
- bounded durations;
- normalized usage and cache counters;
- prefix and content hashes;
- tool names and statuses;
- result counts and state transition;
- bounded sanitized warnings.

They must not contain:

- prompts;
- full model messages;
- tool arguments or results unless inside the approved session artifact;
- raw provider fields;
- provider request ids unless explicitly approved;
- endpoints containing secrets;
- auth material;
- private paths;
- exception stack traces on normal surfaces.

Sensitive OpenTelemetry or AI middleware logging is disabled by default.

## Restricted Debug Capture

Raw provider or prompt capture is not part of the initial target architecture.

If a future diagnostic mode requires it, that mode needs a separate design covering:

- trusted manual invocation only;
- explicit acknowledgement;
- isolated retention;
- access control;
- secret redaction limits;
- no upload on PR-triggered untrusted workflows;
- guaranteed separation from normal state, trace, result, and summary surfaces.

Existing legacy raw-debug behavior does not automatically migrate to the new runtime.

## Current Implementation Compatibility

The current TypeScript host, deterministic C# bridge, `StateManifestV2`, `ProviderSessionLedgerV1`, provider metadata, state-acceptance, and prefix contracts retain their documented safeguards while they remain in the codebase.

Migration work may simplify or replace their shape, but it must not remove:

- provenance binding;
- safe bootstrap/fail-closed distinction;
- stale-head checks;
- no-overwrite candidate outputs;
- bounded parsing;
- closed validation;
- secret exclusion;
- deterministic publisher ownership;
- candidate-before-acceptance transaction semantics;

without an explicitly documented equivalent.
