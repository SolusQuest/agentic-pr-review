using AgenticPrReview.Runtime.Agent;
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
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using AgenticPrReview.Runtime.Tests.Host.Action;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

// Separate production Host composition proof with synthetic network/artifact ports.
// The large snapshot/local-store chain is genuinely cross-process; this Host probe is in-process.
internal sealed class CapacityHostProbe(int world = 0)
{
    private readonly ScriptedLocatorStore store = new() { FilterListsByName = true, UseNumericObjectIds = true };
    private readonly ResetProbeRemote remote = new();
    private readonly ResetProbeClock time = new();
    private string? acceptedHead;
    private int genuineSeedAcceptances;
    // Retain independently observed contexts across reset. Previously rejected and retired
    // contexts remain forbidden even when they are absent from the active accepted suffix.
    private readonly List<string> observedUsers = [];
    private string? previousSession;

    internal async Task<CapacityHostReceipt[]> RunAsync(CancellationToken token)
    {
        var receipts = new List<CapacityHostReceipt>();
        receipts.Add(await InvokeAsync(new("host_seed", "success", 64, 1, 64), 0, false, token));
        for (var index = 0; index < CapacitySpec.Failures.Length; index++)
        {
            // Each failed round consumes its own genuinely accepted source.
            // Independent stores/worlds preserve every burned claim; none is
            // refunded or removed to let another failed round reuse it.
            var isolated = new CapacityHostProbe(index + 1);
            await isolated.InvokeAsync(new("host_seed", "success", 64, 1, 64), 0, false, token);
            var mode = CapacitySpec.Failures[index];
            receipts.Add(await isolated.InvokeAsync(new("host_" + mode, mode,
                mode == "context" ? 0 : 1, 0, 64), 1, false, token));
        }
        receipts.Add(await InvokeAsync(new("host_reset", "reset", 2, 1, 64), 1, true, token));
        receipts.Add(await InvokeAsync(new("host_reset_restore", "success", 1, 0, 64), 2, true, token));
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
        var provider = new Provider(selected, observedUsers.ToArray(), fresh);
        var before = Inventory(launch);
        var protectedUpload = false;
        var claimUploads = 0;
        var sourceRecordBound = true;
        store.AfterUpload = (request, _) =>
        {
            if (request.Name.Value == LocatorRootFormat.StoreName) return;
            var objectClass = ReadControl(launch, request.Name, request.EncryptedBytes.Span, (header, payload) =>
            {
                if (header.ObjectClass != StateObjectClass.ContextClaim) return;
                claimUploads++;
                CapacitySpec.Require(provider.Request is null, "current_claim_precedes_work");
                CapacitySpec.Require(SolusContextClaimRecordCodec.TryDecode(payload, out var claim) && claim is not null &&
                    SolusContextClaimRecordCodec.MatchesHeader(claim, header) &&
                    header.ProducingRunIdentity == store.ProducingRunIdentity && header.ProducingRunAttempt == store.ProducingRunAttempt,
                    "current_claim_authentication");
                sourceRecordBound &= before.RecordHashes.Contains(claim!.RecordSha256);
            });
            if (selected.Mode is not ("success" or "reset"))
                protectedUpload |= objectClass is not (StateObjectClass.ContextClaim or StateObjectClass.PublicationIntent or
                    StateObjectClass.PublicationFailure or StateObjectClass.Abandonment or StateObjectClass.Cleanup);
        };
        var writes = remote.MutationAttempts;
        var staging = Path.Combine(Path.GetTempPath(), "apr-r7-capacity-host-" + Guid.NewGuid().ToString("N"));
        var completion = await new CurrentEvaluationHostTestComposition(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, new StatePorts(store, github),
            remote, provider, time, () => staging)).RunAsync(launch, token);
        CapacitySpec.Require(!Directory.Exists(staging), "host_cleanup");
        CapacitySpec.Require(provider.Request is not null && provider.Outcome is not null && provider.ObservedInputs > 0, "host_execution");
        var code = provider.Outcome!.Diagnostic?.Code ?? "completed";
        CapacitySpec.Require(code == CapacitySpec.ExpectedCode(selected.Mode), "host_agent_" + code);
        var success = selected.Mode is "success" or "reset";
        store.AfterUpload = null;
        var after = Inventory(launch);
        var expectsClaim = phase > 0 && selected.Mode != "reset";
        CapacitySpec.Require(claimUploads == (expectsClaim ? 1 : 0) && sourceRecordBound, "current_source_claim");
        var priorClaims = before.Objects.Where(item => item.Value.Class == StateObjectClass.ContextClaim).ToArray();
        var retainedClaims = after.Objects.Where(item => item.Value.Class == StateObjectClass.ContextClaim).ToArray();
        CapacitySpec.Require(retainedClaims.Length == priorClaims.Length + claimUploads && priorClaims.All(item =>
            after.Objects.TryGetValue(item.Key, out var retained) && retained == item.Value), "current_claim_never_refunded");
        CapacitySpec.Require(provider.Request!.Continuation is null && provider.Request.StablePlan.PriorSessionSha256 is null,
            "current_native_history_absent");
        var preserved = before.Hash == after.Hash;
        var noMutation = remote.MutationAttempts == writes;
        var noSummary = completion.Summary.FindingCount is null && completion.Summary.ReviewedSha is null && completion.Summary.PublicationUrl is null;
        var isFresh = selected.Mode == "reset" && provider.Request!.Continuation is null &&
            provider.Request.SessionId != previousSession && provider.OldHistoryAbsent;
        if (success)
        {
            CapacitySpec.Require(completion.Status == ActionHostStatus.Reviewed && completion.ProcessExitCode == 0 &&
                completion.Summary.StateDisposition == ActionHostStateDisposition.Accepted && !noMutation, "host_acceptance");
            CapacitySpec.Require(phase == 0 ? provider.Request!.Continuation is null : selected.Mode == "reset" ? isFresh :
                provider.Request!.Continuation is null && provider.Request.SessionId == previousSession, "host_continuation");
            acceptedHead = scenario.Transport.PullRequest.HeadSha;
            previousSession = provider.Request!.SessionId;
            if (phase == 0) genuineSeedAcceptances++;
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
        observedUsers.Add(provider.CurrentUser!);
        return new(selected.Id, code, completion.Status.ToString(), completion.ProcessExitCode,
            completion.Summary.StateDisposition.ToString(), 0,
            0, provider.Request!.Continuation is not null, noSummary, noMutation,
            preserved && !protectedUpload, preserved, isFresh || fresh && provider.OldHistoryAbsent,
            new("apr-r7-authored-capacity-host-v1", 403, false, EvaluationSource.Commit, EvaluationSource.Tree,
                EvaluationSource.Clean, world, phase, genuineSeedAcceptances, claimUploads, sourceRecordBound,
                provider.Request.Continuation is null && provider.Request.StablePlan.PriorSessionSha256 is null,
                new(provider.ExecutedTools, provider.ObservedInputs, success ? "actual-apr-terminal" : "authored-failure-outcome")));
    }

