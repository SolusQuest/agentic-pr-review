using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Api.Execution;

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
            var seed = mode.StartsWith("seed", StringComparison.Ordinal);
            var resetting = mode is "reset-claimed" or "reset-claimed-retry";
            var ahead = id == "ahead";
            if (ahead) id = seed || mode == "stale-evidence" ? "cs-defect" : "ts-defect";
            var recovering = mode is "recover" or "retry" or "review-reuse" or "recover-unknown";
            using var corpus = new Corpus(args[4]);
            var tight = id == "repository-rule-tight";
            if (tight) id = "repository-rule";
            using var fixture = new HostFixture(corpus, root, mode, seed || !recovering && mode is not ("readback" or "missing-key"), now,
                reviewJson: tight ? "{\"maxOutputTokens\":1044}" : mode == "policy-mismatch" ? "{\"maxModelCalls\":1}" : null,
                continuation: !mode.EndsWith("-reuse", StringComparison.Ordinal),
                ahead: ahead && !seed, verifiedAhead: mode != "unverified-ahead");
            await fixture.BindNamesAsync();
            if (mode == "invalid-event") fixture.Authorization.EventReader.Bytes = [];
            if (mode is "readback" or "readback-reset") { await Readback(fixture); return 0; }
            using var handler = new ScriptedHandler(corpus, id) { PrivateReasoning = true, Restored = !seed && !resetting,
                Malformed = mode == "malformed-response", MissingUsage = mode == "missing-usage",
                MixedFinal = mode == "mixed-final", Ahead = ahead && !seed && mode != "stale-evidence" };
            if (mode == "cancel-provider") handler.BeforeResponse = fixture.Fault.Cancellation.Cancel;
            if (mode == "stale-head") handler.BeforeFinal = () => fixture.GitHub.CurrentOverride =
                fixture.GitHub.PullRequest with { HeadSha = new string('f', 40) };
            if (mode == "cancel-before") fixture.Fault.Cancellation.Cancel();
            var label = resetting ? "reset" : seed ? "source" : "target";
            SolusFreshReviewResult? work;
            ActionHostCompletion? completion;
            bool? capture;
            var accepted = false;
            ImportFailure? import = null;
            MeteredProvider? meter = null;
            Proof.Stage = seed ? "seed" : "restore";
            if (seed || resetting)
            {
                var result = await new SolusFreshStateCommitComposition(fixture.Host, () => handler,
                    trustedCaptureDecorator: mode.StartsWith("seed-source-", StringComparison.Ordinal)
                        ? sink => new SourceFault(sink, mode) : null,
                    trustedCapturedRecord: record => { SaveCaptured(root, label, record); fixture.Fault.CurrentReviewCaptured = true; })
                    .RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token,
                        [new(SolusAgent.Api.Execution.AgentInputSource.Repository, handler.Focus),
                         new(SolusAgent.Api.Execution.AgentInputSource.Repository, ScriptedHandler.PriorOnly)]);
                work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
                capture = result.Value?.CurrentCaptureValid; accepted = result.Value?.DurableStateAccepted is true;
                if (mode == "reset-claimed-retry")
                    Proof.Require(result.Value?.RecoveredWithoutWork is true && work is null && capture is null &&
                        handler.Sends == 0 && completion?.Accounting.IsCompleteZero is true,
                        "claimed_reset_reentry_without_work");
                else Proof.Require(work?.ProductReviewAccepted is true && capture is true && accepted,
                    resetting ? "actual_claimed_source_reset_accepted" : "real_seed_accepted");
                if (mode == "reset-claimed") Proof.Require(work?.Context?.History.Count == 0,
                    "claimed_reset_has_fresh_sdk_history");
            }
            else
            {
                var result = await new SolusRestoredReviewComposition(fixture.Host, () => handler,
                    trustedProviderDecorator: provider => {
                        IModelProvider actual = mode is "reject-import" or "reject-import-next" ? import = new(provider) :
                            mode == "capture-unavailable" ? new ExportFailure(provider) :
                            mode == "capabilities-mismatch" ? new CapabilitiesMismatch(provider) : provider;
                        return meter = new(actual);
                    },
                    trustedFileAccess: mode == "cancel-tool" ? _ => new CancellingFiles(fixture.Fault.Cancellation) : null,
                    trustedCaptureDecorator: mode.StartsWith("capture-", StringComparison.Ordinal) && mode != "capture-unavailable" || mode == "cancel-capture"
                        ? sink => new CaptureFault(sink, mode, fixture.Fault.Cancellation) : null,
                    trustedCapturedRecord: record => { SaveCaptured(root, label, record); fixture.Fault.CurrentReviewCaptured = true; })
                    .RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token,
                        Inputs(mode, handler));
                work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
                capture = result.Value?.CurrentCaptureValid; accepted = result.Value?.DurableStateAccepted is true;
                if (mode == "review")
                {
                    Proof.Require(work?.ProductReviewAccepted is true && capture is true && accepted && handler.Sends > 0,
                        ("second_review_accepted_" + completion?.Status + "_" + work?.Failure + "_" +
                            work?.Context?.Admission + "_" + work?.Work?.Termination + "_" + handler.FailureCode).ToLowerInvariant());
                    var context = work!.Context!;
                    using var source = JsonDocument.Parse(File.ReadAllBytes(Path.Join(root, "source.json")));
                    var old = source.RootElement;
                    Proof.Require(context.Intent == ContextExecutionIntent.NewRunFromContext && context.Admission == ContextAdmission.Supplied &&
                        context.History.Count == 1 && context.Checkpoint is not null &&
                        context.History[0].Usage.Attempts.Count == old.GetProperty("attempts").GetInt32() &&
                        work.Work?.CurrentUsage?.Attempts.Count == handler.Sends &&
                        context.Checkpoint.RoundId.ToString("N") != old.GetProperty("round").GetString() &&
                        context.Checkpoint.LogicalWorkId.ToString("N") != old.GetProperty("work").GetString() &&
                        context.ExecutionId.ToString("N") != old.GetProperty("execution").GetString(), "distinct_current_round_and_usage");
                    foreach (var fact in ahead ? new[] { Proof.DecisiveFacts[2], "export const timeout = config.timeoutMs || 5000;" } : Proof.RequiredFacts(id))
                        Proof.Require(handler.ReadFacts.Contains(fact), "useful_current_source_facts");
                    if (ahead) Proof.Require(work.Review?.Findings.Length == 1 &&
                        work.Review.Findings[0].Evidence.All(e => e.Path == "src/client.ts" && e.StartLine == 2),
                        "successor_fixes_old_defect_and_finds_current_condition");
                    Proof.Require(work.Observations.Length == handler.Turns - 1, "current_tools_only_no_replay");
                }
                if (recovering && mode != "recover-unknown") Proof.Require(result.Value?.RecoveredWithoutWork is true && work is null && capture is null &&
                    handler.Sends == 0 && completion?.Accounting.IsCompleteZero is true,
                    ("recovery_" + completion?.Status + "_" + completion?.Summary.StateDisposition + "_" +
                        result.Failure?.Status + "_" + work?.Failure + "_sends_" + handler.Sends).ToLowerInvariant());
                if (mode is "reject-import" or "reject-import-next") Proof.Require(!accepted && work?.Context?.Admission == ContextAdmission.Rejected &&
                    work.Work is null && handler.Sends == 0 && import?.Imports == 1 &&
                    completion?.TerminationReason == ActionHostTerminationReason.NotStarted && completion.Accounting.IsCompleteZero,
                    mode == "reject-import-next" ? ("import_rejection_has_no_provider_work_" +
                        completion?.Status + "_" + completion?.Summary.StateDisposition + "_" +
                        result.Value?.PreparationFailure + "_" + work?.Failure + "_" + work?.Context?.Admission +
                        "_imports_" + (import?.Imports ?? 0) + "_sends_" + handler.Sends + "_" +
                        completion?.TerminationReason + "_zero_" + completion?.Accounting.IsCompleteZero + "_" +
                        fixture.Fault.StorageObservationCode).ToLowerInvariant()
                        : "import_rejection_has_no_provider_work");
                if (mode == "spent") Proof.Require(!accepted && work is null && handler.Sends == 0 &&
                    completion?.Status == ActionHostStatus.StateConflict, "spent_source_not_reused");
                if (mode == "wrong-producer" || mode.StartsWith("invalid-producer", StringComparison.Ordinal)) Proof.Require(!accepted && work is null && handler.Sends == 0,
                    "foreign_producing_run_refused");
                if (mode.StartsWith("capture-", StringComparison.Ordinal)) Proof.Require(
                    work?.ProductReviewAccepted is true && capture is false && !accepted,
                    "valid_work_independent_of_capture");
                if (mode is "malformed-response" or "missing-usage" or "mixed-final" or "cancel-provider" or "cancel-tool" or "cancel-capture")
                    Proof.Require(!accepted && handler.Sends > 0, "failed_current_work_not_accepted");
                if (mode is "unauthorized-input" or "input-over" or "input-count-over" or "missing-key" or "cancel-before" or "capabilities-mismatch" or "invalid-event")
                    Proof.Require(!accepted && handler.Sends == 0, "current_admission_refusal_no_exchange");
                if (mode == "input-exact") Proof.Require(accepted && work?.ProductReviewAccepted is true,
                    "exact_current_data_item_boundary");
                if (mode == "input-cumulative") Proof.Require(!accepted && handler.Sends < handler.Turns,
                    "retained_inputs_not_dropped_to_fit_current_round");
                if (mode == "stale-head") Proof.Require(!accepted && work?.ProductReviewAccepted is true && capture is true,
                    "current_work_preserved_before_stale_publication");
                if (mode == "tamper-source") Proof.Require(!accepted && work is null && handler.Sends == 0,
                    "authenticated_source_tamper_refused");
                if (mode == "policy-mismatch") Proof.Require(!accepted && work is null && handler.Sends == 0,
                    "changed_compatibility_refused_before_work");
                if (mode == "malformed-source") Proof.Require(!accepted && work?.Context?.Admission == ContextAdmission.Rejected &&
                    work.Work is null && handler.Sends == 0 && completion?.Accounting.IsCompleteZero is true,
                    "unsupported_saved_context_has_no_invented_work");
                if (mode == "unverified-ahead") Proof.Require(!accepted && work is null && handler.Sends == 0,
                    "no_unverified_ancestry_authority");
                if (mode == "stale-evidence") Proof.Require(!accepted && work?.ProductReviewAccepted is false && handler.Sends > 0,
                    "historical_evidence_cannot_ground_current_finding");
                if (mode.StartsWith("fail-", StringComparison.Ordinal) || mode.StartsWith("unknown-", StringComparison.Ordinal))
                    Proof.Require(!accepted || mode is "unknown-context_claim" or "unknown-sticky" or "unknown-acceptance" or "fail-cleanup",
                        "storage_failure_truthful_acceptance");
                if (mode == "recover-unknown") Proof.Require(!accepted && work is null && handler.Sends == 0,
                    "unknown_intent_never_replays_work_or_publication");
            }
            Proof.Require(fixture.NativeCreates == 0, "zero_native_provider");
            var output = JsonSerializer.Serialize(new { mode, accepted, work = work?.ProductReviewAccepted,
                admission = work?.Context?.Admission.ToString(), capture, status = completion?.Status.ToString(),
                sends = handler.Sends, native_creates = fixture.NativeCreates,
                current_attempts = work?.Work?.CurrentUsage?.Attempts.Count,
                historical_attempts = work?.Context?.History.Sum(round => round.Usage.Attempts.Count),
                current_tools = work?.Observations.Length, wire_request_bytes = handler.RequestBytes,
                wire_response_bytes = handler.ResponseBytes, logical_request_bytes = meter?.MaxRequestBytes,
                logical_response_bytes = meter?.MaxResponseBytes, provider_inputs = meter?.MaxInputs,
                input_reservation = work?.Work?.CurrentUsage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.InputTokens,
                output_reservation = work?.Work?.CurrentUsage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.OutputTokens,
                input_tokens = work?.Work?.CurrentUsage?.InputTokens.ObservedTokens,
                output_tokens = work?.Work?.CurrentUsage?.OutputTokens.ObservedTokens,
                capture_status = work?.Context?.CaptureStatus.ToString(), termination = work?.Work?.Termination.ToString(),
                findings = completion?.Summary.FindingCount, checks = Proof.Checks });
            foreach (var secret in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey,
                ScriptedHandler.PriorOnly, ScriptedHandler.CurrentOnly, "R5_PRIVATE_REASONING_CANARY" })
            {
                Proof.Require(!output.Contains(secret, StringComparison.Ordinal), "safe_result_output");
                if (File.Exists(Path.Join(root, "sticky.txt"))) Proof.Require(
                    !File.ReadAllText(Path.Join(root, "sticky.txt")).Contains(secret, StringComparison.Ordinal), "safe_publication");
            }
            Console.WriteLine(output);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("APR_SOLUS_RESTORED_FAILED " + Proof.Stage + "_" +
                (error is ProofFailure failure ? failure.Code : "unexpected"));
            return 1;
        }
    }

    private static AgentInput[] Inputs(string mode, ScriptedHandler handler)
    {
        var data = new List<AgentInput> { new(AgentInputSource.Repository, handler.Focus),
            new(AgentInputSource.Repository, ScriptedHandler.CurrentOnly) };
        if (mode == "unauthorized-input") data.Add(new(AgentInputSource.Model, "untrusted model control"));
        if (mode is "input-exact" or "input-over") data.Add(new(AgentInputSource.Repository,
            new string('x', mode == "input-exact" ? 4096 : 4097)));
        if (mode is "input-count-over" or "input-cumulative")
            for (var i = 0; i < (mode == "input-count-over" ? 30 : 20); i++)
                data.Add(new(AgentInputSource.Repository, "current classified item " + i));
        return data.ToArray();
    }

    private static void SaveCaptured(string root, string label, SolusRestrictedStateRecord record)
    {
        Proof.Stage = "capture";
        var payload = record.CopyRestrictedPayload();
        try
        {
            using var document = JsonDocument.Parse(payload);
            var value = document.RootElement;
            var rounds = value.GetProperty("Rounds");
            var candidates = value.GetProperty("Candidates");
            File.WriteAllText(Path.Join(root, label + ".json"), JsonSerializer.Serialize(new {
                payload = Convert.ToHexStringLower(SHA256.HashData(record.RestrictedPayload)),
                encoded_bytes = record.PayloadByteCount, records = value.GetProperty("Records").GetArrayLength(),
                attempts = rounds.EnumerateArray().Sum(round => round.GetProperty("Usage").GetProperty("Attempts").GetArrayLength()),
                rounds = rounds.GetArrayLength(), members = value.GetProperty("Members").GetArrayLength(),
                episodes = candidates.GetProperty("Episodes").GetArrayLength(),
                submissions = candidates.GetProperty("Submissions").GetArrayLength(),
                execution = record.Checkpoint.ExecutionId.ToString("N"), round = record.Checkpoint.RoundId.ToString("N"),
                work = record.Checkpoint.LogicalWorkId.ToString("N") }));
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static async Task Readback(HostFixture fixture)
    {
        Proof.Stage = "readback";
        var phase = "not_entered";
        var reset = fixture.Fault.Mode == "readback-reset";
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Path.Join(fixture.Root, reset ? "reset.json" : "target.json")));
        var result = await fixture.Host.RunPreparedFreshAsync(fixture.Launch, async prepared =>
        {
            phase = "state_restore";
            var execution = new SolusFreshReviewComposition(fixture.Host).PrepareStateExecution(prepared);
            var restored = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, execution.Compatibility, execution.ExecutionId,
                    SolusTransactionMode.CompletedContextReview) }, default);
            using var state = restored.Context;
            Proof.Require(restored.Succeeded && state is not null &&
                RestrictedStateService.TryReadAcceptedSolusRecord(state, out var record) && record is not null &&
                Convert.ToHexStringLower(SHA256.HashData(record.RestrictedPayload)) == expected.RootElement.GetProperty("payload").GetString() &&
                record.Checkpoint.ExecutionId.ToString("N") == expected.RootElement.GetProperty("execution").GetString() &&
                record.Checkpoint.RoundId.ToString("N") == expected.RootElement.GetProperty("round").GetString() &&
                record.Checkpoint.LogicalWorkId.ToString("N") == expected.RootElement.GetProperty("work").GetString(),
                "exact_successor_authenticated_after_restart");
            // The accessor authenticates the official selector's generation and
            // predecessor binding. S2's source-consumption selector also requires
            // NoPendingWork; it is deliberately not a general read-only inspector.
            phase = "verified";
            return new ReadbackResult(true);
        }, default);
        Proof.Require(result.Value?.Verified is true && fixture.NativeCreates == 0,
            ("readback_no_provider_" + phase + "_" + result.Failure?.Status).ToLowerInvariant());
        Console.WriteLine(JsonSerializer.Serialize(new { mode = reset ? "readback-reset" : "readback", accepted = true, sends = 0, native_creates = 0 }));
    }
    private sealed record ReadbackResult(bool Verified);
}

internal sealed class ImportFailure(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    internal int Imports;
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) { Imports++; return false; }
}
