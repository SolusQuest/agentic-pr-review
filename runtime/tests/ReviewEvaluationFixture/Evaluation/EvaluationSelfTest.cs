using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

internal sealed record SyntheticEvaluation(EvaluationCase Case, EvaluationRunInput Run, AgentSessionBuildInput Input,
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

    internal static async Task<SyntheticEvaluation> CreateAsync(
        int findingCount = 1, bool includeTool = true, string prose = "Synthetic candidate", string mode = "deterministic",
        bool ungrounded = false, string? providerFailure = null, string? toolFailure = null,
        Func<string, ImmutableArray<AgentFinding>>? findingFactory = null, string buildId = "r5-synthetic",
        string reviewContext = "Inspect the synthetic input.", string policy = "Review synthetic inputs using bounded tools.",
        string model = "synthetic-model", string provider = "synthetic-provider", string adapter = "synthetic-adapter",
        EvaluationReviewedIdentity? reviewedIdentity = null)
    {
        var identity = reviewedIdentity ?? Identity;
        var execution = ReadExecution(identity);
        var observation = execution.Observation!.ObservationId;
        var defect = new ExpectedDefect("defect-a", "high", observation, SourcePath, 1, 1);
        var spec = new EvaluationCaseInput("synthetic-scorer", Hash("authored-r5-scorer-corpus"), identity,
            [defect], [new(AgentToolRegistry.ReadFileName, observation)], []);
        var testCase = EvaluationCase.Admit(spec)!;
        var trusted = new AgentSessionTrustedRequest(identity.RepositoryId, identity.ReviewTarget,
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
        var responses = new Queue<ProjectChatResponse>();
        if (includeTool) responses.Enqueue(Response(new("read0", AgentToolRegistry.ReadFileName,
            "{\"path\":\"src/Synthetic.cs\",\"start_line\":1,\"line_count\":2}")));
        responses.Enqueue(Response(new("finish0", AgentToolRegistry.FinishReviewName,
            Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview("Synthetic review. " + Canary, findings)))));
        var outcome = await new AgentLoop(new SyntheticChat(responses, providerFailure),
            new SyntheticTools(execution, toolFailure)).RunAsync(run, CancellationToken.None);
        var input = new AgentSessionBuildInput(run, outcome, trusted, run.InitialMessages.Length - 1,
            SyntheticCodec.Instance, null, AgentSessionHeadTransition.SameHead);
        return new(testCase, descriptor, input, attempt);
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
            EvaluationFailure.FromSessionBuild(AgentSessionBuilder.Build(valid.Input with { CurrentReviewContextIndex = -1 })),
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

    private static AgentToolExecution ReadExecution(EvaluationReviewedIdentity identity)
    {
        var value = new ReadFileResult("ok", identity.Runtime, SourcePath, Hash(Canary), 1, 2, 1, 2,
            [new(1, "return value!.Trim(); // " + Canary), new(2, "return other!.Trim();")], false, null, null);
        var observation = AgentCanonical.HashDomain(AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(value, includeObservationId: false));
        var bytes = ReadFileResultWriter.Write(value with { ObservationId = observation });
        return new(true, null, Encoding.UTF8.GetString(bytes), bytes, new(observation, identity.Runtime,
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(SourcePath, [1, 2])));
    }

    private static ProjectChatResponse Response(ProjectToolCallContent content) =>
        new(new("assistant", [content]), new(1, 1), CapturedResponseBodyBytes: 1);

    private sealed class SyntheticChat(Queue<ProjectChatResponse> responses, string? failure) : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken cancellationToken) =>
            failure is not null ? throw new HttpRequestException(failure) : Task.FromResult(responses.Dequeue());
    }
    private sealed class SyntheticTools(AgentToolExecution execution, string? failure) : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;
        public ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken cancellationToken) =>
            ValueTask.FromResult(failure is null ? execution : AgentToolExecution.Failure(failure));
    }
    private sealed class SyntheticCodec : IAgentContinuationCodec
    {
        internal static readonly SyntheticCodec Instance = new();
        public string CodecId => "r5-synthetic";
        public string CodecDiscriminator => "current";
        public bool TryEncode(AgentContinuationCodecValue value, out AgentContinuationEncodedPayload? payload)
        { payload = null; return false; }
        public bool TryDecode(string encoding, ReadOnlySpan<byte> payload, out AgentContinuationCodecValue? value)
        { value = null; return false; }
    }
}
