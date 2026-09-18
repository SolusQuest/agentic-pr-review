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
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record LiveRunResult(
    string StopReason,
    int Attempted,
    int Completed,
    int Failed,
    int Invalid,
    ImmutableArray<EvaluationOutcome> Outcomes,
    LiveRunSummary Summary,
    byte[] ReportJson);

internal sealed class LiveOptions
{
    internal Func<string, CancellationToken, ReplayAdmissionResult> AdmitBundle { get; init; } =
        ReplayAdmission.Load;
    internal ILiveSecretSource SecretSource { get; init; } = new LiveEnvironmentSecretSource();
    internal ILiveTransportFactory TransportFactory { get; init; } = LiveDeepSeekTransportFactory.Instance;
    internal Func<AdmittedReplayRun, IDeepSeekTransport> DryRunTransport { get; init; } =
        run => new ReplayTransport(run.Script, ReplayFault.None);
    internal Action<string>? WriteLine { get; init; }
}

// Serial bounded scheduler over the admitted plan. Each expanded evaluation is
// attempted at most once; reservations, outcomes and stops are recorded at the
// transport/chat boundaries and reconciled into the public-safe report.
internal static class LiveRunner
{
    private const string BuildId = "r5-live-local";

    internal static async Task<int> InvokeAsync(string planPath, bool execute)
    {
        using var commandCancel = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = null;
        try
        {
            handler = (_, args) =>
            {
                args.Cancel = true;
                commandCancel.Cancel();
            };
            Console.CancelKeyPress += handler;
            var result = await RunAsync(planPath, execute, null, commandCancel.Token);
            return result.StopReason == "complete" ? 0 : 1;
        }
        catch (LivePlanRejected rejection)
        {
            Console.Error.WriteLine("r5_live_" + Code(rejection.Code));
            return 2;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("r5_live_cancelled");
            return 1;
        }
        catch
        {
            Console.Error.WriteLine("r5_live_infrastructure_failed");
            return 1;
        }
        finally
        {
            if (handler is not null) Console.CancelKeyPress -= handler;
        }
    }

    internal static async Task<LiveRunResult> RunAsync(
        string planPath, bool execute, LiveOptions? options, CancellationToken token)
    {
        options ??= new();
        var write = options.WriteLine ?? Console.WriteLine;
        var plan = LivePlanAdmission.Load(planPath, execute, token);
        var fixture = AdmitCorpus(plan, options, token);
        var cases = ResolveCases(plan, fixture);
        var nonce = "live-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        if (!AgentValueDomains.IsIdentifier(nonce + "-" + plan.Schedule.Length))
            throw new InvalidOperationException("live_id_invalid");
        var mode = execute ? "live" : "deterministic";
        DeepSeekCredential? credential = null;
        if (execute)
        {
            var secret = options.SecretSource.TakeProviderCredential();
            if (secret is null) throw new LivePlanRejected(LiveAdmissionCode.SecretInvalid);
            try
            {
                credential = DeepSeekCredential.Create(secret);
            }
            catch (ArgumentException)
            {
                throw new LivePlanRejected(LiveAdmissionCode.SecretInvalid);
            }
        }

        var accounting = new LiveAccounting(plan.Bounds);
        var rows = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        var outcomes = ImmutableArray.CreateBuilder<EvaluationOutcome>();
        var completed = 0;
        var failed = 0;
        var invalid = 0;
        var stopReason = "complete";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(plan.Bounds.MaxSeconds));

