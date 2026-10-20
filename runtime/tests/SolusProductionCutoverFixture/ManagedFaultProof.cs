#if APR_SOLUS_MANAGED
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;
using SolusAgent.Runtime.Api.Exposure;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Supporting negative proof only. Genuine on-disk issuers and the actual
// package run remain in use; capture faults enter through compiled test seams.
internal static class ManagedFaultProof
{
    private static readonly string Head = new('b', 40), Next = new('c', 40);
    private static readonly string Fact = "APR_PRIOR_ONLY_" + new string('e', 64);
    internal static async Task<int> RunAsync(string root)
    {
        try
        {
            var revokedPath = Path.Join(root, "revoked");
            new Program.DirectRoot(revokedPath).Write(Head, Head, "before", Fact + "\n");
            using (var revoked = await Authority.Open(revokedPath))
            {
                Check(LiveAgentFreshProcessFileSystem.TryCreate(revokedPath, out var other), "other_owner");
                Check(SolusDirectReviewAuthorization.TryCreate(other!, revoked.Root) is null, "owner_reference");
                new Program.DirectRoot(revokedPath).Write(Head, Head, "after", Fact + "\n");
                Check(!revoked.Authorization.IsLive && SolusFreshHostContext.TryCreateDirect(revoked.Authorization) is null,
                    "changed_authorization_revokes");
            }
            await ActualFreshProviderBodyWithholdsFactAndSecrets(root);
            await ActualAcceptedCommitFailurePreservesPrimaryCause(root);
            await InvalidSecrets(root);
            await ActualDirectOwnedBuffersDisposeAcrossCallerFailure(root);
            await PrecancelledCurrentReviewDoesNoWorkOrConsumeSource(root);
            foreach (var mode in new[] { "missing", "duplicate", "wrong-format", "throw", "cancel" })
            {
                var path = Path.Join(root, mode);
                var input = new Program.DirectRoot(path);
                input.Write(Head, Head, "capture_first", Fact + "\n");
                using var authority = await Authority.Open(path);
                using var cancellation = new CancellationTokenSource();
                SetSecrets();
                var execution = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                    null, 0, null, authority.Sink, cancellation.Token,
                    trustedCaptureDecorator: sink => new CaptureFault(sink, mode, cancellation));
                Check(execution.Execution.Result is { ModelCalls: 2, ToolCalls: 1, AcceptedGeneration: null,
                    HandoffReady: false } && !File.Exists(input.LineagePath), "capture_incomplete");
                Check(execution.Execution.DiagnosticCode == R3LiveAgentDiagnosticCodes.AgentRunFailed,
                    "capture_current_stage");
                Check(await authority.EmptyAccepted(), "capture_no_acceptance");
            }

            // A structurally valid encrypted record can still contain an opaque
            // runtime payload that fails import. The trusted fixture seeds it
            // through the real typed transaction, not a copied context codec.
            var importPath = Path.Join(root, "import");
            var importInput = new Program.DirectRoot(importPath);
            importInput.Write(Head, Head, "source", Fact + "\n");
            using (var source = await Authority.Open(importPath)) await source.Seed(false);
            var lineage = File.ReadAllBytes(importInput.LineagePath);
            importInput.Write(Head, Next, "import_first", "Current input only.\n", lineage);
            using (var first = await Authority.Open(importPath))
            {
                SetSecrets();
                var rejected = await first.Run();
                Check(rejected.Execution.Result is { ModelCalls: 0, ToolCalls: 0, AcceptedGeneration: null,
                    HandoffReady: false }, "import_no_work");
            }
            var spent = importInput.StateFiles();
            Check(File.ReadAllBytes(importInput.LineagePath).AsSpan().SequenceEqual(lineage), "import_preserves_source");
            importInput.Write(Head, Next, "import_second", "Current input only.\n", lineage);
            using (var second = await Authority.Open(importPath))
            {
                SetSecrets();
                var rejected = await second.Run();
                Check(rejected.Execution.Result is { Code: RestrictedStateCodes.Conflict, ModelCalls: 0,
                    ToolCalls: 0, AcceptedGeneration: null }, "import_claim_spent");
            }
            Check(importInput.StateFiles().SequenceEqual(spent), "import_no_second_claim");

            var incompatiblePath = Path.Join(root, "incompatible");
            var incompatibleInput = new Program.DirectRoot(incompatiblePath);
            incompatibleInput.Write(Head, Head, "source", Fact + "\n");
            using (var source = await Authority.Open(incompatiblePath)) await source.Seed(true);
            var incompatibleLineage = File.ReadAllBytes(incompatibleInput.LineagePath);
            var incompatibleState = incompatibleInput.StateFiles();
            incompatibleInput.Write(Head, Next, "incompatible_first", "Current input only.\n", incompatibleLineage);
            using (var current = await Authority.Open(incompatiblePath))
            {
                SetSecrets();
                var rejected = await current.Run();
                Check(rejected.Execution.Result is { ModelCalls: 0, ToolCalls: 0, AcceptedGeneration: null,
                    HandoffReady: false }, "incompatible_no_work");
                Check(rejected.Execution.DiagnosticCode == R3LiveAgentDiagnosticCodes.StateRestoreFailed,
                    "incompatible_current_stage");
            }
            Check(incompatibleInput.StateFiles().SequenceEqual(incompatibleState) &&
                File.ReadAllBytes(incompatibleInput.LineagePath).AsSpan().SequenceEqual(incompatibleLineage),
                "incompatible_no_mutation");
            Console.WriteLine("APR_SOLUS_DIRECT_FAULTS capture_cases=5 import_burn=true repeated_claim_refused=true incompatible_no_work=true");
            return 0;
        }
        catch (Exception exception)
        {
            var code = CutoverProofFailure.Describe(exception);
            Console.Error.WriteLine("APR_SOLUS_DIRECT_FAULTS_FAILED " + code);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable, null);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, null);
        }
    }

    private static async Task ActualAcceptedCommitFailurePreservesPrimaryCause(string root)
    {
        var path = Path.Join(root, "accepted-publication-failure");
        var input = new Program.DirectRoot(path);
        input.Write(Head, Head, "accepted_publication_failure", Fact + "\n");
        using var authority = await Authority.Open(path);
        Check(await authority.EmptyAccepted() && !File.Exists(input.LineagePath), "commit_failure_initial_absence");
        var sink = new ThrowingPublicationSink();
        SetSecrets();
        var failed = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
            null, 0, null, sink, CancellationToken.None);
        Check(failed.Execution.Result is { Code: R3LiveAgentCodes.HandoffUnavailable, ModelCalls: 2,
            ToolCalls: 1, AcceptedGeneration: 0, HandoffReady: false } && failed.Execution.DiagnosticCode is null,
            "commit_failure_primary_cause");
        Check(sink.Calls == 1 && sink.Accepted is { Generation: 0 } &&
            !File.Exists(input.LineagePath) && authority.Sink.PublicationReceipt is null,
            "commit_failure_no_output_lineage");
        Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "commit_failure_key");
        using var ownedKeys = keys!;
        var stored = await new RestrictedStateOpaqueSnapshotStore(
            authority.Files.CreateStateStore(authority.Root, authority.Root.StateRootPath), keys!)
            .ReadAsync(authority.Root.Access, CancellationToken.None);
        Check(stored.Succeeded && stored.Snapshot is { Accepted.Length: 1, Staging: null },
            "commit_failure_durable_acceptance");
        var accepted = stored.Snapshot!.Accepted[0];
        Check(accepted.Binding.Generation == 0 && accepted.Binding.Scope == authority.Root.Access.Scope &&
            accepted.SessionSha256 == failed.Execution.Result.AcceptedSessionSha256 &&
            accepted.EnvelopeSha256 == failed.Execution.Result.AcceptedEnvelopeSha256 &&
            sink.Accepted!.SessionSha256 == accepted.SessionSha256 &&
            sink.Accepted.EnvelopeSha256 == accepted.EnvelopeSha256 &&
            sink.Accepted.Scope == accepted.Binding.Scope &&
            accepted.Binding.PredecessorEnvelopeSha256 is null &&
            sink.Accepted.ExpectedPredecessorEnvelopeSha256 is null,
            "commit_failure_exact_accepted_binding");
        Check(RestrictedStateEnvelope.TryDecrypt(authority.Root.Access, accepted.Binding,
            accepted.Envelope, keys!, out var plaintext, out _), "commit_failure_authenticated_record");
        try
        {
            using var targets = SolusFreshHostContext.TryCreateDirect(authority.Authorization);
            Check(targets is not null, "commit_failure_targets");
            var tools = new SolusSnapshotTools(Guid.NewGuid(), authority.Authorization.Snapshot,
                new SnapshotToolExecutor(authority.Authorization.Snapshot, authority.Authorization.CreateFileAccess()));
            var expected = SolusRuntimeConstruction.DescribeFreshConfiguration(targets!, tools.Registrations, new Hooks());
            Check(SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext!) == accepted.SessionSha256 &&
                SolusRestrictedStateSnapshotCodec.TryRead(plaintext!, expected, out var record, out _) &&
                record is { PayloadByteCount: > 0 }, "commit_failure_actual_current_record");
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
        input.RequireEncryptedState(Fact);
        Check(Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable) is null &&
            Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable) is null,
            "commit_failure_environment_cleared");
    }

    private sealed class ThrowingPublicationSink : ILiveAgentAcceptedLineageSink
    {
        internal int Calls { get; private set; }
        internal AcceptedLineage? Accepted { get; private set; }
        public LiveAgentLineagePublicationOutcome PublishAtomically(AcceptedLineage? priorLineage,
            AcceptedLineage acceptedLineage, CancellationToken cancellationToken)
        {
            Calls++;
            Check(priorLineage is null && Calls == 1, "commit_failure_single_publish");
            Accepted = acceptedLineage;
            throw new InvalidOperationException("synthetic_commit_publication_failure");
        }
    }

    private static async Task ActualFreshProviderBodyWithholdsFactAndSecrets(string root)
    {
        // First vary only the decorator in a separately authorized fresh world.
        // The exact same credential and public input bytes must complete through
        // the original transport before any observer result is interpreted.
        var controlPath = Path.Join(root, "body-privacy-control");
        var controlInput = new Program.DirectRoot(controlPath);
        controlInput.Write(Head, Head, "body_privacy", Fact + "\n");
        string[] controlInputs;
        using (var control = await Authority.Open(controlPath))
        {
            Check(await control.EmptyAccepted() && !File.Exists(controlInput.LineagePath),
                "body_privacy_control_initial_absence");
            controlInputs = control.Authorization.PublicInputs.Select(bytes =>
                Convert.ToHexStringLower(SHA256.HashData(bytes))).ToArray();
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable,
                FreshBodyObserver.Provider);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, Program.Key);
            var baseline = await SolusDirectReviewComposition.RunAsync(control.Files, control.Authorization,
                null, 0, null, control.Sink, CancellationToken.None);
            Check(baseline.Execution.Result is { Code: R3LiveAgentCodes.Completed, ModelCalls: 2,
                ToolCalls: 1, AcceptedGeneration: 0, HandoffReady: true }, "body_privacy_control_completed");
            var baselineLineage = LiveAgentFreshProcessCodec.ReadLineage(File.ReadAllBytes(controlInput.LineagePath));
            Check(control.Sink.PublicationReceipt is not null && baselineLineage is { Generation: 0 } &&
                baselineLineage.SessionSha256 == baseline.Execution.Result.AcceptedSessionSha256 &&
                baselineLineage.EnvelopeSha256 == baseline.Execution.Result.AcceptedEnvelopeSha256,
                "body_privacy_control_lineage");
            controlInput.RequireEncryptedState(Fact);
            Check(Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable) is null &&
                Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable) is null,
                "body_privacy_control_environment_cleared");
        }
        var path = Path.Join(root, "body-privacy");
        var input = new Program.DirectRoot(path);
        input.Write(Head, Head, "body_privacy", Fact + "\n");
        using var authority = await Authority.Open(path);
        Check(controlInputs.SequenceEqual(authority.Authorization.PublicInputs.Select(bytes =>
            Convert.ToHexStringLower(SHA256.HashData(bytes)))), "body_privacy_control_same_inputs");
        Check(await authority.EmptyAccepted() && !File.Exists(input.LineagePath), "body_privacy_initial_absence");
        Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable,
            FreshBodyObserver.Provider);
        Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, Program.Key);
        FreshBodyObserver? observed = null;
        var completed = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
            null, 0, null, authority.Sink, CancellationToken.None,
            trustedTransportDecorator: transport => observed = new FreshBodyObserver(transport));
        Check(observed is not null, "body_privacy_observer_created");
        Check(observed!.BodiesWithheldSecrets, "body_privacy_bodies_withheld_secrets");
        Check(observed.HeadersAuthorized, "body_privacy_headers_authorized");
        if (observed.Requests != 2)
        {
            Check(observed.EnteredRequests > 0, "body_privacy_dispatch_observed");
            Check(observed.BufferedRequests == observed.EnteredRequests, "body_privacy_buffer_completed");
            Check(observed.Requests == observed.BufferedRequests, "body_privacy_read_completed");
        }
        Check(observed is { Requests: 2, EnteredRequests: 2, BufferedRequests: 2 }, "body_privacy_observed_requests");
        Check(observed.FirstWithheldFact, "body_privacy_first_withheld_fact");
        Check(observed.SecondUsedFact, "body_privacy_second_used_fact");
        Check(completed.Execution.Result is { Code: R3LiveAgentCodes.Completed, ModelCalls: 2,
            ToolCalls: 1, AcceptedGeneration: 0, HandoffReady: true } && File.Exists(input.LineagePath),
            "body_privacy_real_completion");
        var lineage = LiveAgentFreshProcessCodec.ReadLineage(File.ReadAllBytes(input.LineagePath));
        Check(authority.Sink.PublicationReceipt is not null && lineage is { Generation: 0 } &&
            lineage.SessionSha256 == completed.Execution.Result.AcceptedSessionSha256 &&
            lineage.EnvelopeSha256 == completed.Execution.Result.AcceptedEnvelopeSha256,
            "body_privacy_single_generation");
        input.RequireEncryptedState(Fact);
        Check(Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable) is null &&
            Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable) is null,
            "body_privacy_environment_cleared");
    }

    // Observe the actual SDK-serialized body, then forward the original request
    // to the real deterministic transport. The credential belongs in the HTTP
    // authorization header; body privacy is a separate assertion. These fresh
    // observations do not establish restored history or count secret-source reads.
    private sealed class FreshBodyObserver(SolusDirectTransport inner) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker forward = new(inner, disposeHandler: true);
        internal const string Provider = "provider-secret-must-not-enter-body";
        internal int Requests { get; private set; }
        internal int EnteredRequests { get; private set; }
        internal int BufferedRequests { get; private set; }
        internal bool FirstWithheldFact { get; private set; }
        internal bool SecondUsedFact { get; private set; }
        internal bool BodiesWithheldSecrets { get; private set; } = true;
        internal bool HeadersAuthorized { get; private set; } = true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            EnteredRequests++;
            Check(request.Content is not null && Requests < 2, "body_privacy_request_shape");
            // Buffering retains HttpContent for the unchanged inner handler.
            // This is the selected SDK adapter's existing wire byte bound.
            await request.Content!.LoadIntoBufferAsync(Cr1DevelopmentProfile.WireRequestBytes, token);
            BufferedRequests++;
            var bytes = await request.Content.ReadAsByteArrayAsync(token);
            try
            {
                Check(bytes.Length is > 0 and <= Cr1DevelopmentProfile.WireRequestBytes,
                    "body_privacy_request_bound");
                Requests++;
                BodiesWithheldSecrets &= bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Provider)) < 0 &&
                    bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Program.Key)) < 0;
                HeadersAuthorized &= request.Headers.Authorization is { Scheme: "Bearer", Parameter: Provider };
                var containsFact = bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(Fact)) >= 0;
                if (Requests == 1) FirstWithheldFact = !containsFact;
                else SecondUsedFact = containsFact;
                Check(BodiesWithheldSecrets && HeadersAuthorized, "body_privacy_credentials");
                return await forward.SendAsync(request, token);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) forward.Dispose();
            base.Dispose(disposing);
        }
    }

    private static async Task PrecancelledCurrentReviewDoesNoWorkOrConsumeSource(string root)
    {
        async Task Reject(Authority authority, Program.DirectRoot input)
        {
            var before = StateSnapshot(authority.Root.StateRootPath);
            var lineage = File.Exists(input.LineagePath) ? File.ReadAllBytes(input.LineagePath) : null;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            SetSecrets();
            var actual = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                authority.Prior, authority.Prior is null ? 0 : authority.Prior.Lineage.Generation + 1,
                authority.Prior?.Lineage.EnvelopeSha256, authority.Sink, cancellation.Token);
            Check(actual.Execution.Result is { ModelCalls: 0, ToolCalls: 0, AcceptedGeneration: null,
                AcceptedSessionSha256: null, AcceptedEnvelopeSha256: null, HandoffReady: false }, "precancel_zero_work");
            // Read the actual current classification. An unexpected result is
            // reported through a fixed code, never private exception details.
            var code = actual.Execution.Result.Code;
            Check(code == RestrictedStateCodes.Cancelled, code switch {
                RestrictedStateCodes.Conflict => "precancel_classified_conflict",
                LiveAgentFreshProcessCodes.RootInvalid => "precancel_classified_root_invalid",
                _ => "precancel_classification_unexpected" });
            Check(authority.Sink.PublicationReceipt is null, "precancel_no_publication_receipt");
            Check(Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable) is null &&
                Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable) is null,
                "precancel_environment_cleared");
            Check(StateSnapshot(authority.Root.StateRootPath).SequenceEqual(before), "precancel_state_unchanged");
            Check(lineage is null ? !File.Exists(input.LineagePath) :
                File.ReadAllBytes(input.LineagePath).AsSpan().SequenceEqual(lineage), "precancel_lineage_unchanged");
        }
        var freshPath = Path.Join(root, "precancel-fresh");
        var fresh = new Program.DirectRoot(freshPath);
        fresh.Write(Head, Head, "precancel_fresh", Fact + "\n");
        using (var authority = await Authority.Open(freshPath))
        {
            Check(await authority.EmptyAccepted(), "precancel_initial_absent");
            await Reject(authority, fresh);
            Check(await authority.EmptyAccepted(), "precancel_fresh_not_accepted");
        }
        // A real positive control creates usable current SDK history. The same
        // accepted source must remain available after a precancelled restore.
        var restoredPath = Path.Join(root, "precancel-restored");
        var restored = new Program.DirectRoot(restoredPath);
        restored.Write(Head, Head, "precancel_control", Fact + "\n");
        using (var authority = await Authority.Open(restoredPath))
        {
            SetSecrets();
            var control = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                null, 0, null, authority.Sink, CancellationToken.None);
            Check(control.Execution.Result is { Code: R3LiveAgentCodes.Completed, ModelCalls: 2, ToolCalls: 1,
                AcceptedGeneration: 0, HandoffReady: true } && File.Exists(restored.LineagePath), "precancel_valid_control");
        }
        var accepted = File.ReadAllBytes(restored.LineagePath);
        restored.Write(Head, Next, "precancel_restore", "Current input only.\n", accepted);
        using (var authority = await Authority.Open(restoredPath))
        {
            Check(authority.Prior?.Lineage.Generation == 0, "precancel_real_current_source");
            await Reject(authority, restored);
        }
        restored.Write(Head, Next, "precancel_retry", "Current input only.\n", accepted);
        using (var authority = await Authority.Open(restoredPath))
        {
            SetSecrets();
            var retry = await authority.Run();
            Check(retry.Execution.Result is { Code: R3LiveAgentCodes.Completed, ModelCalls: 2, ToolCalls: 1,
                AcceptedGeneration: 1, HandoffReady: true }, "precancel_source_not_burned");
        }
    }

    // Component ownership only: the real typed seed intentionally contains an
    // invalid SDK payload. This does not establish SDK import/history or expose
    // RunAsync locals, immutable records/strings or universal managed zeroing.
    private static async Task ActualDirectOwnedBuffersDisposeAcrossCallerFailure(string root)
    {
        var path = Path.Join(root, "owned-buffers");
        var input = new Program.DirectRoot(path);
        input.Write(Head, Head, "owned_source", Fact + "\n");
        using (var seed = await Authority.Open(path)) await seed.Seed(false);
        var lineage = File.ReadAllBytes(input.LineagePath);
        input.Write(Head, Next, "owned_failure", "Current input only.\n", lineage);
        using var authority = await Authority.Open(path);
        using var targets = SolusFreshHostContext.TryCreateDirect(authority.Authorization);
        Check(targets is not null && authority.Prior is not null, "owned_current_authority");
        var tools = new SolusSnapshotTools(Guid.NewGuid(), authority.Authorization.Snapshot,
            new SnapshotToolExecutor(authority.Authorization.Snapshot, authority.Authorization.CreateFileAccess()));
        var compatibility = SolusRuntimeConstruction.DescribeFreshConfiguration(targets!, tools.Registrations, new Hooks());
        Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "owned_key");
        using var ownedKeys = keys!;
        var state = new SolusDirectStateComposer(authority.Files, authority.Authorization, targets!, compatibility,
            keys!, TimeProvider.System);
        var context = new RestrictedStateSessionAdmissionContext(authority.Authorization.Snapshot.Identity.BaseSha,
            authority.Authorization.Snapshot.Identity.HeadSha, authority.Prior!.Lineage.Generation + 1,
            authority.Prior.Lineage.EnvelopeSha256, null!);
        byte[]? selectedBytes = null, preparedBytes = null;
        SolusDirectSelectedState? selected = null;
        SolusDirectPreparedContext? prepared = null;
        var injected = false;
        try
        {
            var selection = await state.SelectAsync(authority.Prior, context, CancellationToken.None);
            Check(selection.Code == RestrictedStateCodes.Restored && selection.Selection?.Session?.SolusRecord is not null,
                "owned_real_selection");
            using var ownedSelection = selected = selection.Selection!;
            selectedBytes = selected.Session!.Plaintext;
            using var ownedPreparation = prepared = await state.PrepareAsync(selected, Guid.NewGuid(), Guid.NewGuid(),
                CancellationToken.None);
            Check(prepared is not null, "owned_real_preparation");
            var field = typeof(SolusDirectPreparedContext).GetField("payload",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly);
            Check(field is { IsStatic: false } && field.DeclaringType == typeof(SolusDirectPreparedContext) &&
                field.FieldType == typeof(byte[]), "owned_exact_payload_field");
            // Observe the real underlying reference, never a copied surrogate.
            preparedBytes = field!.GetValue(prepared) as byte[];
            Check(selectedBytes.Length > 0 && selectedBytes.Any(value => value != 0) && preparedBytes is { Length: > 0 } &&
                preparedBytes.Any(value => value != 0) && !ReferenceEquals(selectedBytes, preparedBytes), "owned_distinct_live_buffers");
            throw new OwnedBufferProbeFailure();
        }
        catch (OwnedBufferProbeFailure) { injected = true; }
        Check(injected && selectedBytes is not null && preparedBytes is not null &&
            selectedBytes.All(value => value == 0) && preparedBytes.All(value => value == 0), "owned_disposed_zero");
        var afterClaim = StateSnapshot(authority.Root.StateRootPath);
        prepared!.Dispose();
        selected!.Dispose();
        Check(!selected.IsLive && selectedBytes!.All(value => value == 0) && preparedBytes!.All(value => value == 0),
            "owned_dispose_idempotent");
        Check(!prepared.TryTransfer(out var refusedTargets, out var refusedClaim, out var refusedCompatibility,
            out var refusedBytes, out var refusedClock) && refusedTargets is null && refusedClaim is null &&
            refusedCompatibility is null && refusedBytes is null && refusedClock is null, "owned_disposed_transfer_refused");
        var retry = await state.SelectAsync(authority.Prior, context, CancellationToken.None);
        Check(retry.Code == RestrictedStateCodes.Restored && retry.Selection is not null, "owned_source_still_accepted");
        using var retrySelection = retry.Selection!;
        using var retryPreparation = await state.PrepareAsync(retrySelection, Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None);
        Check(retryPreparation is null && StateSnapshot(authority.Root.StateRootPath).SequenceEqual(afterClaim) &&
            File.ReadAllBytes(input.LineagePath).AsSpan().SequenceEqual(lineage), "owned_claim_remains_spent");
    }

    private sealed class OwnedBufferProbeFailure : Exception { }

    private static async Task InvalidSecrets(string root)
    {
        // A genuine current successful run establishes the same fixture's
        // authority, manifest, runtime construction and provider prerequisites.
        var positivePath = Path.Join(root, "secret-positive");
        var positive = new Program.DirectRoot(positivePath);
        positive.Write(Head, Head, "secret_control", Fact + "\n");
        using (var authority = await Authority.Open(positivePath))
        {
            SetSecrets();
            var completed = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                null, 0, null, authority.Sink, CancellationToken.None);
            Check(completed.Execution.Result is { Code: R3LiveAgentCodes.Completed, ModelCalls: 2,
                ToolCalls: 1, AcceptedGeneration: 0, HandoffReady: true } &&
                File.Exists(positive.LineagePath), "secret_positive_control");
        }
        const string originalSyntheticProvider = "provider-secret-must-not-enter-body";
        (string? Provider, string? Key)[] invalid = [
            (null, Program.Key), ("", Program.Key), (originalSyntheticProvider, null),
            (originalSyntheticProvider, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=="),
            (originalSyntheticProvider, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")];
        for (var index = 0; index < invalid.Length; index++)
        {
            var path = Path.Join(root, "secret-negative-" + index);
            var input = new Program.DirectRoot(path);
            input.Write(Head, Head, "secret_negative", Fact + "\n");
            using var authority = await Authority.Open(path);
            Check(authority.Authorization.IsLive, "secret_live_authority");
            Check(await authority.EmptyAccepted(), "secret_initial_absent");
            var before = StateSnapshot(authority.Root.StateRootPath);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable, invalid[index].Provider);
            Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, invalid[index].Key);
            var rejected = await SolusDirectReviewComposition.RunAsync(authority.Files, authority.Authorization,
                null, 0, null, authority.Sink, CancellationToken.None);
            Check(rejected.Execution.Result is { Code: R3LiveAgentCodes.SecretInvalid, ModelCalls: 0,
                ToolCalls: 0, AcceptedGeneration: null, AcceptedSessionSha256: null,
                AcceptedEnvelopeSha256: null, HandoffReady: false }, "secret_no_work");
            Check(rejected.Execution.DiagnosticCode == R3LiveAgentDiagnosticCodes.PreparationFailed,
                "secret_current_stage");
            Check(Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable) is null &&
                Environment.GetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable) is null,
                "secret_environment_cleared");
            Check(!File.Exists(input.LineagePath) && await authority.EmptyAccepted(), "secret_no_acceptance");
            Check(before.SequenceEqual(StateSnapshot(authority.Root.StateRootPath)), "secret_state_unchanged");
        }
    }

    private static string[] StateSnapshot(string root)
    {
        var entries = new List<string>();
        foreach (var path in Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root)
            .Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            var directory = (info.Attributes & FileAttributes.Directory) != 0;
            var mode = OperatingSystem.IsWindows() ? 0 : (int)File.GetUnixFileMode(path);
            var identity = Path.GetRelativePath(root, path) + ":" + (int)info.Attributes + ":" + mode + ":" +
                info.CreationTimeUtc.Ticks + ":" + info.LastWriteTimeUtc.Ticks;
            if (!directory)
            {
                var bytes = File.ReadAllBytes(path);
                try { identity += ":" + bytes.Length + ":" + Convert.ToHexStringLower(SHA256.HashData(bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
            }
            entries.Add(identity);
        }
        return entries.ToArray();
    }

    private static void Check(bool valid, string code)
    { if (!valid) throw new CutoverProofFailure(code); }
    private static void SetSecrets()
    {
        Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.ProviderVariable, "synthetic-header-only");
        Environment.SetEnvironmentVariable(R3LiveAgentEnvironmentSecretSource.StateKeyVariable, Program.Key);
    }

    private sealed class Authority : IDisposable
    {
        internal required LiveAgentFreshProcessFileSystem Files;
        internal required LiveAgentFreshProcessAuthorizedRoot Root;
        internal required SolusDirectReviewAuthorization Authorization;
        internal required LiveAgentFreshProcessLineageSink Sink;
        internal LiveAgentFreshProcessAdmittedLineage? Prior;
        internal static async Task<Authority> Open(string path)
        {
            Check(LiveAgentFreshProcessFileSystem.TryCreate(path, out var files), "filesystem");
            var read = files!.ReadAuthorization();
            Check(read is not null, "authorization");
            Check(LiveAgentFreshProcessCommand.TryAdmitAuthorization(
                LiveAgentFreshProcessCodec.ReadAuthorization(read!.Bytes)!, out var input, out _, out var admitted), "scope");
            var hasPrior = input!.LocatorFamily == RestrictedStateLocatorFamily.Current;
            Check(files.TryAuthorizeLayout(read, admitted!, hasPrior, out var root), "layout");
            try
            {
                Check(await files.AcquireContextExclusionAsync(root!, CancellationToken.None), "exclusion");
                var authority = SolusDirectReviewAuthorization.TryCreate(files, root!);
                Check(authority is not null, "issuer");
                LiveAgentFreshProcessAdmittedLineage? prior = null;
                if (hasPrior) Check(LiveAgentFreshProcessLineageAdmission.TryAdmit(files.ReadLineage(root!)!,
                    input.Document.ExpectedLineageSha256!, root!.Access.Scope, out prior), "lineage");
                return new() { Files = files, Root = root!, Authorization = authority!, Prior = prior,
                    Sink = new(files, root!, authority!.Snapshot.Identity, input.InvocationIdentitySha256, prior) };
            }
            catch { root?.Dispose(); throw; }
        }
        internal Task<SolusDirectExecution> Run() => SolusDirectReviewComposition.RunAsync(Files, Authorization,
            Prior, Prior!.Lineage.Generation + 1, Prior.Lineage.EnvelopeSha256, Sink, CancellationToken.None);
        internal async Task<bool> EmptyAccepted()
        {
            Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "key");
            using var owned = keys!;
            var state = await new RestrictedStateOpaqueSnapshotStore(Files.CreateStateStore(Root, Root.StateRootPath), keys!)
                .ReadAsync(Root.Access, CancellationToken.None);
            return state.Snapshot is { Accepted.IsEmpty: true, Staging: null };
        }
        internal async Task Seed(bool incompatible)
        {
            using var targets = SolusFreshHostContext.TryCreateDirect(Authorization);
            Check(targets is not null, "targets");
            var tools = new SolusSnapshotTools(Guid.NewGuid(), Authorization.Snapshot,
                new SnapshotToolExecutor(Authorization.Snapshot, Authorization.CreateFileAccess()));
            var expected = SolusRuntimeConstruction.DescribeFreshConfiguration(targets!, tools.Registrations, new Hooks());
            if (incompatible) expected = expected with { RuntimeConfigurationSha256 = new string('e', 64) };
            Check(SolusRestrictedStateRecord.TryCreate(expected,
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()), "invalid opaque context"u8,
                out var record), "record");
            Check(SolusRestrictedStateSnapshotCodec.TryWrite(record, out var bytes), "encode");
            Check(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var keys), "key");
            using var owned = keys!;
            try
            {
                var state = new SolusDirectStateComposer(Files, Authorization, targets!, expected, keys!, TimeProvider.System);
                var result = await state.CreateCommitCoordinator(Sink).CommitPreparedAsync(
                    new(bytes, SolusRestrictedStateSnapshotCodec.RecordSha256(bytes), 0), Root.Access, null,
                    ReviewedHeadTransition.SameHead, new(Authorization.Snapshot.Identity.BaseSha,
                        Authorization.Snapshot.Identity.HeadSha, 0, null, null!), Root.StateRootPath, keys!, CancellationToken.None);
                Check(result.Code == R3LiveAgentCodes.Completed && result.AcceptedGeneration == 0, "seed_acceptance");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose() { Authorization.Dispose(); Root.Dispose(); }
    }

    private sealed class CaptureFault(IRestrictedContextSink inner, string mode, CancellationTokenSource cancellation)
        : IRestrictedContextSink
    {
        public void Capture(AgentContextEnvelope context)
        {
            if (mode == "missing") return;
            if (mode == "wrong-format")
            {
                var bytes = context.CopyRestrictedPayload();
                try { inner.Capture(new(context.ImplementationId, context.FormatVersion + 1, context.CompatibilityVersion, bytes)); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                return;
            }
            inner.Capture(context);
            if (mode == "duplicate") inner.Capture(context);
            if (mode == "throw") throw new InvalidOperationException("private_capture_failure");
            if (mode == "cancel") cancellation.Cancel();
        }
    }
    private sealed class Hooks : IRuntimeExposureHooks
    {
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token) =>
            ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged,
                ExposureDecision.Permit, exposure.RequiredAcknowledgement));
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token) =>
            ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged,
                RuntimeContinuation.Continue, settlement.Accounting));
    }
}
#endif
