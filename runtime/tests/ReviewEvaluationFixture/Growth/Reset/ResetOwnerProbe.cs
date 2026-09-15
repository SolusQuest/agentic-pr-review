using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.Tests.Host.Action;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;

internal sealed record ResetOwnerProbeReport(string Schema, string Code, string Topology,
    string SourceCommit, string SourceTree, bool SourceClean, string[] PassedCases);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(ResetOwnerProbeReport))]
internal partial class ResetOwnerProbeJson : JsonSerializerContext;

internal static class ResetOwnerProbe
{
    internal static async Task<int> RunAsync()
    {
        var passed = new List<string>();
        try
        {
            var world = new ResetProbeWorld();
            var initial = await world.RunAsync(0);
            Require(initial.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted);
            var reset = await world.RunAsync(1, reset: true);
            Require(reset.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted &&
                reset.Provider.Request is { Continuation: null } && initial.Provider.Request is not null &&
                reset.Provider.Request!.SessionId != initial.Provider.Request.SessionId && world.Remote.Writes == 2);
            passed.Add("reset_carry_historical_target_acceptance");
            var retry = await world.RunAsync(1, reset: true);
            Require(retry.Completion.Status == ActionHostStatus.Reviewed && retry.Provider.Creates == 0 && world.Remote.Writes == 2);
            passed.Add("completed_reset_reentry");
            var next = await world.RunAsync(2);
            Require(next.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted &&
                next.Provider.Request?.SessionId == reset.Provider.Request!.SessionId &&
                next.Provider.Request!.Continuation is not null && world.Remote.Writes == 3);
            passed.Add("accepted_successor_continuation");

            var copied = new ResetProbeWorld();
            Require((await copied.RunAsync(0)).Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted);
            copied.Remote.BeforeCreate = copied.Remote.CopyTarget;
            var rejected = await copied.RunAsync(1, reset: true);
            Require(rejected.Completion.Summary.StateDisposition != ActionHostStateDisposition.Accepted &&
                rejected.Provider.Creates == 1 && copied.Remote.Writes == 1);
            passed.Add("post_match_target_substitution_rejected");

            foreach (var (retryWrite, appeared) in new[] { (false, false), (false, true), (true, false), (true, true) })
            {
                var absent = new ResetProbeWorld();
                var predecessor = await absent.RunAsync(1);
                Require(predecessor.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted);
                var oldId = absent.Remote.Sticky!.Id;
                absent.Remote.RemoveTarget();
                absent.Remote.AppearBeforeCreate = appeared && !retryWrite;
                absent.Remote.KnownNotSentOnce = retryWrite;
                absent.Remote.AppearOnRetry = appeared && retryWrite;
                var successor = await absent.RunAsync(2);
                Require(successor.Provider.Creates == 1 &&
                    successor.Provider.Request?.SessionId == predecessor.Provider.Request!.SessionId &&
                    successor.Provider.Request!.Continuation is not null);
                Require(appeared
                    ? successor.Completion.Summary.StateDisposition != ActionHostStateDisposition.Accepted &&
                        absent.Remote.MutationAttempts == (retryWrite ? 2 : 1)
                    : successor.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted &&
                        absent.Remote.MutationAttempts == (retryWrite ? 3 : 2) && absent.Remote.Sticky!.Id != oldId);
                passed.Add((retryWrite ? "retry_" : "initial_") +
                    (appeared ? "ordinary_absence_late_target_rejected" : "ordinary_absence_recreation_accepted"));
            }
        }
        catch
        {
            Write("r5_reset_owner_failed", passed);
            return 1;
        }
        Write("r5_reset_owner_passed", passed);
        return 0;
    }

    private static void Write(string code, List<string> cases) => Console.WriteLine(JsonSerializer.Serialize(
        new ResetOwnerProbeReport("r5-reset-owner-v1", code,
            "production_host_synthetic_ports",
            EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean, cases.ToArray()),
        ResetOwnerProbeJson.Default.ResetOwnerProbeReport));

    internal static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("r5_reset_owner_invariant");
    }
}

