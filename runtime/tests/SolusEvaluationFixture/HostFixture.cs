using AgenticPrReview.Runtime.ActionHost.Policy;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal sealed class HostFixture : IDisposable
{
    internal const string ProviderKey = "R5_SYNTHETIC_PROVIDER_KEY_PRIVATE";
    internal const string GitHubKey = "R5_SYNTHETIC_GITHUB_KEY_PRIVATE";
    internal static readonly string StateKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x73, 32).ToArray());
    internal readonly string Root;
    internal readonly ActionHostAuthorizationScenario Authorization;
    internal readonly ActionHostLaunchContract Launch;
    internal readonly CorpusGitHub GitHub;
    internal readonly ActionHostComposition Host;
    internal readonly FaultControl Fault;
    internal readonly FaultStore Store;
    internal readonly StickyPort Sticky;
    private readonly DeniedProvider native = new();
    internal int NativeCreates => native.Creates;
    internal string PolicyFingerprint { get; private set; } = string.Empty;
    internal readonly TimeProvider Clock;

    private readonly bool continuation;
    internal HostFixture(Corpus corpus, string root, string mode, bool providerKey, long now, string? reviewJson = null,
        bool inline = false, bool continuation = true, bool ahead = false, bool verifiedAhead = false, long? authorizedRunId = null, byte[]? instructionOverride = null)
    {
        this.continuation = continuation;
        Root = root; Clock = new FrozenClock(now); Fault = new(root, mode);
        Authorization = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch,
            tokenValue: GitHubKey, pullRequestNumber: 242);
        var transport = Authorization.Transport;
        var runId = authorizedRunId ?? (mode.StartsWith("seed", StringComparison.Ordinal) ? 7001L : 7002L);
        var attempt = mode == "retry" ? 3 : Authorization.Launch.RunAttempt;
        var source = continuation ? TrustedProofPayloadBuildIdentity.SourceCommit : Authorization.Launch.WorkflowSha;
        if (continuation)
        {
            var workflow = Encoding.UTF8.GetBytes(TrustedProofV2WorkflowAdmission.Render(source));
            transport.Source = transport.Source with { Bytes = workflow, BlobSha = Convert.ToHexStringLower(
                SHA1.HashData(Encoding.ASCII.GetBytes($"blob {workflow.Length}\0").Concat(workflow).ToArray())) };
        }
        transport.Repository = transport.Repository with { Id = 242 };
        transport.CurrentRun = transport.CurrentRun with { Id = runId, Attempt = attempt, HeadSha = source,
            Repository = transport.CurrentRun.Repository with { Id = 242 },
            HeadRepository = transport.CurrentRun.HeadRepository with { Id = 242 } };
        transport.PullRequest = transport.PullRequest with { BaseSha = new string('1', 40), HeadSha = new string(ahead ? '3' : '2', 40),
            BaseRepository = transport.PullRequest.BaseRepository with { Id = 242 },
            HeadRepository = transport.PullRequest.HeadRepository with { Id = 242 } };
        var document = JsonNode.Parse(Authorization.EventBytes)!;
        document["repository"]!["id"] = 242;
        var eventBytes = Encoding.UTF8.GetBytes(document.ToJsonString());
        Authorization.EventReader.Bytes = eventBytes;
        Proof.Require(ActionHostProviderApiKey.TryCreate(ProviderKey, out var provider), "synthetic_provider");
        Proof.Require(ActionHostStateKey.TryCreate(StateKey, out var state), "synthetic_state_key");
        Proof.Require(ActionHostInputs.TryCreate(Authorization.Launch.Inputs.GitHubToken,
            providerKey ? provider : null, state, null, null, 242, ActionHostStateMode.Auto, out var inputs), "synthetic_inputs");
        var original = Authorization.Launch;
        Proof.Require(ActionHostLaunchContract.TryCreate(inputs, original.EventJsonPath,
            Convert.ToHexStringLower(SHA256.HashData(eventBytes)), original.RepositoryName, 242, runId,
            attempt, original.WorkflowPath, original.WorkflowRef, source,
            continuation ? source : original.ActionSourceSha, original.PayloadSha256, original.BuildDiscriminator,
            original.Cancellation, Path.Join(root, "bridge.sock"), out var launch), "synthetic_launch");
        Launch = launch!;
        GitHub = new(corpus, transport.PullRequest, Launch.WorkflowSha, instructionOverride: instructionOverride, reviewJson: reviewJson, inline: inline,
            ahead: ahead, verifiedAhead: ahead && verifiedAhead);
        Store = new(new GitHubArtifactRestrictedStateStore(Launch.ArtifactBridgeEndpoint, Launch.BuildDiscriminator,
            Path.Join(root, "bridge-staging")), Fault);
        Sticky = new(Launch.RepositoryName, Fault) { BodyPath = Path.Join(root, "sticky.txt") };
        Host = new(new ActionHostCompositionDependencies(Authorization.EventReader, Authorization.Factory,
            GitHub, GitHub, new StatePorts(Store, GitHub), Sticky, native, Clock,
            () => Path.Join(root, "snapshot-" + Guid.NewGuid().ToString("N")), new CountingInline(Fault),
            workflowAdmission: continuation ? TrustedProofV2WorkflowAdmission.Instance : ActionHostV1TrustedWorkflowAdmission.Instance));
    }

    internal async Task BindNamesAsync()
    {
        // Test-only fault classification derives locators from real authorized
        // policy. It never changes production authority or decrypts store bytes.
        var authorized = await new ActionHostAuthorizer(Authorization.EventReader, Authorization.Factory,
            ActionHostAuthorizationPolicy.TrustedProof,
            workflowAdmission: continuation ? TrustedProofV2WorkflowAdmission.Instance : ActionHostV1TrustedWorkflowAdmission.Instance)
            .AuthorizeAsync(Launch, default);
        var invocation = authorized.Invocation!;
        Proof.Require(invocation is not null, "fault_invocation");
        Proof.Require(ActionHostTrustedPolicyRequest.TryBind(Launch, invocation!,
            out var policyRequest, out _), "fault_locator_authorization");
        using var transport = GitHub.CreateExactObjectTransport(Launch.Inputs.GitHubToken!);
        var policy = await ActionHostTrustedPolicy.MaterializeAsync(policyRequest!, transport, default);
        PolicyFingerprint = AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation.EvaluationAttempt.Hash("solus-policy",
            policy.Policy!.ProviderId, policy.Policy.ModelId, policy.Policy.AdapterId, policy.Policy.Endpoint,
            policy.Policy.PolicySha256, policy.Policy.ConfigSha256, policy.Policy.ToolsetSha256, policy.Policy.LimitsSha256);
        var request = new ArtifactStateRestoreRequest(Launch, invocation!, policy.Policy!,
            new("user", [new ProjectTextContent("synthetic locator classification")]), DeepSeekReasoningContinuationCodec.Instance);
        Proof.Require(AcceptedStateProductionAuthorization.TryAuthorize(request, out var production), "fault_scope_authority");
        Proof.Require(LineageBaseScopeCodec.TryEncode(AuthorizedAcceptedStateComposer.BaseScope(production!), out var scope), "fault_scope_bytes");
        var key = Convert.FromBase64String(StateKey);
        var root = LocatorCryptography.InitialRoot(key, "242");
        try
        {
            foreach (var objectClass in StateObjectClasses.All)
                Fault.Names[LocatorCryptography.OpaqueName(root, StateObjectClasses.ToWireName(objectClass), scope)] =
                    StateObjectClasses.ToWireName(objectClass);
        }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(root); CryptographicOperations.ZeroMemory(scope); }
    }

    public void Dispose() { }
    private sealed class FrozenClock(long now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(now); }
    private sealed class StatePorts(IRestrictedStateStore store, CorpusGitHub github) : IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) => store;
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => github.CreateExactObjectTransport(token);
    }
    private sealed class DeniedProvider : IActionHostProviderRunnerFactory
    {
        internal int Creates;
        public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
            AgenticPrReview.Runtime.Agent.Tools.ReviewedSnapshot snapshot, TimeProvider timeProvider)
        { Creates++; throw new ProofFailure("native_provider_forbidden"); }
    }
}

