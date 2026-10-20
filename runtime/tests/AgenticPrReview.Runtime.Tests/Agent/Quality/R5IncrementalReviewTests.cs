using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality.Incremental;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class R5IncrementalReviewTests
    {
        internal static string Bundle => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "incremental");

        [Fact]
        public async Task AdmittedRevisionObservationsMatchIndependentCurrentTools()
        {
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            Assert.True(IncrementalCoverage.VerifyFixture(fixture));
            for (var phase = 0; phase < 3; phase++)
            {
                var run = fixture.Runs[phase];
                var snapshot = run.CreateSnapshot(Directory.GetCurrentDirectory());
                Assert.True(AgentToolArguments.TryReadFile("{\"path\":\"file.txt\",\"start_line\":1,\"line_count\":20}", out var arguments));
                var actual = await new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot))
                    .ExecuteAsync(new PreparedReadFileCall("independent-audit", arguments!), CancellationToken.None);
                Assert.Equal(IncrementalCoverage.Observation(phase), actual.Observation!.ObservationId);
            }
        }

        [Fact]
        public async Task ProductionHostPublishesTheSameRevisionScenariosInTwoFreshWorlds()
        {
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            // The two genuine current Host worlds below independently enforce
            // the authored source/observation/finding oracle. Native SESSION
            // replay is no longer a prerequisite for this Host-only regression.
            Assert.True(IncrementalCoverage.VerifyFixture(fixture));
            var first = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture);
            var second = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture);
            Assert.Equal(first, second);
        }

        [Fact]
        public async Task IdenticalBodyOnAnotherCommentCannotAuthorizeTheNewHostTransaction()
        {
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            var first = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, copiedComment: true);
            var second = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, copiedComment: true);
            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task SameHeadRecoveryRequiresOneSuccessfulFreshDiscovery(int failedDiscovery)
        {
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            var first = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, failedDiscovery: failedDiscovery);
            var second = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, failedDiscovery: failedDiscovery);
            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public async Task CompletedIncrementalReviewCannotPublishOrAcceptAfterHeadAdvances(int barrier)
        {
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            var first = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, barrier);
            var second = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, barrier);
            Assert.Equal(first, second);
        }

        private static string CopyBundle()
        {
            var root = Path.Combine(ReplayProcess.CreatePrivateRoot(), "bundle");
            Directory.CreateDirectory(root);
            foreach (var file in Directory.GetFiles(Bundle)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            return root;
        }

        [Fact]
        public void CurrentReferenceCannotResolveHistoricalOrAmbiguousToolMessages()
        {
            var historical = new string('a', 64);
            var current = new string('b', 64);
            var tool = (string id) => new { role = "tool", contents = new[] { new { kind = "tool_result", call_id = "read", result = "{\"observation_id\":\"" + id + "\"}" } } };
            var context = new { role = "user", contents = new[] { new { kind = "text", text = "Current context" } } };
            var arguments = "{\"summary\":\"s\",\"findings\":[{\"evidence\":[{\"observation_id\":\"$current:read\"}]}]}";
            using var prior = JsonDocument.Parse(JsonSerializer.Serialize(new { messages = new object[] { tool(historical), context } }));
            Assert.Throws<InvalidOperationException>(() => IncrementalArguments.Expand(arguments, prior.RootElement));
            using var duplicate = JsonDocument.Parse(JsonSerializer.Serialize(new { messages = new object[] { context, tool(current), tool(current) } }));
            Assert.Throws<InvalidOperationException>(() => IncrementalArguments.Expand(arguments, duplicate.RootElement));
            using var valid = JsonDocument.Parse(JsonSerializer.Serialize(new { messages = new object[] { tool(historical), context, tool(current) } }));
            var expanded = IncrementalArguments.Expand(arguments, valid.RootElement);
            Assert.Contains(current, expanded);
            Assert.DoesNotContain(historical, expanded);
        }

        private static void Rewrite(string root, string name, JsonNode value)
        {
            var bytes = Encoding.UTF8.GetBytes(value.ToJsonString() + "\n");
            File.WriteAllBytes(Path.Combine(root, name), bytes);
            var path = Path.Combine(root, "manifest.json");
            var manifest = JsonNode.Parse(File.ReadAllText(path))!;
            var entry = manifest["files"]!.AsArray().Single(file => file!["path"]!.GetValue<string>() == name)!;
            entry["length"] = bytes.Length;
            entry["sha256"] = AgentCanonical.HashRaw(bytes);
            File.WriteAllText(path, manifest.ToJsonString() + "\n", new UTF8Encoding(false));
        }
    }
}

