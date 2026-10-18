using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Host.State;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal static class CapacityHostRun
{
    internal static async Task Run(string[] args)
    {
        Proof.Require(args.Length == 8, "host_arguments");
        var root = Path.GetFullPath(args[0]); var mode = args[1]; var id = args[2];
        var now = long.Parse(args[3], System.Globalization.CultureInfo.InvariantCulture);
        var round = int.Parse(args[5], System.Globalization.CultureInfo.InvariantCulture);
        var config = args[6] == "omitted" ? null : args[6]; var scenario = args[7];
        using var corpus = new Corpus(args[4]);
        if (scenario is "escaped" or "result-over")
            corpus.Files.Add("src/capacity-helper.cs", Encoding.UTF8.GetBytes(
                "// bounded companion source \"<tag>\\tail\" " + new string('x', scenario == "result-over" ? 5000 : 2700) + "\n"));
        using var fixture = new HostFixture(corpus, root, round == 0 ? "seed" : mode, mode != "recover", now,
            reviewJson: config, round: round);
        await fixture.BindNamesAsync();
        using var handler = new ScriptedHandler(corpus, id, scenario) { Round = round, Restored = round > 0,
            PrivateReasoning = true, DelayMilliseconds = scenario == "deadline" ? 1500 : 0 };
        if (scenario == "cancel-provider") handler.BeforeResponse = fixture.Fault.Cancellation.Cancel;
        if (scenario == "cancel-before") fixture.Fault.Cancellation.Cancel();
        var data = new List<AgentInput> { new(AgentInputSource.Repository, handler.Focus),
            new(AgentInputSource.Repository, round == 0 ? ScriptedHandler.PriorOnly : ScriptedHandler.CurrentOnly) };
        if (scenario is "data-exact" or "data-over" or "recovery-input-over")
            data.Add(new(AgentInputSource.Repository, new string('x', scenario == "data-exact" ? 4096 : 4097)));
        if (scenario == "input-count-over")
            while (data.Count < 31) data.Add(new(AgentInputSource.Repository, "classified data"));
        CapacityMeter? meter = null;
        SolusFreshReviewResult? work;
        ActionHostCompletion? completion;
        var accepted = false;
        var recovered = false;
        var preparationFailure = SolusFreshReviewFailure.None;
        bool? captured = null;
        object? retained = null;
        void Capture(SolusRestrictedStateRecord record)
        {
            var bytes = record.CopyRestrictedPayload();
            try
            {
                File.WriteAllBytes(Path.Join(root, "capture-r" + round + ".json"), bytes);
                using var document = JsonDocument.Parse(bytes);
                var value = document.RootElement;
                var rounds = value.GetProperty("Rounds").EnumerateArray().ToArray();
                var ordinary = value.GetProperty("Records").GetArrayLength();
                var episodes = value.GetProperty("Candidates").GetProperty("Episodes").GetArrayLength();
                var submissions = value.GetProperty("Candidates").GetProperty("Submissions").GetArrayLength();
                var recovery = rounds.Sum(r => r.TryGetProperty("RecoveryActions", out var actions) &&
                    actions.ValueKind == JsonValueKind.Array ? actions.GetArrayLength() : 0);
                retained = new { encoded_bytes = bytes.Length, ordinary_records = ordinary,
                    candidate_episodes = episodes, candidate_submissions = submissions, recovery_actions = recovery,
                    candidate_record_inventory = ordinary + episodes + submissions + recovery,
                    tool_members = value.GetProperty("Members").GetArrayLength(), rounds = rounds.Length,
                    attempts = rounds.Sum(r => r.GetProperty("Usage").GetProperty("Attempts").GetArrayLength()),
                    payload_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
                fixture.Fault.CurrentReviewCaptured = true;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        Proof.Stage = "host_" + mode;
        if (mode == "ordinary")
        {
            var result = await new SolusFreshReviewComposition(fixture.Host, () => handler,
                trustedProviderDecorator: provider => meter = new(provider)).RunAsync(fixture.Launch,
                    cancellationToken: fixture.Fault.Cancellation.Token, reviewData: data);
            work = result.Value; completion = result.Failure;
        }
        else if (round == 0)
        {
            var result = await new SolusFreshStateCommitComposition(fixture.Host, () => handler,
                trustedProviderDecorator: provider => meter = new(provider),
                trustedFileAccess: scenario == "cancel-tool" ? _ => new CancellingFiles(fixture.Fault.Cancellation) : null,
                trustedCaptureDecorator: scenario == "cancel-capture" ? sink => new CancelCapture(sink, fixture.Fault.Cancellation) : null,
                trustedCapturedRecord: Capture).RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token, data);
            work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
            captured = result.Value?.CurrentCaptureValid; accepted = result.Value is { DurableStateAccepted: true };
            preparationFailure = result.Value?.PreparationFailure ?? SolusFreshReviewFailure.None;
            recovered = result.Value is { RecoveredWithoutWork: true };
        }
        else
        {
            var result = await new SolusRestoredReviewComposition(fixture.Host, () => handler,
                trustedProviderDecorator: provider => meter = new(provider),
                trustedFileAccess: scenario == "cancel-tool" ? _ => new CancellingFiles(fixture.Fault.Cancellation) : null,
                trustedCaptureDecorator: scenario == "cancel-capture" ? sink => new CancelCapture(sink, fixture.Fault.Cancellation) : null,
                trustedCapturedRecord: Capture).RunAsync(fixture.Launch, fixture.Fault.Cancellation.Token, data);
            work = result.Value?.Work; completion = result.Value?.Completion ?? result.Failure;
            captured = result.Value?.CurrentCaptureValid; accepted = result.Value is { DurableStateAccepted: true };
            preparationFailure = result.Value?.PreparationFailure ?? SolusFreshReviewFailure.None;
            recovered = result.Value is { RecoveredWithoutWork: true };
        }
        if (work?.ProductReviewAccepted == true && scenario is "useful" or "six-tools" or "escaped")
        {
            foreach (var fact in Proof.RequiredFacts(id)) Proof.Require(handler.ReadFacts.Contains(fact), "useful_current_facts");
            Proof.Require(work.Review!.Findings.Length == (id.EndsWith("safe", StringComparison.Ordinal) ? 0 : 1), "authored_findings_oracle");
        }
        Proof.Require(fixture.NativeCreates == 0, "no_native_fallback");
        var usage = work?.Work?.CurrentUsage;
        var report = JsonSerializer.Serialize(new { mode, scenario, case_id = id, round, accepted, recovered_without_work = recovered,
            clean_review = work?.ProductReviewAccepted == true, captured, retained,
            sends = handler.Sends, native_creates = fixture.NativeCreates, imports = meter?.Imports ?? 0,
            state_names_bound = fixture.NamesBound,
            status = completion?.Status.ToString(), input_failure = work?.Failure.ToString(),
            preparation_failure = preparationFailure.ToString(),
            accounting_zero = completion?.Accounting.IsCompleteZero,
            admission = work?.Context?.Admission.ToString(), rejection = work?.Context?.RejectionCode.ToString(),
            termination = work?.Work?.Termination.ToString(), capture_status = work?.Context?.CaptureStatus.ToString(),
            findings = work?.Review?.Findings.Length, current_observations = ObservationCount(work),
            current_attempts = usage?.Attempts.Count, historical_attempts = work?.Context?.History.Sum(r => r.Usage.Attempts.Count),
            current_logical_calls = usage?.Attempts.Select(a => a.LogicalCallId).Distinct().Count(),
            current_model_work_units = work?.Context?.Outcome?.CompletedWorkUnits,
            exposure = usage?.Attempts.GroupBy(a => a.Exposure.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            tools_admitted = usage?.ToolCallAdmissions?.Admitted, tools_invoked = usage?.ToolInvocations?.Invoked,
            current_input_tokens = usage?.InputTokens.ObservedTokens, current_output_tokens = usage?.OutputTokens.ObservedTokens,
            historical_input_tokens = work?.Context is { } context ? SumKnown(context.History.Select(r => r.Usage.InputTokens.ObservedTokens)) : null,
            historical_output_tokens = work?.Context is { } outputContext ? SumKnown(outputContext.History.Select(r => r.Usage.OutputTokens.ObservedTokens)) : null,
            cached_tokens = work?.Work?.Accounting?.CacheHitTokens, uncached_tokens = work?.Work?.Accounting?.CacheMissTokens,
            input_allowance = usage?.Accounting?.Input.Allowance, output_allowance = usage?.Accounting?.Output.Allowance,
            input_remaining = usage?.Accounting?.Input.RemainingAllowance, output_remaining = usage?.Accounting?.Output.RemainingAllowance,
            input_reservation = usage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.InputTokens,
            output_reservation = usage?.Accounting?.Attempts.FirstOrDefault()?.Reservation.OutputTokens,
            wire_request_bytes = handler.RequestBytes, wire_response_bytes = handler.ResponseBytes,
            controlled_transport_failure = handler.FailureCode,
            wire = handler.Wire, exchanges = meter?.Exchanges, admission_checks = meter?.AdmissionChecks, checks = Proof.Checks });
        foreach (var secret in new[] { HostFixture.ProviderKey, HostFixture.GitHubKey, HostFixture.StateKey,
            ScriptedHandler.PriorOnly, ScriptedHandler.CurrentOnly, "R5_PRIVATE_REASONING_CANARY" })
            Proof.Require(!report.Contains(secret, StringComparison.Ordinal), "sanitized_capacity_report");
        Console.WriteLine(report);
    }

    internal static int? ObservationCount(SolusFreshReviewResult? work) =>
        work is { Observations.IsDefault: false } ? work.Observations.Length : null;

    private static long? SumKnown(IEnumerable<long?> values)
    {
        long total = 0;
        foreach (var value in values)
        {
            if (value is null) return null;
            total = checked(total + value.Value);
        }
        return total;
    }

    private sealed class CancelCapture(IRestrictedContextSink inner, CancellationTokenSource source) : IRestrictedContextSink
    {
        public void Capture(AgentContextEnvelope context) { inner.Capture(context); source.Cancel(); }
    }
}
