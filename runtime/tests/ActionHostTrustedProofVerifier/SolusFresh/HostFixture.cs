using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal sealed class HostFixture : IDisposable
{
    internal const string ProviderKey = "R5_SYNTHETIC_PROVIDER_KEY_PRIVATE";
    internal const string GitHubKey = "R5_SYNTHETIC_GITHUB_KEY_PRIVATE";
    internal static readonly string StateKey = Convert.ToBase64String(Enumerable.Repeat((byte)0x73, 32).ToArray());
    internal readonly string Root = Path.Join(Path.GetTempPath(), "apr-r5-host-" + Guid.NewGuid().ToString("N"));
    internal readonly ActionHostAuthorizationScenario Authorization;
    internal readonly ActionHostLaunchContract Launch;
    internal readonly CorpusGitHub GitHub;
    internal readonly DeniedEffects Effects = new();
    internal readonly ActionHostComposition Host;
    internal MeteredProvider? Meter;

    internal HostFixture(Corpus corpus, byte[]? instructions = null, string? reviewJson = null,
        bool providerKey = true, bool rejectAuthorization = false, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(Root);
        Authorization = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch,
            tokenValue: GitHubKey, pullRequestNumber: 242);
        var transport = Authorization.Transport;
        transport.Repository = transport.Repository with { Id = 242 };
        var runId = RandomNumberGenerator.GetInt32(1_000_000, int.MaxValue);
        transport.CurrentRun = transport.CurrentRun with { Id = runId,
            Repository = transport.CurrentRun.Repository with { Id = 242 },
            HeadRepository = transport.CurrentRun.HeadRepository with { Id = 242 } };
        transport.PullRequest = transport.PullRequest with { BaseSha = new string('1', 40), HeadSha = new string('2', 40),
            BaseRepository = transport.PullRequest.BaseRepository with { Id = 242 },
            HeadRepository = transport.PullRequest.HeadRepository with { Id = 242 } };
        if (rejectAuthorization) transport.Permission = new("read");
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
            original.RunAttempt, original.WorkflowPath, original.WorkflowRef, original.WorkflowSha,
            original.ActionSourceSha, original.PayloadSha256, original.BuildDiscriminator,
            original.Cancellation, original.ArtifactBridgeEndpoint, out var launch), "synthetic_launch");
        Launch = launch!;
        GitHub = new(corpus, transport.PullRequest, Launch.WorkflowSha, instructions, reviewJson);
        Host = new(new ActionHostCompositionDependencies(Authorization.EventReader, Authorization.Factory,
            GitHub, GitHub, Effects, Effects, Effects, clock ?? TimeProvider.System, () => Path.Join(Root, "stage")));
    }
    internal Task<ActionHostPreparedResult<SolusFreshReviewResult>> Run(ScriptedHandler handler,
        IRestrictedContextSink? sink = null, CancellationToken token = default,
        Func<IModelProvider, IModelProvider>? decorator = null,
        Func<ReviewedSnapshot, IReviewedFileAccess>? files = null, bool metered = true)
    {
        Func<IModelProvider, IModelProvider>? wrap = !metered ? null : provider => {
            Meter = new MeteredProvider(provider);
            return decorator?.Invoke(Meter) ?? Meter;
        };
        return new SolusFreshReviewComposition(Host, () => handler, wrap, files).RunAsync(Launch, sink, token,
            [new(SolusAgent.Api.Execution.AgentInputSource.Repository, handler.Focus)]);
    }
    internal void AssertEffectsAbsent()
    {
        Proof.Require(Effects.Calls == 0, "zero_state_publication_native_runner_calls");
        Proof.Require(!Directory.Exists(Path.Join(Root, "stage")), "snapshot_cleanup");
    }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}

internal sealed class DeniedEffects : IAcceptedStateProductionDependencies,
    IStickyGitHubPublisherTransportFactory, IActionHostProviderRunnerFactory
{
    internal int Calls;
    private Exception Denied() { Calls++; return new ProofFailure("forbidden_effect"); }
    public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) => throw Denied();
    public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => throw Denied();
    public IStickyGitHubPublisherTransport Create(ActionHostGitHubToken token,
        AuthorizedStickyPublicationRequest request, CancellationToken reconciliationToken = default) => throw Denied();
    public IStickyGitHubReadbackTransport CreateReadback(ActionHostGitHubToken token,
        AuthorizedStickyReadbackRequest request, CancellationToken reconciliationToken = default) => throw Denied();
    public IActionHostProviderRunner Create(ActionHostProviderPolicy policy, ActionHostProviderApiKey key,
        ReviewedSnapshot snapshot, TimeProvider timeProvider) => throw Denied();
}