namespace AgenticPrReview.Runtime.Tests.Host.Action
{
    // Reuse the existing private composition scaffolding without exporting or bypassing production authority.
    public sealed partial class ActionHostCompositionTests
    {
        internal static async Task<string[]> RunIncrementalAsync(AdmittedReplayFixture fixture, int staleBarrier = 0, bool copiedComment = false, int failedDiscovery = 0)
        {
            Assert.True(IncrementalCoverage.VerifyFixture(fixture));
            var remote = new IncrementalRemote();
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var first = TrustedV2Scenario(12430, 1, new string('a', 64));
            var store = FullPathStore(first.Launch);
            var normalized = new List<string>();
            string? session = null;
            for (var phase = 0; phase < (staleBarrier != 0 ? 1 : copiedComment || failedDiscovery != 0 ? 2 : 3); phase++)
            {
                var scenario = phase == 0 ? first : TrustedV2Scenario(12430 + phase, 1, new string('a', 64));
                var identity = fixture.Runs[phase].Input.ReviewedIdentity.Runtime;
                scenario.Scenario.Transport.PullRequest = scenario.Scenario.Transport.PullRequest with { BaseSha = identity.BaseSha, HeadSha = identity.HeadSha };
                var github = new FullPathGitHubFactory(scenario.Scenario.Transport.PullRequest, withInlineFile: true,
                    workflowSha: TrustedProofPayloadBuildIdentity.SourceCommit,
                    fileBytes: Encoding.UTF8.GetBytes(IncrementalCoverage.Source(phase)),
                    previousHead: phase == 2 ? IncrementalCoverage.Identity(1).HeadSha : null);
                store.ProducingRunIdentity = scenario.Launch.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                store.ProducingRunAttempt = 1;
                var setupUploads = 0;
                var provider = new IncrementalProvider(fixture.Runs[phase], phase, () =>
                {
                    setupUploads = store.UploadCalls;
                    if (copiedComment && phase == 1) remote.CopyStickyToAnotherIdentity();
                    if (failedDiscovery != 0 && phase == 1) remote.FailedDiscovery = failedDiscovery;
                });
                var headChanges = 0;
                github.CurrentPullRequestFact = _ =>
                {
                    var change = provider.Outcome is not null && staleBarrier switch
                    {
                        1 => store.UploadCalls == setupUploads,
                        2 => store.UploadCalls > setupUploads,
                        _ => false,
                    };
                    if (change) headChanges++;
                    return change ? scenario.Scenario.Transport.PullRequest with { HeadSha = new string('f', 40) }
                        : scenario.Scenario.Transport.PullRequest;
                };
                var staging = StagingPath();
                var inlineBefore = remote.InlineComments.Count;
                var completion = await new CurrentHostTestComposition(new ActionHostCompositionDependencies(
                    scenario.Scenario.EventReader, scenario.Scenario.Factory, github, github,
                    new FullPathStateDependencies(store, github), remote, provider, time, () => staging,
                    new PostAcceptanceInlinePublisherHook(remote), workflowAdmission: TrustedProofV2WorkflowAdmission.Instance))
                    .RunAsync(scenario.Launch, CancellationToken.None);
                Assert.False(Directory.Exists(staging));
                Assert.True(provider.Outcome is not null, $"phase={phase};status={completion.Status};state={completion.Summary.StateDisposition};provider={provider.Failure}");
                Assert.True(provider.Outcome!.Succeeded, provider.Outcome.Diagnostic?.Code);
                Assert.True(IncrementalCoverage.ClosedFindings(phase, provider.Outcome.Review!.Findings));
                Assert.Equal(identity, provider.Request!.ReviewedIdentity);
                if (phase == 0) { session = provider.Request.SessionId; Assert.Null(provider.Request.Continuation); }
                else { Assert.Equal(session, provider.Request.SessionId); Assert.Null(provider.Request.Continuation); }
                Assert.Null(provider.Request.StablePlan.PriorSessionSha256);
                var records = ReadAcceptedStateRecords(store, scenario.Launch, time);
                if (failedDiscovery == 1 && phase == 1)
                {
                    Assert.Equal(1, remote.PrewriteDiscoveries);
                    Assert.Equal(ActionHostStatus.StateConflict, completion.Status);
                    Assert.Equal(ActionHostStateDisposition.Conflict, completion.Summary.StateDisposition);
                    Assert.Empty(records.Acceptances.Where(item => item.Header.ProducingRunIdentity == store.ProducingRunIdentity));
                    Assert.Single(remote.StickyWrites);
                    Assert.Equal(3, remote.InlineComments.Count);
                    normalized.Add("first-discovery-failed:no-new-publication-or-acceptance");
                    continue;
                }
                if (copiedComment && phase == 1)
                {
                    Assert.Equal(ActionHostStatus.StateConflict, completion.Status);
                    Assert.Empty(records.Acceptances.Where(item => item.Header.ProducingRunIdentity == store.ProducingRunIdentity));
                    Assert.Single(remote.StickyWrites);
                    Assert.Equal(3, remote.InlineComments.Count);
                    normalized.Add("copied-predecessor:conflict:no-new-publication-or-acceptance");
                    continue;
                }
                if (staleBarrier != 0)
                {
                    Assert.True(headChanges > 0);
                    Assert.Equal(ActionHostStatus.StaleHead, completion.Status);
                    Assert.Equal(ActionHostStateDisposition.NotCommitted, completion.Summary.StateDisposition);
                    Assert.Empty(records.Acceptances);
                    Assert.Empty(remote.StickyWrites);
                    Assert.Empty(remote.InlineComments);
                    Assert.Equal(0, remote.InlineCreates);
                    normalized.Add($"barrier:{staleBarrier}:completed:stale:zero-writes:zero-acceptances");
                    continue;
                }
                Assert.True(completion.Status == ActionHostStatus.Reviewed, $"phase={phase};status={completion.Status};sticky={remote.StickyWrites.Count}");
                if (failedDiscovery == 2 && phase == 1) Assert.Equal(1, remote.PrewriteDiscoveries);
                Assert.Equal(ActionHostStateDisposition.Accepted, completion.Summary.StateDisposition);
                Assert.Single(records.Acceptances.Where(item => item.Header.ProducingRunIdentity == store.ProducingRunIdentity));
                Assert.Equal(phase + 1, remote.StickyWrites.Count);
                Assert.Equal(phase == 0 ? "create" : "update", remote.StickyWrites[phase].Operation);
                Assert.Equal(phase == 1 ? 0 : IncrementalCoverage.Defects(phase).Length, remote.InlineComments.Count - inlineBefore);
                Assert.Equal(7, remote.Sticky!.Id);
                Assert.Equal(phase + 1, remote.Maps.Count);
                var map = remote.Maps[phase];
                Assert.Equal(identity, map.ReviewedIdentity);
                Assert.Equal(IncrementalCoverage.Findings(phase).Select(R4PublicationIdentityV1.ComputeFindingFingerprint).Order(),
                    map.Candidates.Select(candidate => candidate.FindingIdentity.FingerprintSha256).Order());
                if (phase == 1) Assert.Equal(remote.Maps[0].Candidates.Select(candidate => candidate.InlineKey), map.Candidates.Select(candidate => candidate.InlineKey));
                if (phase == 2) Assert.Empty(remote.Maps[0].Candidates.Select(candidate => candidate.InlineKey).Intersect(map.Candidates.Select(candidate => candidate.InlineKey)));
                var marker = R4StickyMarker.Inspect(remote.Sticky.Body);
                Assert.Equal(R4StickyInspectionKind.ValidR4, marker.Kind);
                Assert.Equal(identity.HeadSha, marker.Identity!.HeadSha);
                foreach (var defect in IncrementalCoverage.Defects(phase)) Assert.Contains(R4Markdown.Escape($"Synthetic {defect.Id} defect."), remote.Sticky.Body);
                if (phase == 2)
                {
                    Assert.DoesNotContain(R4Markdown.Escape("Synthetic repaired defect."), remote.Sticky.Body);
                    Assert.DoesNotContain(R4Markdown.Escape("Synthetic deleted defect."), remote.Sticky.Body);
                }
                var newInline = remote.InlineComments.Skip(inlineBefore).ToArray();
                Assert.Equal(phase == 1 ? [] : IncrementalCoverage.Defects(phase).Select(item => (int?)item.Line).Order().ToArray(),
                    newInline.Select(item => item.Line).Order().ToArray());
                Assert.All(newInline, comment => { Assert.Equal(identity.HeadSha, comment.CommitId); Assert.Equal("file.txt", comment.Path); Assert.Equal("RIGHT", comment.Side); });
                if (phase != 1) Assert.Equal(map.Candidates.Select(candidate => candidate.InlineKey).Order(),
                    newInline.Select(comment => InlineCommentMarker.Inspect(comment.Body).InlineKey).Order());
                normalized.Add($"{fixture.Runs[phase].Input.Id}:{identity.HeadSha}:{remote.StickyWrites[phase].Operation}:{marker.Identity.ScopeSha256}:{marker.Identity.BodySha256}:{newInline.Length}");
                normalized.AddRange(newInline.Select(comment => $"{comment.CommitId}:{comment.Path}:{comment.Line}:{AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(comment.Body))}"));
            }
            return normalized.ToArray();
        }