// Thin execution setup over shared synthetic ports. All authorization, reset, P5/P6 and
// acceptance operations below run through the production Host; no positive capability is forged.
internal sealed class ResetProbeWorld
{
    internal ResetProbeClock Time { get; } = new();
    internal ScriptedLocatorStore Store { get; } = new() { FilterListsByName = true, UseNumericObjectIds = true };
    internal ResetProbeRemote Remote { get; } = new();

    internal async Task<ResetProbeInvocation> RunAsync(int phase, bool reset = false, bool failProvider = false)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        if (phase > 0) scenario.Transport.PullRequest = scenario.Transport.PullRequest with { HeadSha = new(phase == 1 ? 'f' : '9', 40) };
        var old = scenario.Launch;
        ResetOwnerProbe.Require(ActionHostProviderApiKey.TryCreate("synthetic-provider-key", out var providerKey));
        ResetOwnerProbe.Require(ActionHostStateKey.TryCreate(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()), out var key));
        ResetOwnerProbe.Require(ActionHostInputs.TryCreate(old.Inputs.GitHubToken, providerKey, key, null, null,
            old.Inputs.PullRequestNumber, reset ? ActionHostStateMode.Reset : ActionHostStateMode.Auto, out var inputs));
        ResetOwnerProbe.Require(ActionHostLaunchContract.TryCreate(inputs, old.EventJsonPath, old.EventJsonSha256,
            old.RepositoryName, old.RepositoryId, old.RunId + phase, old.RunAttempt, old.WorkflowPath,
            old.WorkflowRef, old.WorkflowSha, old.ActionSourceSha, old.PayloadSha256, old.BuildDiscriminator,
            old.Cancellation, old.ArtifactBridgeEndpoint, out var launch));
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with { Id = launch!.RunId };
        Store.ProducingRunIdentity = launch.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Store.ProducingRunAttempt = launch.RunAttempt;
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, previousHead: phase > 1 ? new string('f', 40) : null);
        var provider = new ResetProbeProvider(phase, failProvider);
        var staging = Path.Combine(Path.GetTempPath(), "apr-reset-owner-" + Guid.NewGuid().ToString("N"));
        var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, new StatePorts(Store, github),
            Remote, provider, Time, () => staging)).RunAsync(launch, CancellationToken.None);
        ResetOwnerProbe.Require(!Directory.Exists(staging));
        return new(completion, launch, provider);
    }

    private sealed class StatePorts(IRestrictedStateStore store, FullPathGitHubFactory github) : IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) => store;
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => github.CreateExactObjectTransport(token);
    }
}

internal sealed record ResetProbeInvocation(ActionHostCompletion Completion, ActionHostLaunchContract Launch, ResetProbeProvider Provider);

internal sealed class ResetProbeClock : TimeProvider
{
    internal long UnixSeconds { get; set; } = 1_700_000_000;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(UnixSeconds);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        TimeProvider.System.CreateTimer(callback, state,
            dueTime == TimeSpan.FromSeconds(5) || dueTime == TimeSpan.FromSeconds(10) ? TimeSpan.Zero : dueTime, period);
}

internal sealed class ResetProbeProvider(int phase, bool fail) : IActionHostProviderRunnerFactory
{
    internal int Creates { get; private set; }
    internal AgentRunRequest? Request { get; private set; }
    public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot, TimeProvider timeProvider)
    {
        Creates++;
        return new Runner(this, phase, fail, timeProvider);
    }
    private sealed class Runner(ResetProbeProvider owner, int phase, bool fail, TimeProvider time) : IActionHostProviderRunner
    {
        public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken token)
        {
            owner.Request = request;
            if (fail) return AgentRunOutcome.Failure(AgentFailureCodes.ChatFailed, 0, 0, []);
            var terminal = Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview($"Synthetic reset review {phase}.", []));
            var script = new ReplayScript([new([new("reset-finish-" + phase, "finish_review", terminal)], "synthetic reset continuation")]);
            using var transport = new ReplayTransport(script, ReplayFault.None);
            var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                DeepSeekAdapterContext.Adapter, request.SessionId), transport);
            return await new AgentLoop(client, new NoTools(), time).RunAsync(request, token);
        }
        public void Dispose() { }
    }
    private sealed class NoTools : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => "unexpected_reset_probe_tool";
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token) =>
            throw new InvalidOperationException("unexpected_reset_probe_tool");
    }
}

