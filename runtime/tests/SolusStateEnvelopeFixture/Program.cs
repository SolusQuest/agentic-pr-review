using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

try
{
    Check(!Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key =>
        key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("NUGET_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NuGetPackageSourceCredentials_", StringComparison.OrdinalIgnoreCase)));
    Check(typeof(AgentContextEnvelope).Assembly.GetName().Name == "SolusAgent.Api");
    var lockBytes = File.ReadAllBytes("runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json");
    Check(Convert.ToHexStringLower(SHA256.HashData(lockBytes)) == SolusRestrictedStateFormat.PackageClosureSha256);
    var compatibility = Compatibility();
    var execution = Guid.NewGuid();
    using var capture = new SolusRestrictedStateCapture(execution, compatibility);
    var (result, provider, observed) = await Run(execution, capture);
    Check(result.Context.CaptureStatus == ContextCaptureStatus.Delivered && provider.Calls == 1);
    Check(capture.TryComplete(result, out var record));
    Check(!capture.TryComplete(result, out _));
    var source = result.Context.Checkpoint!;
    Check(record!.Checkpoint == new SolusRestrictedStateCheckpoint(source.CheckpointId, source.LogicalWorkId, source.RoundId, source.ExecutionId));
    Check(record.CopyRestrictedPayload().AsSpan().SequenceEqual(observed.Context!.CopyRestrictedPayload()));

    var scope = new RestrictedStateScope("synthetic-repo", "synthetic-workflow", 1, "s1_session",
        "s1-controlled", "synthetic", "s1-adapter", Hash("policy"), Hash("limits"), Hash("no-tools"), "synthetic-build");
    AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var access);
    var binding = new RestrictedStateBinding(scope, new string('a', 40), new string('b', 40), 0, null, 1_700_000_000, 1_700_604_800);
    Check(SolusRestrictedStateSnapshotCodec.TrySeal(access, binding, record, new Keys(), out var envelope, out var digest, out _));
    var lineage = new AcceptedLineage(scope, 0, digest!, RestrictedStateEnvelope.EnvelopeSha256(envelope!), null,
        binding.AcceptedAtUnixSeconds, binding.ExpiresAtUnixSeconds, true);
    Check(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, lineage, envelope, compatibility,
        binding.AcceptedAtUnixSeconds, new Keys(), out var restored, out _));
    var reconstructed = new AgentContextEnvelope(restored!.Compatibility.ImplementationId,
        restored.Compatibility.FormatVersion, restored.Compatibility.CompatibilityVersion, restored.CopyRestrictedPayload());
    Check(reconstructed.CopyRestrictedPayload().AsSpan().SequenceEqual(observed.Context.CopyRestrictedPayload()));
    Check(reconstructed.ImplementationId == observed.Context.ImplementationId &&
        reconstructed.FormatVersion == observed.Context.FormatVersion && reconstructed.CompatibilityVersion == observed.Context.CompatibilityVersion);
    var returnedSource = new ContextCheckpointInfo(restored.Checkpoint.CheckpointId, restored.Checkpoint.LogicalWorkId,
        restored.Checkpoint.RoundId, restored.Checkpoint.ExecutionId);
    Check(returnedSource == source && returnedSource != source with { CheckpointId = Guid.NewGuid() });

    var negatives = 1; // repeated Complete above
    Rejected(() => new ContextExecutionResult(execution, ContextExecutionIntent.Fresh, ContextAdmission.Fresh,
        result.Context.Outcome, captureStatus: ContextCaptureStatus.Delivered,
        checkpoint: source with { CheckpointId = Guid.Empty })); negatives++;
    Rejected(() => new ContextExecutionResult(execution, ContextExecutionIntent.Fresh, ContextAdmission.Fresh,
        result.Context.Outcome, captureStatus: ContextCaptureStatus.Delivered,
        checkpoint: source with { ExecutionId = Guid.NewGuid() })); negatives++;
    using (var otherExecution = new SolusRestrictedStateCapture(Guid.NewGuid(), compatibility))
    {
        var actual = await Run(Guid.NewGuid(), otherExecution);
        Check(!otherExecution.TryComplete(actual.Result, out _)); negatives++;
    }
    using (var duplicate = new SolusRestrictedStateCapture(execution, compatibility))
    {
        duplicate.Capture(observed.Context);
        Rejected(() => duplicate.Capture(observed.Context));
        Check(!duplicate.TryComplete(result, out _)); negatives++;
    }
    using (var missing = new SolusRestrictedStateCapture(execution, compatibility))
    {
        var actual = await Run(execution, null);
        Check(actual.Result.Context.CaptureStatus == ContextCaptureStatus.NotRequested);
        Check(!missing.TryComplete(actual.Result, out _)); negatives++;
    }
    using (var incomplete = new SolusRestrictedStateCapture(execution, compatibility))
    {
        var actual = await Run(execution, incomplete, accept: false);
        Check(actual.Result.Candidates!.StopReason == CandidateStopReason.HostEnded);
        Check(actual.Result.Context.CaptureStatus == ContextCaptureStatus.Delivered);
        Check(!incomplete.TryComplete(actual.Result, out _)); negatives++;
    }
    foreach (var malformed in new[] {
        new AgentContextEnvelope(Guid.NewGuid(), 1, 1, [1]),
        new AgentContextEnvelope(SolusRestrictedStateFormat.Implementation, 2, 1, [1]),
        new AgentContextEnvelope(SolusRestrictedStateFormat.Implementation, 1, 2, [1]),
        new AgentContextEnvelope(SolusRestrictedStateFormat.Implementation, 1, 1, []),
        new AgentContextEnvelope(SolusRestrictedStateFormat.Implementation, 1, 1, new byte[196_609]),
    })
    {
        using var invalid = new SolusRestrictedStateCapture(execution, compatibility);
        var before = GC.GetAllocatedBytesForCurrentThread();
        Rejected(() => invalid.Capture(malformed));
        Check(GC.GetAllocatedBytesForCurrentThread() - before < 64 * 1024);
        Check(!invalid.TryComplete(result, out _)); negatives++;
    }
    using (var undelivered = new SolusRestrictedStateCapture(execution, compatibility))
    {
        Rejected(() => undelivered.Capture(new AgentContextEnvelope(Guid.NewGuid(), 1, 1, [1])));
        var actual = await Run(execution, undelivered);
        Check(actual.Result.Context.CaptureStatus == ContextCaptureStatus.Failed);
        Check(!undelivered.TryComplete(actual.Result, out _)); negatives++;
    }
    using (var continuing = new SolusRestrictedStateCapture(Guid.NewGuid(), compatibility))
    {
        var next = Request(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var agent = Agent(new ControlledProvider());
        var actual = await agent.ExecuteCandidatesWithContextAsync(new(next, ContextExecutionIntent.ContinueRun,
            reconstructed, new(Guid.NewGuid(), source, next.ExecutionId, Guid.NewGuid())), new(1, 0, 0), new Host(), continuing,
            cancellationToken: cancellation.Token);
        Check(actual.Context.Intent == ContextExecutionIntent.ContinueRun && actual.Context.Admission == ContextAdmission.NotAttempted);
        Check(!continuing.TryComplete(actual, out _)); negatives++;
    }
    using (var late = new SolusRestrictedStateCapture(execution, compatibility))
    {
        late.Dispose(); Rejected(() => late.Capture(observed.Context));
        Check(!late.TryComplete(result, out _)); negatives++;
    }
    Check(capture.ToString() == nameof(SolusRestrictedStateCapture) && record.ToString() == nameof(SolusRestrictedStateRecord));
    Console.WriteLine($"APR_SOLUS_STATE_PROOF capture=true byte_exact=true authenticated=true checkpoint=true negative_checks={negatives} credential_free=true http_calls=0");
    return 0;
}
catch
{
    Console.Error.WriteLine("APR_SOLUS_STATE_PROOF_FAILED");
    return 1;
}