        for (var index = 0; index < plan.Schedule.Length; index++)
        {
            var caseId = plan.Schedule[index];
            var run = cases[caseId];
            var runId = nonce + "-" + (index + 1);
            var trusted = run.CreateTrustedRequest(BuildId) with
            {
                ProviderId = DeepSeekAdapterContext.Provider,
                ModelId = DeepSeekAdapterContext.Model,
                AdapterId = DeepSeekAdapterContext.Adapter,
            };
            var descriptor = new EvaluationRunInput(runId, mode, EvaluationSource.Commit,
                EvaluationSource.Tree, EvaluationSource.Clean, plan.Provider.ConfigurationSha256);
            var attempt = EvaluationAttempt.Admit(trusted, descriptor);
            EvaluationOutcome? outcome = null;
            if (attempt is null || !AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable))
            {
                invalid++;
                outcome = EvaluationScorer.Failure(run.Expected, EvaluationFailure.Invalid, attempt);
            }
            else
            {
                try
                {
                    outcome = await AttemptAsync(run, trusted, stable!, descriptor, attempt, runId,
                        execute, credential, options, accounting, deadline.Token);
                    switch (outcome.ExecutionStatus)
                    {
                        case EvaluationStatus.Completed: completed++; break;
                        case EvaluationStatus.Invalid: invalid++; break;
                        default: failed++; break;
                    }
                }
                catch (OperationCanceledException)
                {
                    stopReason = token.IsCancellationRequested ? "caller_cancelled" : "deadline";
                    outcome = EvaluationScorer.Failure(run.Expected, EvaluationFailure.Unknown, attempt);
                    failed++;
                }
            }
            rows.Add(EvaluationJson.Write(outcome));
            outcomes.Add(outcome);
            if (stopReason != "complete") break;
            if (accounting.BudgetRefused) { stopReason = "bound_stop"; break; }
            if (accounting.RateLimited) { stopReason = "rate_limited"; break; }
            if (accounting.AccountingViolation) { stopReason = "accounting_violation"; break; }
            if (deadline.IsCancellationRequested)
            {
                stopReason = token.IsCancellationRequested ? "caller_cancelled" : "deadline";
                break;
            }
        }

        var report = EvaluationReport.Create(rows.ToImmutable());
        if (!report.Succeeded || report.Value is null)
            throw new InvalidOperationException("live_report_invalid");
        var reportBytes = EvaluationReportJson.Write(report.Value);
        if (!reportBytes.Succeeded || reportBytes.Value is null)
            throw new InvalidOperationException("live_report_invalid");
        var attempted = completed + failed + invalid;
        var summary = new LiveRunSummary("r5-live-local-v1", execute ? "live" : "loopback",
            plan.Digest, fixture.CorpusSha256, EvaluationSource.Commit, EvaluationSource.Tree,
            EvaluationSource.Clean, plan.Schedule.Length, attempted, completed, failed, invalid,
            plan.Schedule.Length - attempted,
            execute ? 0 : (int)accounting.Sends, execute ? (int)accounting.Sends : 0,
            accounting.KnownInputTokens, accounting.KnownOutputTokens, accounting.KnownCombinedTokens,
            accounting.ReservedInputTokens, accounting.ReservedOutputTokens, accounting.ReservedCombinedTokens,
            accounting.UsageUnknownCalls, accounting.AccountingViolation, accounting.Outcomes,
            accounting.ReservedSpendMicroUsd, plan.Bounds.SpendCeilingMicroUsd, stopReason, "none");
        foreach (var row in rows) write(Encoding.UTF8.GetString(row.Span));
        write(Encoding.UTF8.GetString(reportBytes.Value));
        write(JsonSerializer.Serialize(summary, LiveJsonContext.Default.LiveRunSummary));
        return new(stopReason, attempted, completed, failed, invalid, outcomes.ToImmutable(),
            summary, reportBytes.Value.ToArray());
    }

    private static AdmittedReplayFixture AdmitCorpus(LivePlan plan, LiveOptions options, CancellationToken token)
    {
        var admitted = options.AdmitBundle(plan.Corpus.Path, token);
        if (admitted.Code == ReplayAdmissionCode.Cancelled) throw new OperationCanceledException(token);
        if (admitted.Code == ReplayAdmissionCode.IoFailure) throw new IOException();
        if (admitted.Fixture is not { } fixture ||
            !StringComparer.Ordinal.Equals(fixture.CorpusSha256, plan.Corpus.Sha256))
            throw new LivePlanRejected(LiveAdmissionCode.InvalidCorpus);
        return fixture;
    }

    private static Dictionary<string, AdmittedReplayRun> ResolveCases(
        LivePlan plan, AdmittedReplayFixture fixture)
    {
        var cases = new Dictionary<string, AdmittedReplayRun>(StringComparer.Ordinal);
        foreach (var run in fixture.Runs) cases.TryAdd(run.Input.CaseId, run);
        foreach (var caseId in plan.Schedule)
            if (!cases.ContainsKey(caseId))
                throw new LivePlanRejected(LiveAdmissionCode.InvalidCorpus);
        return cases;
    }

    private static async Task<EvaluationOutcome> AttemptAsync(
        AdmittedReplayRun run, AgentSessionTrustedRequest trusted,
        AgentSessionMaterializedStableRequest stable, EvaluationRunInput descriptor,
        EvaluationAttempt attempt, string runId, bool execute,
        DeepSeekCredential? credential, LiveOptions options, LiveAccounting accounting,
        CancellationToken token)
    {
        var request = new AgentRunRequest(run.Input.ReviewedIdentity.Runtime, stable.StablePlan, runId,
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
        var snapshot = run.CreateSnapshot(Directory.GetCurrentDirectory());
        var inner = execute
            ? options.TransportFactory.Create(credential!)
            : options.DryRunTransport(run);
        using var metered = new LiveMeteredTransport(inner, accounting);
        var adapter = new DeepSeekAdapterContext(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            DeepSeekAdapterContext.Adapter, runId);
        var client = DeepSeekChatBackend.CreateClient(adapter, metered);
        var observed = new LiveChatObserver(client, accounting);
        var outcome = await new AgentLoop(observed, new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot)))
            .RunAsync(request, token);
        if (!outcome.Succeeded || outcome.Review is null || outcome.Diagnostic is not null)
            return EvaluationScorer.Failure(run.Expected, EvaluationFailure.FromAgentOutcome(outcome), attempt);
        var build = new AgentSessionBuildInput(request, outcome, trusted, request.InitialMessages.Length - 1,
            DeepSeekReasoningContinuationCodec.Instance, null, AgentSessionHeadTransition.SameHead);
        var subject = EvaluationSubject.Admit(build, descriptor);
        return subject is not null
            ? EvaluationScorer.Evaluate(run.Expected, subject)
            : EvaluationScorer.Failure(run.Expected, EvaluationFailure.Unknown, attempt);
    }

    private static string Code(LiveAdmissionCode code) => code switch
    {
        LiveAdmissionCode.InvalidPlan => "plan_invalid",
        LiveAdmissionCode.InvalidSource => "source_invalid",
        LiveAdmissionCode.InvalidCorpus => "corpus_invalid",
        LiveAdmissionCode.UnsupportedConfiguration => "configuration_unsupported",
        LiveAdmissionCode.Unpriceable => "spend_unpriceable",
        LiveAdmissionCode.SecretInvalid => "secret_invalid",
        LiveAdmissionCode.Cancelled => "cancelled",
        _ => "input_invalid",
    };
}
