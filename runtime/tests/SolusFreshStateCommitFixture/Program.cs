using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            foreach (var key in new[] { "GITHUB_TOKEN", "GH_TOKEN", "NUGET_AUTH_TOKEN", "NuGetPackageSourceCredentials_solus" })
                Proof.Require(Environment.GetEnvironmentVariable(key) is null, "credential_free_process");
            Proof.Require(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64, "linux_x64");
            Proof.Require(args.Length == 5, "arguments");
            var root = Path.GetFullPath(args[0]); var mode = args[1]; var id = args[2];
            var now = long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            using var corpus = new Corpus(args[4]);
            var noProvider = mode.StartsWith("recover", StringComparison.Ordinal) ||
                mode.StartsWith("readback", StringComparison.Ordinal) || mode.StartsWith("reject-compat", StringComparison.Ordinal);
            using var fixture = new HostFixture(corpus, root, mode, !noProvider, now, inline: mode.EndsWith("-inline", StringComparison.Ordinal));
            await fixture.BindNamesAsync();
            using var handler = new ScriptedHandler(corpus, id) { PrivateReasoning = true };
            if (mode.StartsWith("readback", StringComparison.Ordinal) || mode.StartsWith("reject-compat", StringComparison.Ordinal))
            {
                await Readback(fixture, mode);
                return 0;
            }
            if (mode == "stale-head") handler.BeforeFinal = () => fixture.GitHub.CurrentOverride =
                fixture.GitHub.PullRequest with { HeadSha = new string('f', 40) };
            if (mode == "malformed") handler.Malformed = true;
            if (mode == "cancel-provider") handler.BeforeResponse = fixture.Fault.Cancellation.Cancel;
            if (mode == "cancel-before") fixture.Fault.Cancellation.Cancel();
            var composition = new SolusFreshStateCommitComposition(fixture.Host, () => handler,
                trustedProviderDecorator: mode == "capture-unavailable" ? provider => new ExportFailure(provider) :
                    mode == "compat-mismatch" ? provider => new CapabilitiesMismatch(provider) : null,
                trustedFileAccess: mode == "cancel-tool" ? _ => new CancellingFiles(fixture.Fault.Cancellation) : null,
                trustedCaptureDecorator: (mode.StartsWith("capture-", StringComparison.Ordinal) && mode != "capture-unavailable") || mode == "cancel-capture"
                    ? sink => new CaptureFault(sink, mode, fixture.Fault.Cancellation) : null,
                trustedCapturedRecord: record => SaveExpected(root, record));
            var prepared = await composition.RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token,
                [new(SolusAgent.Api.Execution.AgentInputSource.Repository, handler.Focus)]);
            var result = prepared.Value;
            Proof.Require(fixture.NativeCreates == 0, "zero_native_provider_construction");
            var accepted = result?.DurableStateAccepted is true;
            if (mode.StartsWith("success", StringComparison.Ordinal)) Proof.Require(result?.Work?.ProductReviewAccepted is true &&
                result.CurrentCaptureValid is true && accepted, "fresh_complete_acceptance");
            if (mode == "recover-unknown") Proof.Require(result?.Completion?.Status == ActionHostStatus.OutcomeAmbiguous &&
                !accepted && result.Work is null && handler.Sends == 0, "uncertain_intent_not_replayed");
            if (mode is "recover" or "recover-inline") Proof.Require(result?.RecoveredWithoutWork is true &&
                result.Work is null && result.CurrentCaptureValid is null && handler.Sends == 0,
                    ("recovered_without_provider_key_" + (result?.Completion ?? prepared.Failure)?.Status +
                        "_preparation_" + result?.PreparationFailure).ToLowerInvariant());
            if (mode.StartsWith("capture-", StringComparison.Ordinal)) Proof.Require(
                result?.Work?.ProductReviewAccepted is true && result.CurrentCaptureValid != true && !accepted,
                "capture_failure_preserves_work_no_state");
            if (mode is "stale-head" or "malformed" or "compat-mismatch" or "fail-candidate" or "fail-acceptance" or
                "unknown-unreadable-candidate" or "unknown-unwritten-sticky" or "fail-cleanup-upload" or "cancel-provider" or "cancel-before" or
                "cancel-tool" or "cancel-capture" or "cancel-candidate-before-upload" or "cancel-candidate-upload" or
                "cancel-publication_intent-upload")
                Proof.Require(!accepted, "incomplete_never_accepted");
            if (mode is "unknown-candidate" or "unknown-acceptance" or "unknown-sticky" or "fail-cleanup" or "cancel-acceptance-upload" or "cancel-sticky-upload" or "cancel-sticky-readback" or "cancel-accepted-cleanup")
                Proof.Require(accepted, ("known_committed_outcome_preserved_" + (result?.Completion ?? prepared.Failure)?.Status).ToLowerInvariant());
            if (mode is "success-inline" or "recover-inline") Proof.Require(result?.Completion?.Status ==
                (mode == "recover-inline" ? ActionHostStatus.ReviewedWithInlineWarnings : ActionHostStatus.Reviewed),
                "inline_projection_availability_truth");
            if (mode == "compat-mismatch") Proof.Require(handler.Sends == 0, "mismatch_before_exchange");
            if (mode == "stale-head") Proof.Require(result?.Work?.ProductReviewAccepted is true, "stale_preserves_work");
            var summary = new { mode, accepted, recovered = result?.RecoveredWithoutWork is true,
                work = result?.Work?.ProductReviewAccepted, capture = result?.CurrentCaptureValid,
                capture_status = result?.Work?.Context?.CaptureStatus.ToString(), status =
                    (result?.Completion ?? prepared.Failure)?.Status.ToString(),
                findings = result?.Completion?.Summary.FindingCount, sends = handler.Sends,
                native_creates = fixture.NativeCreates, checks = Proof.Checks };
            var output = JsonSerializer.Serialize(summary);
            foreach (var secret in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey, "R5_PRIVATE_REASONING_CANARY" })
            {
                Proof.Require(!output.Contains(secret, StringComparison.Ordinal), "safe_result_output");
                if (File.Exists(Path.Join(root, "sticky.txt"))) Proof.Require(
                    !File.ReadAllText(Path.Join(root, "sticky.txt")).Contains(secret, StringComparison.Ordinal), "safe_publication");
            }
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("APR_SOLUS_STATE_COMMIT_FAILED " +
                (exception is ProofFailure failure ? failure.Code : "unexpected"));
            return 1;
        }
    }

    private static void SaveExpected(string root, SolusRestrictedStateRecord record)
    {
        Proof.Require(SolusRestrictedStateSnapshotCodec.TryWrite(record, out var bytes), "capture_record_encoding");
        try
        {
            File.WriteAllText(Path.Join(root, "expected.json"), JsonSerializer.Serialize(new {
                record = SolusRestrictedStateSnapshotCodec.RecordSha256(bytes),
                payload = Convert.ToHexStringLower(SHA256.HashData(record.RestrictedPayload)),
                checkpoint = record.Checkpoint, count = record.PayloadByteCount,
                runtime = record.Compatibility.RuntimeConfigurationSha256,
                provider = record.Compatibility.ProviderConfigurationSha256 }));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task Readback(HostFixture fixture, string mode)
    {
        var expectedBytes = File.ReadAllBytes(Path.Join(fixture.Root, "expected.json"));
        using var expected = JsonDocument.Parse(expectedBytes);
        var observed = await fixture.Host.RunPreparedFreshAsync(fixture.Launch, async prepared =>
        {
            var execution = new SolusFreshReviewComposition(fixture.Host).PrepareStateExecution(prepared);
            var compatibility = mode.StartsWith("reject-compat", StringComparison.Ordinal) ? execution.Compatibility with {
                RuntimeConfigurationSha256 = new string('0', 64) } : execution.Compatibility;
            var request = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, execution.ExecutionId) };
            var restored = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(request, default);
            using var state = restored.Context;
            if (mode.StartsWith("reject-compat", StringComparison.Ordinal))
            {
                Proof.Require(!restored.Succeeded, "selected_current_incompatible_closed");
                return new ReadbackResult(true);
            }
            Proof.Require(restored.Succeeded && state is not null, "fresh_process_selected_acceptance");
            if (!RestrictedStateService.TryReadAcceptedSolusRecord(state!, out var record) || record is null)
                throw new ProofFailure("fresh_process_authenticated_record");
            Proof.Require(record.PayloadByteCount == expected.RootElement.GetProperty("count").GetInt32() &&
                Convert.ToHexStringLower(SHA256.HashData(record.RestrictedPayload)) == expected.RootElement.GetProperty("payload").GetString(),
                "exact_payload_after_process_restart");
            Proof.Require(SolusRestrictedStateSnapshotCodec.TryWrite(record, out var bytes), "readback_record_encoding");
            try { Proof.Require(SolusRestrictedStateSnapshotCodec.RecordSha256(bytes) ==
                expected.RootElement.GetProperty("record").GetString(), "exact_record_after_process_restart"); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            Proof.Require(JsonSerializer.Serialize(record.Checkpoint) == expected.RootElement.GetProperty("checkpoint").GetRawText(),
                "exact_checkpoint_after_process_restart");
            return new ReadbackResult(true);
        }, default);
        Proof.Require(observed.Value?.Verified is true && fixture.NativeCreates == 0, "readback_no_execution_or_claim");
        Console.WriteLine(JsonSerializer.Serialize(new { mode, readback = true, sends = 0, native_creates = fixture.NativeCreates }));
    }
    private sealed record ReadbackResult(bool Verified);
}

