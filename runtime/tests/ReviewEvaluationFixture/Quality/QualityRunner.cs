using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;

internal sealed record QualityCaseResult(string CaseId, string Category, string? PositiveCase,
    EvaluationCode ExpectedCode, EvaluationCode ActualCode, string? AgentDiagnostic,
    bool AuditPassed, bool ContextWithheld, bool Verified);
internal sealed record QualitySummary(string Mode, string Code, string? CorpusSha256,
    int ExpectedCases, int ExecutedCases, int VerifiedCases, ImmutableArray<QualityCaseResult> Cases);
internal sealed record QualityExecution(AdmittedReplayRun Input, QualityCaseSpec Spec, AgentRunRequest Request,
    AgentRunOutcome AgentOutcome, EvaluationSubject? Subject, EvaluationOutcome Outcome,
    bool FirstRequestSeen, bool ContextWithheld, int ConsumedTurns, bool ScriptExhausted);
internal sealed record QualityResult(QualitySummary Summary, ImmutableArray<QualityExecution> Executions)
{
    internal int ExitCode => Summary.Code == "verified" ? 0 : Summary.Code == "input_invalid" ? 2 : 1;
}

[JsonSerializable(typeof(QualitySummary))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UseStringEnumConverter = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    AllowDuplicateProperties = false, MaxDepth = 12)]
internal sealed partial class QualityJsonContext : JsonSerializerContext;

internal static class QualityRunner
{
    internal static byte[] Write(QualitySummary summary) => JsonSerializer.SerializeToUtf8Bytes(summary, QualityJsonContext.Default.QualitySummary);

    internal static async Task<QualityResult> RunAsync(string root, CancellationToken cancellationToken = default)
    {
        try
        {
            return await RunAsync(ReplayAdmission.Load(Path.Combine(root, "bundle"), cancellationToken), cancellationToken);
        }
        catch
        {
            return InfrastructureFailure();
        }
    }

    internal static async Task<QualityResult> RunAsync(ReplayAdmissionResult admission, CancellationToken cancellationToken = default)
    {
        try
        {
            if (admission.Code is ReplayAdmissionCode.IoFailure or ReplayAdmissionCode.Cancelled) return InfrastructureFailure();
            if (admission.Fixture is not { } fixture || !InventoryMatches(fixture)) return Invalid();
            var audits = new List<QualityAuditResult>();
            for (var index = 0; index < QualityCoverage.Cases.Length; index++)
            {
                var audit = await QualityAudit.ObserveAsync(fixture.Runs[index], QualityCoverage.Cases[index], cancellationToken);
                if (!QualityAudit.Matches(fixture.Runs[index], QualityCoverage.Cases[index], audit)) return Invalid(fixture.CorpusSha256);
                audits.Add(audit);
            }
            var executions = ImmutableArray.CreateBuilder<QualityExecution>();
            var rows = ImmutableArray.CreateBuilder<QualityCaseResult>();
            for (var index = 0; index < QualityCoverage.Cases.Length; index++)
            {
                var input = fixture.Runs[index];
                var spec = QualityCoverage.Cases[index];
                var execution = await ExecuteAsync(input, spec, cancellationToken);
                executions.Add(execution);
                var verified = Verify(execution, audits[index]);
                rows.Add(new(spec.Id, spec.Category, spec.PositiveCase, spec.ExpectedCode, execution.Outcome.Code,
                    SafeDiagnostic(execution.AgentOutcome.Diagnostic?.Code), true, execution.ContextWithheld, verified));
            }
            var complete = rows.Select(row => row.CaseId).SequenceEqual(QualityCoverage.Cases.Select(spec => spec.Id));
            var passed = complete && rows.All(row => row.Verified);
            return new(new("deterministic", passed ? "verified" : "assertion_failed", fixture.CorpusSha256,
                QualityCoverage.Cases.Length, executions.Count, rows.Count(row => row.Verified), rows.ToImmutable()), executions.ToImmutable());
        }
        catch
        {
            return InfrastructureFailure();
        }
    }

    private static QualityResult InfrastructureFailure() =>
        new(new("deterministic", "infrastructure_failed", null, QualityCoverage.Cases.Length, 0, 0, []), []);

    private static QualityResult Invalid(string? corpus = null) =>
        new(new("deterministic", "input_invalid", corpus, QualityCoverage.Cases.Length, 0, 0, []), []);

    private static bool InventoryMatches(AdmittedReplayFixture fixture)
    {
        if (fixture.Runs.Length != QualityCoverage.Cases.Length) return false;
        var first = fixture.Runs[0].Input;
        for (var index = 0; index < fixture.Runs.Length; index++)
        {
            var run = fixture.Runs[index];
            if (run.Input.Id != QualityCoverage.Cases[index].Id || run.Input.CaseId != QualityCoverage.Cases[index].Id ||
                run.Input.ReviewedIdentity != first.ReviewedIdentity || run.Input.ReviewedIdentity.RepositoryId != "242" ||
                !run.Input.Repository.SequenceEqual(first.Repository) || run.Input.Diff != first.Diff || run.Input.Policy != first.Policy ||
                run.Script.Turns.Any(turn => turn.ReasoningContent != string.Empty)) return false;
        }
        return true;
    }

