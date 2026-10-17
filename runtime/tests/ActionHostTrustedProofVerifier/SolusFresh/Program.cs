using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Context;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        AppDomain.CurrentDomain.FirstChanceException += ObserveBoundaryFault;
        try
        {
            foreach (var key in new[] { "GITHUB_TOKEN", "GH_TOKEN", "NUGET_AUTH_TOKEN", "NuGetPackageSourceCredentials_solus" })
                Proof.Require(Environment.GetEnvironmentVariable(key) is null, "credential_free_process");
            Proof.Require(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64, "linux_x64");
            using var corpus = new Corpus(args.Length == 1 ? args[0] : "runtime/tests/fixtures/agent/r5/quality/bundle");
            foreach (var id in Corpus.Cases) await QualityCase(corpus, id);
            await CaptureCases(corpus);
            await HeadCases(corpus);
            await FailureCases(corpus);
            await InputBoundaries(corpus);
            await MutationControl(corpus);
            Console.WriteLine($"APR_SOLUS_FRESH_HOST_PROOF cases=13 checks={Proof.Checks} controlled_sends={Proof.Sends} " +
                "live_calls=0 effects=0 durable_acceptance=false credential_free=true");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("APR_SOLUS_FRESH_HOST_FAILED " +
                (exception is ProofFailure failure ? failure.Code : "unexpected_" + Proof.Stage));
            return 1;
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= ObserveBoundaryFault; }
    }

    // This controlled, credential-free fixture observes only closed source-authored
    // failure identifiers. Exception text, stacks and provider content stay private.
    private static void ObserveBoundaryFault(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs args)
    {
        var code = args.Exception.Message switch {
            "APR_SOLUS_REVIEW_AUTHORITY" => "review_authority",
            "APR_SOLUS_EXECUTION_INVALID" => "execution_identity",
            "APR_SOLUS_TARGET_AUTHORITY" => "construction_authority",
            "APR_SOLUS_TARGET_PROFILE_UNSUPPORTED" => "construction_profile",
            "APR_SOLUS_PROFILE_UNSUPPORTED" => "runtime_profile",
            "APR_SOLUS_REVIEW_ALREADY_USED" => "review_reused",
            "APR_SOLUS_FRESH_OUTCOME" => "fresh_outcome",
            _ => args.Exception switch {
                ArgumentNullException => "argument_null",
                ArgumentOutOfRangeException => "argument_range",
                ArgumentException => "argument",
                InvalidCastException => "invalid_cast",
                NotSupportedException => "not_supported",
                InvalidOperationException => "invalid_operation",
                _ => null,
            },
        };
        if (code is not null) Proof.BoundaryFault = code;
    }

    private static async Task QualityCase(Corpus corpus, string id)
    {
        Proof.Stage = "quality_" + id;
        using var fixture = new HostFixture(corpus);
        using var handler = new ScriptedHandler(corpus, id);
        using var sink = new RestrictedSink();
        var prepared = await fixture.Run(handler, sink);
        var result = prepared.Value;
        Proof.Require(prepared.Failure is null && result is not null, "quality_host_admission_" + id);
        fixture.AssertEffectsAbsent();
        using var expected = corpus.Expected(id);
        var code = Score(expected.RootElement, result!);
        Proof.Require(code == expected.RootElement.GetProperty("expected_code").GetString(),
            ("quality_disposition_" + id + "_actual_" + code + "_host_" + result!.Failure +
                "_work_" + result.Work?.Termination + "_runtime_" + result.Work?.RuntimeReason +
                "_admission_" + result.Context?.Admission + "_capture_" + result.Context?.CaptureStatus +
                "_terminal_" + result.TerminalAccepted + "_sends_" + handler.Sends +
                "_observations_" + result.Observations.Length + "_transport_" + handler.FailureCode +
                "_boundary_" + Proof.BoundaryFault).ToLowerInvariant());
        Proof.Require(handler.Sends == handler.Turns && handler.Sends <= 6, "script_complete_" + id);
        if (code == "Scored")
        {
            Proof.Require(result!.ProductReviewAccepted && result.Publication?.CandidateEligible == true &&
                result.Context?.CaptureStatus == ContextCaptureStatus.Delivered, "whole_host_accepted_" + id);
            sink.AssertMatches(result);
            Proof.Require(result.Work!.CurrentUsage!.ToolInvocations!.Invoked == result.Observations.Length &&
                result.Work.CurrentUsage.ToolCallAdmissions!.Admitted == result.Observations.Length, "actual_tool_counts_" + id);
            var required = expected.RootElement.GetProperty("required_observations").EnumerateArray()
                .Select(o => o.GetProperty("observation_id").GetString()).ToArray();
            Proof.Require(result.Observations.Select(o => o.ObservationId).SequenceEqual(required), "actual_observation_order_" + id);
            foreach (var fact in Proof.RequiredFacts(id)) Proof.Require(handler.ReadFacts.Contains(fact), "actual_decisive_read_" + id);
        }
        else if (code == "ExecutionFailed")
            Proof.Require(!result!.ProductReviewAccepted && result.Review is null &&
                result.Publication?.CandidateEligible != true, "invalid_not_clean_" + id);
        Proof.Require(!result!.DurableStateAccepted && !result.EffectsAuthorized, "no_acceptance_claim_" + id);
        SafeResult(result);
        Emit(corpus, id, code, result, handler, sink, fixture.Meter!);
    }

    // Oracle comparison only; no native SESSION or runtime-completed result is
    // fabricated to reuse the old Q1 admission path. V1 retains that port.
    private static string Score(JsonElement expected, SolusFreshReviewResult result)
    {
        if (!result.ProductReviewAccepted) return "ExecutionFailed";
        var observations = result.Observations.Select(o => o.ObservationId).ToHashSet(StringComparer.Ordinal);
        var required = expected.GetProperty("required_observations").EnumerateArray()
            .Select(o => o.GetProperty("observation_id").GetString()!).ToArray();
        if (required.Any(id => !observations.Contains(id)))
            return observations.Count == 0 ? "RequiredToolMissing" : "RequiredObservationMissing";
        var findings = result.Review!.Findings;
        string Key(AgenticPrReview.Runtime.Agent.Core.AgentFinding finding) => finding.Severity + "|" +
            string.Join(";", finding.Evidence.Select(e => $"{e.ObservationId}|{e.Path}|{e.StartLine}|{e.EndLine}"));
        if (findings.GroupBy(Key, StringComparer.Ordinal).Any(g => g.Count() > 1)) return "DuplicateObservation";
        foreach (var prohibited in expected.GetProperty("prohibited_findings").EnumerateArray())
            if (findings.Any(f => f.Evidence.Any(e => e.Path == prohibited.GetProperty("path").GetString() &&
                e.StartLine == prohibited.GetProperty("start_line").GetInt32() &&
                e.EndLine == prohibited.GetProperty("end_line").GetInt32()))) return "ProhibitedFinding";
        foreach (var defect in expected.GetProperty("defects").EnumerateArray())
            if (!findings.Any(f => f.Severity == defect.GetProperty("severity").GetString() && f.Evidence.Any(e =>
                e.ObservationId == defect.GetProperty("observation_id").GetString() && e.Path == defect.GetProperty("path").GetString() &&
                e.StartLine == defect.GetProperty("start_line").GetInt32() && e.EndLine == defect.GetProperty("end_line").GetInt32())))
                return "ExpectedFindingMissing";
        Proof.Require(findings.Length == expected.GetProperty("defects").GetArrayLength(), "no_extra_findings");
        return "Scored";
    }

    private static async Task CaptureCases(Corpus corpus)
    {
        foreach (var mode in new[] { "not_requested", "unavailable", "failed", "reasoning" })
        {
            Proof.Stage = "capture_" + mode;
            using var fixture = new HostFixture(corpus);
            using var handler = new ScriptedHandler(corpus, "cs-safe") { PrivateReasoning = mode == "reasoning" };
            using var sink = new RestrictedSink(mode == "failed");
            var prepared = await fixture.Run(handler, mode == "not_requested" ? null : sink,
                decorator: mode == "unavailable" ? provider => new ExportFailure(provider) : null);
            var result = prepared.Value!;
            Proof.Require(result?.ProductReviewAccepted == true && result.Publication?.CandidateEligible == true,
                "capture_work_independent_" + mode);
            var status = mode == "not_requested" ? ContextCaptureStatus.NotRequested : mode == "unavailable"
                ? ContextCaptureStatus.Unavailable : mode == "failed" ? ContextCaptureStatus.Failed : ContextCaptureStatus.Delivered;
            Proof.Require(result.Context?.CaptureStatus == status, "capture_status_" + mode);
            if (status is ContextCaptureStatus.Delivered or ContextCaptureStatus.Failed) sink.AssertMatches(result);
            else Proof.Require(sink.Calls == 0 && result.Context?.Checkpoint is null, "capture_absent_" + mode);
            fixture.AssertEffectsAbsent(); SafeResult(result);
        }
        Proof.Stage = "capture_concurrent";
        using var first = new HostFixture(corpus); using var second = new HostFixture(corpus);
        using var left = new ScriptedHandler(corpus, "cs-safe"); using var right = new ScriptedHandler(corpus, "cs-safe");
        using var leftSink = new RestrictedSink(); using var rightSink = new RestrictedSink();
        var pair = await Task.WhenAll(first.Run(left, leftSink), second.Run(right, rightSink));
        Proof.Require(pair.All(r => r.Value?.ProductReviewAccepted == true) &&
            pair[0].Value!.ExecutionId != pair[1].Value!.ExecutionId, "call_scoped_concurrent_capture");
        leftSink.AssertMatches(pair[0].Value!); rightSink.AssertMatches(pair[1].Value!);
        first.AssertEffectsAbsent(); second.AssertEffectsAbsent();
    }

    private static async Task HeadCases(Corpus corpus)
    {
        foreach (var mode in new[] { "changed", "closed", "missing", "transport", "cancelled" })
        {
            Proof.Stage = "head_" + mode;
            using var fixture = new HostFixture(corpus); using var cancellation = new CancellationTokenSource();
            using var handler = new ScriptedHandler(corpus, "cs-safe"); using var sink = new RestrictedSink();
            handler.BeforeFinal = () =>
            {
                if (mode == "changed") fixture.GitHub.CurrentOverride = fixture.GitHub.PullRequest with { HeadSha = new string('f', 40) };
                else if (mode == "closed") fixture.GitHub.CurrentOverride = fixture.GitHub.PullRequest with { State = "closed" };
                else if (mode == "missing") fixture.GitHub.CurrentFailure = ActionHostGitObjectFailure.NotFound;
                else if (mode == "transport") fixture.GitHub.CurrentFailure = ActionHostGitObjectFailure.TransportFailure;
                else fixture.GitHub.OnCurrent = () => cancellation.Cancel();
            };
            var result = (await fixture.Run(handler, sink, cancellation.Token)).Value!;
            Proof.Require(result?.ProductReviewAccepted == true && result.Context?.CaptureStatus == ContextCaptureStatus.Delivered &&
                result.Publication?.CandidateEligible == false, "head_work_independent_" + mode);
            Proof.Require(mode == "cancelled" ? result.Publication!.Status == ActionHostFreshPublicationStatus.Cancelled :
                result.Publication!.Head == (mode == "changed" ? ExactHeadRevalidationStatus.HeadChanged :
                    mode == "missing" ? ExactHeadRevalidationStatus.PullRequestMissing :
                    mode == "transport" ? ExactHeadRevalidationStatus.TransportFailure :
                    ExactHeadRevalidationStatus.PullRequestIneligible), "actual_head_barrier_" + mode);
            sink.AssertMatches(result); fixture.AssertEffectsAbsent(); SafeResult(result);
        }
    }

    private static async Task FailureCases(Corpus corpus)
    {
        foreach (var mode in new[] { "malformed", "missing_usage", "mixed_final", "cancel_provider", "cancel_tool", "timeout", "one_call", "small_output" })
        {
            Proof.Stage = "failure_" + mode;
            using var fixture = new HostFixture(corpus, reviewJson: mode == "one_call" ? "{\"maxModelCalls\":1}" :
                mode == "small_output" ? "{\"maxOutputTokens\":100}" : mode == "timeout" ? "{\"timeoutSeconds\":1}" : null);
            using var cancellation = new CancellationTokenSource();
            using var handler = new ScriptedHandler(corpus, "cs-defect") {
                Malformed = mode == "malformed", MissingUsage = mode == "missing_usage", MixedFinal = mode == "mixed_final",
                WaitForCancellation = mode == "timeout" };
            if (mode == "cancel_provider") handler.BeforeResponse = () => cancellation.Cancel();
            using var sink = new RestrictedSink();
            var cancellingFiles = new CancellingFiles(cancellation);
            var result = (await fixture.Run(handler, sink, cancellation.Token,
                files: mode == "cancel_tool" ? _ => cancellingFiles : null)).Value!;
            Proof.Require(result is not null && !result.ProductReviewAccepted && result.Review is null &&
                result.Publication?.CandidateEligible != true && !result.DurableStateAccepted, "failed_not_clean_" + mode);
            if (mode == "one_call") Proof.Require(handler.Sends == 1 && result.Work?.Termination ==
                ActionHostTerminationReason.ResourceLimit, "trusted_smaller_call_limit");
            if (mode == "small_output") Proof.Require(handler.Sends == 0 && result.Work?.Termination ==
                ActionHostTerminationReason.TokenLimit, "fixed_output_pre_send_refusal");
            if (mode == "cancel_provider") Proof.Require(result.Work?.Termination == ActionHostTerminationReason.Cancelled ||
                result.Failure == SolusFreshReviewFailure.Cancelled, "truthful_provider_cancellation");
            if (mode == "cancel_tool") Proof.Require(cancellingFiles.Reads == 1 &&
                (result.Work?.Termination == ActionHostTerminationReason.Cancelled || result.Failure == SolusFreshReviewFailure.Cancelled),
                "real_tool_cancellation");
            if (mode == "timeout") Proof.Require(handler.Sends == 1 &&
                result.Work?.Termination == ActionHostTerminationReason.DeadlineExceeded, "trusted_smaller_timeout");
            fixture.AssertEffectsAbsent(); SafeResult(result);
        }
        foreach (var mode in new[] { "pre_cancel", "authorization", "credential" })
        {
            Proof.Stage = "admission_" + mode;
            using var fixture = new HostFixture(corpus, providerKey: mode != "credential", rejectAuthorization: mode == "authorization");
            using var handler = new ScriptedHandler(corpus, "cs-safe"); using var sink = new RestrictedSink();
            var result = await fixture.Run(handler, sink, new CancellationToken(mode == "pre_cancel"));
            Proof.Require(handler.Sends == 0 && sink.Calls == 0 && (result.Failure is not null ||
                result.Value?.Failure == SolusFreshReviewFailure.CredentialsMissing), "admission_zero_work_" + mode);
            fixture.AssertEffectsAbsent();
        }
    }

    private static async Task InputBoundaries(Corpus corpus)
    {
        Proof.Stage = "instruction_boundary";
        int control;
        using (var fixture = new HostFixture(corpus))
        using (var handler = new ScriptedHandler(corpus, "cs-safe"))
        {
            var result = await fixture.Run(handler);
            Proof.Require(result.Value?.ProductReviewAccepted == true, "boundary_baseline");
            control = Encoding.UTF8.GetByteCount(handler.FirstInstructions.Single()) - corpus.Member("policy.txt").Length;
            fixture.AssertEffectsAbsent();
        }
        foreach (var delta in new[] { 0, 1 })
        {
            using var fixture = new HostFixture(corpus, Encoding.UTF8.GetBytes(new string('p', 4096 - control + delta)));
            using var handler = new ScriptedHandler(corpus, "cs-safe");
            var result = await fixture.Run(handler);
            Proof.Require(delta == 0 ? result.Value?.ProductReviewAccepted == true && handler.Sends > 0 :
                result.Value?.Failure == SolusFreshReviewFailure.InputProfileRejected && handler.Sends == 0,
                "exact_instruction_boundary");
            fixture.AssertEffectsAbsent();
        }
    }

    private static async Task MutationControl(Corpus corpus)
    {
        Proof.Stage = "current_source_mutation";
        var original = corpus.Files["src/Caller.cs"];
        try
        {
            // Derived synthetic source; the expected finding/observation remains
            // unchanged. The real Host still admits/hash-checks the new tree.
            corpus.Files["src/Caller.cs"] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(original)
                .Replace("Lookup.Find(key).Trim()", "Lookup.Find(key)?.Trim() ?? string.Empty", StringComparison.Ordinal));
            using var fixture = new HostFixture(corpus); using var handler = new ScriptedHandler(corpus, "cs-defect");
            using var sink = new RestrictedSink();
            var result = (await fixture.Run(handler, sink)).Value!;
            Proof.Require(result is not null && !result.ProductReviewAccepted && result.Publication?.CandidateEligible != true,
                "old_answer_cannot_ground_changed_source");
            fixture.AssertEffectsAbsent();
        }
        finally { corpus.Files["src/Caller.cs"] = original; }
    }

    private static void SafeResult(SolusFreshReviewResult result)
    {
        var ordinary = string.Join("|", result, result.Work, result.Context, result.Publication);
        foreach (var canary in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey,
            "R5_PRIVATE_REASONING_CANARY", "R5_PRIVATE_SINK_EXCEPTION_CANARY", "R5_PRIVATE_EXPORT_EXCEPTION_CANARY",
            "APR242_CANDIDATE_CANARY", "APR242_TERMINAL_CANARY" })
            Proof.Require(!ordinary.Contains(canary, StringComparison.Ordinal), "ordinary_sinks_private");
    }

    private static void Emit(Corpus corpus, string id, string evaluation, SolusFreshReviewResult result,
        ScriptedHandler handler, RestrictedSink sink, MeteredProvider meter)
    {
        var usage = result.Work?.CurrentUsage;
        var retainedRecords = 0; var retainedAttempts = 0;
        if (sink.Bytes is not null)
        {
            using var context = JsonDocument.Parse(sink.Bytes);
            retainedRecords = context.RootElement.GetProperty("Records").GetArrayLength();
            retainedAttempts = context.RootElement.GetProperty("Rounds").EnumerateArray()
                .Sum(round => round.GetProperty("Facts").GetArrayLength());
        }
        Proof.Require(meter.MaxRequestBytes <= 65536 && meter.MaxResponseBytes <= 16384 && meter.MaxInputs <= 32 &&
            retainedRecords <= 64 && retainedAttempts <= 64 && (sink.Bytes?.Length ?? 0) <= 196608, "actual_profile_inventory");
        foreach (var attempt in usage?.Accounting?.Attempts ?? [])
            Proof.Require(attempt.Reservation.InputTokens == 8192 && attempt.Reservation.OutputTokens == 1024,
                "actual_reservation_identity");
        Console.WriteLine(JsonSerializer.Serialize(new {
            case_id = id, corpus_manifest_sha256 = corpus.ManifestSha256,
            source_sha = Environment.GetEnvironmentVariable("APR_R5_SOURCE_SHA"),
            profile = "cr1-solus-internal4-dev", package = "0.1.0-internal.4", evaluation,
            admission = result.Context?.Admission.ToString(), runtime = result.Work?.RuntimeReason.ToString(),
            terminal_accepted = result.TerminalAccepted, capture = result.Context?.CaptureStatus.ToString(),
            head = result.Publication?.Head?.ToString(), durable_accepted = false,
            logical_calls = usage?.Attempts.Select(a => a.LogicalCallId).Distinct().Count(),
            physical_attempts = usage?.Attempts.Count, tools_invoked = usage?.ToolInvocations?.Invoked,
            tools_admitted = usage?.ToolCallAdmissions?.Admitted,
            measured_input = usage?.InputTokens.ObservedTokens, measured_output = usage?.OutputTokens.ObservedTokens,
            request_wire_bytes = handler.RequestBytes, response_wire_bytes = handler.ResponseBytes,
            encoded_context_bytes = sink.Bytes?.Length,
            logical_request_bytes = meter.MaxRequestBytes, logical_response_bytes = meter.MaxResponseBytes,
            provider_inputs = meter.MaxInputs, retained_records = retainedRecords, retained_attempts = retainedAttempts,
            input_reservation = usage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.InputTokens,
            output_reservation = usage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.OutputTokens,
        }));
    }
}