internal sealed class ResetProbeRemote : IStickyGitHubPublisherTransportFactory
{
    private const string Api = "https://api.github.com/repos/SolusQuest/agentic-pr-review/issues/comments/";
    private const string Html = "https://github.com/SolusQuest/agentic-pr-review/pull/147#issuecomment-";
    internal BoundedGitHubIssueComment? Sticky { get; private set; }
    internal int Writes { get; private set; }
    internal int MutationAttempts { get; private set; }
    internal bool UnknownWithoutWrite { get; set; }
    internal bool KnownNotSentOnce { get; set; }
    internal bool AppearOnRetry { get; set; }
    internal bool IncompleteDiscovery { get; set; }
    internal bool AppearBeforeCreate { get; set; }
    internal BoundedGitHubIssueComment? Duplicate { get; private set; }
    internal Action? BeforeCreate { get; set; }
    internal Action? AfterWrite { get; set; }
    internal void CopyTarget() => Sticky = Sticky! with { Id = 8, ApiUrl = Api + "8", HtmlUrl = Html + "8" };
    internal void RemoveTarget() => Sticky = null;
    internal void EditTarget() => Sticky = Sticky! with { Body = Sticky.Body + "changed" };
    internal void DuplicateTarget() => Duplicate = Sticky! with { Id = 8, ApiUrl = Api + "8", HtmlUrl = Html + "8" };
    public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token, AuthorizedStickyPublicationRequest request)
    {
        var hook = BeforeCreate;
        BeforeCreate = null;
        hook?.Invoke();
        if (AppearBeforeCreate && Sticky is null)
            Sticky = new(9, Api + "9", Html + "9", request.Rendered.Comment);
        return new Transport(this, request);
    }
    public IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token, AuthorizedStickyReadbackRequest request) => new Transport(this, null);
    private sealed class Transport(ResetProbeRemote owner, AuthorizedStickyPublicationRequest? request) : IStickyGitHubPublisherTransport, IStickyGitHubReadbackTransport
    {
        public bool IsWithinOverallDeadline => true;
        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>> ListIssueCommentsAsync(int page, CancellationToken token) =>
            Task.FromResult(owner.IncompleteDiscovery
                ? BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Failed(BoundedGitHubHttpOutcome.KnownNotSent, BoundedGitHubPublisherReason.TransportFailure)
                : BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Success(new(
                    new[] { owner.Sticky, owner.Duplicate }.OfType<BoundedGitHubIssueComment>().ToArray(), null, null)));
        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>> GetIssueCommentAsync(long commentId, CancellationToken token) =>
            Task.FromResult(owner.Sticky is { } value && value.Id == commentId
                ? BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(value)
                : BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(BoundedGitHubHttpOutcome.KnownNotSent, BoundedGitHubPublisherReason.TransportFailure));
        public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>> MutateStickyCommentAsync(CancellationToken token)
        {
            ResetOwnerProbe.Require(request is not null);
            owner.MutationAttempts++;
            if (owner.KnownNotSentOnce)
            {
                owner.KnownNotSentOnce = false;
                owner.AppearBeforeCreate = owner.AppearOnRetry;
                return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                    BoundedGitHubHttpOutcome.KnownNotSent, BoundedGitHubPublisherReason.Deadline));
            }
            if (owner.UnknownWithoutWrite)
                return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                    BoundedGitHubHttpOutcome.OutcomeUnknown, BoundedGitHubPublisherReason.TransportFailure));
            owner.Writes++;
            var id = owner.Sticky?.Id ?? (6 + owner.Writes);
            owner.Sticky = new(id, Api + id, Html + id, request!.Rendered.Comment);
            owner.AfterWrite?.Invoke();
            return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(owner.Sticky));
        }
        public void Dispose() { }
    }
}