        private sealed class IncrementalProvider(AdmittedReplayRun run, int phase, System.Action completed) : IActionHostProviderRunnerFactory
        {
            private readonly AdmittedReplayRun input = run;
            private readonly int index = phase;
            private readonly System.Action onCompleted = completed;
            internal AgentRunOutcome? Outcome { get; private set; }
            internal string? Failure { get; private set; }
            internal AgentRunRequest? Request { get; private set; }
            public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
                ReviewedSnapshot snapshot, TimeProvider timeProvider)
            {
                var work = new ResetProbeProvider(index, false, input.Script);
                return new Runner(this, snapshot, work, work.Create(policy, key, snapshot, timeProvider));
            }
            private sealed class Runner(IncrementalProvider owner, ReviewedSnapshot snapshot,
                ResetProbeProvider work, IActionHostProviderRunner inner) : IActionHostProviderRunner
            {
                public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken token)
                {
                    try
                    {
                        owner.Request = request;
                        Assert.Equal(owner.input.Input.ReviewedIdentity.Runtime, snapshot.Identity);
                        var outcome = await inner.RunAsync(request, token);
                        owner.Outcome = outcome;
                        Assert.True(outcome.CompletedSessionEligible);
                        Assert.True(work.Requests.Length > 1);
                        using var initial = JsonDocument.Parse(work.Requests[0]);
                        // This fixture exercises Host publication from current
                        // work. Genuine retained SDK history is separately qualified
                        // by SolusRestored current_tools_only_no_replay and history
                        // classification; structural records cannot prove it.
                        Assert.Equal(JsonValueKind.Null, initial.RootElement.GetProperty("continuation").ValueKind);
                        Assert.Null(request.Continuation);
                        Assert.Null(request.StablePlan.PriorSessionSha256);
                        Assert.Equal(2, request.InitialMessages.Length);
                        for (var previous = 0; previous < owner.index; previous++)
                        {
                            var currentInput = Encoding.UTF8.GetString(work.Requests[0]);
                            Assert.DoesNotContain(IncrementalCoverage.Reasoning(previous, false), currentInput);
                            Assert.DoesNotContain(IncrementalCoverage.Reasoning(previous, true), currentInput);
                        }
                        owner.onCompleted();
                        return outcome;
                        }
                    catch (Exception error) { owner.Failure = error.Message; throw; }
                }
                public void Dispose() { }
            }
        }

        // The fake holds only what production publishers actually dispatch; readback/discovery observes those writes.
        private sealed class IncrementalRemote : IStickyGitHubPublisherTransportFactory, IInlineGitHubPublisherTransportFactory
        {
            private const string Api = "https://api.github.com/repos/SolusQuest/agentic-pr-review";
            private const string Html = "https://github.com/SolusQuest/agentic-pr-review/pull/147";
            internal BoundedGitHubIssueComment? Sticky { get; private set; }
            internal List<(string Operation, string Body)> StickyWrites { get; } = [];
            internal List<BoundedGitHubReviewComment> InlineComments { get; } = [];
            internal int InlineCreates { get; private set; }
            internal List<InlineCandidateMap> Maps { get; } = [];
            internal int FailedDiscovery { get; set; }
            internal int PrewriteDiscoveries { get; private set; }
            internal void CopyStickyToAnotherIdentity() => Sticky = Sticky! with { Id = 8, ApiUrl = Api + "/issues/comments/8", HtmlUrl = Html + "#issuecomment-8" };
            public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token, AuthorizedStickyPublicationRequest request, CancellationToken reconciliationToken = default)
            {
                if (Sticky is not null) Assert.Equal(R4StickyMarker.Inspect(Sticky.Body).Identity!.ScopeSha256, request.Rendered.Identity.ScopeSha256);
                return new StickyTransport(this, request);
            }
            public IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token, AuthorizedStickyReadbackRequest request, CancellationToken reconciliationToken = default)
            {
                var fail = FailedDiscovery != 0 && StickyWrites.Count == 1 && ++PrewriteDiscoveries == FailedDiscovery;
                return new StickyTransport(this, null, fail);
            }
            public IInlineGitHubPublisherTransport Create(AuthorizedInlinePublicationRequest request, CancellationToken reconciliationToken = default)
            { InlineCreates++; Maps.Add(request.CandidateMap); return new InlineTransport(this, request); }

            private sealed class StickyTransport(IncrementalRemote owner, AuthorizedStickyPublicationRequest? request, bool failDiscovery = false) : IStickyGitHubPublisherTransport, IStickyGitHubReadbackTransport
            {
                public bool IsWithinOverallDeadline => true;
                public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>> ListIssueCommentsAsync(int page, CancellationToken token) =>
                    Task.FromResult(failDiscovery
                        ? BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Failed(BoundedGitHubHttpOutcome.KnownNotSent, BoundedGitHubPublisherReason.TransportFailure)
                        : BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Success(new(owner.Sticky is null ? [] : [owner.Sticky], null, null)));
                public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>> GetIssueCommentAsync(long commentId, CancellationToken token)
                {
                    Assert.Equal(owner.Sticky!.Id, commentId);
                    return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(owner.Sticky));
                }
                public Task<BoundedGitHubHttpResult<BoundedGitHubIssueComment>> MutateStickyCommentAsync(CancellationToken token)
                {
                    Assert.NotNull(request);
                    owner.StickyWrites.Add((owner.Sticky is null ? "create" : "update", request.Rendered.Comment));
                    owner.Sticky = new(7, Api + "/issues/comments/7", Html + "#issuecomment-7", request.Rendered.Comment);
                    return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(owner.Sticky));
                }
                public void Dispose() { }
            }

            private sealed class InlineTransport(IncrementalRemote owner, AuthorizedInlinePublicationRequest request) : IInlineGitHubPublisherTransport
            {
                public bool IsWithinOverallDeadline => true;
                public Task<BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>> ListReviewCommentsAsync(int page, CancellationToken token) =>
                    Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>.Success(new(owner.InlineComments.ToArray(), null, null)));
                public Task<BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>> CreateBatchReviewAsync(ReadOnlyMemory<byte> body, CancellationToken token)
                {
                    using var document = JsonDocument.Parse(body);
                    var head = document.RootElement.GetProperty("commit_id").GetString()!;
                    Assert.Equal(request.CandidateMap.ReviewedIdentity.HeadSha, head);
                    var reviewId = 100 + owner.InlineComments.Count;
                    foreach (var comment in document.RootElement.GetProperty("comments").EnumerateArray())
                    {
                        var id = 200 + owner.InlineComments.Count;
                        owner.InlineComments.Add(new(id, reviewId, Api + $"/pulls/comments/{id}", Api + "/pulls/147",
                            Html + $"#discussion_r{id}", comment.GetProperty("body").GetString()!, comment.GetProperty("path").GetString(),
                            comment.GetProperty("line").GetInt32(), comment.GetProperty("side").GetString(), head));
                    }
                    return Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Success(
                        new(reviewId, Api + $"/pulls/147/reviews/{reviewId}", Api + "/pulls/147", Html + $"#pullrequestreview-{reviewId}", head)));
                }
                public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>> CreateReviewCommentAsync(ReadOnlyMemory<byte> body, CancellationToken token) =>
                    throw new InvalidOperationException("incremental_unexpected_fallback");
                public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>> GetReviewCommentAsync(long commentId, CancellationToken token) =>
                    Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Success(owner.InlineComments.Single(comment => comment.Id == commentId)));
                public void Dispose() { }
            }
        }
    }
}
