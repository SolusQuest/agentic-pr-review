using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal static class Program
{
    private static readonly string[] Modes = ["fresh", "seed", "review", "ahead", "stale-evidence", "malformed-response",
        "missing-usage", "cancel-provider", "cancel-before", "policy-mismatch", "tamper-source", "unverified-ahead",
        "capture-throw", "reject-import", "input-over", "readback", "readback-ahead", "recover", "stale-head", "source-mutation", "initial-leak", "policy-leak", "repeated-read"];
    private static async Task<int> Main(string[] args)
    {
        try
        {
            foreach (var key in new[] { "GITHUB_TOKEN", "GH_TOKEN", "NUGET_AUTH_TOKEN", "NuGetPackageSourceCredentials_solus",
                "DEEPSEEK_API_KEY", "OPENAI_API_KEY" })
                Proof.Require(Environment.GetEnvironmentVariable(key) is null, "credential_free_process");
            Proof.Require(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
                System.Runtime.InteropServices.Architecture.X64, "linux_x64");
            Proof.Require(args.Length == 6, "arguments");
            var root = Path.GetFullPath(args[0]); var mode = args[1]; var id = args[2];
            Proof.Require(Modes.Contains(mode) && Corpus.Cases.Contains(id), "declared_scenario");
            var now = long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
            var runId = long.Parse(args[4], System.Globalization.CultureInfo.InvariantCulture);
            using var corpus = new Corpus(args[5]);
            var seed = mode == "seed";
            var readback = mode is "readback" or "readback-ahead";
            var ahead = mode is "ahead" or "stale-evidence" or "unverified-ahead" or "readback-ahead";
            Captured? predecessor = File.Exists(Path.Join(root, "accepted-capture.json"))
                ? JsonSerializer.Deserialize<Captured>(File.ReadAllBytes(Path.Join(root, "accepted-capture.json"))) : null;
            if (mode == "source-mutation") corpus.Files["src/Caller.cs"] = Encoding.UTF8.GetBytes(
                Encoding.UTF8.GetString(corpus.Files["src/Caller.cs"]).Replace("Lookup.Find(key).Trim()", "Lookup.Find(key)?.Trim() ?? string.Empty", StringComparison.Ordinal));
            using var fixture = new HostFixture(corpus, root, mode, !readback && mode != "recover", now,
                reviewJson: mode == "policy-mismatch" ? "{\"maxModelCalls\":1}" : null,
                ahead: ahead, verifiedAhead: mode != "unverified-ahead", authorizedRunId: runId,
                instructionOverride: mode == "policy-leak" ? Encoding.UTF8.GetBytes(Proof.DecisiveFacts[0]) : null);
            await fixture.BindNamesAsync();
            if (readback)
            {
                Proof.Require(predecessor is not null, "readback_predecessor_present");
                await Readback(fixture, predecessor!);
                Emit(mode, id, null, null, null, false, false, null, predecessor, null, 0, 0, 0, true, 0);
                return 0;
            }
            using var handler = new ScriptedHandler(corpus, id, repeatedRead: mode == "repeated-read") { PrivateReasoning = true,
                Restored = mode is not ("fresh" or "seed" or "source-mutation" or "initial-leak" or "policy-leak" or "repeated-read"),
                Ahead = ahead && mode != "stale-evidence", RoundTag = runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Malformed = mode == "malformed-response", MissingUsage = mode == "missing-usage" };
            if (mode == "initial-leak") handler.Focus += "\n" + Proof.DecisiveFacts[0];
            if (mode == "cancel-before") fixture.Fault.Cancellation.Cancel();
            if (mode == "cancel-provider") handler.BeforeResponse = fixture.Fault.Cancellation.Cancel;
            if (mode == "stale-head") handler.BeforeFinal = () => fixture.GitHub.CurrentOverride =
                fixture.GitHub.PullRequest with { HeadSha = new string('f', 40) };
            SolusWitness? witness = null;
            Captured? captured = null;
            SolusFreshReviewResult? work;
            ActionHostCompletion? completion = null;
            bool? captureValid = null;
            bool durable = false, recovered = false;
            string? preparation = null;
            var currentIdentity = new ReviewedIdentity("242", 242, fixture.GitHub.PullRequest.BaseSha, fixture.GitHub.PullRequest.HeadSha);
            IModelProvider Wrap(IModelProvider provider) => witness = new(mode == "reject-import" ? new ImportFailure(provider) : provider, currentIdentity);
            void Capture(SolusRestrictedStateRecord record) { captured = Captured.From(record); fixture.Fault.CurrentReviewCaptured = true; }
            var data = new List<AgentInput> { new(AgentInputSource.Repository, handler.Focus) };
            if (mode == "seed") data.Add(new(AgentInputSource.Repository, ScriptedHandler.PriorOnly));
            else if (handler.Restored) data.Add(new(AgentInputSource.Repository, ScriptedHandler.CurrentOnly));
            if (mode == "input-over") data.Add(new(AgentInputSource.Repository, new string('x', 4097)));
            Proof.Stage = mode;
            if (mode is "fresh" or "source-mutation" or "initial-leak" or "policy-leak" or "repeated-read")
            {
                var result = await new SolusFreshReviewComposition(fixture.Host, () => handler, Wrap)
                    .RunAsync(fixture.Launch, cancellationToken: fixture.Fault.Cancellation.Token, reviewData: data);
                work = result.Value; completion = result.Failure;
            }
            else if (seed)
            {
                var result = await new SolusFreshStateCommitComposition(fixture.Host, () => handler, Wrap,
                    trustedCapturedRecord: Capture).RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token, data);
                work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
                captureValid = result.Value?.CurrentCaptureValid; durable = result.Value?.DurableStateAccepted is true;
                recovered = result.Value?.RecoveredWithoutWork is true;
                preparation = result.Value?.PreparationFailure.ToString();
            }
            else
            {
                var result = await new SolusRestoredReviewComposition(fixture.Host, () => handler, Wrap,
                    trustedCaptureDecorator: mode == "capture-throw" ? sink => new CaptureFailure(sink) : null,
                    trustedCapturedRecord: Capture).RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token, data);
                work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
                captureValid = result.Value?.CurrentCaptureValid; durable = result.Value?.DurableStateAccepted is true;
                recovered = result.Value?.RecoveredWithoutWork is true;
                preparation = result.Value?.PreparationFailure.ToString();
            }
            Proof.Require(fixture.NativeCreates == 0, "no_native_execution");
            var identity = currentIdentity;
            var testCase = Case(corpus, id, identity, ahead && mode != "stale-evidence");
            var run = witness?.ConfigurationFingerprint is { } fingerprint ? new EvaluationRunInput(
                mode + "-" + id + "-" + runId, "deterministic", EvaluationSource.Commit, EvaluationSource.Tree,
                EvaluationSource.Clean, fingerprint) : null;
            var attempt = run is null ? null : EvaluationAttempt.AdmitSolus(run, witness!, fixture.PolicyFingerprint);
            var subject = run is null ? null : EvaluationSubject.AdmitSolus(work, identity, run, witness!, fixture.PolicyFingerprint, handler.RequestDigests);
            EvaluationOutcome? outcome = recovered ? null : subject is null
                ? EvaluationScorer.Failure(testCase, EvaluationFailure.Unknown, attempt)
                : EvaluationScorer.Evaluate(testCase, subject);
            var guards = subject is null ? 0 : AdmissionGuards(testCase, work!, identity, run!, witness!, fixture.PolicyFingerprint, handler.RequestDigests, subject);
            if (outcome is not null) Proof.Require(EvaluationJson.ReadOutcome(EvaluationJson.Write(outcome)) is not null,
                "closed_outcome_admission");
            if (subject is not null && mode is "fresh" or "seed" or "review" or "ahead" or "repeated-read")
            {
                foreach (var fact in ahead ? new[] { Proof.DecisiveFacts[2], "export const timeout = config.timeoutMs || 5000;" } : Proof.RequiredFacts(id))
                    Proof.Require(handler.ReadFacts.Contains(fact), "independent_current_source_oracle");
                Proof.Require(handler.Sends == handler.Turns, "authored_script_consumed");
            }
            if (mode == "repeated-read") Proof.Require(subject is not null && work!.Observations.Length == 3 &&
                work.Observations.GroupBy(o => o.ObservationId).Any(group => group.Count() == 2), "identical_reads_keep_distinct_call_witnesses");
            if (durable && !recovered)
            {
                Proof.Require(captured is not null && captureValid == true && work?.ProductReviewAccepted == true && !recovered,
                    "actual_current_capture_and_acceptance");
                if (predecessor is not null)
                {
                    var context = work!.Context!;
                    Proof.Require(context.History.Count > 0 && context.Checkpoint is not null &&
                        Captured.Id(context.History[^1].Usage.ExecutionId) == predecessor.Execution &&
                        Captured.Id(context.History[^1].RoundId) == predecessor.Round &&
                        Captured.Id(context.History[^1].LogicalWorkId) == predecessor.Work &&
                        captured!.Execution != predecessor.Execution && captured.Round != predecessor.Round && captured.Work != predecessor.Work,
                        "authenticated_distinct_successor_round");
                }
                File.WriteAllBytes(Path.Join(root, "accepted-capture.json"), JsonSerializer.SerializeToUtf8Bytes(captured));
            }
            if (recovered) Proof.Require(work is null && handler.Sends == 0 && completion?.Accounting.IsCompleteZero == true,
                "recovery_does_not_count_as_review");
            Emit(mode, id, work, outcome, witness, durable, recovered, captureValid, predecessor, captured,
                handler.HttpSends, handler.RequestBytes, handler.ResponseBytes, false, guards, completion, preparation);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("APR_SOLUS_EVALUATION_FAILED " +
                (error is ProofFailure failure ? failure.Code : "unexpected_" + Proof.Stage));
            return 1;
        }
    }

    private static EvaluationCase Case(Corpus corpus, string id, ReviewedIdentity identity, bool ahead)
    {
        using var expected = corpus.Expected(id);
        var value = expected.RootElement;
        var defects = value.GetProperty("defects").EnumerateArray().Select(d => new ExpectedDefect(
            d.GetProperty("id").GetString()!, d.GetProperty("severity").GetString()!, d.GetProperty("observation_id").GetString(),
            d.GetProperty("path").GetString()!, d.GetProperty("start_line").GetInt32(), d.GetProperty("end_line").GetInt32())).ToImmutableArray();
        var observations = value.GetProperty("required_observations").EnumerateArray().Select(o => new RequiredObservation(
            o.GetProperty("tool").GetString()!, o.GetProperty("observation_id").GetString())).ToImmutableArray();
        var prohibited = value.GetProperty("prohibited_findings").EnumerateArray().Select(p => new ProhibitedFinding(
            p.GetProperty("path").GetString()!, p.GetProperty("start_line").GetInt32(), p.GetProperty("end_line").GetInt32())).ToImmutableArray();
        if (ahead)
        {
            // Fixed independent successor: Caller is repaired, client changes
            // its fallback to5000. Coverage is authored before execution.
            defects = [new("successor", "high", "5daf2fec6d43ab9d549ab4991e7129fe9abeaf6e992169140a5bf1ada8f71365", "src/client.ts", 2, 2)];
            observations = [new("read_file", null, new("src/config.ts", 1, 1)),
                new("search_text", null, new("src/client.ts", 2, 2)), new("read_file", null, new("src/client.ts", 1, 2))];
            prohibited = [new("src/Caller.cs", 3, 3)];
        }
        return EvaluationCase.Admit(new(ahead ? "ahead-ts-defect" : id, corpus.ManifestSha256,
            new(identity.RepositoryId, identity.ReviewTarget, identity.BaseSha, identity.HeadSha), defects, observations, prohibited))!;
    }

    private static int AdmissionGuards(EvaluationCase testCase, SolusFreshReviewResult result, ReviewedIdentity identity,
        EvaluationRunInput run, SolusWitness witness, string policy, IReadOnlyList<string> wire, EvaluationSubject subject)
    {
        var checks = 0;
        void Reject(SolusFreshReviewResult value)
        { Proof.Require(EvaluationSubject.AdmitSolus(value, identity, run, witness, policy, wire) is null, "completed_subject_guard"); checks++; }
        Reject(result with { ExecutionId = Guid.NewGuid() }); Reject(result with { Context = null });
        Reject(result with { Work = null }); Reject(result with { Review = null }); Reject(result with { TerminalAccepted = false });
        Reject(result with { Review = result.Review! with { Summary = "detached synthetic terminal" } });
        Proof.Require(EvaluationSubject.AdmitSolus(result, identity with { HeadSha = new string('f', 40) }, run, witness, policy, wire) is null,
            "wrong_current_snapshot_guard"); checks++;
        if (result.Observations.Length > 0)
        {
            Reject(result with { Observations = [] });
            Reject(result with { Observations = result.Observations.SetItem(0, result.Observations[0] with { ObservationId = new string('f', 64) }) });
            var groundedIndex = Enumerable.Range(0, result.Observations.Length).FirstOrDefault(i => result.Observations[i].ReturnedLines.Count > 0, -1);
            if (groundedIndex >= 0) Reject(result with { Observations = result.Observations.SetItem(groundedIndex, result.Observations[groundedIndex] with {
                ReturnedLines = ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.WithComparers(StringComparer.Ordinal) }) });
        }
        var stale = new EvaluationAdjudication(testCase.Input.CorpusSha256, testCase.Sha256,
            subject.ConfigurationSha256, new string('f', 64), []);
        Proof.Require(!EvaluationScorer.IsValidAdjudication(testCase, subject, stale), "stale_annotation_guard");
        var original = EvaluationScorer.Evaluate(testCase, subject);
        if (original.EvidenceStatus == AssertionStatus.Passed)
            Proof.Require(EvaluationScorer.Evaluate(testCase, subject, stale).Code == EvaluationCode.AdjudicationInvalid, "invalid_annotation_overlay");
        checks++;
        // Completed work with an unmet requirement remains completed; it is
        // evidence failure, never a failed run or a passing empty review.
        var extra = new RequiredObservation("read_file", new string('f', 64));
        var missing = EvaluationCase.Admit(testCase.Input with { RequiredObservations = [extra] })!;
        var rejection = EvaluationScorer.Evaluate(missing, subject);
        Proof.Require(rejection.ExecutionStatus == EvaluationStatus.Completed && rejection.EvidenceStatus == AssertionStatus.Failed &&
            rejection.ModelStatus == ModelObservationStatus.NotEvaluated, "completed_evidence_failure_dimensions"); checks++;
        return checks;
    }

    private static async Task Readback(HostFixture fixture, Captured expected)
    {
        var phase = "not_entered";
        var result = await fixture.Host.RunPreparedFreshAsync(fixture.Launch, async prepared =>
        {
            phase = "target_preparation";
            var execution = new SolusFreshReviewComposition(fixture.Host).PrepareStateExecution(prepared);
            phase = "state_restore";
            var restored = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, execution.Compatibility, execution.ExecutionId,
                    SolusTransactionMode.CompletedContextReview) }, default);
            using var state = restored.Context;
            phase = restored.Succeeded ? "restore_context" : "restore_" + restored.Code;
            Proof.Require(restored.Succeeded && state is not null, "authenticated_predecessor_restore");
            phase = "record_accessor";
            Proof.Require(RestrictedStateService.TryReadAcceptedSolusRecord(state!, out var record) && record is not null,
                "authenticated_predecessor_record");
            phase = "record_projection";
            var actual = Captured.From(record!);
            phase = "record_comparison";
            Proof.Require(actual == expected, "authenticated_predecessor_readback");
            phase = "verified";
            return new ReadbackResult(true);
        }, default);
        Proof.Require(result.Value?.Verified == true && fixture.NativeCreates == 0,
            ("readback_without_work_" + phase + "_" + result.Failure?.Status).ToLowerInvariant());
    }
    private sealed record ReadbackResult(bool Verified);

    private static void Emit(string mode, string id, SolusFreshReviewResult? work, EvaluationOutcome? outcome,
        SolusWitness? witness, bool durable, bool recovered, bool? captureValid, Captured? predecessor, Captured? captured,
        int sends, long wireRequest, long wireResponse, bool readback, int guards,
        ActionHostCompletion? completion = null, string? preparationFailure = null)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject(); writer.WriteString("schema", "apr-solus-evaluation-case-v1");
            writer.WriteString("mode", mode); writer.WriteString("case_id", id);
            writer.WriteString("source_commit", EvaluationSource.Commit); writer.WriteString("source_tree", EvaluationSource.Tree);
            writer.WriteBoolean("source_clean", EvaluationSource.Clean);
            writer.WriteString("package_version", SolusRestrictedStateFormat.PackageVersion);
            writer.WriteString("profile", SolusRestrictedStateFormat.Profile);
            writer.WriteString("package_closure_sha256", SolusRestrictedStateFormat.PackageClosureSha256);
            writer.WriteNumber("context_format", SolusRestrictedStateFormat.ContextFormat);
            writer.WriteNumber("context_compatibility", SolusRestrictedStateFormat.ContextCompatibility);
            writer.WriteBoolean("durable_accepted", durable); writer.WriteBoolean("recovered_without_work", recovered);
            writer.WriteBoolean("readback_verified", readback); writer.WriteBoolean("product_accepted", work?.ProductReviewAccepted == true);
            writer.WriteBoolean("candidate_eligible", work?.Publication?.CandidateEligible == true);
            writer.WriteNumber("sends", sends); writer.WriteNumber("native_creates", 0); writer.WriteNumber("admission_guards", guards);
            writer.WriteString("admission", work?.Context?.Admission.ToString());
            writer.WriteString("termination", work?.Work?.Termination.ToString());
            writer.WriteString("host_status", completion?.Status.ToString());
            writer.WriteString("preparation_failure", preparationFailure);
            writer.WriteString("context_rejection", work?.Context?.RejectionCode.ToString());
            writer.WriteString("capture_status", work?.Context?.CaptureStatus.ToString());
            writer.WritePropertyName("outcome"); if (outcome is null) writer.WriteNullValue(); else writer.WriteRawValue(EvaluationJson.Write(outcome));
            writer.WritePropertyName("capture_valid"); if (captureValid is null) writer.WriteNullValue(); else writer.WriteBooleanValue(captureValid.Value);
            Number(writer, "current_attempts", work?.Work?.CurrentUsage?.Attempts.Count);
            Number(writer, "historical_attempts", work?.Context?.History.Sum(r => r.Usage.Attempts.Count));
            Number(writer, "historical_rounds", work?.Context?.History.Count);
            Number(writer, "current_tools", work?.Observations.IsDefault == false ? work.Observations.Length : null);
            Number(writer, "historical_tool_results", witness?.HistoricalResults);
            Number(writer, "provider_inputs", witness?.MaxInputs); Number(writer, "logical_request_bytes", witness?.MaxRequestBytes);
            Number(writer, "logical_response_bytes", witness?.MaxResponseBytes);
            writer.WriteNumber("wire_request_bytes", wireRequest); writer.WriteNumber("wire_response_bytes", wireResponse);
            Number(writer, "input_tokens", work?.Work?.CurrentUsage?.InputTokens.ObservedTokens);
            Number(writer, "output_tokens", work?.Work?.CurrentUsage?.OutputTokens.ObservedTokens);
            Number(writer, "input_reservation", work?.Work?.CurrentUsage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.InputTokens);
            Number(writer, "output_reservation", work?.Work?.CurrentUsage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.OutputTokens);
            writer.WritePropertyName("predecessor"); JsonSerializer.Serialize(writer, predecessor);
            writer.WritePropertyName("capture"); JsonSerializer.Serialize(writer, captured);
            writer.WriteEndObject();
        }
        var text = Encoding.UTF8.GetString(output.ToArray());
        foreach (var canary in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey,
            ScriptedHandler.PriorOnly, ScriptedHandler.CurrentOnly, "R5_PRIVATE_REASONING_CANARY", "APR242_TERMINAL_CANARY",
            "APR242_CANDIDATE_CANARY", "APR242_NOTES_CANARY", "APR242_POLICY_CANARY", "APR242_SOURCE_CANARY" })
            Proof.Require(!text.Contains(canary, StringComparison.Ordinal), "public_report_privacy");
        Console.WriteLine(text);
    }
    private static void Number(Utf8JsonWriter writer, string key, long? value)
    { if (value is null) writer.WriteNull(key); else writer.WriteNumber(key, value.Value); }
}

internal sealed record Captured(string Payload, int Bytes, int Records, int Attempts, int Rounds,
    string Execution, string Round, string Work, string Checkpoint, string RuntimeConfiguration, string ProviderConfiguration)
{
    internal static string Id(Guid id) => AgentCanonical.HashRaw(Encoding.ASCII.GetBytes(id.ToString("N")));
    internal static Captured From(SolusRestrictedStateRecord record)
    {
        var bytes = record.CopyRestrictedPayload();
        try
        {
            using var document = JsonDocument.Parse(bytes); var value = document.RootElement;
            var rounds = value.GetProperty("Rounds");
            return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, value.GetProperty("Records").GetArrayLength(),
                rounds.EnumerateArray().Sum(r => r.GetProperty("Usage").GetProperty("Attempts").GetArrayLength()), rounds.GetArrayLength(),
                Id(record.Checkpoint.ExecutionId), Id(record.Checkpoint.RoundId), Id(record.Checkpoint.LogicalWorkId),
                Id(record.Checkpoint.CheckpointId), record.Compatibility.RuntimeConfigurationSha256, record.Compatibility.ProviderConfigurationSha256);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public override string ToString() => nameof(Captured);
}

internal sealed class ImportFailure(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => false;
}