static void Check(bool value) { if (!value) throw new InvalidOperationException("APR_SOLUS_STATE_CHECK"); }
static void Rejected(Action action)
{
    try { action(); } catch (ArgumentException) { return; } catch (InvalidOperationException) { return; }
    throw new Exception("APR_SOLUS_STATE_EXPECTED_REJECTION");
}
static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
static SolusRestrictedStateCompatibility Compatibility() => new(
    SolusRestrictedStateFormat.PackageVersion, Cr1DevelopmentProfile.Id, 1, SolusRestrictedStateFormat.Implementation,
    1, 1, SolusRestrictedStateFormat.PackageClosureSha256,
    Hash("s1-controlled|synthetic|tools=[]|inputs=32|tools=5|batch=3|request=65536|response=16384|continuation=2048|volatile|ordered,bounds|attempts=64|records=64|retained=196608|grace=1|requireContinuation=true"),
    Hash("synthetic-no-http-provider|model=synthetic|persistent-format=1"));
static AgentRequest Request(Guid? execution = null) => new(execution ?? Guid.NewGuid(), "S1 synthetic policy", [],
    new(1, TimeSpan.FromSeconds(10)), AgentCapability.None);
static IContextCandidateAgent Agent(ControlledProvider provider) => (IContextCandidateAgent)SolusRuntimeConstruction.Create(
    new(provider, [], new Hooks(), new(Cr1DevelopmentProfile.ProviderInputs, Cr1DevelopmentProfile.NonterminalDefinitions,
        Cr1DevelopmentProfile.ToolCallsPerBatch, Cr1DevelopmentProfile.RequestPayloadBytes,
        Cr1DevelopmentProfile.ResponsePayloadBytes, Cr1DevelopmentProfile.ContinuationBytes)), Cr1DevelopmentProfile.Id);
