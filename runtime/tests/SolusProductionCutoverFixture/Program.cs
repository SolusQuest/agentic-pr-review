using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// The fixture owns inputs and observation only. Every review is a fresh child
// process running the supplied production DLL's real argv entry.
internal static class Program
{
    internal const string Key = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
    private static readonly string Base = new('a', 40), First = new('b', 40), Second = new('c', 40);

    internal static async Task<int> Main(string[] args)
    {
#if APR_SOLUS_MANAGED
        if (args is ["managed-faults", var faultRoot]) return await ManagedFaultProof.RunAsync(faultRoot);
#endif
        try
        {
            if (args is ["transaction-inventory", var transactionInventory]) return await TransactionInventoryProof.RunAsync(transactionInventory);
            if (args is ["reset-inventory", var inventory]) return ResetInventoryProof.Run(inventory);
            if (args is ["redaction-probe"]) throw new InvalidOperationException("PRIVATE_R6_IDENTIFIER_ERROR_CANARY");
            if (!OperatingSystem.IsLinux() || args.Length != 4) throw new InvalidOperationException("arguments");
            var (dotnet, managed, historical, root) = (args[0], args[1], args[2], args[3]);
            Directory.CreateDirectory(root);
            var fixture = new DirectRoot(Path.Join(root, "solus"));
            var fact = "APR_PRIOR_ONLY_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            fixture.Write(First, First, "first", fact + "\n");
            var bootstrap = await fixture.Run(dotnet, managed, historical: false);
            Require(bootstrap == 0, "bootstrap_exit");
            var first = fixture.Result();
            Require(first is { Code: R3LiveAgentCodes.Completed, Generation: 0,
                ModelCalls: 2, ToolCalls: 1, HandoffReady: true }, "bootstrap_result");
            var sourceLineage = File.ReadAllBytes(fixture.LineagePath);
            var sourceState = fixture.StateFiles();
            var firstAccepted = LiveAgentFreshProcessCodec.ReadLineage(sourceLineage);
            Require(firstAccepted is { Generation: 0, TransitionAuthorized: true,
                ExpectedPredecessorEnvelopeSha256: null } && firstAccepted.SessionSha256 == first.SessionSha256 &&
                firstAccepted.EnvelopeSha256 == first.EnvelopeSha256 &&
                firstAccepted.ProducerHeadSha == First &&
                firstAccepted.InvocationIdentitySha256 == first.InvocationIdentitySha256 &&
                first.LineageSha256 == LiveAgentFreshProcessDomain.RawSha256(sourceLineage) &&
                first.SecondRequestSha256 is null, "bootstrap_lineage_identity");
            fixture.RequireEncryptedState(fact);

            // Genuine selected-current SESSION from the explicit historical
            // producer must fail before the new production provider is invoked.
            var legacy = new DirectRoot(Path.Join(root, "legacy"));
            legacy.Write(First, First, "legacy_first", fact + "\n");
            Require(await legacy.Run(dotnet, historical, historical: true) == 0, "legacy_producer");
            var legacyLineage = File.ReadAllBytes(legacy.LineagePath);
            var legacyState = legacy.StateFiles();
            await HistoricalRefusalProof.VerifyAsync(Path.Join(root, "legacy"), legacyLineage);
            legacy.Write(First, Second, "legacy_continue", "Current safe content.\n", legacyLineage);
            Require(await legacy.Run(dotnet, managed, historical: false) != 0, "legacy_rejected_exit");
            Require(legacy.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false,
                Generation: null }, "legacy_rejected_before_provider");
            Require(legacy.StateFiles().SequenceEqual(legacyState) &&
                File.ReadAllBytes(legacy.LineagePath).AsSpan().SequenceEqual(legacyLineage), "legacy_unchanged");

            // A genuine selected lineage without its object is not absence.
            // Use a separate authorized empty store; keep the source untouched.
            var missing = new DirectRoot(Path.Join(root, "missing-record"));
            missing.Write(First, Second, "missing_record", "Current safe content.\n", sourceLineage);
            File.WriteAllBytes(missing.LineagePath, sourceLineage);
            var missingState = missing.StateFiles();
            Require(missingState.Length == 0, "selected_missing_initial_absence");
            Require(await missing.Run(dotnet, managed, historical: false) != 0, "selected_missing_exit");
            Require(missing.Result() is { Code: RestrictedStateCodes.CurrentMissing,
                ModelCalls: 0, ToolCalls: 0, Generation: null, HandoffReady: false },
                "selected_missing_no_work");
            Require(missing.StateFiles().SequenceEqual(missingState) &&
                File.ReadAllBytes(missing.LineagePath).AsSpan().SequenceEqual(sourceLineage),
                "selected_missing_unchanged");
            Require(fixture.StateFiles().SequenceEqual(sourceState) &&
                File.ReadAllBytes(fixture.LineagePath).AsSpan().SequenceEqual(sourceLineage),
                "selected_missing_source_unchanged");

            // An absent locator cannot authorize a fresh run over actual state.
            File.Delete(fixture.LineagePath);
            fixture.Write(First, First, "false_absence", "Current safe content.\n");
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "false_absence_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "false_absence_no_work");
            Require(fixture.StateFiles().SequenceEqual(sourceState), "false_absence_unchanged");
            File.WriteAllBytes(fixture.LineagePath, sourceLineage);

            // The direct transition contract still requires VerifiedAhead.
            fixture.Write(First, First, "same_head", "Current safe content.\n", sourceLineage);
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "same_head_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "same_head_no_work");

            fixture.Write(First, Second, "second", "Current safe content.\n", sourceLineage);
            var continuation = await fixture.Run(dotnet, managed, historical: false);
            Require(continuation == 0, "continue_exit");
            var second = fixture.Result();
            Require(second is { Code: R3LiveAgentCodes.Completed, Generation: 1,
                ModelCalls: 2, ToolCalls: 1, HandoffReady: true } &&
                second.TransitionClass == "verified_ahead" &&
                second.InvocationIdentitySha256 != first.InvocationIdentitySha256 &&
                second.SecondRequestSha256 is not null &&
                second.EnvelopeSha256 != first.EnvelopeSha256 &&
                second.SessionSha256 != first.SessionSha256, "continue_result");
            var accepted = File.ReadAllBytes(fixture.LineagePath);
            var acceptedState = fixture.StateFiles();
            var secondAccepted = LiveAgentFreshProcessCodec.ReadLineage(accepted);
            Require(secondAccepted is { Generation: 1, TransitionAuthorized: true } &&
                secondAccepted.Scope == firstAccepted!.Scope &&
                secondAccepted.SessionSha256 == second.SessionSha256 &&
                secondAccepted.EnvelopeSha256 == second.EnvelopeSha256 &&
                secondAccepted.ProducerHeadSha == Second &&
                secondAccepted.InvocationIdentitySha256 == second.InvocationIdentitySha256 &&
                second.LineageSha256 == LiveAgentFreshProcessDomain.RawSha256(accepted) &&
                secondAccepted.ExpectedPredecessorEnvelopeSha256 == firstAccepted.EnvelopeSha256 &&
                secondAccepted.AcceptedAtUnixSeconds >= firstAccepted.AcceptedAtUnixSeconds &&
                secondAccepted.ExpiresAtUnixSeconds - secondAccepted.AcceptedAtUnixSeconds ==
                    RestrictedStateFormat.MaximumRetentionSeconds, "successor_lineage_identity");
            fixture.RequireEncryptedState(fact);

            var publicRoot = Path.Join(root, "solus");
            var statePrefix = Path.Join(publicRoot, "state") + Path.DirectorySeparatorChar;
            var publicBytes = Directory.EnumerateFiles(publicRoot, "*", SearchOption.AllDirectories)
                .Where(path => !path.StartsWith(statePrefix, StringComparison.Ordinal))
                .SelectMany(File.ReadAllBytes).ToArray();
            try
            {
                foreach (var canary in new[] { fact, "synthetic-header-only", Key })
                    Require(publicBytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)) < 0, "public_files_canary");
            }
            finally { CryptographicOperations.ZeroMemory(publicBytes); }

            // Repeating the producer invocation is rejected and cannot consume
            // another source grant or advance state.
            fixture.Write(Second, new string('d', 40), "second", "Current safe content.\n", accepted);
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "reused_invocation_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "reused_invocation_no_work");
            Require(fixture.StateFiles().SequenceEqual(acceptedState) &&
                File.ReadAllBytes(fixture.LineagePath).AsSpan().SequenceEqual(accepted), "reused_invocation_unchanged");

            // Exact original source restored after successor publication remains
            // spent; the durable claim is not derived from the new invocation.
            File.WriteAllBytes(fixture.LineagePath, sourceLineage);
            fixture.Write(First, Second, "third", "Current safe content.\n", sourceLineage);
            Require(await fixture.Run(dotnet, managed, historical: false) != 0, "spent_source_exit");
            Require(fixture.Result() is { ModelCalls: 0, ToolCalls: 0, HandoffReady: false }, "spent_source_no_work");
            Require(fixture.StateFiles().SequenceEqual(acceptedState), "spent_source_unchanged");
            Console.WriteLine("APR_SOLUS_CUTOVER direct_processes=9 fresh=true restored=true old_session_rejected=true false_absence_rejected=true same_head_rejected=true invocation_reuse_rejected=true spent_source_rejected=true selected_current_missing_rejected=true");
            return 0;
        }
        catch (Exception exception)
        {
            var reason = CutoverProofFailure.Describe(exception);
            Console.Error.WriteLine("APR_SOLUS_CUTOVER_FAILED " + reason);
            return 1;
        }
    }

    private static void Require(bool value, string code)
    { if (!value) throw new CutoverProofFailure(code); }

    internal sealed class DirectRoot
    {
        private readonly string root;
        private bool continuation;
        private readonly AgentTrustedRequest trusted = new("owner/repository", 109,
            "trusted-r3-workflow", Encoding.UTF8.GetBytes("Use the immutable reviewed snapshot for all evidence."),
            "build-109", DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter);
        internal DirectRoot(string root)
        {
            this.root = root;
            foreach (var directory in new[] { "input", "host", "state", "private", "output" })
                Directory.CreateDirectory(Path.Join(root, directory));
        }
        internal string LineagePath => Path.Join(root, "host", "accepted-lineage.json");
        private string ResultPath => Path.Join(root, "output", "result.json");
        internal string[] StateFiles() => Directory.GetFiles(Path.Join(root, "state"), "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))
            .Order(StringComparer.Ordinal).ToArray();
        internal void RequireEncryptedState(string repositoryFact)
        {
            var files = Directory.GetFiles(Path.Join(root, "state"), "*", SearchOption.AllDirectories);
            Require(files.Length > 0, "encrypted_state_present");
            var bytes = files.SelectMany(File.ReadAllBytes).ToArray();
            var key = Convert.FromBase64String(Key);
            try
            {
                foreach (var canary in new[] { repositoryFact, "Review the current immutable snapshot.",
                    "synthetic-header-only", Key })
                    Require(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(canary)) < 0, "encrypted_state_canary");
                Require(bytes.AsSpan().IndexOf(key) < 0, "encrypted_state_key");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
                CryptographicOperations.ZeroMemory(key);
            }
        }
        internal LiveAgentFreshProcessResultDocument Result() => LiveAgentFreshProcessCodec.ReadResult(File.ReadAllBytes(ResultPath))
            ?? throw new InvalidOperationException("missing_result");
        internal void Write(string from, string head, string invocation, string text, byte[]? lineage = null)
        {
            File.Delete(ResultPath);
            continuation = lineage is not null;
            Require(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var materialized), "materialize");
            var stable = materialized!.StablePlan;
            var scope = new RestrictedStateScope(trusted.RepositoryId, trusted.WorkflowIdentity, trusted.ReviewTarget,
                "session-109", trusted.ProviderId, trusted.ModelId, trusted.AdapterId, stable.PolicySha256,
                stable.LimitsSha256, stable.ToolsetSha256, trusted.BuildId);
            var expected = lineage is null ? null : LiveAgentFreshProcessDomain.RawSha256(lineage);
            var transition = from == head ? "same_head" : "verified_ahead";
            var authorization = new LiveAgentFreshProcessAuthorizationDocument(LiveAgentFreshProcessDomain.AuthorizationKind,
                new(trusted.RepositoryId, trusted.ReviewTarget, trusted.WorkflowIdentity,
                    Encoding.UTF8.GetString(trusted.TrustedPolicyBytes), trusted.BuildId, trusted.ProviderId,
                    trusted.ModelId, trusted.AdapterId, "session-109"), LiveAgentFreshProcessDomain.ScopeDocument(scope),
                true, true, false, LiveAgentFreshProcessDomain.DeterministicProfile,
                lineage is null ? "absent" : "current", lineage is null ? "automatic" : "explicit",
                new(transition, from, Base, head, LiveAgentFreshProcessDomain.TransitionReceiptSha256(expected,
                    transition, from, Base, head, invocation)), invocation, expected);
            var identity = new ReviewedIdentity(trusted.RepositoryId, trusted.ReviewTarget, Base, head);
            var content = Encoding.UTF8.GetBytes(text);
            var reviewed = new LiveAgentFreshProcessReviewedInputDocument(LiveAgentFreshProcessDomain.ReviewedInputKind,
                LiveAgentFreshProcessDomain.IdentityDocument(identity), "Review the current immutable snapshot.");
            var manifest = new LiveAgentFreshProcessSnapshotManifestDocument(LiveAgentFreshProcessDomain.SnapshotManifestKind,
                LiveAgentFreshProcessDomain.IdentityDocument(identity), ["fact.txt"],
                [new("fact.txt", content.Length, LiveAgentFreshProcessDomain.RawSha256(content), Convert.ToBase64String(content))], [], []);
            File.WriteAllBytes(Path.Join(root, "host", "authorization.json"), LiveAgentFreshProcessCodec.Write(authorization));
            File.WriteAllBytes(Path.Join(root, "input", "reviewed-input.json"), LiveAgentFreshProcessCodec.Write(reviewed));
            File.WriteAllBytes(Path.Join(root, "input", "snapshot-manifest.json"), LiveAgentFreshProcessCodec.Write(manifest));
        }
        internal async Task<int> Run(string dotnet, string dll, bool historical)
        {
            var start = new ProcessStartInfo(dotnet) { RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false };
            start.Environment.Clear();
            start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet);
            start.Environment["HOME"] = root;
            start.Environment[R3LiveAgentEnvironmentSecretSource.ProviderVariable] = "synthetic-header-only";
            start.Environment[R3LiveAgentEnvironmentSecretSource.StateKeyVariable] = Key;
            start.ArgumentList.Add(dll);
            if (historical) start.ArgumentList.Add("historical-direct");
            foreach (var argument in new[] { "review-live-agent-r3", "--mode", continuation ? "continue" : "bootstrap", "--root", root })
                start.ArgumentList.Add(argument);
            using var child = Process.Start(start) ?? throw new InvalidOperationException("child_start");
            var output = child.StandardOutput.ReadToEndAsync();
            var diagnostic = child.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await child.WaitForExitAsync(timeout.Token); }
            catch { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); throw; }
            Require((await output).Length == 0, "child_stdout");
            var privateDiagnostic = await diagnostic; // Never publish private child output.
            if (child.ExitCode == 0) Require(privateDiagnostic.Length == 0, "child_success_stderr");
            return child.ExitCode;
        }
    }
}

