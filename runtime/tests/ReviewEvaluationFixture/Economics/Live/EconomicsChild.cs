using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsChild
{
    internal static string StateRoot(string root, int chain) => Path.Combine(root, "chain-" + chain);

    internal static async Task<int> MainAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(300));
        EconomicsChildInput? input = null;
        try
        {
            input = await EconomicsWire.ReadAsync(Console.OpenStandardInput(), EconomicsLiveJson.Default.EconomicsChildInput,
                EconomicsLiveLimits.InputBytes, deadline.Token);
            if (!ValidHeader(input)) return 2;
            deadline.CancelAfter(TimeSpan.FromSeconds(input.Plan.ChildSeconds));
            if (input.Fault == EconomicsFault.BeforeReadyCrash) return 9;
            var plan = EconomicsPlan.Admit(input.Plan, input.Transport == "live");
            _ = plan.LoadTariff(deadline.Token);
            if (input.PlanSha256 != plan.Sha256 || input.WorkloadSha256 != plan.WorkloadSha256 ||
                input.Slot != plan.Slots[input.Slot.Index] || input.Lease.Allocation != plan.ChildAllocation ||
                input.Lease.Index != input.Slot.Index)
                return 2;
            var workload = EconomicsWorkload.Load(plan, deadline.Token);
            var run = workload.Run(input.Slot, input.Campaign);
            var prior = input.Slot.Previous is { } previous ? workload.Run(plan.Slots[previous], input.Campaign) : null;
            using var state = new ReplayState(run, input.Session, StateRoot(input.Root, input.Slot.Chain), input.StateKey,
                input.Fault == EconomicsFault.PrepareWriteFailure ? () => throw new IOException() : null,
                plan.TrustedRequest(run));
            var identity = run.Input.ReviewedIdentity.Runtime;
            var producer = prior?.Input.ReviewedIdentity.Runtime ?? identity;
            var transition = ReplayState.Transition(run, prior);
            if (input.Predecessor is { } selected && (selected.Scope != state.Access.Scope ||
                selected.Generation != input.Slot.Phase - 1) || (input.Slot.Phase == 0) != (input.Predecessor is null)) return 2;
            var context = state.Context(producer, identity, input.Predecessor?.Generation ?? 0,
                input.Predecessor?.ExpectedPredecessorEnvelopeSha256, transition, run.InitialContext);
            var predecessor = input.Fault == EconomicsFault.WrongPredecessor && input.Predecessor is { } wrong
                ? wrong with { Generation = wrong.Generation + 1 } : input.Predecessor;
            var restored = await state.Service.RestoreAsync(state.Access,
                new(predecessor is null ? RestrictedStateLocatorFamily.Absent : RestrictedStateLocatorFamily.Current,
                    predecessor is null ? RestrictedStateRestoreIntent.Automatic : RestrictedStateRestoreIntent.Explicit,
                    predecessor, context), deadline.Token);
            AgentRunRequest request;
            AgentSessionPredecessor? sessionPredecessor = null;
            PrefixBoundary boundary;
            if (predecessor is null)
            {
                if (restored.Result.Action != StateAction.Bootstrap ||
                    !AgentStableRequestMaterializer.TryMaterialize(state.Trusted, null, out var stable)) return 2;
                request = new(identity, stable!.StablePlan, input.Session,
                    [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
                boundary = PrefixBoundary.Bootstrap(state.Trusted, request);
            }
            else
            {
                if (restored.Result.Action != StateAction.Restored || restored.Session?.Value is not { } admitted) return 2;
                request = admitted.RunRequest;
                boundary = PrefixBoundary.Restored(state.Trusted, AgentSessionRestoreResult.Success(request, admitted.Artifact));
                sessionPredecessor = new(admitted.Artifact.Plaintext, predecessor.SessionSha256, predecessor.EnvelopeSha256,
                    predecessor.Generation, producer.BaseSha, producer.HeadSha, predecessor.ExpectedPredecessorEnvelopeSha256);
            }
            var baseline = HistoryChatClient.Measure(boundary, HistoryCapture.Request(request));
            if (baseline is null) return 2;
            var startup = Guid.NewGuid().ToString("N");
            var ready = new EconomicsChildReady(input.Operation, plan.Sha256, input.Slot.Index, input.Lease.Id,
                EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean, EconomicsBuild.Current(),
                Environment.ProcessId, startup, "ready", predecessor is not null);
            await EconomicsWire.WriteAsync(Console.OpenStandardOutput(), ready,
                EconomicsLiveJson.Default.EconomicsChildReady, 16384, deadline.Token);
            var secret = await EconomicsWire.ReadAsync(Console.OpenStandardInput(), EconomicsLiveJson.Default.EconomicsSecretFrame,
                16384, deadline.Token);
            if (secret.Operation != input.Operation || secret.LeaseId != input.Lease.Id ||
                input.Transport == "loopback" && (input.Fault == EconomicsFault.CredentialProbe
                    ? secret.Credential != EconomicsCredentialProbe.Provider : secret.Credential is not null)) return 2;
            if (input.Fault == EconomicsFault.Hang) await Task.Delay(Timeout.Infinite, deadline.Token);
            var accounting = new LiveAccounting(plan.ChildBounds);
            var calls = new EconomicsCalls();
            using var credentialProbe = input.Fault == EconomicsFault.CredentialProbe ? new EconomicsCredentialProbe(run.Script) : null;
            IDeepSeekTransport underlying = input.Transport == "live"
                ? LiveDeepSeekTransportFactory.Instance.Create(DeepSeekCredential.Create(secret.Credential ?? throw new IOException()))
                : credentialProbe is not null ? DeepSeekTransport.CreateForTesting(DeepSeekCredential.Create(secret.Credential!),
                    credentialProbe, TimeSpan.FromSeconds(input.Plan.ChildSeconds))
                : new EconomicsLoopback(run.Script, input.Fault, plan.Input.Bounds.PerCall.MaxInputTokens,
                    plan.Input.Bounds.PerCall.MaxOutputTokens);
            using var metered = new LiveMeteredTransport(underlying, accounting, calls);
            var backend = DeepSeekChatBackend.CreateClient(new(state.Trusted.ProviderId, state.Trusted.ModelId,
                state.Trusted.AdapterId, input.Session), metered);
            var observer = new LiveChatObserver(backend, accounting, calls);
            IProjectChatClient chat = input.Fault == EconomicsFault.CancelAfterUsage ? new CancelAfterUsage(observer, deadline) : observer;
            var measurement = new GrowthChatMeasurement(chat);
            var history = new HistoryChatClient(boundary, measurement, baseline);
            Directory.CreateDirectory(Path.Combine(input.Root, "reviewed"));
            var snapshot = run.CreateSnapshot(Path.Combine(input.Root, "reviewed"));
            var descriptor = new EvaluationRunInput(input.Campaign + "-" + (input.Slot.Index + 1),
                input.Transport == "live" ? "live" : "deterministic", EvaluationSource.Commit, EvaluationSource.Tree,
                EvaluationSource.Clean, input.Plan.Provider.ConfigurationSha256);
            var attempt = EvaluationAttempt.Admit(state.Trusted, descriptor) ?? throw new IOException();
            var outcome = await new AgentLoop(history, new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot)),
                limitAuthority: state.Trusted.LimitAuthority)
                .RunAsync(request, deadline.Token);
            EvaluationOutcome evaluation;
            PreparedStateReceipt? prepared = null;
            EconomicsCredentialProof? credentialProof = null;
            string code, stage;
            string? diagnostic, sessionSha = null;
            if (!outcome.Succeeded)
            {
                code = "agent_failed"; stage = "agent"; diagnostic = outcome.Diagnostic?.Code;
                evaluation = EvaluationScorer.Failure(run.Expected, EvaluationFailure.FromAgentOutcome(outcome), attempt);
            }
            else
            {
                var buildInput = new AgentSessionBuildInput(request, outcome, state.Trusted, request.InitialMessages.Length - 1,
                    DeepSeekReasoningContinuationCodec.Instance, sessionPredecessor, transition);
                var built = AgentSessionBuilder.Build(buildInput);
                if (!built.Succeeded || built.Artifact is null)
                {
                    code = "session_failed"; stage = "build"; diagnostic = built.FailureCode;
                    evaluation = EvaluationScorer.Failure(run.Expected, EvaluationFailure.FromSessionBuild(built), attempt);
                }
                else
                {
                    evaluation = EvaluationScorer.Evaluate(run.Expected, EvaluationSubject.Admit(buildInput, descriptor));
                    sessionSha = built.Artifact.SessionSha256;
                    var prepareContext = state.Context(identity, identity, input.Slot.Phase, predecessor?.EnvelopeSha256,
                        transition, run.InitialContext);
                    var result = await state.Service.PrepareAsync(state.Access,
                        new(predecessor, built.Artifact.Plaintext, prepareContext), deadline.Token);
                    // Prove the real service's failed-write recovery shape before C2 normalizes it.
                    if (input.Fault == EconomicsFault.PrepareWriteFailure &&
                        (result.Result.Action == StateAction.Prepared || result.Receipt is null)) throw new IOException();
                    prepared = result.Result.Action == StateAction.Prepared ? result.Receipt : null;
                    stage = "prepare"; diagnostic = result.Result.Code;
                    code = result.Result.Action == StateAction.Prepared ? "prepared" : "state_failed";
                    credentialProof = credentialProbe?.Verify(input.StateKey, sessionPredecessor?.Plaintext,
                        built.Artifact.Plaintext, StateRoot(input.Root, input.Slot.Chain));
                }
            }
            // Finish and seal before publishing a receipt. Late callbacks retain the
            // same call handles and cannot alter either accounting snapshot.
            var callRows = calls.Seal(deadline.IsCancellationRequested);
            var receipt = new EconomicsReceipt(input.Operation, plan.Sha256, plan.WorkloadSha256, input.Slot.Index,
                input.Lease.Id, Environment.ProcessId, startup, input.Transport, input.Session,
                predecessor?.SessionSha256, code, stage, diagnostic, predecessor is not null,
                outcome.Succeeded ? "succeeded" : "failed", evaluation, prepared, callRows, accounting.Seal(),
                outcome.Events.OfType<AgentToolResultEvent>().Count(), baseline.Provider.Whole.Sha256, sessionSha, measurement.Counts,
                credentialProof);
            if (prepared is not null && input.Fault == EconomicsFault.AfterPrepareCrash) return 9;
            if (input.Fault == EconomicsFault.PartialReply) { Console.Write("partial"); return 0; }
            if (input.Fault == EconomicsFault.OversizedReply)
            { await Console.OpenStandardOutput().WriteAsync(new byte[] { 255, 255, 255, 127 }); return 0; }
            if (input.Fault == EconomicsFault.WrongReply) receipt = receipt with { Index = receipt.Index + 1 };
            // A known receipt after cancellation still needs a bounded delivery
            // opportunity; the parent deadline decides whether it was observed.
            using var delivery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await EconomicsWire.WriteAsync(Console.OpenStandardOutput(), receipt,
                EconomicsLiveJson.Default.EconomicsReceipt, EconomicsLiveLimits.ReplyBytes, delivery.Token);
            return 0;
        }
        catch { return 2; }
        finally { if (input?.StateKey is { } key) CryptographicOperations.ZeroMemory(key); }
    }

    private static bool ValidHeader(EconomicsChildInput input) => input is
        { Plan: not null, Slot: not null, Lease: not null, StateKey.Length: 32 } &&
        Guid.TryParseExact(input.Operation, "N", out _) && Guid.TryParseExact(input.Session, "N", out _) &&
        Guid.TryParseExact(input.Lease.Id, "N", out _) && EvaluationLimits.Id(input.Campaign) &&
        input.Slot.Index is >= 0 and < EconomicsLiveLimits.Slots && Enum.IsDefined(input.Fault) &&
        input.Transport is "live" or "loopback" && (input.Transport != "live" || input.Fault == EconomicsFault.None) &&
        input.Root is { Length: > 0 and <= 4096 } && Path.IsPathFullyQualified(input.Root) &&
        Path.GetFileName(input.Root).StartsWith("apr-r5-replay-", StringComparison.Ordinal) &&
        Path.GetFullPath(Directory.GetCurrentDirectory()) == Path.GetFullPath(input.Root) &&
        (File.GetAttributes(input.Root) & FileAttributes.ReparsePoint) == 0 &&
        input.Plan.Replay?.Path == Path.Combine(input.Root, "replay") &&
        input.Plan.Growth?.Path == Path.Combine(input.Root, "growth") && input.Plan.TariffPath == Path.Combine(input.Root, "tariff.json");

    private sealed class CancelAfterUsage(IProjectChatClient inner, CancellationTokenSource cancellation) : IProjectChatClient
    {
        public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            // The observer must seal known usage first. Never return a completed
            // response alongside a cancelled token: WaitAsync may choose either
            // completion, making Agent response measurements timing-dependent.
            _ = await inner.GetResponseAsync(request, token);
            await cancellation.CancelAsync();
            throw new OperationCanceledException(token);
        }
    }
}

