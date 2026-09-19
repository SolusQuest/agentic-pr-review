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

P2 owns fresh-process encrypted-state lifecycle, cross-generation restoration and reset evidence. Later R6 leaves join admitted lifecycle, usage and pricing evidence. This leaf's same-boundary prefix comparisons neither replace those gates nor infer cache hits from stable request structure.