static async Task<(ContextCandidateExecutionResult Result, ControlledProvider Provider, ObservedSink Observed)> Run(
    Guid execution, SolusRestrictedStateCapture? sink, bool accept = true)
{
    var provider = new ControlledProvider(); var observed = new ObservedSink(sink);
    var result = await Agent(provider).ExecuteCandidatesWithContextAsync(new(Request(execution), ContextExecutionIntent.Fresh),
        new(1, 0, 0), new Host(accept), sink is null ? null : observed);
    return (result, provider, observed);
}
sealed class ObservedSink(SolusRestrictedStateCapture? sink) : IRestrictedContextSink
{
    internal AgentContextEnvelope? Context { get; private set; }
    public void Capture(AgentContextEnvelope context) { sink!.Capture(context); Context = context; }
}
sealed class Host(bool accept = true) : ICandidateHost
{
    public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
            CandidateAcknowledgement.Acknowledged, accept ? CandidateDecision.Accept : CandidateDecision.Reject, CandidateContinuation.End));
}
sealed class Hooks : IRuntimeExposureHooks
{
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken cancellationToken) =>
        ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken cancellationToken) =>
        ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
}
sealed class ControlledProvider() : ModelProvider(new("s1-controlled", "synthetic"),
    ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolCalls), IProviderContextPersistence
{
    internal int Calls { get; private set; }
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    {
        Calls++; observation.ObserveDispatch(DispatchExposure.Dispatched);
        observation.CaptureUsage(new(1, 1), new(UsageSettlement.Settled));
        return ValueTask.FromResult(new ProviderResponse(Scope, request.Attempt, ProviderFinish.Final, "S1_RESTRICTED_FINAL", [],
            new(Scope, request.Attempt, "S1_RESTRICTED_REPLAY"u8.ToArray())));
    }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => new(binding.Scope, binding.Origin, 1, [0, 255, 128]);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => false; // no restored execution in S1
}
sealed class Keys : IRestrictedStateKeyResolver
{
    public bool TryGetCurrentWriteKey(AuthorizedStateAccess access, out RestrictedStateKey? key)
    { key = new("s1-test", Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()); return true; }
    public bool TryGetApprovedReadKey(AuthorizedStateAccess access, string keyId, long expiresAtUnixSeconds, out RestrictedStateKey? key)
    { key = new(keyId, Enumerable.Range(0, 32).Select(x => (byte)x).ToArray()); return true; }
}