internal sealed class RestrictedSink(bool failAfterCopy = false) : IRestrictedContextSink, IDisposable
{
    internal byte[]? Bytes;
    internal Guid Implementation;
    internal int Format, Compatibility, Calls;
    public void Capture(AgentContextEnvelope context)
    {
        Proof.Require(Calls++ == 0, "one_sink_delivery");
        Bytes = context.CopyRestrictedPayload(); Implementation = context.ImplementationId;
        Format = context.FormatVersion; Compatibility = context.CompatibilityVersion;
        if (failAfterCopy) throw new InvalidOperationException("R5_PRIVATE_SINK_EXCEPTION_CANARY");
    }
    internal void AssertMatches(SolusFreshReviewResult result)
    {
        Proof.Require(Calls == 1 && Bytes is { Length: > 0 } && Format == 2 && Compatibility == 1 &&
            Implementation == AgenticPrReview.Runtime.Host.State.SolusRestrictedStateFormat.Implementation &&
            result.Context?.Checkpoint?.ExecutionId == result.ExecutionId, "capture_identity");
        using var payload = System.Text.Json.JsonDocument.Parse(Bytes!);
        var checkpoint = payload.RootElement.GetProperty("Info");
        var expected = result.Context!.Checkpoint!;
        Proof.Require(checkpoint.GetProperty("ExecutionId").GetGuid() == result.ExecutionId &&
            checkpoint.GetProperty("CheckpointId").GetGuid() == expected.CheckpointId &&
            checkpoint.GetProperty("LogicalWorkId").GetGuid() == expected.LogicalWorkId &&
            checkpoint.GetProperty("RoundId").GetGuid() == expected.RoundId, "capture_checkpoint");
        var text = Encoding.UTF8.GetString(Bytes!);
        foreach (var canary in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey })
            Proof.Require(!text.Contains(canary, StringComparison.Ordinal), "capture_no_credentials");
    }
    public void Dispose() { if (Bytes is not null) CryptographicOperations.ZeroMemory(Bytes); Bytes = null; }
}

// Trusted fixture-only failure at actual runtime capture, after real provider
// work. No package result or Host work outcome is mocked.
internal sealed class ExportFailure(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default) =>
        inner.ExchangeAsync(request, cancellationToken);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) =>
        throw new InvalidOperationException("R5_PRIVATE_EXPORT_EXCEPTION_CANARY");
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => false;
}

// Numeric witness around the real R4 adapter; all exchanges and persistence
// delegate unchanged. No response, continuation or source bytes are retained.
internal sealed class MeteredProvider(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    internal int MaxRequestBytes, MaxResponseBytes, MaxInputs;
    internal Guid? ExecutionId;
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        ExecutionId ??= request.Attempt.ExecutionId;
        Proof.Require(ExecutionId == request.Attempt.ExecutionId, "provider_execution_witness");
        MaxRequestBytes = Math.Max(MaxRequestBytes, request.PayloadByteCount);
        MaxInputs = Math.Max(MaxInputs, request.Inputs.Count);
        var result = await inner.ExchangeAsync(request, cancellationToken);
        MaxResponseBytes = Math.Max(MaxResponseBytes, result.Response?.PayloadByteCount ?? 0);
        return result;
    }
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
}

internal sealed class CancellingFiles(CancellationTokenSource cancellation) : IReviewedFileAccess
{
    private readonly VerifiedReviewedFileAccess inner = new();
    internal int Reads;
    public ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path) => inner.InspectMetadata(snapshot, path);
    public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path) => inner.Probe(snapshot, path);
    public async ValueTask<ReviewedFileRead> ReadAsync(ReviewedSnapshot snapshot, string path,
        ReviewedFileProbe expected, CancellationToken token)
    {
        var read = await inner.ReadAsync(snapshot, path, expected, token);
        Reads++; cancellation.Cancel(); return read;
    }
}

// Real reads arm an infrastructure fault in the existing Host clock seam.
// Underlying timers are disposed before the one-shot throw; no runtime result
// or tool evidence is fabricated by this fixture.
internal sealed class AbortClock(CancellationTokenSource? cancellation = null) : TimeProvider
{
    private int timerCount, armedTimer, consumed;
    internal int Faults;
    internal void Arm() => Interlocked.CompareExchange(ref armedTimer, Volatile.Read(ref timerCount), 0);
    public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
    public override long GetTimestamp() => TimeProvider.System.GetTimestamp();
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new AbortTimer(this, Interlocked.Increment(ref timerCount), TimeProvider.System.CreateTimer(callback, state, dueTime, period));
    private void AfterDispose(int timer)
    {
        if (timer != Volatile.Read(ref armedTimer) || Interlocked.CompareExchange(ref consumed, 1, 0) != 0) return;
        Interlocked.Increment(ref Faults);
        if (cancellation is not null)
        {
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        }
        throw new InvalidOperationException("R5_PRIVATE_ABORT_EXCEPTION_CANARY");
    }
    private sealed class AbortTimer(AbortClock owner, int id, ITimer inner) : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => inner.Change(dueTime, period);
        public void Dispose() { inner.Dispose(); owner.AfterDispose(id); }
        public async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false); owner.AfterDispose(id);
        }
    }
}

internal sealed class ReadWitness(AbortClock clock) : IReviewedFileAccess
{
    private readonly VerifiedReviewedFileAccess inner = new();
    internal int Reads;
    public ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path) => inner.InspectMetadata(snapshot, path);
    public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path) => inner.Probe(snapshot, path);
    public async ValueTask<ReviewedFileRead> ReadAsync(ReviewedSnapshot snapshot, string path,
        ReviewedFileProbe expected, CancellationToken token)
    {
        var read = await inner.ReadAsync(snapshot, path, expected, token);
        Reads++; clock.Arm(); return read;
    }
}
