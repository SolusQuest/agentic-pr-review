using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.State
{
    [Collection(ProcessEnvironmentCollection.Name)]
    public sealed class R5CapacityResetTests
    {
        [Fact]
        public Task SamePublicFactCanBeReadAgainWithoutImportingOldContinuation() =>
            Action.ActionHostCompositionTests.VerifyFreshPublicFactAsync();

        [Fact]
        public async Task ExecutableResetOwnerProbeUsesProductionAuthorizationAndPublication()
        {
            Assert.Equal(0, await ResetOwnerProbe.RunAsync());
        }

        [Fact]
        public async Task CompletedResetReentryRetainsAcceptedSuccessorThroughProductionHost()
        {
            await Action.ActionHostCompositionTests.VerifyCompletedResetReentryAsync();
        }

        [Fact]
        public async Task CompletedResetBeforeProviderFailureReusesTheSameEmptySuccessor()
        {
            await Action.ActionHostCompositionTests.VerifyEmptyResetReentryAsync();
        }

        [Fact]
        public async Task CancellationBeforeResetAdmissionPreservesAcceptedState()
        {
            await Action.ActionHostCompositionTests.VerifyCancelledResetAsync();
        }

        [Fact]
        public async Task DifferentAuthorizedResetRunStillCreatesAnotherSession()
        {
            await Action.ActionHostCompositionTests.VerifyDistinctResetAsync();
        }

        [Fact]
        public async Task ResetAfterAcceptedStickyResultCanAcceptFreshReview()
        {
            await Action.ActionHostCompositionTests.VerifyResetWithPreviousStickyAsync();
        }

        [Fact]
        public async Task CapacityRejectionResetAndIndependentContinuationUseOneProductionHistory()
        {
            await Action.ActionHostCompositionTests.VerifyCapacityResetAsync();
        }
    }
}

namespace AgenticPrReview.Runtime.Tests.Host.Action
{
    public sealed partial class ActionHostCompositionTests
    {
        internal static async Task VerifyFreshPublicFactAsync()
        {
            var world = new CapacityResetWorld();
            var old = await world.RunAsync(0);
            Assert.Equal(ActionHostStateDisposition.Accepted, old.Completion.Summary.StateDisposition);
            var fresh = await world.RunAsync(1, reset: true, fresh: true, samePublicFact: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, fresh.Completion.Summary.StateDisposition);
            Assert.DoesNotContain(ResetWorkload.OldFact, Encoding.UTF8.GetString(fresh.Provider.Requests[0]));
            Assert.Contains(ResetWorkload.OldFact, Encoding.UTF8.GetString(fresh.Provider.Requests[1]));
            AssertSessionMarkers(world, fresh, ResetWorkload.OldFact, ResetWorkload.FreshReasoning, ResetWorkload.OldReasoning);
        }