internal sealed class EconomicsLoopback : IDeepSeekTransport
{
    private readonly ReplayTransport inner;
    private readonly EconomicsFault fault;
    private readonly long inputBasis;
    private readonly long? expectedOutputCap;
    internal EconomicsLoopback(ReplayScript script, EconomicsFault fault, long inputBasis, long? expectedOutputCap = null)
    {
        this.fault = fault; this.inputBasis = inputBasis; this.expectedOutputCap = expectedOutputCap;
        if (fault is EconomicsFault.ThreeCalls or EconomicsFault.EightCalls)
        {
            var first = script.Turns[0];
            var turns = Enumerable.Range(0, fault == EconomicsFault.ThreeCalls ? 2 : 8)
                .Select(index => first with { ToolCalls = first.ToolCalls.Select(call => call with
                    { Id = index == 0 ? call.Id : "c2-extra-" + index + "-" + call.Id }).ToImmutableArray() }).ToImmutableArray();
            script = new(fault == EconomicsFault.ThreeCalls ? turns.Add(script.Turns[^1]) : turns);
        }
        inner = new(script, ReplayFault.None);
    }
    public async Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody, CancellationToken token)
    {
        if (expectedOutputCap is { } cap)
        {
            try
            {
                using var request = JsonDocument.Parse(requestBody);
                if (request.RootElement.GetProperty("max_tokens").GetInt64() != cap)
                    return DeepSeekTransportResult.RequestRejected();
            }
            catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                return DeepSeekTransportResult.RequestRejected();
            }
        }
        if (fault == EconomicsFault.RateLimit) return DeepSeekTransportResult.HttpFailure(DeepSeekHttpStatusClass.TooManyRequests, 0);
        if (fault == EconomicsFault.ProviderFailure) return DeepSeekTransportResult.TransportFailure();
        var result = await inner.SendAsync(requestBody, token);
        if (fault == EconomicsFault.UsageViolation && result.HasBody)
        {
            var body = Encoding.UTF8.GetString(result.Body.AsSpan())
                .Replace("\"prompt_tokens\":3", "\"prompt_tokens\":" + (inputBasis + 1), StringComparison.Ordinal)
                .Replace("\"total_tokens\":5", "\"total_tokens\":" + (inputBasis + 3), StringComparison.Ordinal)
                .Replace("\"prompt_cache_miss_tokens\":3", "\"prompt_cache_miss_tokens\":" + (inputBasis + 1), StringComparison.Ordinal);
            return DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(body));
        }
        return result;
    }
    public void Dispose() => inner.Dispose();
}
