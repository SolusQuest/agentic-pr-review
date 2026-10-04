using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

internal sealed class CapacityState : IDisposable
{
    internal const long Now = 1_800_000_000;
    internal static readonly ReviewedIdentity Identity = new("r7-capacity-repository", 358, new('a', 40), new('b', 40));
    private readonly Keys keys;
    internal CapacityState(CapacityInput input, bool reset = false)
    {
        Session = reset ? "r7-capacity-reset" : input.Fresh ? "r7-capacity-reset" : input.Case.ModelCallAuthority == 128
            ? "r7-capacity-configured" : "r7-capacity-default";
        // The default case supplies no override. Configured128 is additionally bound by the
        // gate to the production trusted-config regression, rather than pretending this is configuration admission.
        Authority = input.Case.ModelCallAuthority == 128
            ? new(DeepSeekAdapterContext.Adapter, AgentLimitProfile.Current, ModelCalls: 128) : null;
        Trusted = new(Identity.RepositoryId, Identity.ReviewTarget, "r7-capacity-workflow", "{}"u8.ToArray(),
            "r7-capacity-proof", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter, Authority);
        CapacitySpec.Require(AgentStableRequestMaterializer.TryMaterialize(Trusted, null, out var stable), "materialize");
        Stable = stable!;
        var plan = stable!.StablePlan;
        Scope = new(plan.RepositoryId, plan.WorkflowIdentity, plan.ReviewTarget, Session, plan.ProviderId, plan.ModelId,
            plan.AdapterId, plan.PolicySha256, plan.LimitsSha256, plan.ToolsetSha256, plan.BuildId);
        CapacitySpec.Require(AuthorizedStateAccess.Authorize(new(Scope, Scope, true, true, false), out var access).Action == StateAction.Authorized,
            "authorize");
        Access = access!;
        keys = new(input.Key);
        Root = Path.Combine(input.Root, "state");
        Directory.CreateDirectory(Root);
        Store = new LocalRestrictedStateStore(Root);
        Service = new(Store, keys, new AgentSessionRestrictedStateAdmission(), () => Now);
    }
    internal string Root { get; }
    internal string Session { get; }
    internal AgentLimitAuthority? Authority { get; }
    internal AgentSessionTrustedRequest Trusted { get; }
    internal AgentSessionMaterializedStableRequest Stable { get; }
    internal RestrictedStateScope Scope { get; }
    internal AuthorizedStateAccess Access { get; }
    internal LocalRestrictedStateStore Store { get; }
    internal RestrictedStateService Service { get; }
    internal static ProjectChatMessage User(bool fresh, string mode = "success") =>
        new("user", [new ProjectTextContent(mode == "context" ? new string('x', 1_000_000) :
            (fresh ? CapacitySpec.FreshUser : CapacitySpec.OldUser) + " Review this synthetic snapshot.")]);
    internal RestrictedStateSessionAdmissionContext Context(long generation, string? predecessor, bool fresh, string mode = "success") =>
        new(Identity.BaseSha, Identity.HeadSha, generation, predecessor,
            new AgentSessionStateAdmissionContext(Trusted, Session, Identity, User(fresh, mode),
                AgentSessionHeadTransition.SameHead, DeepSeekReasoningContinuationCodec.Instance, null));
    internal async Task<AgentSessionStateAdmittedValue> RestoreAsync(AcceptedLineage lineage, bool fresh, string mode = "success")
    {
        var restored = await Service.RestoreAsync(Access, new(RestrictedStateLocatorFamily.Current,
            RestrictedStateRestoreIntent.Explicit, lineage,
            Context(lineage.Generation, lineage.ExpectedPredecessorEnvelopeSha256, fresh, mode)), default);
        CapacitySpec.Require(restored.Result.Action == StateAction.Restored && restored.Session?.Value is AgentSessionStateAdmittedValue,
            "restore_" + restored.Result.Code);
        var value = (AgentSessionStateAdmittedValue)restored.Session!.Value!;
        CapacitySpec.Require(value.Artifact.SessionSha256 == lineage.SessionSha256, "restore_session_identity");
        return value;
    }
    internal async Task<AcceptedLineage> AcceptAsync(AgentSessionArtifact artifact, AcceptedLineage? previous, bool fresh)
    {
        var generation = previous is null ? 0 : previous.Generation + 1;
        var context = Context(generation, previous?.EnvelopeSha256, fresh);
        var prepared = await Service.PrepareAsync(Access, new(previous, artifact.Plaintext, context), default);
        CapacitySpec.Require(prepared.Result.Action == StateAction.Prepared && prepared.Receipt is not null, "prepare_" + prepared.Result.Code);
        var accepted = await Service.AcceptAsync(Access, previous, prepared.Receipt!, context, default);
        CapacitySpec.Require(accepted.Action == StateAction.Accepted && accepted.Generation == generation, "accept_" + accepted.Code);
        return new(Scope, generation, accepted.SessionSha256!, accepted.EnvelopeSha256!, previous?.EnvelopeSha256,
            Now, Now + RestrictedStateFormat.MaximumRetentionSeconds, true);
    }
    internal async Task<int> EnvelopeBytesAsync()
    {
        var snapshot = await new RestrictedStateOpaqueSnapshotStore(Store, keys).ReadAsync(Access, default);
        CapacitySpec.Require(snapshot.Succeeded && snapshot.Snapshot is not null, "opaque_read");
        return snapshot.Snapshot!.Accepted.Select(candidate => candidate.Envelope.Length).DefaultIfEmpty().Max();
    }
    internal (string Hash, long Bytes, bool Private) Inventory()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long bytes = 0; var privateData = true;
        foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(Path.GetRelativePath(Root, path));
            var content = File.ReadAllBytes(path);
            hash.AppendData(name); hash.AppendData(SHA256.HashData(content)); bytes += content.Length;
            privateData &= CapacitySpec.IsPrivate(name) && CapacitySpec.IsPrivate(content);
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), bytes, privateData);
    }
    public void Dispose() => keys.Dispose();
    private sealed class Keys(byte[] key) : IRestrictedStateKeyResolver, IDisposable
    {
        private readonly byte[] bytes = key.Length == 32 ? key.ToArray() : throw new InvalidOperationException("r7_capacity_key");
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess access, out RestrictedStateKey? result)
        { result = new("r7-synthetic", bytes); return true; }
        public bool TryGetApprovedReadKey(AuthorizedStateAccess access, string id, long expiry, out RestrictedStateKey? result)
        { result = id == "r7-synthetic" ? new("r7-synthetic", bytes) : null; return result is not null; }
        public void Dispose() => CryptographicOperations.ZeroMemory(bytes);
    }
}

