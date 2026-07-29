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