internal sealed class FaultControl(string root, string mode)
{
    internal readonly Dictionary<string, string> Names = new(StringComparer.Ordinal);
    internal readonly CancellationTokenSource Cancellation = new();
    internal string Mode => mode;
    internal bool Fired;
    internal bool DenyReads;
    internal bool AcceptanceObserved;
    internal bool CurrentReviewCaptured;
    internal string Class(OpaqueStoreName name) => Names.GetValueOrDefault(name.Value, "locator_root");
    internal void Log(object value) => File.AppendAllText(Path.Join(root, "operations.jsonl"), JsonSerializer.Serialize(value) + "\n");
    internal bool Once(string point) => !Fired && mode == point && (Fired = true);
    internal void Boundary(string point)
    {
        if (mode == "crash-" + point)
        {
            File.WriteAllText(Path.Join(root, "fault-triggered"), point);
            Environment.Exit(73);
        }
        if (Once("cancel-" + point)) Cancellation.Cancel();
    }
}

internal sealed class FaultStore(IRestrictedStateStore inner, FaultControl fault) : IRestrictedStateStore
{
    public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token)
    {
        var kind = fault.Class(request.Name);
        Scan(request.EncryptedBytes.Span);
        fault.Boundary(kind + "-before-upload");
        if (kind == "cleanup" ? fault.Once("fail-cleanup-upload") : fault.Once("fail-" + kind))
            return OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.Io);
        var result = await inner.UploadImmutableAsync(request, token);
        if (kind == "acceptance" && result.MutationState == OpaqueStoreMutationState.Committed) fault.AcceptanceObserved = true;
        fault.Log(new { operation = "upload", kind, digest = request.EncryptedObjectDigest.Sha256,
            bytes = request.EncryptedBytes.Length, committed = result.MutationState == OpaqueStoreMutationState.Committed,
            from_current_capture = fault.CurrentReviewCaptured });
        fault.Boundary(kind + "-upload");
        if (fault.Once("unknown-" + kind) || fault.Once("unknown-unreadable-" + kind))
        {
            fault.DenyReads = fault.Mode.StartsWith("unknown-unreadable-", StringComparison.Ordinal);
            return OpaqueStoreUploadResult.Fail(OpaqueStoreFailure.OutcomeUnknown, OpaqueStoreMutationState.OutcomeUnknown, result.Metadata);
        }
        if (fault.Once("foreign-" + kind) && result.Metadata is not null)
            return result with { Metadata = result.Metadata with { ProducingRun = new("foreign-run", 2) } };
        return result;
    }
    public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token) =>
        fault.DenyReads ? Task.FromResult(OpaqueStoreListResult.Fail(OpaqueStoreFailure.Incomplete)) : inner.ListExactAsync(request, token);
    public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) =>
        fault.DenyReads ? Task.FromResult(OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.Io)) : inner.ReadMetadataAsync(request, token);
    public async Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token)
    {
        if (fault.DenyReads) return OpaqueStoreDownloadResult.Fail(OpaqueStoreFailure.Io);
        var result = await inner.DownloadAsync(request, token);
        if (result.Succeeded) Scan(result.EncryptedBytes.Span);
        if (result.Succeeded && fault.Mode == "tamper-source")
        {
            var bytes = result.EncryptedBytes.ToArray();
            if (bytes.Length > 0) bytes[^1] ^= 0x80;
            return result with { EncryptedBytes = bytes };
        }
        return result;
    }
    public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token)
    {
        if (fault.DenyReads) return OpaqueStoreReadBackResult.Fail(OpaqueStoreFailure.Io);
        var result = await inner.ReadBackExactAsync(request, token);
        if (result.Succeeded) fault.Boundary(fault.Class(request.Expected.Reference.Name) + "-readback");
        return result;
    }
    public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token)
    {
        fault.Boundary("cleanup");
        if (fault.AcceptanceObserved) fault.Boundary("accepted-cleanup");
        if (fault.Mode == "fail-cleanup" && fault.AcceptanceObserved) return Task.FromResult(OpaqueStoreDeleteResult.Fail(OpaqueStoreFailure.Cleanup));
        return inner.DeleteExactAsync(request, token);
    }
    private static void Scan(ReadOnlySpan<byte> bytes)
    {
        foreach (var canary in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey,
            "R5_PRIVATE_REASONING_CANARY", "S4_PRIVATE_CAPTURE_CANARY", ScriptedHandler.PriorOnly, ScriptedHandler.CurrentOnly })
            Proof.Require(bytes.IndexOf(Encoding.UTF8.GetBytes(canary)) < 0, "encrypted_transport_canary");
    }
}