        private static void AssertSessionMarkers(CapacityResetWorld world, CapacityResetInvocation invocation,
            string fact, string reasoning, params string[] absent)
        {
            var records = ReadAcceptedStateRecords(world.Store, invocation.Launch, world.Time);
            // The accepted tail can share a fixed test timestamp; select by its non-predecessor identity.
            var receipt = records.Acceptances.Single(value => !records.Acceptances.Any(other =>
                other.Receipt.PreviousAcceptanceReceiptIdentity == value.Header.ObjectIdentity));
            var generation = records.Generations.First(value => value.Header.ObjectIdentity == receipt.Receipt.OriginalCandidateObjectIdentity).Generation;
            var run = invocation.Provider.Request!;
            var plan = run.StablePlan;
            var scope = new RestrictedStateScope(plan.RepositoryId, plan.WorkflowIdentity,
                plan.ReviewTarget, run.SessionId,
                plan.ProviderId, plan.ModelId, plan.AdapterId, plan.PolicySha256, plan.LimitsSha256, plan.ToolsetSha256, plan.BuildId);
            AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var stateAccess);
            var state = Assert.IsType<AuthorizedStateAccess>(stateAccess);
            ReadAcceptedStateRecords(world.Store, invocation.Launch, world.Time, inspectLocator: (locator, access) =>
            {
                var binding = new RestrictedStateBinding(scope, generation.ProducerBaseSha, generation.ProducerHeadSha,
                    generation.Generation, generation.PredecessorEnvelopeSha256, generation.PreparedAtUnixSeconds, generation.PreparedExpiresAtUnixSeconds);
                Assert.True(RestrictedStateEnvelope.TryDecrypt(state, binding, generation.EncryptedStateEnvelope.AsSpan(),
                    new ResetSessionReadKeys(state, access, locator), out var plaintext, out var code), code);
                try
                {
                    var text = Encoding.UTF8.GetString(plaintext!);
                    Assert.Contains(fact, text);
                    Assert.Contains(reasoning, text);
                    foreach (var marker in absent) Assert.DoesNotContain(marker, text);
                    Assert.True(AgentSessionCodec.TryParse(plaintext!, out var session, out var sessionCode), sessionCode);
                    Assert.Equal(generation.SessionSha256, session!.SessionSha256);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext!); }
            });
        }

        private sealed class ResetSessionReadKeys(AuthorizedStateAccess state, AuthorizedLocatorAccess access,
            LocatorContext locator) : IRestrictedStateKeyResolver
        {
            public bool TryGetCurrentWriteKey(AuthorizedStateAccess authority, out RestrictedStateKey? key)
            { key = null; return false; }
            public bool TryGetApprovedReadKey(AuthorizedStateAccess authority, string keyId, long expiry, out RestrictedStateKey? key)
            {
                key = null;
                Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
                try
                {
                    if (!ReferenceEquals(authority, state) || !locator.TryCopyApprovedReadKey(access, keyId, material)) return false;
                    key = new RestrictedStateKey(keyId, material);
                    return true;
                }
                finally { CryptographicOperations.ZeroMemory(material); }
            }
        }

        internal static async Task VerifyResetWithPreviousStickyAsync()
        {
            var world = new CapacityResetWorld();
            var initial = await world.RunAsync(0);
            Assert.Equal(ActionHostStateDisposition.Accepted, initial.Completion.Summary.StateDisposition);
            Assert.Equal(1, world.Publications);
            var reset = await world.RunAsync(1, reset: true, fresh: true);
            Assert.NotNull(reset.Provider.Outcome);
            Assert.True(reset.Provider.Outcome.Succeeded);
            Assert.NotEqual(initial.Provider.Request!.SessionId, reset.Provider.Request!.SessionId);
            Assert.Null(reset.Provider.Request.Continuation);
            Assert.Equal(ActionHostStateDisposition.Accepted, reset.Completion.Summary.StateDisposition);
        }

        internal static async Task VerifyCapacityResetAsync()
        {
            var world = new CapacityResetWorld();
            CapacityResetInvocation? previous = null;
            var phase = 0;
            for (; phase < AgentSessionFormat.MaximumCompletedRuns + 1; phase++)
            {
                var before = previous is null ? [] : ReadAcceptedStateRecords(world.Store, previous.Launch, world.Time)
                    .Acceptances.Select(a => a.Header.ObjectIdentity).Order().ToArray();
                var attempt = await world.RunAsync(phase);
                if (attempt.Completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted)
                {
                    previous = attempt;
                    continue;
                }
                Assert.NotNull(previous);
                Assert.NotNull(attempt.Provider.Outcome);
                Assert.True(attempt.Provider.Outcome.Diagnostic?.Code == AgentFailureCodes.ResponseInvalid,
                    $"phase={phase};status={attempt.Completion.Status};agentSucceeded={attempt.Provider.Outcome.Succeeded};agentCode={attempt.Provider.Outcome.Diagnostic?.Code ?? "none"};acceptedReceipts={before.Length};writes={world.Publications}");
                Assert.Equal(1, attempt.Provider.Outcome.Diagnostic!.ModelCalls);
                Assert.True(attempt.Provider.Request!.InitialMessages.Length + 1 + 8 > AgentLimits.Messages,
                    "the actual next tool response must exceed the production message bound");
                Assert.Equal(before, ReadAcceptedStateRecords(world.Store, attempt.Launch, world.Time)
                    .Acceptances.Select(a => a.Header.ObjectIdentity).Order());
                break;
            }
            Assert.InRange(phase, 1, AgentSessionFormat.MaximumCompletedRuns);
            var capacityPhase = phase;
            AssertSessionMarkers(world, previous!, ResetWorkload.OldFact, ResetWorkload.OldReasoning);
            var previousEpoch = ReadAcceptedStateRecords(world.Store, previous!.Launch, world.Time).Acceptances[0].Header.Epoch;
            var oldSession = previous!.Provider.Request!.SessionId;
            var restored = await world.RunAsync(++phase, failProvider: true);
            Assert.Equal(oldSession, restored.Provider.Request!.SessionId);
            Assert.NotNull(restored.Provider.Request.Continuation);
            var reset = await world.RunAsync(++phase, reset: true, fresh: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, reset.Completion.Summary.StateDisposition);
            Assert.NotEqual(oldSession, reset.Provider.Request!.SessionId);
            Assert.Null(reset.Provider.Request.Continuation);
            var resetHeads = new List<(StateControlHeaderV1 Header, LineageHeadV1 Head)>();
            var resetRecords = ReadAcceptedStateRecords(world.Store, reset.Launch, world.Time, (header, payload, _) =>
            {
                if (header.ObjectClass != StateObjectClass.LineageHead) return;
                Assert.True(LineageHeadCodec.TryDecode(payload, out var head));
                resetHeads.Add((header, head!));
            });
            Assert.NotEmpty(resetHeads);
            var resetHead = resetHeads.MaxBy(value => value.Head.Ordinal);
            Assert.Equal(LineageTransitionKind.Reset, resetHead.Head.Transition);
            Assert.NotEqual(previousEpoch, resetHead.Header.Epoch);
            var resetReceipt = Assert.Single(resetRecords.Acceptances).Receipt;
            Assert.Equal(0, resetRecords.Generations.First(value =>
                value.Header.ObjectIdentity == resetReceipt.OriginalCandidateObjectIdentity).Generation.Generation);
            foreach (var body in reset.Provider.Requests)
            {
                var text = Encoding.UTF8.GetString(body);
                Assert.DoesNotContain(ResetWorkload.OldFact, text);
                Assert.DoesNotContain(ResetWorkload.OldReasoning, text);
            }
            AssertSessionMarkers(world, reset, ResetWorkload.FreshFact, ResetWorkload.FreshReasoning, ResetWorkload.OldFact, ResetWorkload.OldReasoning);
            var continued = await world.RunAsync(++phase, fresh: true);
            Assert.Equal(ActionHostStateDisposition.Accepted, continued.Completion.Summary.StateDisposition);
            Assert.Equal(reset.Provider.Request.SessionId, continued.Provider.Request!.SessionId);
            Assert.NotNull(continued.Provider.Request.Continuation);
            var first = Encoding.UTF8.GetString(continued.Provider.Requests[0]);
            Assert.Contains(ResetWorkload.FreshReasoning, first);
            Assert.DoesNotContain(ResetWorkload.OldReasoning, first);
            Assert.DoesNotContain(ResetWorkload.OldFact, first);
            Assert.Equal(reset.Provider.Request.StablePlan, continued.Provider.Request.StablePlan with { PriorSessionSha256 = null });
            AssertSessionMarkers(world, continued, ResetWorkload.FreshFact, ResetWorkload.FreshReasoning,
                ResetWorkload.OldFact, ResetWorkload.OldReasoning);
            Console.WriteLine(ResetCapacityReport.Write(world.SeedCorpusSha256, capacityPhase,
                AgentLimits.Messages, world.Observations.ToArray()));
        }

        internal static async Task VerifyCancelledResetAsync()
        {
            var world = new CapacityResetWorld();
            var initial = await world.RunAsync(0);
            Assert.Equal(ActionHostStateDisposition.Accepted, initial.Completion.Summary.StateDisposition);
            var before = world.Store.Objects;
            var uploads = world.Store.UploadCalls;
            var deletes = world.Store.DeleteCalls;
            var writes = world.Publications;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var reset = await world.RunAsync(1, reset: true, token: cancellation.Token);
            Assert.Equal(0, reset.Provider.Creates);
            Assert.NotEqual(ActionHostStateDisposition.Accepted, reset.Completion.Summary.StateDisposition);
            Assert.True(before.SequenceEqual(world.Store.Objects), "pre-admission cancellation changed artifact metadata");
            Assert.Equal(uploads, world.Store.UploadCalls);
            Assert.Equal(deletes, world.Store.DeleteCalls);
            Assert.Equal(writes, world.Publications);
        }

        internal static async Task VerifyDistinctResetAsync()
        {
            var world = new CapacityResetWorld();
            await world.RunAsync(0, failProvider: true);
            var first = await world.RunAsync(1, reset: true, failProvider: true);
            var next = await world.RunAsync(2, reset: true, failProvider: true);
            Assert.NotNull(first.Provider.Request);
            Assert.NotNull(next.Provider.Request);
            Assert.NotEqual(first.Provider.Request.SessionId, next.Provider.Request.SessionId);
            Assert.Null(next.Provider.Request.Continuation);
            var nextAttempt = await world.RunAsync(2, reset: true, failProvider: true, attempt: 2);
            Assert.NotEqual(next.Provider.Request.SessionId, nextAttempt.Provider.Request!.SessionId);
            Assert.Equal(0, world.Publications);
        }

        internal static async Task VerifyEmptyResetReentryAsync()
        {
            var world = new CapacityResetWorld();
            Assert.Equal(ActionHostStatus.Reviewed, (await world.RunAsync(0)).Completion.Status);
            var reset = await world.RunAsync(1, reset: true, fresh: true, failProvider: true);
            Assert.NotNull(reset.Provider.Request);
            Assert.NotEqual(ActionHostStatus.Reviewed, reset.Completion.Status);
            var retried = await world.RunAsync(1, reset: true, fresh: true, failProvider: true);
            Assert.Equal(reset.Provider.Request.SessionId, retried.Provider.Request!.SessionId);
        }

        internal static async Task VerifyCompletedResetReentryAsync()
        {
            var world = new CapacityResetWorld();
            var initial = await world.RunAsync(0, failProvider: true);
            Assert.NotNull(initial.Provider.Request);
            var reset = await world.RunAsync(1, reset: true, fresh: true);
            Assert.Equal(ActionHostStatus.Reviewed, reset.Completion.Status);
            Assert.NotEqual(initial.Provider.Request!.SessionId, reset.Provider.Request!.SessionId);
            var accepted = ReadAcceptedStateRecords(world.Store, reset.Launch, world.Time);
            var writes = world.Publications;
            var retried = await world.RunAsync(1, reset: true, fresh: true);
            Assert.Equal(ActionHostStatus.Reviewed, retried.Completion.Status);
            Assert.Equal(0, retried.Provider.Creates);
            Assert.Equal(writes, world.Publications);
            var after = ReadAcceptedStateRecords(world.Store, retried.Launch, world.Time);
            Assert.Equal(accepted.Acceptances.Select(a => a.Header.ObjectIdentity).Order(),
                after.Acceptances.Select(a => a.Header.ObjectIdentity).Order());
        }

        private sealed class CapacityResetWorld
        {
            private readonly AdmittedReplayFixture fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "growth")).Fixture);
            private readonly IncrementalRemote remote = new();
            internal FrozenLocatorTimeProvider Time { get; } = new(LocatorTestData.Now);
            internal ScriptedLocatorStore Store { get; } = FullPathStore(TrustedV2Scenario(12480, 1, new string('a', 64)).Launch);
            internal int Publications => remote.StickyWrites.Count;
            internal string SeedCorpusSha256 => fixture.CorpusSha256;
            internal List<ResetCapacityObservation> Observations { get; } = [];

            internal async Task<CapacityResetInvocation> RunAsync(int phase, bool reset = false, bool fresh = false,
                CancellationToken token = default, bool failProvider = false, int attempt = 1, bool samePublicFact = false)
            {
                var scenario = TrustedV2Scenario(12480 + phase, attempt, new string('a', 64));
                if (fresh) scenario.Scenario.Transport.PullRequest = scenario.Scenario.Transport.PullRequest with { HeadSha = new string('f', 40) };
                var launch = WithResetMode(scenario.Launch, reset);
                var github = new FullPathGitHubFactory(scenario.Scenario.Transport.PullRequest, withInlineFile: true,
                    workflowSha: TrustedProofPayloadBuildIdentity.SourceCommit,
                    fileBytes: Encoding.UTF8.GetBytes((fresh && !samePublicFact ? ResetWorkload.FreshFact : ResetWorkload.OldFact) + "\n"));
                Store.ProducingRunIdentity = launch.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                Store.ProducingRunAttempt = launch.RunAttempt;
                var script = ResetWorkload.Script(fixture, phase, fresh);
                var provider = new CapacityResetProvider(script, failProvider);
                var staging = StagingPath();
                var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
                    scenario.Scenario.EventReader, scenario.Scenario.Factory, github, github,
                    new FullPathStateDependencies(Store, github), remote, provider, Time, () => staging,
                    workflowAdmission: TrustedProofV2WorkflowAdmission.Instance)).RunAsync(launch, token);
                Assert.False(Directory.Exists(staging));
                if (provider.Request is { } request)
                    Observations.Add(new(phase, reset, ResetWorkload.Digest(script), request.StablePlan.PolicySha256,
                        request.StablePlan.LimitsSha256, request.StablePlan.ToolsetSha256,
                        completion.Summary.StateDisposition.ToString(), provider.Outcome?.Diagnostic?.Code,
                        provider.Creates, provider.Outcome?.Diagnostic?.ModelCalls, Publications));
                return new(completion, launch, provider);
            }
        }

        private sealed record CapacityResetInvocation(ActionHostCompletion Completion,
            ActionHostLaunchContract Launch, CapacityResetProvider Provider);

        private static ActionHostLaunchContract WithResetMode(ActionHostLaunchContract launch, bool reset)
        {
            Assert.True(ActionHostInputs.TryCreate(launch.Inputs.GitHubToken, launch.Inputs.ProviderApiKey,
                launch.Inputs.StateKey, launch.Inputs.PreviousStateKey, launch.Inputs.ConfigPath,
                launch.Inputs.PullRequestNumber, reset ? ActionHostStateMode.Reset : ActionHostStateMode.Auto, out var inputs));
            Assert.True(ActionHostLaunchContract.TryCreate(inputs, launch.EventJsonPath, launch.EventJsonSha256,
                launch.RepositoryName, launch.RepositoryId, launch.RunId, launch.RunAttempt, launch.WorkflowPath,
                launch.WorkflowRef, launch.WorkflowSha, launch.ActionSourceSha, launch.PayloadSha256, launch.BuildDiscriminator,
                launch.Cancellation, launch.ArtifactBridgeEndpoint, out var value));
            return value!;
        }

        private sealed class CapacityResetProvider(ReplayScript script, bool fail = false) : IActionHostProviderRunnerFactory
        {
            internal int Creates { get; private set; }
            internal AgentRunRequest? Request { get; private set; }
            internal AgentRunOutcome? Outcome { get; private set; }
            internal ImmutableArray<byte[]> Requests { get; private set; } = [];
            public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
                ReviewedSnapshot snapshot, TimeProvider timeProvider)
            {
                Creates++;
                return new Runner(this, script, snapshot, timeProvider, fail);
            }
            private sealed class Runner(CapacityResetProvider owner, ReplayScript script, ReviewedSnapshot snapshot,
                TimeProvider time, bool fail) : IActionHostProviderRunner
            {
                public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken token)
                {
                    owner.Request = request;
                    if (fail) return owner.Outcome = AgentRunOutcome.Failure(AgentFailureCodes.ChatFailed, 0, 0, []);
                    using var transport = new ReplayTransport(script, ReplayFault.None);
                    var client = DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                        DeepSeekAdapterContext.Adapter, request.SessionId), transport);
                    var outcome = await new AgentLoop(client, new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), time)
                        .RunAsync(request, token);
                    owner.Outcome = outcome;
                    owner.Requests = transport.Requests.ToImmutableArray();
                    return outcome;
                }
                public void Dispose() { }
            }
        }
    }
}
