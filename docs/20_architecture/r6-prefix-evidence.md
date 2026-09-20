# R6 prefix observation contract

Issue [#275](https://github.com/SolusQuest/agentic-pr-review/issues/275) supplies deterministic, test-only observations of the existing Agent and DeepSeek thinking request path. These are local segment identities, not provider cache keys or cache-hit measurements. DeepSeek's internal cache key remains unknown. R5's live model-quality conclusion remains inconclusive; this evidence does not establish cost savings, quality, or R7 readiness.

## Ownership and request seams

`ReviewEvaluationFixture/Economics/Prefix` observes `IProjectChatClient` calls using the production `AgentRequestWriter.Write`, `MinimalChatClient.Materialize`, and `DeepSeekRequestWriter.Write`. It does not implement another request serializer. The tests run the actual Agent, DeepSeek backend with a synthetic transport, SESSION builder, and SESSION restorer. They independently inspect the outbound provider roles, reasoning placement, tool associations, and terminal-result framing. A parity assertion binds the observed projection to bytes passed by the backend to its transport.

The boundary comes from `AgentStableRequestMaterializer` and a successful `AgentSessionRestorer` result. The restorer constructs control messages, reconstructed accepted history, and one current review context, in that order. The boundary freezes the control count and the complete accepted history count. Callers cannot choose an arbitrary shorter prefix. Bootstrap has no accepted history. The observer retains no raw request or SESSION artifact.

Dynamic-suffix positives come from a two-turn actual Agent run: a synthetic provider supplies `read_file` with the production `line_count` argument, a bounded synthetic executor uses `ReadFileResultWriter` and the observation digest/line map, and `AgentToolResultAdmission` admits the canonical result before the second model request. Repeating that run with a different current call ID tests dynamic identity without inventing an unreachable Agent history. Negative controls prove that the former `end_line` spelling and an unadmitted result prevent the second request.

## Segments

| Observation | Logical serialization                                                                                              | DeepSeek serialization                                                                                     |
| ----------- | ------------------------------------------------------------------------------------------------------------------ | ---------------------------------------------------------------------------------------------------------- |
| Control     | Actual initial control-message tokens                                                                              | Corresponding actual provider message tokens                                                               |
| History     | All accepted historical message tokens, followed by their exact continuation-item tokens in original order         | All corresponding historical provider message tokens, including reasoning inserted into assistant messages |
| Dynamic     | Current review context and subsequently appended messages, plus their continuation-item tokens                     | Current context and subsequent provider messages                                                           |
| Settings    | Actual ordered non-message properties, with the continuation envelope identities separate from its segmented items | Every actual ordered root property except `messages`, including model, thinking controls and ordered tools |
| Whole       | Complete production logical serialization                                                                          | Complete production provider serialization                                                                 |

Each segment contains a domain-separated SHA-256 digest, UTF-8 byte count, and framed-part count. Each original JSON token is hashed with a four-byte big-endian length prefix; root property names are separate framed parts. No parsed JSON is reserialized to manufacture a request. Counts measure the observed parts, not tokens or provider billing units. Whole-request hashes are diagnostic observations and are excluded from prefix-stability decisions.

Continuation is logically outside the message array. Its original values, absolute message/content positions, call association and ordering are included in the owning historical or dynamic segment. No run ID, deadline, call ID, reasoning, or opaque value is scrubbed or normalized. DeepSeek receives the materialized `reasoning_content` inside assistant messages; it does not receive the intermediate continuation envelope. Provider hashes therefore cover only actual serializer output. Provider controls and tools occur after messages on the wire, so these semantic segments are not claimed to be a contiguous byte prefix of the complete JSON body.

At bootstrap, the first continuation envelope belongs entirely to the current dynamic suffix because there is no accepted history. Its appearance does not invalidate the empty historical prefix. For a restored boundary, its envelope identity participates in logical settings while its items remain segmented by absolute message position.

## Comparison domain and outcomes

The domain records build-time source commit/tree/clean state separately from the production stable-plan digest. That digest binds repository/review/workflow, policy, ordered toolset, limits, build, provider/model/adapter and prior SESSION identity. A domain-separated session ID hash, accepted generation, and accepted SESSION digest bind the fixed historical boundary. Bootstrap uses generation `-1` and no accepted digest. Raw identity strings from the request are not output.

Comparisons require equal domains and equal fixed boundaries. A domain or boundary change yields `incomparable`, with no stability Boolean. Within that domain, logical and provider stability independently require equality of control, history and settings. Dynamic and whole-request segments may change. A fresh suffix tool-call/result ID is explicitly dynamic; an already accepted historical call ID is not. A stable-content change within the same boundary changes its segment and invalidates stability. Policy/build/model/adapter/source/session-generation identity changes intentionally prevent comparison.

Projection observation is not replay admission. In negative probes, an intermediate continuation mutation can change logical bytes without changing provider-visible JSON. The actual DeepSeek backend rejects unsupported framing, opaque fields or positions before transport. Such probes illustrate the distinction between projections; they are not evidence of valid provider traffic or cache eligibility. The tests label these rejection cases explicitly.

## Bounds, privacy and validation

Input message, part, tool and byte bounds apply before logical serialization; the production writers also enforce their own contracts. A wrapper retains at most `AgentLimits.ModelCalls` immutable observations. Cancellation before observation retains nothing; a failed downstream call can retain its safe request-shape observation. Raw policy, message, tool and continuation content is used transiently in memory and is absent from observations and fixed observer failure diagnostics. Tests use synthetic canaries and do not emit raw bodies in assertions. There is no report command, new JSON/IPC schema, production logging, provider call, credential use, or durable state change.

Run the independent segment, mutation, bounds, privacy and backend-parity tests with:

```sh
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo --filter FullyQualifiedName~R6PrefixProjectionTests -m:1
```

The leaf also requires the full Runtime Release test suite and `npm run check`. Existing Runtime CI supplies Linux execution; no new workflow or dispatcher is introduced. All observation code is in the existing AOT-compatible fixture and uses no reflection-based serialization.

## Restored lifecycle evidence (P2)

Issue [#276](https://github.com/SolusQuest/agentic-pr-review/issues/276) composes these observations with the existing R5 replay, growth and Host reset owners. `Economics/Histories` supplies internal bounded lifecycle results; `R6PrefixHistoryTests` runs the complete callable matrix. The [authored inventory](../../runtime/tests/fixtures/agent/r6/histories/README.md) names each outcome and its reused synthetic fixture. No new dispatcher or workflow is introduced.

For each replay phase, the supervisor independently restores the actual accepted encrypted predecessor before starting the child. The child independently restores that same predecessor, derives its P1 boundary from the admitted `RunRequest` and artifact, and observes actual Agent calls. The supervisor compares these observations at the same accepted generation and historical boundary. Subsequent calls may grow the dynamic suffix while control/history/settings remain stable. Different accepted generations deliberately compare as `incomparable`; complete-request and normalized R5 reproducibility hashes never substitute for prefix evidence.

The existing process runner binds each reply PID to the actual launched process. P2 additionally requires distinct PIDs and startup identities across phases. The existing compiled replay oracle requires the provider script to retrieve a prior-only fact from the restored historical tool result. The fact is absent from new context, policy, diff and current tool results; the ahead snapshot also removes its source file. This proves scripted historical use, not live model recall.

Every executed Replay child carries a bounded private `HistoryCapture` sidecar. There is no new input flag: Replay and Growth execute and admit the same original input. The common admission path checks sidecar shape, bounds and source/session/generation identity. A non-mutating chat wrapper delegates the original request even when measurement is unavailable. Thus instrumentation does not replace existing R5 failure classifications. A missing/unmeasurable sidecar never supplies positive R6 continuity evidence.

Raw child replies remain private. `HistoryBridge` verifies provider observation hashes against actual transport bytes and drops the raw reply after staging bounded facts. Only after the existing supervisor has admitted the reply, accepted state, and completed its lifecycle checks can the collector promote an accepted row. Growth additionally performs its existing accepted readback. `HistoryReport` contains fixed codes, source identities, PIDs, accepted-state digests, segment hashes/counts and bounded capacity counters. Its strict source-generated codec rejects unknown members, invalid domains/counts and oversized input. It contains no request, SESSION, environment, tool-result, continuation, private path or raw session identifier.

The tools and continuation profiles reach their current effective boundaries: five and six accepted runs respectively, followed by a rejected next response. Tests assert the actual message/continuation counters straddle the production limit and that the encrypted accepted predecessor remains unchanged. The other R5 growth profiles remain covered by their existing regression suite. No limits, truncation, compaction or automatic rollover are added.

Missing/reordered history, changed/missing/mis-positioned continuation, wrong scope/head, stale generation and policy/tool/model/adapter commitments have explicit rejection or invalidation outcomes. Negative projection differences are separate from production rejection; rejected or unmeasurable traffic cannot receive positive continuity credit. None of the mutations rewrites the accepted predecessor.

Reset uses the existing production Host capacity/reset assertion and reset owner probe. They prove capacity rejection and predecessor preservation, explicit authorized reset, changed lineage epoch and session, generation 0, exclusion of old fact/reasoning, and independently accepted new-epoch continuation. Additional P2 assertions bind the request-level session/prior-state changes. This is an in-process Host with synthetic ports, distinct from Replay's fresh-process topology; low-level state deletion is not substituted for reset authority. The existing bounded `ResetCapacityReport` remains the reset evidence artifact.

Run the P2 matrix with:

```sh
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo --filter FullyQualifiedName~R6PrefixHistoryTests -m:1
```

The full Runtime suite and `npm run check` remain required. V1 owns later dispatcher/framework/AOT integration; C1 consumes bounded lifecycle facts separately from usage/pricing and quality eligibility. All P2 results are deterministic synthetic evidence. Actual provider cache keys, hits and economics remain unknown; R5 live quality uncertainty is unchanged.
