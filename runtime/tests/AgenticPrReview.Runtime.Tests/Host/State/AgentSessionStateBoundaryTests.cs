using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class AgentSessionStateBoundaryTests
{
    private const string FinishJson =
        "{\"summary\":\"complete\",\"findings\":[]}";

    [Fact]
    public async Task RealCurrentSessionPassesCompleteAdmissionBeforeAndAfterState()
    {
        var fixture = await BuildSessionAsync();
        var access = Access(fixture.Artifact.Document);
        var stateContext = StateContext(fixture, envelopeSha256: null);
        var adapter = new AgentSessionRestrictedStateAdmission();

        var admitted = adapter.Admit(
            access,
            fixture.Artifact.Plaintext,
            stateContext);

        Assert.True(admitted.Succeeded);
        Assert.Equal(
            fixture.Artifact.SessionSha256,
            admitted.Session!.SessionSha256);
        Assert.Equal(
            fixture.Artifact.Document.Generation,
            admitted.Session.Generation);
        Assert.Equal(
            fixture.Artifact.Document.ProducerHeadSha,
            admitted.Session.ProducerHeadSha);
        Assert.IsType<AgentSessionStateAdmittedValue>(
            admitted.Session.Value);
        var continuation = Assert.Single(
            fixture.Artifact.Document.CompletedRuns).Continuation;
        Assert.Equal(
            DeepSeekReasoningContinuationCodec.Id,
            continuation.CodecId);
        Assert.Equal(
            DeepSeekReasoningContinuationCodec.Discriminator,
            continuation.CodecDiscriminator);
        var continuationItem = Assert.Single(continuation.Items);
        Assert.Equal("utf8", continuationItem.Encoding);
        Assert.Equal("state reasoning", continuationItem.Payload);
        Assert.Equal(
            "state reasoning"u8.ToArray(),
            continuationItem.PayloadBytes);

        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var service = new RestrictedStateService(
            store,
            keys,
            adapter,
            () => RestrictedStateTestData.Now);
        var prepared = await service.PrepareAsync(
            access,
            new RestrictedStatePrepareRequest(
                null,
                fixture.Artifact.Plaintext,
                stateContext),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        var accepted = await service.AcceptAsync(
            access,
            null,
            prepared.Receipt!,
            stateContext,
            CancellationToken.None);
        Assert.Equal(StateAction.Accepted, accepted.Action);
        var current = Assert.Single(store.Snapshot.Accepted);
        var lineage = new AcceptedLineage(
            access.Scope,
            current.Binding.Generation,
            current.SessionSha256,
            current.EnvelopeSha256,
            current.Binding.PredecessorEnvelopeSha256,
            current.Binding.AcceptedAtUnixSeconds,
            current.Binding.ExpiresAtUnixSeconds,
            TransitionAuthorized: true);

        var restored = await service.RestoreAsync(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                stateContext),
            CancellationToken.None);

        Assert.Equal(StateAction.Restored, restored.Result.Action);
        Assert.IsType<AgentSessionStateAdmittedValue>(
            restored.Session!.Value);
    }

    [Fact]
    public async Task AuthenticatedButClassificationInvalidSessionIsRejected()
    {
        var fixture = await BuildSessionAsync();
        var run = fixture.Artifact.Document.CompletedRuns[0];
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            run.Records[0]);
        var records = run.Records.ToBuilder();
        records[0] = context with
        {
            Classification = "trusted_control_data",
        };
        var mutatedDocument = fixture.Artifact.Document with
        {
            CompletedRuns =
            [
                run with
                {
                    Records = records.MoveToImmutable(),
                },
            ],
        };
        Assert.True(AgentSessionCodec.TryWrite(
            mutatedDocument,
            out var mutated,
            out var writeFailure),
            writeFailure);
        var adapter = new AgentSessionRestrictedStateAdmission();

        var admitted = adapter.Admit(
            Access(mutatedDocument),
            mutated!.Plaintext,
            StateContext(
                fixture with
                {
                    Artifact = mutated,
                },
                envelopeSha256: null));

        Assert.False(admitted.Succeeded);
    }

    [Fact]
    public async Task AuthenticatedRealSessionRejectsEveryNamedSemanticFamily()
    {
        var fixture = await BuildSessionAsync();
        var run = fixture.Artifact.Document.CompletedRuns[0];
        var context = Assert.IsType<AgentSessionReviewContextRecord>(
            run.Records.First(
                record => record is AgentSessionReviewContextRecord));
        var outcome = Assert.IsType<AgentSessionReviewOutcomeRecord>(
            run.Records.First(
                record => record is AgentSessionReviewOutcomeRecord));
        var contextIndex = run.Records.IndexOf(context);
        var outcomeIndex = run.Records.IndexOf(outcome);
        var mutations = new[]
        {
            ReplaceRecord(
                fixture.Artifact.Document,
                run,
                contextIndex,
                context with { Role = "assistant" }),
            ReplaceRecord(
                fixture.Artifact.Document,
                run,
                contextIndex,
                context with { Framing = "json" }),
            ReplaceRecord(
                fixture.Artifact.Document,
                run,
                contextIndex,
                context with { Sequence = context.Sequence + 1 }),
            ReplaceRecord(
                fixture.Artifact.Document,
                run,
                outcomeIndex,
                outcome with { TerminalMessageId = "missing_message" }),
            ReplaceRecord(
                fixture.Artifact.Document,
                run,
                contextIndex,
                context with
                {
                    Text = new string(
                        'x',
                        AgentLimits.SessionRecordBytes),
                }),
            fixture.Artifact.Document with
            {
                CompletedRuns =
                [
                    run with
                    {
                        Continuation = run.Continuation with
                        {
                            CodecId = "other_codec",
                        },
                    },
                ],
            },
        };
        var adapter = new AgentSessionRestrictedStateAdmission();

        foreach (var document in mutations)
        {
            Assert.True(
                AgentSessionCodec.TryWrite(
                    document,
                    out var artifact,
                    out var writeFailure),
                writeFailure);
            var mutated = fixture with
            {
                Artifact = artifact!,
            };

            var admitted = adapter.Admit(
                Access(document),
                artifact!.Plaintext,
                StateContext(mutated, envelopeSha256: null));

            Assert.False(admitted.Succeeded);
        }
    }

    [Fact]
    public async Task AuthenticatedContinuationDefectIsStateEnvelopeInvalid()
    {
        var fixture = await BuildSessionAsync();
        var run = Assert.Single(fixture.Artifact.Document.CompletedRuns);
        var item = Assert.Single(run.Continuation.Items);
        var document = fixture.Artifact.Document with
        {
            CompletedRuns =
            [
                run with
                {
                    Continuation = run.Continuation with
                    {
                        Items =
                        [
                            item with
                            {
                                AssociatedCallId = "finish0",
                            },
                        ],
                    },
                },
            ],
        };
        Assert.True(AgentSessionCodec.TryWrite(
            document,
            out var mutatedArtifact,
            out var writeFailure),
            writeFailure);
        await AssertAuthenticatedEnvelopeInvalid(
            fixture with { Artifact = mutatedArtifact! });
    }

    [Fact]
    public async Task AuthenticatedWrongContinuationTokenIsStateEnvelopeInvalid()
    {
        var fixture = await BuildSessionAsync();
        var json = Encoding.UTF8.GetString(
            fixture.Artifact.Plaintext[AgentSessionFormat.FramingBytes..]);
        var mutatedJson = json.Replace(
            "\"associated_call_id\":null",
            "\"associated_call_id\":{}",
            StringComparison.Ordinal);
        Assert.NotEqual(json, mutatedJson);
        var jsonBytes = Encoding.UTF8.GetBytes(mutatedJson);
        var plaintext = new byte[
            AgentSessionFormat.FramingBytes + jsonBytes.Length];
        "APRSES01"u8.CopyTo(plaintext);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            plaintext.AsSpan(8, 4),
            checked((uint)jsonBytes.Length));
        jsonBytes.CopyTo(plaintext, AgentSessionFormat.FramingBytes);
        var artifact = new AgentSessionArtifact(
            plaintext,
            AgentCanonical.HashDomain(
                AgentCanonical.SessionDomain,
                plaintext),
            fixture.Artifact.Document);

        await AssertAuthenticatedEnvelopeInvalid(
            fixture with { Artifact = artifact });
    }

    private static async Task AssertAuthenticatedEnvelopeInvalid(
        SessionFixture fixture)
    {
        var document = fixture.Artifact.Document;
        var access = Access(document);
        var keys = new TestKeyResolver();
        var scope = access.Scope;
        var binding = new RestrictedStateBinding(
            scope,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorStateSha256,
            RestrictedStateTestData.Now,
            RestrictedStateTestData.Expires);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            fixture.Artifact.Plaintext,
            keys,
            out var envelope,
            out var encryptCode),
            encryptCode);
        var envelopeSha = RestrictedStateEnvelope.EnvelopeSha256(envelope!);
        var candidate = new RestrictedStateCandidate(
            binding,
            fixture.Artifact.SessionSha256,
            envelopeSha,
            RestrictedStateEnvelope.ObjectIdentity(
                binding,
                fixture.Artifact.SessionSha256,
                envelopeSha),
            envelope!);
        var store = new MemoryRestrictedStateStore
        {
            Snapshot = new RestrictedStateSnapshot([candidate], null),
        };
        var service = new RestrictedStateService(
            store,
            keys,
            new AgentSessionRestrictedStateAdmission(),
            () => RestrictedStateTestData.Now);
        var lineage = new AcceptedLineage(
            scope,
            binding.Generation,
            candidate.SessionSha256,
            candidate.EnvelopeSha256,
            binding.PredecessorEnvelopeSha256,
            binding.AcceptedAtUnixSeconds,
            binding.ExpiresAtUnixSeconds,
            TransitionAuthorized: true);

        var restored = await service.RestoreAsync(
            access,
            new RestrictedStateRestoreRequest(
                RestrictedStateLocatorFamily.Current,
                RestrictedStateRestoreIntent.Explicit,
                lineage,
                StateContext(fixture, envelopeSha256: null)),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateCodes.EnvelopeInvalid,
            restored.Result.Code);
        Assert.Null(restored.Session);
    }

    [Fact]
    public async Task MalformedTypedSessionContextsFailWithoutThrowing()
    {
        var fixture = await BuildSessionAsync();
        var valid = StateContext(fixture, envelopeSha256: null);
        var inner = valid.SessionContext;
        AgentSessionStateAdmissionContext[] malformed =
        [
            inner with { TrustedRequest = null! },
            inner with { SessionId = null! },
            inner with { CurrentReviewedIdentity = null! },
            inner with { CurrentReviewContext = null! },
            inner with { ContinuationCodec = null! },
            inner with
            {
                Transition = (AgentSessionHeadTransition)int.MaxValue,
            },
        ];
        var adapter = new AgentSessionRestrictedStateAdmission();

        foreach (var admitted in malformed.Select(context =>
            adapter.Admit(
                Access(fixture.Artifact.Document),
                fixture.Artifact.Plaintext,
                valid with { SessionContext = context })))
        {
            Assert.False(admitted.Succeeded);
        }
    }

    [Fact]
    public async Task EveryStableScopeSubstitutionIsRejectedBySessionAdmission()
    {
        var fixture = await BuildSessionAsync();
        var document = fixture.Artifact.Document;
        var original = Access(document).Scope;
        var substitutions = new[]
        {
            original with { RepositoryId = "other-repo" },
            original with { WorkflowIdentity = "other-workflow" },
            original with { ReviewTarget = 2 },
            original with { SessionId = "session_1" },
            original with { ProviderId = "other-provider" },
            original with { ModelId = "other-model" },
            original with { AdapterId = "other-adapter" },
            original with { PolicySha256 = new string('a', 64) },
            original with { LimitsSha256 = new string('b', 64) },
            original with { ToolsetSha256 = new string('c', 64) },
            original with { BuildId = "other-build" },
        };
        var adapter = new AgentSessionRestrictedStateAdmission();

        foreach (var substituted in substitutions)
        {
            var admitted = adapter.Admit(
                RestrictedStateTestData.Access(substituted),
                fixture.Artifact.Plaintext,
                StateContext(fixture, envelopeSha256: null));

            Assert.False(admitted.Succeeded);

            var access = RestrictedStateTestData.Access(substituted);
            var keys = new TestKeyResolver();
            var binding = new RestrictedStateBinding(
                substituted,
                document.ProducerBaseSha,
                document.ProducerHeadSha,
                document.Generation,
                document.PredecessorStateSha256,
                RestrictedStateTestData.Now,
                RestrictedStateTestData.Expires);
            Assert.True(RestrictedStateEnvelope.TryEncrypt(
                access,
                binding,
                fixture.Artifact.Plaintext,
                keys,
                out var envelope,
                out var code),
                code);
            var envelopeSha =
                RestrictedStateEnvelope.EnvelopeSha256(envelope!);
            var candidate = new RestrictedStateCandidate(
                binding,
                fixture.Artifact.SessionSha256,
                envelopeSha,
                RestrictedStateEnvelope.ObjectIdentity(
                    binding,
                    fixture.Artifact.SessionSha256,
                    envelopeSha),
                envelope!);
            var store = new MemoryRestrictedStateStore
            {
                Snapshot = new RestrictedStateSnapshot(
                    [candidate],
                    null),
            };
            var service = new RestrictedStateService(
                store,
                keys,
                adapter,
                () => RestrictedStateTestData.Now);
            var lineage = new AcceptedLineage(
                substituted,
                binding.Generation,
                candidate.SessionSha256,
                candidate.EnvelopeSha256,
                binding.PredecessorEnvelopeSha256,
                binding.AcceptedAtUnixSeconds,
                binding.ExpiresAtUnixSeconds,
                TransitionAuthorized: true);

            var restored = await service.RestoreAsync(
                access,
                new RestrictedStateRestoreRequest(
                    RestrictedStateLocatorFamily.Current,
                    RestrictedStateRestoreIntent.Explicit,
                    lineage,
                    StateContext(fixture, envelopeSha256: null)),
                CancellationToken.None);

            Assert.Equal(
                RestrictedStateCodes.EnvelopeInvalid,
                restored.Result.Code);
        }
    }

    internal static async Task<SessionFixture> BuildSessionAsync(
        string repositoryId = "repo",
        long reviewTarget = 1,
        string sessionId = "session_0")
    {
        var trusted = new AgentSessionTrustedRequest(
            repositoryId,
            reviewTarget,
            "workflow",
            "trusted policy"u8.ToArray(),
            "build",
            DeepSeekAdapterContext.Provider,
            DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var identity = new ReviewedIdentity(
            repositoryId,
            reviewTarget,
            new string('4', 40),
            new string('5', 40));
        var user = User("synthetic review context");
        var run = new AgentRunRequest(
            identity,
            materialized!.StablePlan,
            sessionId,
            [.. materialized.ControlMessages, user]);
        var loop = new AgentLoop(
            new TerminalChatClient(sessionId),
            new NeverToolExecutor());
        var outcome = await loop.RunAsync(
            run,
            CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        var built = AgentSessionBuilder.Build(
            new AgentSessionBuildInput(
                run,
                outcome,
                trusted,
                run.InitialMessages.Length - 1,
                DeepSeekReasoningContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead));
        Assert.True(built.Succeeded, built.FailureCode);
        return new SessionFixture(
            built.Artifact!,
            trusted,
            identity);
    }

    private static AuthorizedStateAccess Access(
        AgentSessionDocument document)
    {
        var scope = new RestrictedStateScope(
            document.RepositoryId,
            document.WorkflowIdentity,
            document.ReviewTarget,
            document.SessionId,
            document.ProviderId,
            document.ModelId,
            document.AdapterId,
            document.PolicySha256,
            document.LimitsSha256,
            document.ToolsetSha256,
            document.BuildId);
        return RestrictedStateTestData.Access(scope);
    }

    private static RestrictedStateSessionAdmissionContext StateContext(
        SessionFixture fixture,
        string? envelopeSha256) =>
        new(
            fixture.Artifact.Document.ProducerBaseSha,
            fixture.Artifact.Document.ProducerHeadSha,
            fixture.Artifact.Document.Generation,
            fixture.Artifact.Document.PredecessorStateSha256,
            new AgentSessionStateAdmissionContext(
                fixture.Trusted,
                fixture.Artifact.Document.SessionId,
                fixture.Identity,
                User("next synthetic review context"),
                AgentSessionHeadTransition.SameHead,
                DeepSeekReasoningContinuationCodec.Instance,
                envelopeSha256));

    private static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    private static AgentSessionDocument ReplaceRecord(
        AgentSessionDocument document,
        AgentSessionCompletedRun run,
        int index,
        AgentSessionRecord replacement) =>
        document with
        {
            CompletedRuns =
            [
                run with
                {
                    Records = run.Records.SetItem(
                        index,
                        replacement),
                },
            ],
        };

    internal sealed record SessionFixture(
        AgentSessionArtifact Artifact,
        AgentSessionTrustedRequest Trusted,
        ReviewedIdentity Identity);

    private sealed class TerminalChatClient(string sessionId)
        : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectReasoningContent(
                                "state reasoning",
                                string.Empty,
                                DeepSeekReasoningContinuationCodec.FramingName,
                                AssociatedCallId: null,
                                MessagePosition: request.Messages.Length,
                                Position: 0),
                            new ProjectToolCallContent(
                                "finish0",
                                AgentToolRegistry.FinishReviewName,
                                FinishJson),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        DeepSeekAdapterContext.Provider,
                        DeepSeekAdapterContext.Model,
                        DeepSeekAdapterContext.Adapter,
                        sessionId,
                        [
                            new ProjectContinuationItem(
                                "state reasoning",
                                string.Empty,
                                DeepSeekReasoningContinuationCodec.FramingName,
                                AssociatedCallId: null,
                                MessagePosition: request.Messages.Length,
                                ContentPosition: 0),
                        ])));
    }

    private sealed class NeverToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "No non-terminal tool call expected.");
    }

    private sealed class EmptyContinuationCodec
        : IAgentContinuationCodec
    {
        internal static EmptyContinuationCodec Instance { get; } =
            new();

        public string CodecId => "r2-synthetic";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            return false;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            return false;
        }
    }
}
