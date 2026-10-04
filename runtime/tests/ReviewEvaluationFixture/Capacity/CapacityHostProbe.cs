using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.Tests.Host.Action;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

// Separate production Host composition proof with synthetic network/artifact ports.
// The large snapshot/local-store chain is genuinely cross-process; this Host probe is in-process.
internal sealed class CapacityHostProbe
{
    private readonly ScriptedLocatorStore store = new() { FilterListsByName = true, UseNumericObjectIds = true };
    private readonly ResetProbeRemote remote = new();
    private readonly ResetProbeClock time = new();
    private string? acceptedHead;
    private readonly List<CapacityHistory> history = [];
    private string? previousSession;

    internal async Task<CapacityHostReceipt[]> RunAsync(CancellationToken token)
    {
        var receipts = new List<CapacityHostReceipt>();
        receipts.Add(await InvokeAsync(new("host_seed", "success", 64, 1, 64), 0, false, token));
        foreach (var mode in CapacitySpec.Failures)
            receipts.Add(await InvokeAsync(new("host_" + mode, mode, mode == "context" ? 0 : 1, 0, 64), receipts.Count, false, token));
        receipts.Add(await InvokeAsync(new("host_reset", "reset", 2, 1, 64), receipts.Count, true, token));
        receipts.Add(await InvokeAsync(new("host_reset_restore", "success", 1, 0, 64), receipts.Count, true, token));
        return receipts.ToArray();
    }