internal sealed class ProofFailure(string code) : Exception
{
    internal string Code { get; } = code;
}
internal static class Proof
{
    internal static int Checks, Sends;
    internal static string Stage = "startup";
    internal static string BoundaryFault = "none";
    internal static void Require(bool condition, string code)
    { Interlocked.Increment(ref Checks); if (!condition) throw new ProofFailure(code); }
    internal static readonly string[] DecisiveFacts = [
        "    public static string? Find(string key) => null;",
        "    public static string Label(string key) => Lookup.Find(key).Trim();",
        "export const config = { timeoutMs: 0 }; // zero disables the timeout",
        "export const timeout = config.timeoutMs || 3000;",
        "Never log an upload access token, including during failure handling.",
        "    public static void Failed(string accessToken) => Log(accessToken);",
        "    private static void Log(string value) => System.Console.Error.WriteLine(value);",
    ];
    internal static string[] RequiredFacts(string id) => id switch {
        "cs-defect" or "sticky-only" => DecisiveFacts[..2],
        "cs-safe" => [DecisiveFacts[0], "    public static string Label(string key) => Lookup.Find(key)?.Trim() ?? string.Empty;"],
        "ts-defect" => DecisiveFacts[2..4],
        "ts-safe" => [DecisiveFacts[2], "export const timeout = config.timeoutMs ?? 3000;"],
        "repository-rule" => DecisiveFacts[4..], _ => [],
    };
}