internal static class CapacityChild
{
    private static readonly string StartupId = Guid.NewGuid().ToString("N");
    internal static async Task<CapacityReply> RunAsync(CapacityInput input)
    {
        var measured = new CapacityMeasurement();
        CapacitySpec.Require(CapacitySpec.Cases.Contains(input.Case) && input.History.Length <= 3 && input.Key.Length == 32 &&
            Directory.Exists(input.Root) && Path.GetFullPath(input.Root) == Directory.GetCurrentDirectory(), "child_input");
        using var state = new CapacityState(input);
        var before = state.Inventory();
        AgentSessionArtifact? prior = null;
        AgentRunRequest request;
        AgentSessionPredecessor? predecessor = null;
        var lineage = input.Lineage;
        if (lineage is null)
            request = new(CapacityState.Identity, state.Stable.StablePlan, state.Session,
                [.. state.Stable.ControlMessages, CapacityState.User(input.Fresh)]);
        else
        {
            var restored = await state.RestoreAsync(lineage, input.Fresh, input.Case.Mode);
            prior = restored.Artifact;
            CapacitySpec.Require(input.PlaintextSha256 is null ||
                Convert.ToHexStringLower(SHA256.HashData(prior.Plaintext)) == input.PlaintextSha256, "restore_exact");
            request = restored.RunRequest;
            predecessor = new(prior.Plaintext, lineage.SessionSha256, lineage.EnvelopeSha256, lineage.Generation,
                CapacityState.Identity.BaseSha, CapacityState.Identity.HeadSha, lineage.ExpectedPredecessorEnvelopeSha256);
        }
        if (input.Case.Mode is "role" or "association" or "policy" or "scope")
        {
            CapacitySpec.Require(prior is not null && prior.Document.CompletedRuns[0].Records.Length > 256, "negative_large_history");
            var context = state.Context(lineage!.Generation,
                lineage.ExpectedPredecessorEnvelopeSha256, false).SessionContext;
            if (input.Case.Mode is "role" or "association")
            {
                var first = prior!.Document.CompletedRuns[0];
                var index = input.Case.Mode == "role"
                    ? Array.FindIndex(first.Records.ToArray(), record => record is AgentSessionReviewContextRecord)
                    : Array.FindIndex(first.Records.ToArray(), record => record is AgentSessionToolResultRecord);
                var records = first.Records.SetItem(index, input.Case.Mode == "role"
                    ? ((AgentSessionReviewContextRecord)first.Records[index]) with { Role = "assistant" }
                    : ((AgentSessionToolResultRecord)first.Records[index]) with { CallId = "absent_call" });
                var document = prior.Document with { CompletedRuns = prior.Document.CompletedRuns.SetItem(0, first with { Records = records }) };
                CapacitySpec.Require(AgentSessionCodec.TryWrite(document, out var malformed, out _), "negative_encoding");
                CapacitySpec.Require(!AgentSessionStateBoundary.Admit(malformed!.Plaintext, context).Succeeded, "negative_admission");
            }
            else
            {
                var changed = input.Case.Mode == "policy"
                    ? context with { TrustedRequest = state.Trusted with { TrustedPolicyBytes = "{\"changed\":true}"u8.ToArray() } }
                    : context with { SessionId = "different-selected-session" };
                var refused = await state.Service.RestoreAsync(state.Access, new(RestrictedStateLocatorFamily.Current,
                    RestrictedStateRestoreIntent.Explicit, lineage,
                    state.Context(lineage!.Generation, lineage.ExpectedPredecessorEnvelopeSha256, false) with { SessionContext = changed }), default);
                CapacitySpec.Require(refused.Result.Action == StateAction.Failed && refused.Session is null, "selected_current_rejection");
            }
            var after = state.Inventory();
            CapacitySpec.Require(before.Hash == after.Hash, "negative_preservation");
            await state.RestoreAsync(lineage, false);
            return new(Receipt("admission_rejected", 0, 0, null, prior!, true, true, false, false, false, after,
                await state.EnvelopeBytesAsync()), lineage,
                Convert.ToHexStringLower(SHA256.HashData(prior!.Plaintext)));
        }
        var reset = input.Case.Mode == "reset";
        CapacityState? freshState = null;
        try
        {
            if (reset)
            {
                CapacitySpec.Require(lineage is not null && (await state.Service.ResetAsync(state.Access, default)).Action == StateAction.Reset,
                    "reset_action");
                var gone = await state.Service.RestoreAsync(state.Access, new(RestrictedStateLocatorFamily.Current,
                    RestrictedStateRestoreIntent.Explicit, lineage,
                    state.Context(lineage!.Generation, lineage.ExpectedPredecessorEnvelopeSha256, false)), default);
                CapacitySpec.Require(gone.Session is null && gone.Result.Action == StateAction.Failed, "reset_old_absent");
                freshState = new(input, reset: true);
                request = new(CapacityState.Identity, freshState.Stable.StablePlan, freshState.Session,
                    [.. freshState.Stable.ControlMessages, CapacityState.User(true)]);
                lineage = null; predecessor = null;
            }
            var active = freshState ?? state;
            var fresh = reset || input.Fresh;
            var snapshot = CapacitySnapshot.Create(input.Root, CapacityState.Identity, fresh);
            var clock = new CapacityClock();
            using var transport = new CapacityTransport(input.Case, reset ? [] : input.History, clock, fresh);
            var client = new CapacityObservedClient(DeepSeekChatBackend.CreateClient(new(DeepSeekAdapterContext.Provider,
                DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter, active.Session), transport), measured);
            var outcome = await new AgentLoop(client, new SnapshotToolExecutor(snapshot.Snapshot, snapshot.Files), clock, active.Authority)
                .RunAsync(request, default);
            var tools = outcome.Events.OfType<AgentToolCallEvent>().Count();
            var code = outcome.Diagnostic?.Code ?? "completed";
            if (transport.OracleFailure is { } oracleFailure) throw new InvalidOperationException(oracleFailure);
            CapacitySpec.Require(code == CapacitySpec.ExpectedCode(input.Case.Mode) && transport.Sends == input.Case.Calls,
                "outcome_" + code);
            var built = AgentSessionBuilder.Build(new(request, outcome, active.Trusted, request.InitialMessages.Length - 1,
                DeepSeekReasoningContinuationCodec.Instance, predecessor, AgentSessionHeadTransition.SameHead));
            if (input.Case.Mode is "success" or "reset")
            {
                CapacitySpec.Require(outcome.CompletedSessionEligible && built.Succeeded && built.Artifact is not null &&
                    tools == (input.Case.Calls - 1) * input.Case.ToolsPerTurn + 1, "successful_session");
                lineage = await active.AcceptAsync(built.Artifact!, lineage, fresh);
                var acceptedRestore = await active.RestoreAsync(lineage, fresh);
                CapacitySpec.Require(acceptedRestore.Artifact.Plaintext.AsSpan().SequenceEqual(built.Artifact!.Plaintext), "accept_exact_plaintext");
                var after = active.Inventory();
                CapacitySpec.Require(after.Private, "ciphertext_privacy");
                return new(Receipt(code, outcome.Accounting!.ModelCalls, tools, transport, built.Artifact!, prior is not null,
                    false, false, reset, fresh && transport.OldHistoryAbsent, after, await active.EnvelopeBytesAsync(), snapshot.Bytes), lineage,
                    Convert.ToHexStringLower(SHA256.HashData(built.Artifact!.Plaintext)));
            }
            CapacitySpec.Require(!outcome.Succeeded && outcome.Review is null && !outcome.CompletedSessionEligible &&
                !built.Succeeded && built.Artifact is null && tools == 0, "failure_incomplete");
            var preserved = state.Inventory();
            CapacitySpec.Require(before.Hash == preserved.Hash, "failure_preservation");
            await state.RestoreAsync(lineage!, input.Fresh);
            return new(Receipt(code, outcome.Accounting!.ModelCalls, tools, transport, prior!, true,
                true, true, false, false, preserved, await state.EnvelopeBytesAsync(), snapshot.Bytes), lineage,
                Convert.ToHexStringLower(SHA256.HashData(prior!.Plaintext)));
        }
        finally { freshState?.Dispose(); }

        CapacityReceipt Receipt(string code, int calls, int tools, CapacityTransport? transport, AgentSessionArtifact artifact,
            bool restored, bool preserved, bool rejected, bool didReset, bool absent,
            (string Hash, long Bytes, bool Private) inventory, int envelopeBytes, long snapshotBytes = 0) =>
            new(input.Case.Id, code, calls, tools, transport?.Sends ?? 0, transport?.VerifiedAssistants ?? 0,
                transport?.VerifiedToolResults ?? 0, transport?.ListedFiles ?? 0, transport?.DiffReads ?? 0,
                restored, true, preserved, rejected, rejected, didReset, absent, inventory.Private,
                Environment.ProcessId, StartupId, EvaluationSource.Commit, EvaluationSource.Tree,
                EvaluationSource.Clean, measured.Finish(transport,
                    artifact.Document.CompletedRuns.Sum(run => run.Records.Length + run.Continuation.Items.Length),
                    artifact.Plaintext.Length, envelopeBytes, inventory.Bytes, snapshotBytes));
    }
}
