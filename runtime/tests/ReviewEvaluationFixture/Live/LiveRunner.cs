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
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Accounting;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
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
    byte[] ReportJson,
    UsageJournal Journal);

internal sealed class LiveOptions
{
    internal Func<string, CancellationToken, ReplayAdmissionResult> AdmitBundle { get; init; } =
        ReplayAdmission.Load;
    internal ILiveSecretSource SecretSource { get; init; } = new LiveEnvironmentSecretSource();
    internal ILiveTransportFactory TransportFactory { get; init; } = LiveDeepSeekTransportFactory.Instance;
    internal Func<AdmittedReplayRun, IDeepSeekTransport> DryRunTransport { get; init; } =
        run => new ReplayTransport(run.Script, ReplayFault.None);
    internal Action<string>? WriteLine { get; init; }
    internal LiveAdjudicator? Adjudicator { get; init; }
}

// Serial bounded scheduler over the admitted plan. Each expanded evaluation is
// attempted at most once; reservations, outcomes and stops are recorded at the
// transport/chat boundaries and reconciled into the public-safe report.
internal static class LiveRunner
{
    internal const string BuildId = "r5-live-local";

    internal static async Task<int> InvokeAsync(string planPath, bool execute, bool adjudicate = false)
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
            var result = await RunAsync(planPath, execute, adjudicate
                ? new LiveOptions { Adjudicator = new LiveAdjudicator(Console.In, Console.Error) } : null,
                commandCancel.Token);
            return result.StopReason == "complete" &&
                result.Summary.AdjudicationStatus is "not_requested" or "adjudicated" ? 0 : 1;
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
        if (!LivePlanAdmission.TryProviderProfile(plan.Provider, out var profile))
            throw new InvalidOperationException("live_profile_invalid");
        var limitAuthority = DeepSeekAdapterContext.LimitAuthorityFor(profile);
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
        var expectation = new UsageJournalExpectation(new(nonce, EvaluationSource.Commit, EvaluationSource.Tree,
            EvaluationSource.Clean, BuildId, fixture.CorpusSha256, plan.Provider.ConfigurationSha256,
            plan.Digest, execute ? "live" : "loopback"),
            new(LiveLimits.PlanFormat, new(EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean),
                fixture.CorpusSha256, plan.Provider, plan.Schedule, plan.Bounds));
        var journal = new UsageJournalCollector(expectation);
        var rows = ImmutableArray.CreateBuilder<ReadOnlyMemory<byte>>();
        var outcomes = ImmutableArray.CreateBuilder<EvaluationOutcome>();
        var subjects = new List<LiveAdjudicationCase>();
        var diagnostics = new List<LiveAgentDiagnostic>();
        var completed = 0;
        var failed = 0;
        var invalid = 0;
        var stopReason = "complete";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(plan.Bounds.MaxSeconds));

        LiveAccountingSnapshot frozenAccounting;
        UsageJournal frozenJournal;
        var schedulingEnded = false;
        try
        {
            for (var index = 0; index < plan.Schedule.Length; index++)
            {
                var scope = journal.BeginAttempt(index);
                var caseId = plan.Schedule[index];
                var run = cases[caseId];
                var runId = nonce + "-" + (index + 1);
                var trusted = run.CreateTrustedRequest(BuildId) with
                {
                    ProviderId = DeepSeekAdapterContext.Provider,
                    ModelId = DeepSeekAdapterContext.Model,
                    AdapterId = plan.Provider.AdapterId,
                    LimitAuthority = limitAuthority,
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
                            execute, credential, options, accounting, scope, subjects, diagnostics, index, deadline.Token);
                        switch (outcome.ExecutionStatus)
                        {
                            case EvaluationStatus.Completed: completed++; break;
                            case EvaluationStatus.Invalid: invalid++; break;
                            default: failed++; break;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        diagnostics.Add(LiveAgentDiagnostic.Capture(index, null));
                        stopReason = token.IsCancellationRequested ? "caller_cancelled" : "deadline";
                        outcome = EvaluationScorer.Failure(run.Expected, EvaluationFailure.Unknown, attempt);
                        failed++;
                    }
                }
                scope.AdmitEvaluation(outcome.AttemptSha256);
                scope.Finish(outcome.ExecutionStatus switch
                {
                    EvaluationStatus.Completed => "completed",
                    EvaluationStatus.Invalid => "invalid",
                    _ => "failed",
                }, deadline.IsCancellationRequested);
                rows.Add(EvaluationJson.Write(outcome));
                outcomes.Add(outcome);
                if (stopReason != "complete") break;
                if (accounting.AccountingViolation) { stopReason = "accounting_violation"; break; }
                if (accounting.BudgetRefused) { stopReason = "bound_stop"; break; }
                if (accounting.RateLimited) { stopReason = "rate_limited"; break; }
                if (deadline.IsCancellationRequested)
                {
                    stopReason = token.IsCancellationRequested ? "caller_cancelled" : "deadline";
                    break;
                }
            }
            schedulingEnded = true;
        }
        finally
        {
            frozenAccounting = accounting.Seal();
            frozenJournal = journal.Seal(schedulingEnded ? stopReason : "infrastructure_failed",
                new(frozenAccounting.Sends, frozenAccounting.ReservedInputTokens, frozenAccounting.ReservedOutputTokens,
                    frozenAccounting.ReservedCombinedTokens, frozenAccounting.ReservedSpendMicroUsd));
        }
        // Exercise both generated writing and strict admission on the maintained
        // framework/AOT path. Public output is only the admitted frozen value.
        frozenJournal = UsageJournalJson.Read(UsageJournalJson.Write(frozenJournal)) ??
            throw new InvalidOperationException("usage_journal_roundtrip_invalid");
        if (!frozenJournal.Matches(expectation)) throw new InvalidOperationException("usage_journal_selection_mismatch");

        // Provider execution has ended. Keep only admitted subjects, never SESSION
        // or provider bytes, while a maintainer reviews the private projections.
        credential = null;
        var adjudication = options.Adjudicator is { } reviewer
            ? await reviewer.ReviewAsync(subjects, outcomes, token)
            : new LiveAdjudicationStatus("not_requested", "none", 0);
        rows.Clear();
        foreach (var outcome in outcomes) rows.Add(EvaluationJson.Write(outcome));
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
            execute ? 0 : (int)frozenAccounting.Sends, execute ? (int)frozenAccounting.Sends : 0,
            frozenAccounting.KnownInputTokens, frozenAccounting.KnownOutputTokens, frozenAccounting.KnownCombinedTokens,
            frozenAccounting.ReservedInputTokens, frozenAccounting.ReservedOutputTokens, frozenAccounting.ReservedCombinedTokens,
            frozenAccounting.UsageUnknownCalls, frozenAccounting.AccountingViolation, frozenAccounting.Outcomes,
            frozenAccounting.ReservedSpendMicroUsd, plan.Bounds.SpendCeilingMicroUsd, stopReason, adjudication.Cleanup,
            diagnostics.ToImmutableArray(),
            adjudication.Status, adjudication.ConfirmedCases, adjudication.AiCases,
            frozenAccounting.CacheUsage, frozenJournal.Document);
        foreach (var row in rows) write(Encoding.UTF8.GetString(row.Span));
        write(Encoding.UTF8.GetString(reportBytes.Value));
        write(JsonSerializer.Serialize(summary, LiveJsonContext.Default.LiveRunSummary));
        return new(stopReason, attempted, completed, failed, invalid, outcomes.ToImmutable(),
            summary, reportBytes.Value.ToArray(), frozenJournal);
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
        UsageJournalCollector.AttemptScope journalAttempt,
        List<LiveAdjudicationCase> subjects, List<LiveAgentDiagnostic> diagnostics, int index,
        CancellationToken token)
    {
        var request = new AgentRunRequest(run.Input.ReviewedIdentity.Runtime, stable.StablePlan, runId,
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
        var snapshot = run.CreateSnapshot(Directory.GetCurrentDirectory());
        var inner = execute
            ? options.TransportFactory.Create(credential!)
            : options.DryRunTransport(run);
        using var metered = new LiveMeteredTransport(inner, accounting, journalAttempt);
        var adapter = new DeepSeekAdapterContext(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
            trusted.AdapterId, runId);
        var client = DeepSeekChatBackend.CreateClient(adapter, metered);
        var executor = new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot));
        var observed = new LiveChatObserver(client, accounting, journalAttempt,
            response => LiveToolRejectionProjector.Project(response, executor));
        journalAttempt.AgentStarted();
        var outcome = await new AgentLoop(observed, executor, limitAuthority: trusted.LimitAuthority)
            .RunAsync(request, token);
        journalAttempt.AgentFinished(outcome.Succeeded);
        var rejection = observed.TakeRejection();
        var normalizationReason = observed.TakeNormalizationReason();
        if (!outcome.Succeeded || outcome.Review is null || outcome.Diagnostic is not null)
        {
            diagnostics.Add(LiveAgentDiagnostic.Capture(index, outcome.Diagnostic,
                rejection ?? LiveToolRejectionProjection.Unknown(outcome.Diagnostic?.Code ?? "unknown"),
                normalizationReason));
            return EvaluationScorer.Failure(run.Expected, EvaluationFailure.FromAgentOutcome(outcome), attempt);
        }
        var build = new AgentSessionBuildInput(request, outcome, trusted, request.InitialMessages.Length - 1,
            DeepSeekReasoningContinuationCodec.Instance, null, AgentSessionHeadTransition.SameHead);
        var subject = EvaluationSubject.Admit(build, descriptor);
        // A failed post-Agent admission has no typed Agent rejection. Preserve
        // its schedule position without inventing a reason or call counts.
        if (subject is null) diagnostics.Add(LiveAgentDiagnostic.Capture(index, null));
        if (subject is not null && options.Adjudicator is not null)
            subjects.Add(new(index, run, subject));
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
        _ => "input_invalid",
    };
}
