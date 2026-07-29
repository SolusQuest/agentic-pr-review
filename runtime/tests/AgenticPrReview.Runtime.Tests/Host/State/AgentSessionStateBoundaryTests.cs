using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
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

        var store = new MemoryRestrictedStateStore();
        var keys = new TestKeyResolver();
        var service = new RestrictedStateService(
            store,
            keys,
            adapter,
            () => RestrictedStateTestData.Now);
        var prepared = service.Prepare(
            access,
            new RestrictedStatePrepareRequest(
                null,
                fixture.Artifact.Plaintext,
                stateContext),
            CancellationToken.None);
        Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        var accepted = service.Accept(
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

        var restored = service.Restore(
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

            var restored = service.Restore(
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

    private static async Task<SessionFixture> BuildSessionAsync()
    {
        var trusted = new AgentSessionTrustedRequest(
            "repo",
            1,
            "workflow",
            "trusted policy"u8.ToArray(),
            "build",
            "provider",
            "model",
            "adapter");
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var identity = new ReviewedIdentity(
            "repo",
            1,
            new string('4', 40),
            new string('5', 40));
        var user = User("synthetic review context");
        var run = new AgentRunRequest(
            identity,
            materialized!.StablePlan,
            "session_0",
            [.. materialized.ControlMessages, user]);
        var loop = new AgentLoop(
            new TerminalChatClient(),
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
                EmptyContinuationCodec.Instance,
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
                EmptyContinuationCodec.Instance,
                envelopeSha256));

    private static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    private sealed record SessionFixture(
        AgentSessionArtifact Artifact,
        AgentSessionTrustedRequest Trusted,
        ReviewedIdentity Identity);

    private sealed class TerminalChatClient : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectToolCallContent(
                                "finish0",
                                AgentToolRegistry.FinishReviewName,
                                FinishJson),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1));
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