    internal static async Task<QualityExecution> ExecuteAsync(AdmittedReplayRun input, QualityCaseSpec spec,
        CancellationToken cancellationToken = default)
    {
        var trusted = input.CreateTrustedRequest("r5-quality");
        if (!AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable))
            throw new InvalidOperationException("quality_request_invalid");
        var request = new AgentRunRequest(input.Input.ReviewedIdentity.Runtime, stable!.StablePlan, "q2-" + spec.Id,
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(input.InitialContext)])]);
        var descriptor = new EvaluationRunInput(spec.Id, "deterministic", EvaluationSource.Commit, EvaluationSource.Tree,
            EvaluationSource.Clean, input.ProviderConfigurationSha256);
        var attempt = EvaluationAttempt.Admit(trusted, descriptor) ?? throw new InvalidOperationException("quality_attempt_invalid");
        var snapshot = input.CreateSnapshot(Directory.GetCurrentDirectory());
        var chat = new QualityScriptClient(input.Script, spec.Facts);
        var outcome = await new AgentLoop(chat, new SnapshotToolExecutor(snapshot, input.CreateFileAccess(snapshot)))
            .RunAsync(request, cancellationToken);
        EvaluationSubject? subject = null;
        EvaluationOutcome scored;
        if (outcome.Succeeded)
        {
            var build = new AgentSessionBuildInput(request, outcome, trusted, request.InitialMessages.Length - 1,
                NoContinuationCodec.Instance, null, AgentSessionHeadTransition.SameHead);
            subject = EvaluationSubject.Admit(build, descriptor);
            scored = EvaluationScorer.Evaluate(input.Expected, subject);
        }
        else scored = EvaluationScorer.Failure(input.Expected, EvaluationFailure.FromAgentOutcome(outcome), attempt);
        return new(input, spec, request, outcome, subject, scored, chat.FirstRequestSeen, chat.ContextWithheld,
            chat.ConsumedTurns, chat.Exhausted);
    }

    private static bool Verify(QualityExecution execution, QualityAuditResult audit)
    {
        var spec = execution.Spec;
        var result = execution.Outcome;
        if (!execution.FirstRequestSeen || !execution.ContextWithheld || execution.ScriptExhausted ||
            execution.ConsumedTurns != execution.Input.Script.Turns.Length || result.Mode != "deterministic" ||
            result.ConfigurationSha256 != execution.Input.ConfigurationSha256 || result.Code != spec.ExpectedCode ||
            execution.AgentOutcome.Diagnostic?.Code != spec.AgentFailure) return false;
        if (spec.AgentFailure is not null)
            return !execution.AgentOutcome.Succeeded && execution.Subject is null &&
                // These events come from the real loop after tool-result admission, even without a completed SESSION.
                execution.AgentOutcome.Events.OfType<AgentToolResultEvent>()
                    .Select(tool => new RequiredObservation(tool.Name, tool.ObservationId)).SequenceEqual(audit.Observations) &&
                execution.AgentOutcome.Diagnostic!.ModelCalls == execution.ConsumedTurns &&
                execution.AgentOutcome.Diagnostic.ToolCalls == spec.Operations.Length + 1 &&
                result.ExecutionStatus == EvaluationStatus.Failed && result.FailureSource == EvaluationFailureSource.Agent &&
                result.FailureKind == EvaluationFailureKind.MalformedOutput && result.EvidenceStatus == AssertionStatus.NotEvaluated &&
                result.ScenarioStatus == AssertionStatus.NotEvaluated && result.ModelStatus == ModelObservationStatus.NotEvaluated;
        if (!execution.AgentOutcome.Succeeded || execution.Subject is not { } subject ||
            result.ExecutionStatus != EvaluationStatus.Completed || result.FailureSource != EvaluationFailureSource.None ||
            result.FailureKind != EvaluationFailureKind.None) return false;
        if (spec.ExpectedCode is EvaluationCode.RequiredToolMissing or EvaluationCode.RequiredObservationMissing)
            return result.EvidenceStatus == AssertionStatus.Failed && result.ScenarioStatus == AssertionStatus.NotEvaluated &&
                result.ModelStatus == ModelObservationStatus.NotEvaluated && result.FindingCount == 0 &&
                result.ToolObservationCount == (spec.Id == "no-required-tool" ? 0 : spec.Operations.Length);
        if (result.EvidenceStatus != AssertionStatus.Passed ||
            !audit.Observations.All(subject.Observations.Contains) ||
            result.AdjudicatedTrue != 0 || result.AdjudicatedFalse != 0 ||
            result.UnadjudicatedFindings != result.FindingCount) return false;
        var expectedCount = spec.ExpectedCode == EvaluationCode.DuplicateObservation ? 2 : spec.Safe && spec.ExpectedCode == EvaluationCode.Scored ? 0 : 1;
        if (result.FindingCount != expectedCount || result.ToolObservationCount != spec.Operations.Length) return false;
        var scenario = spec.ExpectedCode switch
        {
            EvaluationCode.Scored => result.ScenarioStatus == AssertionStatus.Passed && result.StructurallyMissingDefects == 0 &&
                result.DuplicateObservations == 0 && result.ProhibitedObservations == 0 && result.StructuralMatches == (spec.Safe ? 0 : 1),
            EvaluationCode.ExpectedFindingMissing => result.ScenarioStatus == AssertionStatus.Failed && result.StructuralMatches == 0 && result.StructurallyMissingDefects == 1,
            EvaluationCode.ProhibitedFinding => result.ScenarioStatus == AssertionStatus.Failed && result.ProhibitedObservations == 1,
            EvaluationCode.DuplicateObservation => result.ScenarioStatus == AssertionStatus.Failed && result.DuplicateObservations == 1 && result.StructuralMatches == 1,
            _ => false,
        };
        return scenario && (!spec.Sticky || HasNoInlineLocation(execution));
    }

    internal static bool HasNoInlineLocation(QualityExecution execution)
    {
        var snapshot = execution.Input.CreateSnapshot(Directory.GetCurrentDirectory());
        var identity = snapshot.Identity;
        var diffHash = AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(string.Join(',', snapshot.DiffByChangedPath.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Value.PatchSha256))));
        // Pure location projection, not production Host snapshot or publication authority.
        var coordinatesIdentity = new ReviewedSnapshotIdentities(242, identity.ReviewTarget, identity.BaseSha, identity.HeadSha,
            diffHash, diffHash, diffHash, diffHash);
        return InlineDiffCoordinates.TryCreate(snapshot, coordinatesIdentity, out var coordinates) &&
            execution.Subject is { Findings.Length: > 0 } subject &&
            subject.Findings.All(finding => finding.Evidence.All(evidence =>
                !coordinates!.TryFindFirst(evidence.Path, evidence.StartLine, evidence.EndLine, out _)));
    }

    private static string? SafeDiagnostic(string? value) => value switch
    {
        null => null,
        AgentFailureCodes.TerminalInvalid => AgentFailureCodes.TerminalInvalid,
        _ => "unexpected_agent_failure",
    };

    private sealed class NoContinuationCodec : IAgentContinuationCodec
    {
        internal static readonly NoContinuationCodec Instance = new();
        public string CodecId => "r5-quality";
        public string CodecDiscriminator => "bootstrap";
        public bool TryEncode(AgentContinuationCodecValue value, out AgentContinuationEncodedPayload? payload) { payload = null; return false; }
        public bool TryDecode(string encoding, ReadOnlySpan<byte> payload, out AgentContinuationCodecValue? value) { value = null; return false; }
    }
}