internal sealed class CountingInline(FaultControl fault) : IActionHostPostAcceptanceInlineHook
{
    public Task<ActionHostInlineHookResult> PublishAsync(ActionHostCoordinator.PostAcceptanceInlineRequest request,
        CancellationToken cancellationToken)
    {
        Proof.Require(request.TryConsume(out var operation) && operation is not null &&
            operation.CandidateMap.Candidates.Length > 0 && !request.TryConsume(out _), "single_live_inline_capability");
        fault.Log(new { operation = "inline", authorized = true });
        return Task.FromResult(ActionHostInlineHookResult.Complete);
    }
}

internal sealed class CancellingFiles(CancellationTokenSource cancellation) : AgenticPrReview.Runtime.Agent.Tools.IReviewedFileAccess
{
    private readonly AgenticPrReview.Runtime.Agent.Tools.VerifiedReviewedFileAccess inner = new();
    public AgenticPrReview.Runtime.Agent.Tools.ReviewedFileMetadata InspectMetadata(AgenticPrReview.Runtime.Agent.Tools.ReviewedSnapshot snapshot, string path) => inner.InspectMetadata(snapshot, path);
    public AgenticPrReview.Runtime.Agent.Tools.ReviewedFileProbe Probe(AgenticPrReview.Runtime.Agent.Tools.ReviewedSnapshot snapshot, string path) => inner.Probe(snapshot, path);
    public async ValueTask<AgenticPrReview.Runtime.Agent.Tools.ReviewedFileRead> ReadAsync(AgenticPrReview.Runtime.Agent.Tools.ReviewedSnapshot snapshot,
        string path, AgenticPrReview.Runtime.Agent.Tools.ReviewedFileProbe expected, CancellationToken token)
    {
        var result = await inner.ReadAsync(snapshot, path, expected, token);
        cancellation.Cancel(); return result;
    }
}
