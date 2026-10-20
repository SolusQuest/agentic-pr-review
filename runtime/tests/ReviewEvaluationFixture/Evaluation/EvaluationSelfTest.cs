using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

internal sealed record SyntheticEvaluation(EvaluationCase Case, EvaluationRunInput Run, FreshQualityRun Input,
    EvaluationAttempt Attempt)
{
    internal EvaluationSubject? Admit() => EvaluationSubject.Admit(Input, Run);
    public override string ToString() => "synthetic_evaluation";
}
// Authored in-memory scorer vectors, not a repository snapshot/replay loader or Q2 corpus.
internal static class EvaluationSelfTest
{
    internal const string Canary = "APR241_PRIVATE_CONTENT_CANARY";
    internal const string SourcePath = "src/Synthetic.cs";
    internal static readonly EvaluationReviewedIdentity Identity = new("synthetic/r5", 241,
        new string('1', 40), new string('2', 40));

    internal static Task<SyntheticEvaluation> CreateAsync(
        int findingCount = 1, bool includeTool = true, string prose = "Synthetic candidate", string mode = "deterministic",
        bool ungrounded = false, string? providerFailure = null, string? toolFailure = null,
        Func<string, ImmutableArray<AgentFinding>>? findingFactory = null, string buildId = "r5-synthetic",
        string reviewContext = "Inspect the synthetic input.", string policy = "Review synthetic inputs using bounded tools.",
        string model = "synthetic-model", string provider = "synthetic-provider", string adapter = "synthetic-adapter",
        EvaluationReviewedIdentity? reviewedIdentity = null, int readLineCount = 2,
        bool reasoning = false, bool associateReasoning = false, bool recovery = false)
    {
        var identity = reviewedIdentity ?? Identity;
        var execution = ReadExecution(identity, readLineCount);
        var observation = execution.Observation!.ObservationId;
        var defect = new ExpectedDefect("defect-a", "high", observation, SourcePath, 1, 1);
        var spec = new EvaluationCaseInput("synthetic-scorer", Hash("authored-r5-scorer-corpus"), identity,
            [defect], [new(AgentToolRegistry.ReadFileName, observation)], []);
        var testCase = EvaluationCase.Admit(spec)!;
        var trusted = new AgentTrustedRequest(identity.RepositoryId, identity.ReviewTarget,
            "r5@synthetic", Encoding.UTF8.GetBytes(policy), buildId, provider, model, adapter);
        if (!AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable))
            throw new InvalidOperationException("r5_self_test_setup_failed");
        var run = new AgentRunRequest(identity.Runtime, stable!.StablePlan, "synthetic-session",
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(reviewContext)])]);
        var descriptor = new EvaluationRunInput("self-test-run", mode, EvaluationSource.Commit,
            EvaluationSource.Tree, EvaluationSource.Clean, Hash("synthetic-provider-settings"));
        var attempt = EvaluationAttempt.Admit(trusted, descriptor)!;
        var findings = findingFactory?.Invoke(observation) ?? Enumerable.Range(0, findingCount).Select(i => new AgentFinding("high",
            prose + " " + i, Canary + " " + prose,
            [new AgentEvidence(ungrounded ? new string('f', 64) : observation, SourcePath, 1, 1)])).ToImmutableArray();
        var outcome = AuthoredOutcome(run, execution, findings, includeTool, recovery, reasoning,
            associateReasoning, providerFailure, toolFailure, readLineCount);
        var input = new FreshQualityRun(run, outcome, trusted, run.InitialMessages.Length - 1);
        return Task.FromResult(new SyntheticEvaluation(testCase, descriptor, input, attempt));
    }

    internal static EvaluationAdjudication Annotation(EvaluationCase testCase, EvaluationSubject subject,
        params FindingAdjudication[] findings) => new(testCase.Input.CorpusSha256, testCase.Sha256,
            subject.ConfigurationSha256, subject.ExecutionSha256, findings.ToImmutableArray());

    internal static async Task<(bool Passed, ImmutableArray<EvaluationOutcome> Outcomes)> RunAsync()
    {
        var results = ImmutableArray.CreateBuilder<EvaluationOutcome>();
        var passed = true;
        void Check(EvaluationOutcome outcome, EvaluationCode expected)
        {
            results.Add(outcome);
            var bytes = EvaluationJson.Write(outcome);
            passed &= outcome.Code == expected && EvaluationJson.ReadOutcome(bytes) == outcome &&
                !Encoding.UTF8.GetString(bytes).Contains(Canary, StringComparison.Ordinal);
        }
        var valid = await CreateAsync();
        var subject = valid.Admit();
        if (subject is null) return (false, []);
        passed &= EvaluationJson.ReadCase(EvaluationJson.Write(valid.Case.Input))?.Sha256 == valid.Case.Sha256 &&
            EvaluationJson.ReadRun(EvaluationJson.Write(valid.Run)) == valid.Run &&
            EvaluationJson.ReadAdjudication(EvaluationJson.Write(Annotation(valid.Case, subject,
                new FindingAdjudication(0, "confirmed", "defect-a")))) is { Findings.Length: 1 };
        Check(EvaluationScorer.Evaluate(valid.Case, subject, Annotation(valid.Case, subject, new FindingAdjudication(0, "confirmed", "defect-a"))), EvaluationCode.Scored);
        var bad = await CreateAsync(ungrounded: true);
        Check(EvaluationScorer.Failure(bad.Case, EvaluationFailure.FromAgentOutcome(bad.Input.Outcome),
            bad.Attempt, EvaluationCode.SubjectInvalid), EvaluationCode.SubjectInvalid);
        var wrongSnapshot = EvaluationCase.Admit(valid.Case.Input with
        { ReviewedIdentity = Identity with { HeadSha = new string('3', 40) } })!;
        Check(EvaluationScorer.Evaluate(wrongSnapshot, subject), EvaluationCode.WrongSnapshot);
        var wrongObservation = EvaluationCase.Admit(valid.Case.Input with
        { RequiredObservations = [new(AgentToolRegistry.ReadFileName, new string('e', 64))] })!;
        Check(EvaluationScorer.Evaluate(wrongObservation, subject), EvaluationCode.RequiredObservationMissing);
        var duplicateCredit = EvaluationCase.Admit(valid.Case.Input with
        { Defects = [valid.Case.Input.Defects[0], valid.Case.Input.Defects[0] with { Id = "defect-b" }] })!;
        Check(EvaluationScorer.Evaluate(duplicateCredit, subject), EvaluationCode.ExpectedFindingMissing);
        var prohibited = EvaluationCase.Admit(valid.Case.Input with { ProhibitedFindings = [new(SourcePath, 1, 1)] })!;
        Check(EvaluationScorer.Evaluate(prohibited, subject), EvaluationCode.ProhibitedFinding);
        var noTool = await CreateAsync(findingCount: 0, includeTool: false);
        Check(EvaluationScorer.Evaluate(noTool.Case, noTool.Admit()), EvaluationCode.RequiredToolMissing);
        var duplicates = await CreateAsync(findingCount: 2);
        Check(EvaluationScorer.Evaluate(duplicates.Case, duplicates.Admit()), EvaluationCode.DuplicateObservation);
        foreach (var (testCase, completed) in new[] { (duplicateCredit, subject), (prohibited, subject), (duplicates.Case, duplicates.Admit()!) })
        {
            var baseline = EvaluationScorer.Evaluate(testCase, completed);
            var stale = Annotation(testCase, completed) with { ExecutionSha256 = new string('0', 64) };
            var rejected = EvaluationScorer.Evaluate(testCase, completed, stale);
            Check(rejected, EvaluationCode.AdjudicationInvalid);
            passed &= rejected.ScenarioStatus == AssertionStatus.Failed && rejected.StructuralMatches == baseline.StructuralMatches &&
                rejected.StructurallyMissingDefects == baseline.StructurallyMissingDefects &&
                rejected.DuplicateObservations == baseline.DuplicateObservations && rejected.ProhibitedObservations == baseline.ProhibitedObservations;
        }
        var changed = await CreateAsync(prose: "Changed prose", mode: "live");
        var changedSubject = changed.Admit()!;
        var unadjudicated = EvaluationScorer.Evaluate(changed.Case, changedSubject);
        Check(unadjudicated, EvaluationCode.Scored);
        passed &= unadjudicated.ModelStatus == ModelObservationStatus.Unadjudicated && unadjudicated.AdjudicatedTrue == 0;
        Check(EvaluationScorer.Evaluate(changed.Case, changedSubject, Annotation(valid.Case, subject, new FindingAdjudication(0, "confirmed", "defect-a"))), EvaluationCode.AdjudicationInvalid);
        foreach (var failure in new[]
        {
            EvaluationFailure.FromProviderTransport(new HttpRequestException(Canary)),
            EvaluationFailure.FromAgentOutcome((await CreateAsync(ungrounded: true)).Input.Outcome),
            EvaluationFailure.FromToolExecution(AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed)),
            CurrentStateFailure(),
            EvaluationFailure.Invalid,
            EvaluationFailure.Unknown,
        })
        {
            var result = EvaluationScorer.Failure(valid.Case, failure, valid.Attempt);
            Check(result, EvaluationCode.ExecutionFailed);
            passed &= result.ModelStatus == ModelObservationStatus.NotEvaluated;
        }
        return (passed && results.Count == 19, results.ToImmutable());
    }

    internal static string Hash(string value) => AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(value));

    private static AgentToolExecution ReadExecution(EvaluationReviewedIdentity identity, int readLineCount)
    {
        var value = new ReadFileResult("ok", identity.Runtime, SourcePath, Hash(Canary), 1, readLineCount, 1, 2,
            [new(1, "return value!.Trim(); // " + Canary), new(2, "return other!.Trim();")], false, null, null);
        var observation = AgentCanonical.HashDomain(AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(value, includeObservationId: false));
        var bytes = ReadFileResultWriter.Write(value with { ObservationId = observation });
        return new(true, null, Encoding.UTF8.GetString(bytes), bytes, new(observation, identity.Runtime,
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(SourcePath, [1, 2])));
    }

    // Explicit data vectors for the scorer's readonly native-format reader.
    // This is not an execution engine, context serializer or provider adapter.
    private static AgentRunOutcome AuthoredOutcome(AgentRunRequest run, AgentToolExecution execution,
        ImmutableArray<AgentFinding> findings, bool includeTool, bool recovery, bool reasoning,
        bool associated, string? providerFailure, string? toolFailure, int readLineCount)
    {
        var events = ImmutableArray.CreateBuilder<AgentLogicalEvent>();
        events.Add(new AgentPlanEvent(AgentCanonical.StablePlanSha256(run.StablePlan)));
        for (var index = 0; index < run.InitialMessages.Length; index++)
            events.Add(new AgentMessageEvent(index, run.InitialMessages[index].Role,
                [new AgentTextPart(((ProjectTextContent)run.InitialMessages[index].Contents[0]).Text)]));
        var nextMessage = run.InitialMessages.Length;
        var items = ImmutableArray.CreateBuilder<AgentContinuationCandidateItem>();
        var observations = new List<AgentObservation>();
        var authoredCalls = 0;
        void Assistant(string firstId, ImmutableArray<AgentMessagePart> parts)
        {
            var position = nextMessage++;
            AgentContinuationCandidateItem? item = reasoning
                ? new("fresh reasoning " + position, "opaque-" + position, "synthetic-reasoning.v1",
                    associated ? firstId : null, position, 0) : null;
            if (item is not null)
            {
                items.Add(item);
                parts = [new AgentReasoningReferencePart(Hash(item.Readable), Hash(item.Opaque), Hash(item.Framing),
                    item.AssociatedCallId, position, 0), .. parts];
            }
            events.Add(new AgentMessageEvent(position, "assistant", parts));
            if (item is not null) events.Add(new AgentContinuationEvent(Hash(item.Readable), Hash(item.Opaque),
                Hash(item.Framing), item.AssociatedCallId, position, 0));
            authoredCalls++;
        }
        AgentRunOutcome Failed(string code, int toolCalls)
        {
            events.Add(new AgentFailureEvent(code));
            return AgentRunOutcome.Failure(code, Math.Max(1, authoredCalls), toolCalls, events.ToImmutable());
        }
        if (providerFailure is not null) return Failed(AgentFailureCodes.ChatFailed, 0);
        if (!AgentToolArguments.TryReadFileProvider("{\"path\":\"src/Synthetic.cs\",\"start_line\":1,\"line_count\":" + readLineCount + "}", out var read))
            throw new InvalidOperationException("authored_scorer_arguments_invalid");
        if (recovery)
        {
            var rejected = Encoding.UTF8.GetBytes(AgentRecoveryFeedback.RejectedArguments);
            Assistant("rejected0", [new AgentRecoveryToolCallReferencePart("rejected0", AgentToolRegistry.ReadFileName, Hash(AgentRecoveryFeedback.RejectedArguments), true),
                new AgentRecoveryToolCallReferencePart("skipped0", AgentToolRegistry.ReadFileName, AgentCanonical.HashRaw(read!.CanonicalBytes), false)]);
            foreach (var (id, bytes, rejectedMember, feedback) in new[] {
                ("rejected0", rejected, true, AgentRecoveryFeedback.ArgumentsInvalid),
                ("skipped0", read!.CanonicalBytes, false, AgentRecoveryFeedback.BatchNotExecuted) })
            {
                var error = Encoding.UTF8.GetBytes(feedback);
                events.Add(new AgentRecoveryToolCallEvent(id, AgentToolRegistry.ReadFileName,
                    AgentCanonical.HashRaw(bytes), [.. bytes], rejectedMember));
                events.Add(new AgentToolErrorEvent(id, AgentToolRegistry.ReadFileName,
                    AgentCanonical.HashRaw(error), [.. error]));
                nextMessage++;
            }
        }
        if (includeTool)
        {
            var call = new PreparedReadFileCall("read0", read!);
            var hash = AgentCanonical.HashRaw(call.CanonicalArguments);
            Assistant(call.CallId, [new AgentToolCallReferencePart(call.CallId, call.Name, hash)]);
            events.Add(new AgentToolCallEvent(call.CallId, call.Name, hash, [.. call.CanonicalArguments]));
            if (toolFailure is not null) return Failed(toolFailure, 1);
            if (!AgentToolResultAdmission.TryAdmit(call, run.ReviewedIdentity, execution, out _, out var observation))
                return Failed(AgentFailureCodes.ResponseInvalid, 1);
            observations.Add(observation);
            events.Add(new AgentToolResultEvent(call.CallId, call.Name, observation.ObservationId,
                AgentCanonical.HashRaw(execution.CanonicalResult!), [.. execution.CanonicalResult!]));
            nextMessage++;
        }
        var terminal = AgentToolArguments.WriteFinishReview("Synthetic review. " + Canary, findings);
        var terminalHash = AgentCanonical.HashDomain(AgentCanonical.TerminalDomain, terminal);
        Assistant("finish0", [new AgentToolCallReferencePart("finish0", AgentToolRegistry.FinishReviewName, terminalHash)]);
        events.Add(new AgentToolCallEvent("finish0", AgentToolRegistry.FinishReviewName, terminalHash, [.. terminal]));
        if (!AgentToolArguments.TryFinishReviewProvider(Encoding.UTF8.GetString(terminal), out var finish) ||
            !TerminalReviewValidator.TryValidate(finish!, run.ReviewedIdentity, observations, out var review))
            return Failed(AgentFailureCodes.TerminalInvalid, (includeTool ? 1 : 0) + 1);
        events.Add(new AgentTerminalEvent(review!.TerminalSha256));
        var continuation = items.Count == 0 ? null : new AgentContinuationCandidate(run.StablePlan.ProviderId,
            run.StablePlan.ModelId, run.StablePlan.AdapterId, run.SessionId, items.ToImmutable());
        return AgentRunOutcome.Success(review, run.ReviewedIdentity, events.ToImmutable(), continuation);
    }

    internal static EvaluationFailure CurrentStateFailure()
    {
        var expected = new SolusRestrictedStateCompatibility(SolusRestrictedStateFormat.PackageVersion,
            SolusRestrictedStateFormat.Profile, SolusRestrictedStateFormat.CompletedCandidateMode,
            SolusRestrictedStateFormat.Implementation, SolusRestrictedStateFormat.ContextFormat,
            SolusRestrictedStateFormat.ContextCompatibility, SolusRestrictedStateFormat.PackageClosureSha256,
            new string('a', 64), new string('b', 64));
        if (SolusRestrictedStateSnapshotCodec.TryRead([0], expected, out _, out var failure))
            throw new InvalidOperationException("invalid_state_was_admitted");
        return EvaluationFailure.FromSolusAdmission(failure);
    }
}