    private (string Hash, Dictionary<string, (StateObjectClass Class, string Hash)> Objects, HashSet<string> RecordHashes) Inventory(ActionHostLaunchContract launch)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var objects = new Dictionary<string, (StateObjectClass, string)>(StringComparer.Ordinal);
        var recordHashes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in store.Objects.OrderBy(item => item.Reference.ObjectId.Value, StringComparer.Ordinal))
        {
            var bytes = store.Bytes(item);
            var objectClass = item.Reference.Name.Value == LocatorRootFormat.StoreName ? StateObjectClass.LocatorRoot :
                ReadControl(launch, item.Reference.Name, bytes, (header, payload) =>
                {
                    if (header.ObjectClass == StateObjectClass.Candidate &&
                        AcceptedStateGenerationRecordCodec.TryDecode(payload, out var generation))
                        recordHashes.Add(generation!.SessionSha256);
                });
            var digest = SHA256.HashData(bytes);
            objects.Add(item.Reference.ObjectId.Value, (objectClass, Convert.ToHexStringLower(digest)));
            if (objectClass is not (StateObjectClass.PublicationIntent or StateObjectClass.PublicationFailure or StateObjectClass.Abandonment or StateObjectClass.Cleanup or StateObjectClass.ContextClaim))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(item.Reference.ObjectId.Value));
                hash.AppendData(digest);
            }
        }
        return (Convert.ToHexStringLower(hash.GetHashAndReset()), objects, recordHashes);
    }

    private StateObjectClass ReadControl(ActionHostLaunchContract launch, OpaqueStoreName name, ReadOnlySpan<byte> encrypted,
        Action<StateControlHeaderV1, byte[]>? inspect = null)
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
            try { inspect?.Invoke(header!, payload); return header!.ObjectClass; }
            finally { CryptographicOperations.ZeroMemory(payload); }
        }
        finally { CryptographicOperations.ZeroMemory(sentinel!.Root); }
    }

    private sealed class StatePorts(IRestrictedStateStore store, FullPathGitHubFactory github) : IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) => store;
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => github.CreateExactObjectTransport(token);
    }
    // Fixed work and explicitly authored failure outcomes exercise real Host transaction
    // behavior. They do not qualify the retired 64/512 engine's limit triggers.
    private sealed class Provider(CapacityCase selected, string[] forbiddenUsers, bool fresh) : IActionHostProviderRunnerFactory
    {
        private readonly CapacityCase selected = selected;
        private readonly string[] forbiddenUsers = forbiddenUsers;
        private readonly bool fresh = fresh;
        internal AgentRunRequest? Request { get; private set; }
        internal AgentRunOutcome? Outcome { get; private set; }
        internal string? CurrentUser { get; private set; }
        internal int ObservedInputs { get; private set; }
        internal int ExecutedTools { get; private set; }
        internal bool OldHistoryAbsent { get; private set; } = true;
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            ReviewedSnapshot snapshot, TimeProvider timeProvider) => new Runner(this, snapshot);
        private sealed class Runner(Provider owner, ReviewedSnapshot snapshot) : IActionHostProviderRunner
        {
            public async Task<AgentRunOutcome> RunAsync(AgentRunRequest request, CancellationToken token)
            {
                owner.Request = request;
                var current = request.InitialMessages[^1];
                CapacitySpec.Require(current.Role == "user" && current.Contents is [ProjectTextContent], "host_current_user");
                owner.CurrentUser = ((ProjectTextContent)current.Contents[0]).Text;
                CapacitySpec.Require(owner.CurrentUser.Contains("head-sha=" + snapshot.Identity.HeadSha, StringComparison.Ordinal), "host_current_user_identity");
                CapacitySpec.Require(request.Continuation is null && request.StablePlan.PriorSessionSha256 is null,
                    "host_no_native_import");
                var success = owner.selected.Mode is "success" or "reset";
                var count = success ? owner.selected.Calls - 1 : 0;
                var steps = Enumerable.Range(0, count).Select(index => AuthoredReviewWork.PrepareCall(
                    $"{owner.selected.Id}_{index}_0", "read_file", "{\"path\":\"file.txt\",\"start_line\":1,\"line_count\":1}")).ToArray();
                if (!success)
                {
                    Observe(new(request.InitialMessages, AgentToolRegistry.Definitions.ToArray(), null), 1, "before");
                    // This outcome is test input at the Host boundary, not an engine
                    // capacity/deadline/token/response-bound execution claim.
                    return owner.Outcome = AgentRunOutcome.Failure(CapacitySpec.ExpectedCode(owner.selected.Mode), 0, 0, []);
                }
                return owner.Outcome = await AuthoredReviewWork.ExecuteAsync(request,
                    new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()), steps,
                    _ => AgentToolArguments.WriteFinishReview("Synthetic capacity review.", []), Observe, null,
                    token, terminalCallId: $"{owner.selected.Id}_{count}_0", recordAuthoredAccounting: false);

                void Observe(ProjectChatRequest observed, int ordinal, string phase)
                {
                    if (phase == "succeeded") { if (ordinal <= count) owner.ExecutedTools++; return; }
                    if (phase != "before") return;
                    owner.ObservedInputs++;
                    CapacitySpec.Require(observed.Continuation is null && observed.Messages.Length == 2 + 2 * (ordinal - 1),
                        "host_authored_message_count");
                    CapacitySpec.Require(observed.Messages[0].Role == "system" && observed.Messages[1] == current,
                        "host_current_user_placement");
                    var body = AgentRequestWriter.Write(observed);
                    var text = Encoding.UTF8.GetString(body);
                    owner.OldHistoryAbsent &= owner.forbiddenUsers.All(user => !text.Contains(user, StringComparison.Ordinal)) &&
                        (!owner.fresh || !text.Contains(CapacitySpec.OldTool, StringComparison.Ordinal));
                    CapacitySpec.Require(owner.OldHistoryAbsent, "host_old_context_absent");
                    for (var index = 0; index < ordinal - 1; index++)
                    {
                        var expected = steps[index];
                        CapacitySpec.Require(observed.Messages[2 + index * 2] is { Role: "assistant", Contents: [ProjectToolCallContent call] } &&
                            call.CallId == expected.CallId && call.Name == expected.Name &&
                            call.ArgumentsJson == Encoding.UTF8.GetString(expected.CanonicalArguments), "host_authored_call_association");
                        CapacitySpec.Require(observed.Messages[3 + index * 2] is { Role: "tool", Contents: [ProjectToolResultContent result] } &&
                            result.CallId == expected.CallId, "host_authored_result_association");
                        var content = (ProjectToolResultContent)observed.Messages[3 + index * 2].Contents[0];
                        using var document = System.Text.Json.JsonDocument.Parse(content.Result);
                        var value = document.RootElement;
                        CapacitySpec.Require(value.GetProperty("status").GetString() == "ok" &&
                            value.GetProperty("reviewed_identity").GetProperty("head_sha").GetString() == snapshot.Identity.HeadSha &&
                            value.GetProperty("path").GetString() == "file.txt" &&
                            value.GetProperty("lines")[0].GetProperty("text").GetString() ==
                                (owner.fresh ? CapacitySpec.FreshTool : CapacitySpec.OldTool), "host_authored_current_tool_fact");
                    }
                }
            }
            public void Dispose() { }
        }
    }
}