internal sealed class QualityScriptClient(ReplayScript script, ImmutableArray<QualityFact> facts) : IProjectChatClient
{
    internal int ConsumedTurns { get; private set; }
    internal bool Exhausted { get; private set; }
    internal bool FirstRequestSeen { get; private set; }
    internal bool ContextWithheld { get; private set; }

    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!FirstRequestSeen)
        {
            FirstRequestSeen = true;
            var contents = request.Messages.SelectMany(message => message.Contents).ToArray();
            ContextWithheld = request.Continuation is null && contents.All(content => content is ProjectTextContent) &&
                facts.All(fact => contents.Cast<ProjectTextContent>().All(content => !content.Text.Contains(fact.Text.Trim(), StringComparison.Ordinal)));
        }
        if (ConsumedTurns >= script.Turns.Length)
        {
            Exhausted = true;
            throw new InvalidOperationException("quality_script_exhausted");
        }
        var turn = script.Turns[ConsumedTurns++];
        if (turn.ReasoningContent != string.Empty) throw new InvalidOperationException("quality_reasoning_unsupported");
        var contentsOut = turn.ToolCalls.Select(call => (ProjectChatContent)new ProjectToolCallContent(call.Id, call.Name, call.ArgumentsJson)).ToArray();
        var captured = checked(64 + turn.ToolCalls.Sum(call => Encoding.UTF8.GetByteCount(call.ArgumentsJson) + Encoding.UTF8.GetByteCount(call.Name) + 64));
        return Task.FromResult(new ProjectChatResponse(new("assistant", contentsOut), new(1, 1), captured));
    }
}