internal sealed class CutoverProofFailure(string code) : Exception
{
    internal string Code { get; } = code;
    private static readonly HashSet<string> KnownAssertionCodes = new(StringComparer.Ordinal)
    {
        "body_privacy_bodies_withheld_secrets",
        "body_privacy_buffer_completed",
        "body_privacy_control_completed",
        "body_privacy_control_environment_cleared",
        "body_privacy_control_initial_absence",
        "body_privacy_control_lineage",
        "body_privacy_control_same_inputs",
        "body_privacy_credentials",
        "body_privacy_dispatch_observed",
        "body_privacy_environment_cleared",
        "body_privacy_first_withheld_fact",
        "body_privacy_headers_authorized",
        "body_privacy_initial_absence",
        "body_privacy_observed_requests",
        "body_privacy_observer_created",
        "body_privacy_read_completed",
        "body_privacy_real_completion",
        "body_privacy_request_bound",
        "body_privacy_request_shape",
        "body_privacy_second_used_fact",
        "body_privacy_single_generation",
        "bootstrap_exit",
        "bootstrap_lineage_identity",
        "bootstrap_result",
        "capture_current_stage",
        "child_stdout",
        "commit_failure_actual_current_record",
        "commit_failure_authenticated_record",
        "commit_failure_durable_acceptance",
        "commit_failure_environment_cleared",
        "commit_failure_exact_accepted_binding",
        "commit_failure_initial_absence",
        "commit_failure_key",
        "commit_failure_no_output_lineage",
        "commit_failure_primary_cause",
        "commit_failure_single_publish",
        "commit_failure_targets",
        "continue_exit",
        "continue_result",
        "encrypted_state_canary",
        "encrypted_state_key",
        "encrypted_state_present",
        "false_absence_exit",
        "false_absence_no_work",
        "false_absence_unchanged",
        "historical_accepted_object",
        "historical_access",
        "historical_authenticated_bytes",
        "historical_distinct_domains",
        "historical_exact_binding",
        "historical_key",
        "historical_lineage",
        "historical_original_lineage_mismatch",
        "historical_producer_framing",
        "historical_read_only",
        "historical_rebind_incompatible",
        "historical_supported_reader",
        "incompatible_current_stage",
        "materialize",
        "precancel_classification_unexpected",
        "precancel_classified_conflict",
        "precancel_classified_root_invalid",
        "precancel_environment_cleared",
        "precancel_fresh_not_accepted",
        "precancel_initial_absent",
        "precancel_lineage_unchanged",
        "precancel_no_publication_receipt",
        "precancel_real_current_source",
        "precancel_source_not_burned",
        "precancel_state_unchanged",
        "precancel_valid_control",
        "precancel_zero_work",
        "reused_invocation_exit",
        "reused_invocation_no_work",
        "reused_invocation_unchanged",
        "same_head_exit",
        "same_head_no_work",
        "secret_current_stage",
        "selected_missing_exit",
        "selected_missing_initial_absence",
        "selected_missing_no_work",
        "selected_missing_source_unchanged",
        "selected_missing_unchanged",
        "spent_source_exit",
        "spent_source_no_work",
        "spent_source_unchanged",
        "successor_lineage_identity",
    };

    internal static string Describe(Exception exception) => exception switch
    {
        CutoverProofFailure failure => KnownAssertionCodes.Contains(failure.Code)
            ? failure.Code : "unrecognized_authored_assertion",
        OperationCanceledException => "cancelled",
        IOException => "io",
        UnauthorizedAccessException => "access_denied",
        System.Text.Json.JsonException => "json",
        CryptographicException => "crypto",
        ArgumentException => "argument",
        InvalidOperationException => "invalid_operation",
        _ => "unknown_exception",
    };
}