    private async Task<CapacityHostReceipt> InvokeAsync(CapacityCase selected, int phase, bool fresh, CancellationToken token)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        scenario.Transport.PullRequest = scenario.Transport.PullRequest with { HeadSha = (100 + phase).ToString("x40", CultureInfo.InvariantCulture) };
        var old = scenario.Launch;
        CapacitySpec.Require(ActionHostProviderApiKey.TryCreate("synthetic-provider-key", out var providerKey), "host_provider_key");
        CapacitySpec.Require(ActionHostStateKey.TryCreate(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()), out var key), "host_state_key");
        CapacitySpec.Require(ActionHostInputs.TryCreate(old.Inputs.GitHubToken, providerKey, key, null, null,
            old.Inputs.PullRequestNumber, selected.Mode == "reset" ? ActionHostStateMode.Reset : ActionHostStateMode.Auto, out var inputs), "host_inputs");
        CapacitySpec.Require(ActionHostLaunchContract.TryCreate(inputs, old.EventJsonPath, old.EventJsonSha256,
            old.RepositoryName, old.RepositoryId, old.RunId + phase, old.RunAttempt, old.WorkflowPath,
            old.WorkflowRef, old.WorkflowSha, old.ActionSourceSha, old.PayloadSha256, old.BuildDiscriminator,
            old.Cancellation, old.ArtifactBridgeEndpoint, out var launch), "host_launch");
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with { Id = launch!.RunId };
        store.ProducingRunIdentity = launch.RunId.ToString(CultureInfo.InvariantCulture);
        store.ProducingRunAttempt = launch.RunAttempt;
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest, previousHead: acceptedHead,
            withInlineFile: true, fileBytes: Encoding.UTF8.GetBytes((fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool) + "\n"));
        var provider = new Provider(selected, selected.Mode == "reset" ? [] : history.ToArray(), fresh);
        var before = Inventory(launch);
        var protectedUpload = false;
        if (selected.Mode is not ("success" or "reset"))
            store.AfterUpload = (request, _) => protectedUpload |=
                Classify(launch, request.Name, request.EncryptedBytes.Span) is not
                    (StateObjectClass.PublicationIntent or StateObjectClass.PublicationFailure or StateObjectClass.Abandonment or StateObjectClass.Cleanup);
        var writes = remote.MutationAttempts;
        var staging = Path.Combine(Path.GetTempPath(), "apr-r7-capacity-host-" + Guid.NewGuid().ToString("N"));
        var completion = await new ActionHostComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, new StatePorts(store, github),
            remote, provider, time, () => staging)).RunAsync(launch, token);
        CapacitySpec.Require(!Directory.Exists(staging), "host_cleanup");
        CapacitySpec.Require(provider.Request is not null && provider.Outcome is not null && provider.Transport is not null, "host_execution");
        var code = provider.Outcome!.Diagnostic?.Code ?? "completed";
        if (provider.Transport!.OracleFailure is { } oracleFailure) throw new InvalidOperationException(oracleFailure);
        CapacitySpec.Require(code == CapacitySpec.ExpectedCode(selected.Mode), "host_agent_" + code);
        var success = selected.Mode is "success" or "reset";
        store.AfterUpload = null;
        var after = Inventory(launch);
        var preserved = before.Hash == after.Hash;
        var noMutation = remote.MutationAttempts == writes;
        var noSummary = completion.Summary.FindingCount is null && completion.Summary.ReviewedSha is null && completion.Summary.PublicationUrl is null;
        var isFresh = selected.Mode == "reset" && provider.Request!.Continuation is null &&
            provider.Request.SessionId != previousSession && provider.Transport!.OldHistoryAbsent;
        if (success)
        {
            CapacitySpec.Require(completion.Status == ActionHostStatus.Reviewed && completion.ProcessExitCode == 0 &&
                completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted && !noMutation, "host_acceptance");
            CapacitySpec.Require(phase == 0 ? provider.Request!.Continuation is null : selected.Mode == "reset" ? isFresh :
                provider.Request!.Continuation is not null && provider.Request.SessionId == previousSession, "host_continuation");
            acceptedHead = scenario.Transport.PullRequest.HeadSha;
            previousSession = provider.Request!.SessionId;
            if (selected.Mode == "reset") history.Clear();
            history.Add(new(selected.Id, selected.Calls, selected.ToolsPerTurn, provider.Request!.ReviewedIdentity));
        }
        else
        {
            CapacitySpec.Require(completion.Status is ActionHostStatus.ProviderFailed or ActionHostStatus.AgentResultInvalid,
                "host_failure_status_" + completion.Status);
            CapacitySpec.Require(completion.ProcessExitCode != 0, "host_failure_exit");
            CapacitySpec.Require(completion.Summary.StateDisposition == ActionHostStateDisposition.NotCommitted,
                "host_failure_state_" + completion.Summary.StateDisposition);
            CapacitySpec.Require(noSummary, "host_failure_summary");
            CapacitySpec.Require(noMutation, "host_failure_publication_mutation");
            CapacitySpec.Require(preserved, "host_failure_predecessor_mutation");
            CapacitySpec.Require(!protectedUpload, "host_failure_candidate_upload");
            // Recovery of a prior completed publication can remove historical intent/control objects.
            // Every changed object must belong to that explicit family; accepted ciphertext is byte-identical.
            foreach (var item in before.Objects)
                if (!after.Objects.TryGetValue(item.Key, out var current) || item.Value.Hash != current.Hash)
                    CapacitySpec.Require(item.Value.Class is StateObjectClass.PublicationIntent or StateObjectClass.PublicationFailure or
                        StateObjectClass.Abandonment or StateObjectClass.Cleanup, "host_failure_control_change");
            CapacitySpec.Require(!provider.Outcome.CompletedSessionEligible && provider.Outcome.Review is null, "host_failure_terminal");
        }
        return new(selected.Id, code, completion.Status.ToString(), completion.ProcessExitCode,
            completion.Summary.StateDisposition.ToString(), provider.Outcome.Accounting!.ModelCalls,
            provider.Transport!.Sends, provider.Request!.Continuation is not null, noSummary, noMutation,
            preserved && !protectedUpload, preserved, isFresh || fresh && provider.Transport.OldHistoryAbsent);
    }

    private (string Hash, Dictionary<string, (StateObjectClass Class, string Hash)> Objects) Inventory(ActionHostLaunchContract launch)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var objects = new Dictionary<string, (StateObjectClass, string)>(StringComparer.Ordinal);
        foreach (var item in store.Objects.OrderBy(item => item.Reference.ObjectId.Value, StringComparer.Ordinal))
        {
            var bytes = store.Bytes(item);
            var objectClass = item.Reference.Name.Value == LocatorRootFormat.StoreName ? StateObjectClass.LocatorRoot :
                Classify(launch, item.Reference.Name, bytes);
            var digest = SHA256.HashData(bytes);
            objects.Add(item.Reference.ObjectId.Value, (objectClass, Convert.ToHexStringLower(digest)));
            if (objectClass is not (StateObjectClass.PublicationIntent or StateObjectClass.PublicationFailure or StateObjectClass.Abandonment or StateObjectClass.Cleanup))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(item.Reference.ObjectId.Value));
                hash.AppendData(digest);
            }
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), objects);
    }

    private StateObjectClass Classify(ActionHostLaunchContract launch, OpaqueStoreName name, ReadOnlySpan<byte> encrypted)
    {
        var repository = launch.RepositoryId.ToString(CultureInfo.InvariantCulture);
        using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle(repository);
        CapacitySpec.Require(LocatorStateKeyRing.TryCreate(access, repository, launch.Inputs.StateKey!.ExportForPrivateLaunch(),
            null, out var ring, out _), "host_oracle_keys");
        using var keys = ring!;
        var root = store.Objects.Single(item => item.Reference.Name.Value == LocatorRootFormat.StoreName);
        CapacitySpec.Require(LocatorRootSentinelCodec.TryDecrypt(access, keys, store.Bytes(root), out var sentinel, out _), "host_oracle_root");
        try
        {
            CapacitySpec.Require(LocatorContext.TryCreate(access, keys, sentinel!.Root, true, sentinel.RequiredExpiresAtUnixSeconds,
                time, out var selected), "host_oracle_context");
            using var context = selected!;
            CapacitySpec.Require(StateControlEnvelopeV1Codec.TryDecrypt(context, access, name, encrypted,
                out var header, out var payload, out _), "host_oracle_record");
            try { return header!.ObjectClass; }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally { CryptographicOperations.ZeroMemory(sentinel!.Root); }
    }

    private sealed class StatePorts(IRestrictedStateStore store, FullPathGitHubFactory github) : IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) => store;
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => github.CreateExactObjectTransport(token);
    }
    private sealed class Provider(CapacityCase selected, CapacityHistory[] prior, bool fresh) : IActionHostProviderRunnerFactory
    {
        private readonly CapacityCase selected = selected;
        private readonly CapacityHistory[] prior = prior;
        private readonly bool fresh = fresh;
        internal AgentRunRequest? Request { get; private set; }
        internal AgentRunOutcome? Outcome { get; private set; }
        internal CapacityTransport? Transport { get; private set; }
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider) => new Runner(this, policy, snapshot);
        private sealed class Runner(Provider owner, ActionHostProviderPolicy policy, ReviewedSnapshot snapshot) : IActionHostProviderRunner
        {
            public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken token)
            {
                owner.Request = request;
                var stimulus = owner.selected.Mode == "context" ? request with { InitialMessages =
                    [.. request.InitialMessages[..^1], CapacityState.User(owner.fresh, "context")] } : request;
                var clock = new CapacityClock();
                using var transport = new CapacityTransport(owner.selected, owner.prior, clock, owner.fresh, host: true,
                    expectedIdentity: snapshot.Identity);
                owner.Transport = transport;
                var client = DeepSeekChatBackend.CreateClient(new(policy.ProviderId, policy.ModelId, policy.AdapterId, request.SessionId), transport);
                return owner.Outcome = await new AgentLoop(client, new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()),
                    clock, policy.LimitAuthority).RunAsync(stimulus, token);
            }
            public void Dispose() { }
        }
    }
}