internal sealed class CaptureFault(IRestrictedContextSink inner, string mode, CancellationTokenSource cancellation) : IRestrictedContextSink
{
    public void Capture(AgentContextEnvelope context)
    {
        if (mode == "capture-missing") return;
        if (mode == "cancel-capture") { inner.Capture(context); cancellation.Cancel(); return; }
        if (mode == "capture-duplicate") { inner.Capture(context); inner.Capture(context); return; }
        if (mode == "capture-throw") { inner.Capture(context); throw new InvalidOperationException("S3_PRIVATE_CAPTURE_CANARY"); }
        if (mode == "capture-wrong-format")
        {
            var bytes = context.CopyRestrictedPayload();
            try { inner.Capture(new(context.ImplementationId, context.FormatVersion + 1, context.CompatibilityVersion, bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
            return;
        }
        if (mode == "capture-oversized")
        {
            inner.Capture(new(context.ImplementationId, context.FormatVersion, context.CompatibilityVersion,
                new byte[SolusRestrictedStateFormat.MaximumPayloadBytes + 1]));
            return;
        }
        throw new ProofFailure("unknown_capture_fault");
    }
}

internal sealed class ExportFailure(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => throw new InvalidOperationException("S3_PRIVATE_CAPTURE_CANARY");
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => false;
}
internal sealed class CapabilitiesMismatch(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities & ~ProviderCapabilities.UsageReporting;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
}
