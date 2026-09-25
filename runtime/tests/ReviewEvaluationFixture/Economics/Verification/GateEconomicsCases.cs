using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateEconomicsCases
{
    internal static async Task RunAsync(List<GateCase> cases, string fixtures, string root)
    {
        var tariff = Path.Combine(root, "economics-tariff.json");
        File.WriteAllBytes(tariff, GateTokenCases.TariffBytes(GateTokenCases.Input(unit: 1_000_000, places: 6)));
        EconomicsPlanInput Prepare(ImmutableArray<EconomicsScenario> scenarios = default, int childSeconds = 30, int spacing = 0) =>
            EconomicsCommand.Prepare(Path.Combine(fixtures, "replay"), Path.Combine(fixtures, "growth"), tariff,
                scenarios, childSeconds, spacing);
        var replay = Prepare([new("replay", 3, 1, false)]);
        async Task Add(string id, string selected, EconomicsPlanInput? plan = null, EconomicsFault fault = EconomicsFault.None,
            int index = 1, bool execute = false)
        {
            var evidence = await RunCase(root, id, plan ?? replay, fault, index, execute);
            cases.Add(GateCase.Create(id, selected, JsonSerializer.SerializeToUtf8Bytes(evidence, GateJson.Default.GateEconomics)));
        }
        await Add("c2-replay", "replay");
        var candidatePerCall = replay.Bounds.PerCall with { MaxOutputTokens = DeepSeekRequestWriter.CandidateMaxTokens };
        var candidate = replay with
        {
            Provider = replay.Provider with
            {
                AdapterId = DeepSeekAdapterContext.CandidateAdapter,
                ConfigurationSha256 = LivePlanAdmission.ProviderConfigurationSha256(DeepSeekRequestProfile.Output8192),
            },
            Bounds = replay.Bounds with
            {
                PerCall = candidatePerCall,
                MaxOutputTokens = replay.Bounds.MaxModelCalls * candidatePerCall.MaxOutputTokens,
                MaxCombinedTokens = replay.Bounds.MaxModelCalls *
                    (candidatePerCall.MaxInputTokens + candidatePerCall.MaxOutputTokens),
            },
        };
        await Add("c2-output8192", "candidate-output8192", candidate);
        await Add("c2-full", "capacity-reset", Prepare());
        // Deliberately fail on an uncommitted build. Final acceptance cannot
        // silently take the dirty-source rejection branch in place of execute.
        await Add("c2-execute-loopback", "execute-loopback", execute: true);
        foreach (var (id, fault) in new[]
        {
            ("c2-wrong-source", EconomicsFault.WrongSource), ("c2-wrong-build", EconomicsFault.WrongBuild),
            ("c2-wrong-predecessor", EconomicsFault.WrongPredecessor), ("c2-before-ready-crash", EconomicsFault.BeforeReadyCrash),
            ("c2-after-prepare-crash", EconomicsFault.AfterPrepareCrash), ("c2-partial-reply", EconomicsFault.PartialReply),
            ("c2-oversized-reply", EconomicsFault.OversizedReply), ("c2-wrong-reply", EconomicsFault.WrongReply),
            ("c2-rate-limit", EconomicsFault.RateLimit), ("c2-usage-violation", EconomicsFault.UsageViolation),
            ("c2-provider-failure", EconomicsFault.ProviderFailure), ("c2-cancel-after-usage", EconomicsFault.CancelAfterUsage),
            ("c2-cancel-after-prepare", EconomicsFault.CancelAfterPrepare), ("c2-reject-accept", EconomicsFault.RejectAccept),
        }) await Add(id, fault.ToString(), fault: fault);
        // Leave bounded startup headroom under the external syscall audit so
        // the negative proves a ready worker hung, not merely a slow launch.
        await Add("c2-hang", "Hang", replay with { ChildSeconds = 5 }, EconomicsFault.Hang, index: 0);
        await Add("c2-preparation-failure", "PrepareWriteFailure", fault: EconomicsFault.PrepareWriteFailure);
        await Add("c2-credential-probe", "CredentialProbe");

        var invalidPath = Path.Combine(root, "budget-plan.json");
        File.WriteAllBytes(invalidPath, JsonSerializer.SerializeToUtf8Bytes(replay with
        { Bounds = replay.Bounds with { MaxModelCalls = replay.Bounds.MaxModelCalls - 1 } }, EconomicsLiveJson.Default.EconomicsPlanInput));
        var secret = new Secrets(false); var launches = 0; string? rejection = null;
        try { await EconomicsRunner.RunAsync(invalidPath, false, new() { Secrets = secret, BeforeChild = _ => launches++ }); }
        catch (EconomicsRejected error) { rejection = error.Code; }
        Require(rejection == "r6_economics_allocation_invalid" && secret.Reads == 0 && launches == 0);
        cases.Add(Scalar("c2-plan-budget", "allocation-refused", rejection!, [secret.Reads, launches]));
        await Add("c2-spaced-repeat", "repeat-spacing", Prepare([new("replay", 2, 2, false)], spacing: 50));
        await Add("c2-final-missing", "final-missing", fault: EconomicsFault.AfterPrepareCrash, index: 2);
        await Add("c2-cleanup", "cleanup-negative");
        await Add("c2-unreaped", "unreaped-negative");
        await Add("c2-campaign-deadline", "campaign-deadline", Prepare([new("replay", 2, 1, false)], childSeconds: 1));
    }

    internal static async Task<GateEconomics> RunCase(string root, string id, EconomicsPlanInput plan,
        EconomicsFault fault = EconomicsFault.None, int index = 1, bool execute = false)
    {
        var path = Path.Combine(root, id + "-plan.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(plan, EconomicsLiveJson.Default.EconomicsPlanInput));
        var probe = id == "c2-credential-probe";
        var retained = id is "c2-cleanup" or "c2-unreaped";
        var secrets = new Secrets(probe);
        var workers = new List<GateWorker>();
        var proofs = new List<EconomicsCredentialProof>();
        var keys = new List<byte[]>();
        var previous = probe ? EconomicsCredentialProbe.Canaries.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable) : [];
        string? privateRoot = null;
        var cleanupCalled = false;
        using var cancellation = new CancellationTokenSource();
        var elapsed = Stopwatch.StartNew();
        try
        {
            if (probe) foreach (var canary in EconomicsCredentialProbe.Canaries) Environment.SetEnvironmentVariable(canary.Key, canary.Value);
            var result = await EconomicsRunner.RunAsync(path, execute, new()
            {
                LoopbackExecute = execute, CredentialProbe = probe, Fault = fault, FaultIndex = index, Secrets = secrets,
                PrivateRoot = value => privateRoot = value,
                Cleanup = value => { cleanupCalled = true; return !retained && ReplayProcess.Cleanup(value); },
                BeforeChild = input => { if (probe) keys.Add(input.StateKey.ToArray()); },
                BeforeAccept = () => { if (id == "c2-cleanup") cancellation.Cancel(); },
                ObservedReceipt = receipt =>
                {
                    if (!probe) { Require(receipt.CredentialProof is null); return; }
                    var proof = receipt.CredentialProof ?? throw new InvalidOperationException("r6_gate_credential_proof");
                    Require(proof.Requests == 2 && proof.EnvironmentChecked && proof.CompletedSessionChecked &&
                        proof.RestoredSessionChecked == (receipt.Index > 0) && proof.StoredObjects > 0);
                    EconomicsCredentialProbe.AssertProtected(JsonSerializer.SerializeToUtf8Bytes(receipt, EconomicsLiveJson.Default.EconomicsReceipt), keys[receipt.Index]);
                    proofs.Add(proof);
                },
                Process = async (input, credential, token) =>
                {
                    Require(input.Transport == "loopback" && (input.Fault == EconomicsFault.CredentialProbe) == probe);
                    if (id == "c2-unreaped") throw new ReplayProcessUnreaped(); // no worker was launched
                    if (id == "c2-campaign-deadline")
                    {
                        try { await Task.Delay(Timeout.Infinite, token); }
                        catch (OperationCanceledException) { Require(token.IsCancellationRequested && !cancellation.IsCancellationRequested); }
                    }
                    return await EconomicsProcess.RunWithStartAsync(input, credential, token, null,
                        ready => workers.Add(new(ready.Index, ready.ProcessId, ready.Startup)));
                },
            }, cancellation.Token);
            foreach (var worker in workers) GateHistoryOracle.Absent(worker.ProcessId);
            var bytes = EconomicsReportJson.Write(result);
            foreach (var key in keys) EconomicsCredentialProbe.AssertProtected(bytes, key);
            Require(secrets.Reads == (probe ? 1 : 0));
            if (id == "c2-hang") Require(elapsed.Elapsed < TimeSpan.FromSeconds(15) && workers.Count == 1);
            Require(privateRoot is not null);
            if (retained)
            {
                Require(result.Cleanup == "cleanup_failed" && Directory.Exists(privateRoot));
                Require(id == "c2-unreaped" ? !cleanupCalled && workers.Count == 0 && result.StopReason == "child_unreaped" :
                    cleanupCalled && result.StopReason == "caller_cancelled");
                // Only the controlled negative owns this cleanup after the
                // fixture has independently proved no worker remains.
                Require(ReplayProcess.Cleanup(privateRoot!));
            }
            Require(!Directory.Exists(privateRoot));
            return new(result, [.. workers], [.. proofs], secrets.Reads, retained, true);
        }
        finally
        {
            foreach (var entry in previous) Environment.SetEnvironmentVariable(entry.Key, entry.Value);
            foreach (var key in keys) CryptographicOperations.ZeroMemory(key);
            // No unconditional deletion here: an unexpected unreaped process
            // must retain its state and fail the outer ownership check.
        }
    }

    private sealed class Secrets(bool probe) : ILiveSecretSource
    {
        internal int Reads { get; private set; }
        public string? TakeProviderCredential()
        {
            Reads++;
            Require(probe);
            return EconomicsCredentialProbe.Provider;
        }
    }
}
