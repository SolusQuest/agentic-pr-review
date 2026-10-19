using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

// Separate R7 acceptance proof. The old bootstrap/continuation receipts remain regressions.
internal static class SessionCapacityProof
{
    private const int Generations = 20;
    private const string Session = "r7-session-capacity";
    private static readonly string Reasoning = "r7-preserved-中文-" + new string('\u0001', 3072);

    internal static async Task<int> RunAsync(ProofCommand command)
    {
        try { return await RunCoreAsync(command); }
        catch (InvalidOperationException error) when (error.Message.StartsWith("r7-local-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(ProofCommand command)
    {
        var trusted = ProofScenario.Trusted() with { ProviderId = DeepSeekAdapterContext.Provider,
            ModelId = DeepSeekAdapterContext.Model, AdapterId = DeepSeekAdapterContext.Adapter };
        Require(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized), "materialize");
        var plan = materialized!.StablePlan;
        var scope = new RestrictedStateScope(plan.RepositoryId, plan.WorkflowIdentity, plan.ReviewTarget, Session,
            plan.ProviderId, plan.ModelId, plan.AdapterId, plan.PolicySha256, plan.LimitsSha256, plan.ToolsetSha256, plan.BuildId);
        Require(AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var access).Action == StateAction.Authorized,
            "authorize");
        var root = Path.Join(command.Root, "r7-local-state");
        Directory.CreateDirectory(root);
        var identity = ProofScenario.BootstrapIdentity();
        var diff = ProofScenario.BootstrapDiffSource(identity);
        var snapshot = new ReviewedSnapshot(identity, ProofPaths.RepositoryRoot(command), [ProofScenario.ReviewedPath],
            [ProofScenario.BootstrapChangedFile(diff)], [diff]);
        var service = new RestrictedStateService(new LocalRestrictedStateStore(root),
            new SyntheticStateKeyResolver("r7-capacity"), new AgentSessionRestrictedStateAdmission(), () => ProofScenario.Now);
        var codec = DeepSeekReasoningContinuationCodec.Instance;
        AcceptedLineage? lineage = null;
        AgentSessionArtifact? artifact = null;
        var maximumMessages = 0;
        var verifiedReasoning = 0;
        var sends = 0;
        RestrictedStateSessionAdmissionContext Context(long generation, string? previous, string text) =>
            new(identity.BaseSha, identity.HeadSha, generation, previous,
                new AgentSessionStateAdmissionContext(trusted, Session, identity, ProofScenario.User(text),
                    AgentSessionHeadTransition.SameHead, codec, null));
        for (var generation = 0; generation < Generations; generation++)
        {
            AgentRunRequest run;
            AgentSessionPredecessor? predecessor = null;
            if (lineage is null)
                run = new(identity, plan, Session, [.. materialized.ControlMessages, ProofScenario.User("Review this synthetic file.")]);
            else
            {
                var restored = await service.RestoreAsync(access!, new(RestrictedStateLocatorFamily.Current,
                    RestrictedStateRestoreIntent.Explicit, lineage,
                    Context(lineage.Generation, lineage.ExpectedPredecessorEnvelopeSha256, "Continue the synthetic review.")), default);
                Require(restored.Result.Action == StateAction.Restored && restored.Session?.Value is not null, "restore");
                var admitted = restored.Session!.Value;
                Require(admitted.Artifact.Plaintext.AsSpan().SequenceEqual(artifact!.Plaintext), "exact-plaintext");
                run = admitted.RunRequest;
                predecessor = new(admitted.Artifact.Plaintext, lineage.SessionSha256, lineage.EnvelopeSha256,
                    lineage.Generation, identity.BaseSha, identity.HeadSha, lineage.ExpectedPredecessorEnvelopeSha256);
            }
            using var transport = new HistoricalCapacityChat(generation, plan);
            var loop = new AgentLoop(transport,
                new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), new SyntheticTimeProvider(ProofScenario.Now));
            var outcome = await loop.RunAsync(run, default);
            Require(outcome.CompletedSessionEligible && transport.Sends == 6, outcome.Diagnostic?.Code ?? "agent");
            var built = AgentSessionBuilder.Build(new(run, outcome, trusted, run.InitialMessages.Length - 1, codec, predecessor,
                AgentSessionHeadTransition.SameHead));
            Require(built.Succeeded && built.Artifact is not null, built.FailureCode ?? "session");
            artifact = built.Artifact!;
            var context = Context(generation, lineage?.EnvelopeSha256, "Next synthetic review.");
            var prepared = await service.PrepareAsync(access!, new(lineage, artifact.Plaintext, context), default);
            Require(prepared.Result.Action == StateAction.Prepared && prepared.Receipt is not null, "prepare-" + prepared.Result.Code);
            var accepted = await service.AcceptAsync(access!, lineage, prepared.Receipt!, context, default);
            Require(accepted.Action == StateAction.Accepted && accepted.Generation == generation, "accept-" + accepted.Code);
            lineage = new(scope, generation, accepted.SessionSha256!, accepted.EnvelopeSha256!, lineage?.EnvelopeSha256,
                ProofScenario.Now, ProofScenario.Now + RestrictedStateFormat.MaximumRetentionSeconds, true);
            maximumMessages = Math.Max(maximumMessages, transport.MaximumMessages);
            verifiedReasoning += transport.VerifiedReasoning;
            sends += transport.Sends;
        }
        var records = artifact!.Document.CompletedRuns.Sum(r => r.Records.Length + r.Continuation.Items.Length);
        Require(maximumMessages > 64 && records > 256 && artifact.Plaintext.Length > 1_048_576, "meaningful-growth");
        var failureContext = Context(lineage!.Generation, lineage.ExpectedPredecessorEnvelopeSha256, "Continue before the generic input limit stimulus.");
        var failedRestore = await service.RestoreAsync(access!, new(RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit, lineage, failureContext), default);
        Require(failedRestore.Session?.Value is not null, "follow-on-restore");
        using var refused = new HistoricalCapacityChat(Generations, plan);
        var failedLoop = new AgentLoop(refused,
            new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), new SyntheticTimeProvider(ProofScenario.Now));
        var failed = await failedLoop.RunAsync(failedRestore.Session!.Value.RunRequest with
        { InitialMessages = [.. failedRestore.Session.Value.RunRequest.InitialMessages,
            new("user", [new ProjectTextContent(new string('x', AgentLimits.ContentBytes + 1))])] }, default);
        Require(!failed.CompletedSessionEligible && failed.Diagnostic?.Code == AgentFailureCodes.ResponseInvalid && refused.Sends == 0,
            "generic-input-refusal-" + failed.Diagnostic?.Code);
        var unchanged = await service.RestoreAsync(access!, new(RestrictedStateLocatorFamily.Current, RestrictedStateRestoreIntent.Explicit,
            lineage, Context(lineage.Generation, lineage.ExpectedPredecessorEnvelopeSha256, "Read accepted predecessor.")), default);
        Require(unchanged.Session?.Value.Artifact.SessionSha256 == artifact.SessionSha256, "predecessor-preserved");
        // Exercise authentication of the enlarged accepted ciphertext through restore, not only the codec.
        var opaque = new RestrictedStateOpaqueSnapshotStore(new LocalRestrictedStateStore(root),
            new SyntheticStateKeyResolver("r7-capacity"));
        var beforeTamper = await opaque.ReadAsync(access!, default);
        Require(beforeTamper.Succeeded && beforeTamper.Version is not null, "tamper-read");
        var candidate = beforeTamper.Snapshot!.Accepted[0];
        var corrupted = candidate.Envelope.ToArray();
        corrupted[^1] ^= 1;
        var digest = RestrictedStateEnvelope.EnvelopeSha256(corrupted);
        var injected = await opaque.CompareExchangeAsync(access!, beforeTamper.Version!, beforeTamper.Snapshot with
        {
            Accepted = beforeTamper.Snapshot.Accepted.SetItem(0, candidate with
            {
                Envelope = corrupted, EnvelopeSha256 = digest,
                ObjectIdentity = RestrictedStateEnvelope.ObjectIdentity(candidate.Binding, candidate.SessionSha256, digest),
            }),
        }, default);
        Require(injected.Committed, "tamper-inject");
        var rejected = await service.RestoreAsync(access!, new(RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit, lineage,
            Context(lineage.Generation, lineage.ExpectedPredecessorEnvelopeSha256, "Read accepted predecessor.")), default);
        Require(rejected.Result.Action == StateAction.Failed && rejected.Session is null &&
            rejected.Result.Code == RestrictedStateCodes.AuthenticationFailed, "tamper-rejection");
        var afterTamper = await opaque.ReadAsync(access!, default);
        Require(afterTamper.Version?.Sha256 == injected.Version?.Sha256, "tamper-no-write");
        var repaired = await opaque.CompareExchangeAsync(access!, afterTamper.Version!, beforeTamper.Snapshot, default);
        Require(repaired.Committed, "tamper-fixture-restore");
        var restoredAgain = await service.RestoreAsync(access!, new(RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit, lineage,
            Context(lineage.Generation, lineage.ExpectedPredecessorEnvelopeSha256, "Read accepted predecessor.")), default);
        Require(restoredAgain.Session?.Value.Artifact.SessionSha256 == artifact.SessionSha256, "tamper-original-preserved");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "r7-historical-typed-local-session-capacity-v1");
            writer.WriteNumber("accepted_generations", Generations); writer.WriteNumber("messages", maximumMessages);
            writer.WriteNumber("combined_records", records); writer.WriteNumber("plaintext_bytes", artifact.Plaintext.Length);
            writer.WriteString("observation_domain", "apr-historical-authored-typed-chat-v1");
            writer.WriteNumber("synthetic_calls", sends); writer.WriteNumber("verified_reasoning_associations", verifiedReasoning);
            writer.WriteNumber("refused_script_calls", refused.Sends); writer.WriteBoolean("predecessor_preserved", true);
            writer.WriteBoolean("large_ciphertext_tamper_rejected", true);
            writer.WriteString("session_sha256", artifact.SessionSha256); writer.WriteEndObject();
        }
        ProofFiles.WriteNew(command.Output, stream.ToArray());
        Console.WriteLine("APR_R7_LOCAL_SESSION_CAPACITY_OK");
        return 0;
    }

    private static void Require(bool condition, string code)
    { if (!condition) throw new InvalidOperationException("r7-local-" + code); }

    private sealed class HistoricalCapacityChat(int generation, StableAgentPlan plan) : IProjectChatClient, IDisposable
    {
        internal int Sends { get; private set; }
        internal int MaximumMessages { get; private set; }
        internal int VerifiedReasoning { get; private set; }
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            MaximumMessages = Math.Max(MaximumMessages, request.Messages.Length);
            var assistants = request.Messages.Select((message, position) => (message, position))
                .Where(pair => pair.message.Role == "assistant").ToArray();
            Require(assistants.Length == generation * 6 + Sends, "history-count");
            for (var index = 0; index < assistants.Length; index++)
            {
                var (message, position) = assistants[index];
                Require(message.Contents.OfType<ProjectToolCallContent>().ToArray() is [var call] &&
                    call.CallId == $"r7-{index / 6}-{index % 6}", "reasoning-association");
                Require(request.Continuation?.Items.Count(item => item.MessagePosition == position && item.ContentPosition == 0 &&
                    item.Readable == Reasoning && item.Opaque == "" && item.AssociatedCallId is null &&
                    item.Framing == DeepSeekReasoningContinuationCodec.FramingName) == 1, "continuation-association");
                VerifiedReasoning++;
            }
            Require(request.Accounting!.TryBeginDispatch(), "dispatch");
            request.Accounting.RecordUsage(ProviderUsageObservation.Create(10, 10, 0, 10, 20));
            var ordinal = Sends++;
            var next = request.Messages.Length;
            return Task.FromResult(new ProjectChatResponse(new("assistant", [new ProjectReasoningContent(Reasoning, "",
                DeepSeekReasoningContinuationCodec.FramingName, null, next, 0),
                new ProjectToolCallContent($"r7-{generation}-{ordinal}", ordinal == 5 ? "finish_review" : "read_file",
                    ordinal == 5 ? "{\"summary\":\"Synthetic review completed.\",\"findings\":[]}" :
                    "{\"path\":\"reviewed/fact.txt\",\"start_line\":1,\"line_count\":1}")]),
                new(10, 10, new(plan.ProviderId, plan.ModelId, plan.ModelId, 0, 10)), CapturedResponseBodyBytes: 0,
                Continuation: new(plan.ProviderId, plan.ModelId, plan.AdapterId, Session,
                    [new(Reasoning, "", DeepSeekReasoningContinuationCodec.FramingName, null, next, 0)])));
        }
        public void Dispose() { }
    }
}
