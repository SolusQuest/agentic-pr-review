using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
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
        public async Task FreshRevisionHistoriesHaveClosedFindingsAndStableNormalizedEvidence()
        {
            var first = await CommandAsync();
            Assert.True(first.ExitCode == 0, Encoding.UTF8.GetString(ReplayWire.Write(first)));
            var second = await CommandAsync();
            Assert.Equal(0, second.ExitCode);
            Assert.Equal(first.Steps.ToArray(), second.Steps.ToArray());
            Assert.Equal(first.NormalizedSha256, second.NormalizedSha256);
            Assert.Equal(IncrementalCoverage.Cases, first.Steps.Select(step => step.CaseId));
            Assert.All(first.Steps, step => Assert.True(step.Accepted));
            Assert.Equal(new long?[] { 0, 1, 2 }, first.Steps.Select(step => step.Generation));
            Assert.NotEqual(first.Operation, second.Operation);
            Assert.NotEqual(first.Observations[0].Session, second.Observations[0].Session);
            Assert.NotEqual(first.Observations[0].EnvelopeSha256, second.Observations[0].EnvelopeSha256);
            Assert.Empty(first.Observations.Select(item => item.Startup).Intersect(second.Observations.Select(item => item.Startup)));
        }

        [Fact]
        public async Task ProductionHostPublishesTheSameRevisionScenariosInTwoFreshWorlds()
        {
            var replay = await ReplayRunner.RunAsync(Bundle);
            Assert.Equal(0, replay.ExitCode);
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            Assert.Equal(replay.CorpusSha256, fixture.CorpusSha256);
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
        public async Task CompletedIncrementalReviewCannotPublishOrAcceptAfterHeadAdvances(int barrier)
        {
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Bundle).Fixture);
            var first = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, barrier);
            var second = await Host.Action.ActionHostCompositionTests.RunIncrementalAsync(fixture, barrier);
            Assert.Equal(first, second);
        }

        [Theory]
        [InlineData("old-evidence", 2)]
        [InlineData("moved-location", 2)]
        [InlineData("deleted-location", 2)]
        [InlineData("repaired-finding", 2)]
        [InlineData("duplicate", 2)]
        [InlineData("structural-duplicate", 2)]
        [InlineData("extra-unadjudicated", 1)]
        public async Task InvalidCurrentFindingsFailBeyondAdmissionAndPreservePredecessor(string mutation, int phase)
        {
            var root = CopyBundle();
            try
            {
                var name = $"script-{phase}.json";
                var script = JsonNode.Parse(File.ReadAllText(Path.Combine(root, name)))!;
                var call = script["turns"]![1]!["tool_calls"]![0]!;
                var terminal = JsonNode.Parse(call["arguments_json"]!.GetValue<string>())!;
                var findings = terminal["findings"]!.AsArray();
                var evidence = findings[0]!["evidence"]![0]!;
                if (mutation == "old-evidence") evidence["observation_id"] = IncrementalCoverage.Observation(0);
                else if (mutation is "moved-location" or "deleted-location")
                {
                    var line = mutation == "moved-location" ? 2 : 3;
                    evidence["start_line"] = line; evidence["end_line"] = line;
                }
                else
                {
                    var extra = findings[0]!.DeepClone();
                    if (mutation == "repaired-finding")
                    {
                        extra["title"] = "repaired"; extra["message"] = "Synthetic repaired defect.";
                        extra["evidence"]![0]!["start_line"] = 1; extra["evidence"]![0]!["end_line"] = 1;
                    }
                    if (mutation == "structural-duplicate") extra["title"] = "Same defect with different prose";
                    if (mutation == "extra-unadjudicated") { extra["title"] = "Unadjudicated extra"; extra["severity"] = "low"; }
                    findings.Add(extra);
                }
                call["arguments_json"] = terminal.ToJsonString();
                Rewrite(root, name, script);
                Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(root).Code);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    ReplayChildReply? observed = null;
                    var result = await ReplayRunner.RunAsync(root, new() { ObserveReply = (_, _, reply) => { if (reply.Phase == phase) observed = reply; } });
                    Assert.Equal(1, result.ExitCode);
                    Assert.Equal(phase + 1, result.Steps.Length);
                    Assert.All(result.Steps.Take(phase), step => Assert.True(step.Accepted));
                    Assert.False(result.Steps[^1].Accepted);
                    Assert.True(result.Steps[^1].PredecessorPreserved);
                    Assert.DoesNotContain(result.Steps[^1].Code, new[] { "input_invalid", "process_failed", "infrastructure_failed" });
                    Assert.NotNull(observed);
                    if (mutation == "extra-unadjudicated")
                    {
                        // Q1 permits unrelated unadjudicated findings; Q3 must still reject the complete actual set.
                        Assert.Equal("prepared", observed!.Code);
                        Assert.Equal("assertion_failed", result.Steps[^1].Code);
                    }
                }
            }
            finally { Assert.True(ReplayProcess.Cleanup(Path.GetDirectoryName(root)!)); }
        }

        [Theory]
        [InlineData("MissingHistory")]
        [InlineData("ChangedContinuation")]
        [InlineData("MissingContinuation")]
        [InlineData("WrongContinuationPosition")]
        public async Task SkippedOrChangedContinuationCannotPassPlausibleCurrentFindings(string fault)
        {
            for (var repeat = 0; repeat < 2; repeat++)
            {
                var result = await ReplayRunner.RunAsync(Bundle, new() { Fault = Enum.Parse<ReplayFault>(fault), FaultPhase = 1 });
                Assert.Equal(1, result.ExitCode);
                Assert.Equal(2, result.Steps.Length);
                Assert.True(result.Steps[0].Accepted);
                Assert.False(result.Steps[1].Accepted);
                Assert.True(result.Steps[1].PredecessorPreserved);
                Assert.DoesNotContain(result.Steps[1].Code, new[] { "input_invalid", "process_failed", "infrastructure_failed" });
            }
        }

        [Fact]
        public async Task RenamedBundleKeepsScenarioAndCoordinatedExpectedMutationCannotWeakenIt()
        {
            var root = CopyBundle();
            try
            {
                Assert.Equal(0, (await ReplayRunner.RunAsync(root)).ExitCode);
                var expected = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "expected-2.json")))!;
                expected["defects"]!.AsArray().RemoveAt(0);
                Rewrite(root, "expected-2.json", expected);
                var script = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "script-2.json")))!;
                var call = script["turns"]![1]!["tool_calls"]![0]!;
                var terminal = JsonNode.Parse(call["arguments_json"]!.GetValue<string>())!;
                terminal["findings"]!.AsArray().RemoveAt(0);
                call["arguments_json"] = terminal.ToJsonString();
                Rewrite(root, "script-2.json", script);
                Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(root).Code);
                var result = await ReplayRunner.RunAsync(root);
                Assert.Equal("assertion_failed", result.Code);
                Assert.False(Assert.Single(result.Steps).Accepted);
            }
            finally { Assert.True(ReplayProcess.Cleanup(Path.GetDirectoryName(root)!)); }
        }

        private static string CopyBundle()
        {
            var root = Path.Combine(ReplayProcess.CreatePrivateRoot(), "bundle");
            Directory.CreateDirectory(root);
            foreach (var file in Directory.GetFiles(Bundle)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
            return root;
        }

        [Theory]
        [InlineData("unregistered-ahead")]
        [InlineData("replay-ahead")]
        public async Task UnknownOrMixedCaseIdentitiesDoNotSelectAnotherOracle(string caseId)
        {
            var root = CopyBundle();
            try
            {
                var path = Path.Combine(root, "manifest.json");
                var manifest = JsonNode.Parse(File.ReadAllText(path))!;
                manifest["runs"]![2]!["case_id"] = caseId;
                File.WriteAllText(path, manifest.ToJsonString() + "\n", new UTF8Encoding(false));
                Assert.Equal(ReplayAdmissionCode.Admitted, ReplayAdmission.Load(root).Code);
                for (var repeat = 0; repeat < 2; repeat++)
                {
                    var result = await ReplayRunner.RunAsync(root, new() { FaultPhase = 2 });
                    Assert.Equal("assertion_failed", result.Code);
                    Assert.False(Assert.Single(result.Steps).Accepted);
                }
            }
            finally { Assert.True(ReplayProcess.Cleanup(Path.GetDirectoryName(root)!)); }
        }

        [Fact]
        public void CurrentReferenceCannotResolveHistoricalOrAmbiguousToolMessages()
        {
            var historical = new string('a', 64);
            var current = new string('b', 64);
            var tool = (string id) => new { role = "tool", tool_call_id = "read", content = "{\"observation_id\":\"" + id + "\"}" };
            var context = new { role = "user", content = "Current context" };
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

        private static async Task<ReplayReport> CommandAsync()
        {
            var root = ReplayProcess.CreatePrivateRoot();
            try
            {
                var start = ReplayProcess.StartInfo(root);
                start.Environment["TMP"] = start.Environment["TEMP"] = Path.GetTempPath();
                if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = Path.GetTempPath();
                start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
                start.ArgumentList.Add("replay"); start.ArgumentList.Add("--bundle"); start.ArgumentList.Add(Bundle);
                using var process = Process.Start(start)!;
                process.StandardInput.Close();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var output = ReplayWire.ReadBoundedAsync(process.StandardOutput.BaseStream, ReplayWire.ReplyLimit, deadline.Token);
                var error = ReplayWire.ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
                try { await process.WaitForExitAsync(deadline.Token); }
                finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
                Assert.Empty(await error);
                var result = ReplayWire.Read(await output, ReplayExecutionJson.Default.ReplayReport, ReplayWire.ReplyLimit);
                Assert.NotNull(result);
                Assert.Equal(result.ExitCode, process.ExitCode);
                return result;
            }
            finally { Assert.True(ReplayProcess.Cleanup(root)); }
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
        internal static async Task<string[]> RunIncrementalAsync(AdmittedReplayFixture fixture, int staleBarrier = 0, bool copiedComment = false)
        {
            Assert.True(IncrementalCoverage.VerifyFixture(fixture));
            var remote = new IncrementalRemote();
            var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
            var first = TrustedV2Scenario(12430, 1, new string('a', 64));
            var store = FullPathStore(first.Launch);
            var normalized = new List<string>();
            string? session = null;
            for (var phase = 0; phase < (staleBarrier != 0 ? 1 : copiedComment ? 2 : 3); phase++)
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
                var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
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
                else { Assert.Equal(session, provider.Request.SessionId); Assert.NotNull(provider.Request.Continuation); }
                var records = ReadAcceptedStateRecords(store, scenario.Launch, time);
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
                ReviewedSnapshot snapshot, TimeProvider timeProvider) => new Runner(this, snapshot, timeProvider);
            private sealed class Runner(IncrementalProvider owner, ReviewedSnapshot snapshot, TimeProvider timeProvider) : IActionHostProviderRunner
            {
                public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken token)
                {
                    try
                    {
                        owner.Request = request;
                        Assert.Equal(owner.input.Input.ReviewedIdentity.Runtime, snapshot.Identity);
                        using var transport = new ReplayTransport(owner.input.Script, ReplayFault.None);
                        var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                            DeepSeekAdapterContext.Adapter, request.SessionId), transport);
                        var outcome = await new AgentLoop(client, new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), timeProvider).RunAsync(request, token);
                        owner.Outcome = outcome;
                        Assert.Equal(2, transport.Consumed);
                        Assert.False(transport.Failed);
                        using var initial = JsonDocument.Parse(transport.Requests[0]);
                        var assistant = initial.RootElement.GetProperty("messages").EnumerateArray()
                            .Where(message => message.GetProperty("role").GetString() == "assistant");
                        Assert.Equal(Enumerable.Range(0, owner.index).SelectMany(index => new[] { IncrementalCoverage.Reasoning(index, false), IncrementalCoverage.Reasoning(index, true) }),
                            assistant.Select(message => message.GetProperty("reasoning_content").GetString()));
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
            internal void CopyStickyToAnotherIdentity() => Sticky = Sticky! with { Id = 8, ApiUrl = Api + "/issues/comments/8", HtmlUrl = Html + "#issuecomment-8" };
            public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token, AuthorizedStickyPublicationRequest request)
            {
                if (Sticky is not null) Assert.Equal(R4StickyMarker.Inspect(Sticky.Body).Identity!.ScopeSha256, request.Rendered.Identity.ScopeSha256);
                return new StickyTransport(this, request);
            }
            public IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token, AuthorizedStickyReadbackRequest request) => new StickyTransport(this, null);
            public IInlineGitHubPublisherTransport Create(AuthorizedInlinePublicationRequest request)
            { InlineCreates++; Maps.Add(request.CandidateMap); return new InlineTransport(this, request); }

            private sealed class StickyTransport(IncrementalRemote owner, AuthorizedStickyPublicationRequest? request) : IStickyGitHubPublisherTransport, IStickyGitHubReadbackTransport
            {
                public bool IsWithinOverallDeadline => true;
                public Task<BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>> ListIssueCommentsAsync(int page, CancellationToken token) =>
                    Task.FromResult(BoundedGitHubHttpResult<BoundedGitHubIssueCommentPage>.Success(new(owner.Sticky is null ? [] : [owner.Sticky], null, null)));
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
