using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Agent.Quality;

internal enum R3QualitySubjectKind
{
    Completed,
    ProductFailure,
    ProviderFailure,
    ToolFailure,
    StateFailure,
    EvaluatorFailure,
}

internal sealed record R3QualityToolObservation(
    string CallId,
    string Name,
    ImmutableArray<byte> CanonicalArguments,
    ImmutableArray<byte> CanonicalResult,
    AgentObservation Observation)
{
    public override string ToString() => "r3_quality_tool_observation";
}

internal sealed class R3QualitySubject
{
    private R3QualitySubject(
        R3QualitySubjectKind kind,
        ReviewedIdentity? reviewedIdentity,
        bool hasPriorSession,
        ImmutableArray<byte> initialRequest,
        ImmutableArray<byte> trustedPolicy,
        string? currentReviewContext,
        string? priorReviewContext,
        AgentTerminalReview? review,
        ImmutableArray<R3QualityToolObservation> toolObservations,
        R3QualityFreshProcessTwoInputSet? freshInputs,
        string? sourceCode,
        int findingCount,
        int toolCallCount)
    {
        Kind = kind;
        ReviewedIdentity = reviewedIdentity;
        HasPriorSession = hasPriorSession;
        InitialRequest = initialRequest;
        TrustedPolicy = trustedPolicy;
        CurrentReviewContext = currentReviewContext;
        PriorReviewContext = priorReviewContext;
        Review = review;
        ToolObservations = toolObservations;
        FreshInputs = freshInputs;
        SourceCode = sourceCode;
        FindingCount = findingCount;
        ToolCallCount = toolCallCount;
    }

    internal R3QualitySubjectKind Kind { get; }

    internal ReviewedIdentity? ReviewedIdentity { get; }

    internal bool HasPriorSession { get; }

    internal ImmutableArray<byte> InitialRequest { get; }

    internal ImmutableArray<byte> TrustedPolicy { get; }

    internal string? CurrentReviewContext { get; }

    internal string? PriorReviewContext { get; }

    internal AgentTerminalReview? Review { get; }

    internal ImmutableArray<R3QualityToolObservation> ToolObservations { get; }

    internal R3QualityFreshProcessTwoInputSet? FreshInputs { get; }

    internal string? SourceCode { get; }

    internal int FindingCount { get; }

    internal int ToolCallCount { get; }

    internal static R3QualitySubjectCreation TryCreateCompleted(
        AgentSessionBuildInput input,
        R3QualityFreshProcessTwoInputSet freshInputs)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(freshInputs);
        AgentSessionArtifact? artifact = null;
        try
        {
            if (!input.Outcome.Succeeded ||
                input.Outcome.Review is null ||
                input.Outcome.Diagnostic is not null)
            {
                return R3QualitySubjectCreation.Failure("quality_outcome_invalid");
            }

            var built = AgentSessionBuilder.Build(input);
            if (!built.Succeeded)
            {
                return R3QualitySubjectCreation.Failure(
                    SafeSourceCode(built.FailureCode));
            }

            artifact = built.Artifact;
            if (artifact is null ||
                !TryReviewContexts(
                    artifact.Document,
                    out var currentReviewContext,
                    out var priorReviewContext) ||
                !TryReconstructObservations(
                    input.Run.ReviewedIdentity,
                    input.Outcome.Events,
                    out var observations) ||
                !TryReconstructReview(
                    input.Run.ReviewedIdentity,
                    input.Outcome.Review,
                    observations,
                    out var reconstructedReview))
            {
                return R3QualitySubjectCreation.Failure(
                    "quality_projection_invalid");
            }

            var initialRequest = AgentRequestWriter.Write(new ProjectChatRequest(
                input.Run.InitialMessages,
                AgentToolRegistry.Definitions.ToArray(),
                input.Run.Continuation,
                ThinkingRequired: true));
            return R3QualitySubjectCreation.Success(new R3QualitySubject(
                R3QualitySubjectKind.Completed,
                input.Run.ReviewedIdentity,
                artifact.Document.PriorSessionSha256 is not null,
                ImmutableArray.CreateRange(initialRequest),
                ImmutableArray.CreateRange(input.TrustedRequest.TrustedPolicyBytes),
                currentReviewContext,
                priorReviewContext,
                reconstructedReview,
                observations,
                freshInputs,
                sourceCode: null,
                reconstructedReview!.Findings.Length,
                observations.Length));
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            DecoderFallbackException or
            FormatException or
            InvalidOperationException or
            JsonException or
            KeyNotFoundException or
            NotSupportedException or
            OverflowException)
        {
            return R3QualitySubjectCreation.Failure("quality_subject_exception");
        }
        finally
        {
            if (artifact is not null)
            {
                CryptographicOperations.ZeroMemory(artifact.Plaintext);
            }
        }
    }

    internal static bool TryCreateProductFailure(
        string sourceCode,
        int findingCount,
        int toolCallCount,
        out R3QualitySubject? subject) =>
        TryCreateFailure(
            R3QualitySubjectKind.ProductFailure,
            sourceCode,
            findingCount,
            toolCallCount,
            out subject);

    internal static bool TryCreateProviderFailure(
        string sourceCode,
        int findingCount,
        int toolCallCount,
        out R3QualitySubject? subject) =>
        TryCreateFailure(
            R3QualitySubjectKind.ProviderFailure,
            sourceCode,
            findingCount,
            toolCallCount,
            out subject);

    internal static bool TryCreateToolFailure(
        string sourceCode,
        int findingCount,
        int toolCallCount,
        out R3QualitySubject? subject) =>
        TryCreateFailure(
            R3QualitySubjectKind.ToolFailure,
            sourceCode,
            findingCount,
            toolCallCount,
            out subject);

    internal static bool TryCreateStateFailure(
        string sourceCode,
        int findingCount,
        int toolCallCount,
        out R3QualitySubject? subject) =>
        TryCreateFailure(
            R3QualitySubjectKind.StateFailure,
            sourceCode,
            findingCount,
            toolCallCount,
            out subject);

    internal static bool TryCreateEvaluatorFailure(
        string sourceCode,
        int findingCount,
        int toolCallCount,
        out R3QualitySubject? subject) =>
        TryCreateFailure(
            R3QualitySubjectKind.EvaluatorFailure,
            sourceCode,
            findingCount,
            toolCallCount,
            out subject);

    private static bool TryCreateFailure(
        R3QualitySubjectKind kind,
        string sourceCode,
        int findingCount,
        int toolCallCount,
        out R3QualitySubject? subject)
    {
        subject = null;
        if (!AgentValueDomains.IsIdentifier(sourceCode) ||
            findingCount is < 0 or > AgentLimits.Findings ||
            toolCallCount is < 0 or > AgentLimits.ToolCalls)
        {
            return false;
        }

        subject = new R3QualitySubject(
            kind,
            reviewedIdentity: null,
            hasPriorSession: false,
            initialRequest: [],
            trustedPolicy: [],
            currentReviewContext: null,
            priorReviewContext: null,
            review: null,
            toolObservations: [],
            freshInputs: null,
            sourceCode,
            findingCount,
            toolCallCount);
        return true;
    }

    private static bool TryReviewContexts(
        AgentSessionDocument document,
        out string? currentReviewContext,
        out string? priorReviewContext)
    {
        currentReviewContext = null;
        priorReviewContext = null;
        if (document.CompletedRuns.IsDefaultOrEmpty ||
            !TryReviewContext(document.CompletedRuns[^1], out currentReviewContext))
        {
            return false;
        }

        return document.CompletedRuns.Length == 1 ||
            TryReviewContext(document.CompletedRuns[^2], out priorReviewContext);
    }

    private static bool TryReviewContext(
        AgentSessionCompletedRun run,
        out string? reviewContext)
    {
        reviewContext = null;
        if (run.Records.IsDefaultOrEmpty ||
            run.Records[0] is not AgentSessionReviewContextRecord context)
        {
            return false;
        }

        reviewContext = context.Text;
        return reviewContext is not null;
    }

    private static bool TryReconstructObservations(
        ReviewedIdentity identity,
        ImmutableArray<AgentLogicalEvent> events,
        out ImmutableArray<R3QualityToolObservation> observations)
    {
        observations = [];
        var calls = events
            .OfType<AgentToolCallEvent>()
            .ToDictionary(call => call.CallId, StringComparer.Ordinal);
        var builder = ImmutableArray.CreateBuilder<R3QualityToolObservation>();
        foreach (var result in events.OfType<AgentToolResultEvent>())
        {
            if (!calls.TryGetValue(result.CallId, out var call) ||
                !StringComparer.Ordinal.Equals(call.Name, result.Name) ||
                !TryPrepare(call, out var prepared) ||
                !TryReturnedLines(
                    result.Name,
                    result.CanonicalResult,
                    out var returnedLines))
            {
                return false;
            }

            var canonicalResult = result.CanonicalResult.ToArray();
            var resultJson = StrictUtf8(canonicalResult);
            var execution = new AgentToolExecution(
                true,
                FailureCode: null,
                resultJson,
                canonicalResult,
                new AgentObservation(
                    result.ObservationId,
                    identity,
                    returnedLines));
            if (!AgentToolResultAdmission.TryAdmit(
                    prepared!,
                    identity,
                    execution,
                    out _,
                    out var admitted))
            {
                return false;
            }

            builder.Add(new R3QualityToolObservation(
                call.CallId,
                call.Name,
                ImmutableArray.CreateRange(call.CanonicalArguments),
                ImmutableArray.CreateRange(result.CanonicalResult),
                admitted));
        }

        observations = builder.ToImmutable();
        return true;
    }

    private static bool TryPrepare(
        AgentToolCallEvent call,
        out PreparedAgentToolCall? prepared)
    {
        prepared = null;
        var argumentsJson = StrictUtf8(call.CanonicalArguments.AsSpan());
        switch (call.Name)
        {
            case AgentToolRegistry.ListFilesName:
                if (AgentToolArguments.TryListFilesCanonical(
                        argumentsJson,
                        out var list))
                {
                    prepared = new PreparedListFilesCall(call.CallId, list!);
                }

                break;
            case AgentToolRegistry.ListChangedFilesName:
                if (AgentToolArguments.TryListChangedFilesCanonical(
                        argumentsJson,
                        out var changed))
                {
                    prepared = new PreparedListChangedFilesCall(
                        call.CallId,
                        changed!);
                }

                break;
            case AgentToolRegistry.ReadDiffName:
                if (AgentToolArguments.TryReadDiff(argumentsJson, out var diff))
                {
                    prepared = new PreparedReadDiffCall(call.CallId, diff!);
                }

                break;
            case AgentToolRegistry.ReadFileName:
                if (AgentToolArguments.TryReadFile(argumentsJson, out var read))
                {
                    prepared = new PreparedReadFileCall(call.CallId, read!);
                }

                break;
            case AgentToolRegistry.SearchTextName:
                if (AgentToolArguments.TrySearchTextCanonical(
                        argumentsJson,
                        out var search))
                {
                    prepared = new PreparedSearchTextCall(call.CallId, search!);
                }

                break;
        }

        return prepared is not null &&
            prepared.CanonicalArguments.AsSpan().SequenceEqual(
                call.CanonicalArguments.AsSpan());
    }

    private static bool TryReturnedLines(
        string name,
        ImmutableArray<byte> canonicalResult,
        out ImmutableDictionary<string, ImmutableHashSet<int>> returnedLines)
    {
        returnedLines = EmptyReturnedLines();
        using var document = JsonDocument.Parse(canonicalResult.ToArray());
        var root = document.RootElement;
        switch (name)
        {
            case AgentToolRegistry.ListFilesName:
            case AgentToolRegistry.ListChangedFilesName:
                return true;
            case AgentToolRegistry.ReadFileName:
                {
                    var path = root.GetProperty("path").GetString();
                    if (path is null)
                    {
                        return false;
                    }

                    var lines = root.GetProperty("lines")
                        .EnumerateArray()
                        .Select(line => line.GetProperty("line").GetInt32())
                        .ToImmutableHashSet();
                    if (lines.Count > 0)
                    {
                        returnedLines = returnedLines.Add(path, lines);
                    }

                    return true;
                }
            case AgentToolRegistry.SearchTextName:
                returnedLines = root.GetProperty("matches")
                    .EnumerateArray()
                    .GroupBy(
                        match => match.GetProperty("path").GetString()!,
                        StringComparer.Ordinal)
                    .ToImmutableDictionary(
                        group => group.Key,
                        group => group
                            .Select(match =>
                                match.GetProperty("line").GetInt32())
                            .ToImmutableHashSet(),
                        StringComparer.Ordinal);
                return true;
            case AgentToolRegistry.ReadDiffName:
                {
                    var path = root.GetProperty("path").GetString();
                    if (path is null)
                    {
                        return false;
                    }

                    var lines = root.GetProperty("hunks")
                        .EnumerateArray()
                        .SelectMany(hunk =>
                            hunk.GetProperty("lines").EnumerateArray())
                        .Where(line =>
                            line.GetProperty("kind").GetString() is
                                "context" or "addition")
                        .Select(line =>
                            line.GetProperty("new_line").GetInt32())
                        .ToImmutableHashSet();
                    if (lines.Count > 0)
                    {
                        returnedLines = returnedLines.Add(path, lines);
                    }

                    return true;
                }
            default:
                return false;
        }
    }

    private static bool TryReconstructReview(
        ReviewedIdentity identity,
        AgentTerminalReview original,
        ImmutableArray<R3QualityToolObservation> observations,
        out AgentTerminalReview? reconstructed)
    {
        reconstructed = null;
        var terminalJson = StrictUtf8(original.CanonicalBytes);
        if (!AgentToolArguments.TryFinishReview(terminalJson, out var arguments) ||
            !TerminalReviewValidator.TryValidate(
                arguments!,
                identity,
                observations.Select(item => item.Observation).ToArray(),
                out reconstructed))
        {
            return false;
        }

        return ReviewsEqual(original, reconstructed!);
    }

    private static bool ReviewsEqual(
        AgentTerminalReview left,
        AgentTerminalReview right)
    {
        if (!StringComparer.Ordinal.Equals(left.Summary, right.Summary) ||
            !StringComparer.Ordinal.Equals(
                left.TerminalSha256,
                right.TerminalSha256) ||
            !left.CanonicalBytes.AsSpan().SequenceEqual(right.CanonicalBytes) ||
            left.Findings.Length != right.Findings.Length)
        {
            return false;
        }

        for (var findingIndex = 0;
             findingIndex < left.Findings.Length;
             findingIndex++)
        {
            var leftFinding = left.Findings[findingIndex];
            var rightFinding = right.Findings[findingIndex];
            if (!StringComparer.Ordinal.Equals(
                    leftFinding.Severity,
                    rightFinding.Severity) ||
                !StringComparer.Ordinal.Equals(
                    leftFinding.Title,
                    rightFinding.Title) ||
                !StringComparer.Ordinal.Equals(
                    leftFinding.Message,
                    rightFinding.Message) ||
                leftFinding.Evidence.Length != rightFinding.Evidence.Length)
            {
                return false;
            }

            for (var evidenceIndex = 0;
                 evidenceIndex < leftFinding.Evidence.Length;
                 evidenceIndex++)
            {
                if (leftFinding.Evidence[evidenceIndex] !=
                    rightFinding.Evidence[evidenceIndex])
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string StrictUtf8(ReadOnlySpan<byte> bytes) =>
        new UTF8Encoding(false, true).GetString(bytes);

    private static ImmutableDictionary<string, ImmutableHashSet<int>>
        EmptyReturnedLines() =>
        ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
            .WithComparers(StringComparer.Ordinal);

    private static string? SafeSourceCode(string? value) =>
        AgentValueDomains.IsIdentifier(value) ? value : null;

    public override string ToString() => "r3_quality_subject";
}

internal sealed record R3QualitySubjectCreation(
    R3QualitySubject? Subject,
    string? FailureCode)
{
    internal bool Succeeded => Subject is not null && FailureCode is null;

    internal static R3QualitySubjectCreation Success(R3QualitySubject subject) =>
        new(subject, null);

    internal static R3QualitySubjectCreation Failure(string? code) =>
        new(null, code);

    public override string ToString() => "r3_quality_subject_creation";
}

internal static class R3QualityEvaluator
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static R3QualityOutcome FromParseFailure(
        R3QualityParseFailure failure) =>
        Create(
            failure.CaseId,
            failure.CaseSha256,
            "not_evaluated",
            "evaluator",
            R3QualityCodes.FixtureInvalid,
            failure.SourceCode,
            findingCount: 0,
            toolCallCount: 0,
            terminalSha256: null);

    internal static R3QualityOutcome Evaluate(
        R3QualityCase testCase,
        R3QualitySubjectCreation creation)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(creation);
        return creation.Succeeded
            ? Evaluate(testCase, creation.Subject!)
            : Create(
                testCase,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.SubjectInvalid,
                SafeSourceCode(creation.FailureCode),
                findingCount: 0,
                toolCallCount: 0,
                terminalSha256: null);
    }

    internal static R3QualityOutcome Evaluate(
        R3QualityCase testCase,
        R3QualitySubject subject)
    {
        ArgumentNullException.ThrowIfNull(testCase);
        ArgumentNullException.ThrowIfNull(subject);
        if (subject.Kind != R3QualitySubjectKind.Completed)
        {
            return FailureOutcome(testCase, subject);
        }

        if (subject.ReviewedIdentity != testCase.ReviewedIdentity ||
            subject.Review is null ||
            subject.FreshInputs is null)
        {
            return Create(
                testCase,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.SubjectInvalid,
                "quality_scope_mismatch",
                findingCount: 0,
                toolCallCount: 0,
                terminalSha256: null);
        }

        return testCase.Expectation switch
        {
            R3QualityMustFindExpectation mustFind =>
                EvaluateMustFind(testCase, subject, mustFind),
            R3QualityMustNotFindExpectation mustNot =>
                EvaluateMustNotFind(testCase, subject, mustNot),
            R3QualityContinuationExpectation continuation =>
                EvaluateContinuation(testCase, subject, continuation),
            _ => Create(
                testCase,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.FixtureInvalid,
                "quality_expectation_invalid",
                findingCount: 0,
                toolCallCount: 0,
                terminalSha256: null),
        };
    }

    private static R3QualityOutcome EvaluateMustFind(
        R3QualityCase testCase,
        R3QualitySubject subject,
        R3QualityMustFindExpectation expectation)
    {
        var marker = StrictUtf8.GetBytes(expectation.TargetMarker);
        if (subject.HasPriorSession || !MatchesCaseContext(testCase, subject))
        {
            return SubjectMismatch(testCase);
        }

        if (subject.InitialRequest.AsSpan().IndexOf(marker) >= 0)
        {
            return CompletedFailure(
                testCase,
                subject,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.InitialContextLeak);
        }

        if (!subject.FreshInputs!.MatchesManifest([]) ||
            subject.FreshInputs.Contains(marker))
        {
            return CompletedFailure(
                testCase,
                subject,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.FreshInputInvalid);
        }

        var markerObservations = subject.ToolObservations
            .Where(item => item.CanonicalResult.AsSpan().IndexOf(marker) >= 0)
            .ToArray();
        var exact = markerObservations.Where(item =>
                StringComparer.Ordinal.Equals(
                    item.Name,
                    AgentToolRegistry.ReadDiffName) &&
                item.CanonicalArguments.AsSpan().SequenceEqual(
                    expectation.RequiredArguments.AsSpan()) &&
                item.CanonicalResult.AsSpan().SequenceEqual(
                    expectation.RequiredResult.AsSpan()) &&
                StringComparer.Ordinal.Equals(
                    item.Observation.ObservationId,
                    expectation.RequiredObservationId))
            .ToArray();
        if (exact.Length >= 1)
        {
            if (markerObservations.Length != 1)
            {
                return CompletedFailure(
                    testCase,
                    subject,
                    "not_evaluated",
                    "evaluator",
                    R3QualityCodes.ObservationIsolationInvalid);
            }
        }
        else
        {
            if (markerObservations.Any(item =>
                    !StringComparer.Ordinal.Equals(
                        item.Name,
                        AgentToolRegistry.ReadDiffName)))
            {
                return QualityFailure(
                    testCase,
                    subject,
                    R3QualityCodes.RequiredToolWrong);
            }

            if (subject.ToolObservations.Any(item =>
                    StringComparer.Ordinal.Equals(
                        item.Name,
                        AgentToolRegistry.ReadDiffName)))
            {
                return QualityFailure(
                    testCase,
                    subject,
                    R3QualityCodes.RequiredObservationMissing);
            }

            return QualityFailure(
                testCase,
                subject,
                R3QualityCodes.RequiredToolMissing);
        }

        var matchingFindings = subject.Review!.Findings.Count(finding =>
            StringComparer.Ordinal.Equals(
                finding.Severity,
                expectation.ExpectedSeverity) &&
            (finding.Title.Contains(
                    expectation.ExpectedFindingToken,
                    StringComparison.Ordinal) ||
                finding.Message.Contains(
                    expectation.ExpectedFindingToken,
                    StringComparison.Ordinal)) &&
            finding.Evidence.Length == 1 &&
            finding.Evidence[0] == expectation.Evidence);
        return matchingFindings == 1
            ? Passed(testCase, subject)
            : QualityFailure(
                testCase,
                subject,
                R3QualityCodes.ExpectedFindingMissing);
    }

    private static R3QualityOutcome EvaluateMustNotFind(
        R3QualityCase testCase,
        R3QualitySubject subject,
        R3QualityMustNotFindExpectation expectation)
    {
        if (!MatchesCaseContext(testCase, subject))
        {
            return SubjectMismatch(testCase);
        }

        if (!subject.FreshInputs!.MatchesManifest([]))
        {
            return CompletedFailure(
                testCase,
                subject,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.FreshInputInvalid);
        }

        var prohibited = subject.Review!.Findings
            .SelectMany(finding => finding.Evidence)
            .Any(evidence =>
                StringComparer.Ordinal.Equals(evidence.Path, expectation.Path) &&
                expectation.ProhibitedLines.Any(line =>
                    evidence.StartLine <= line && evidence.EndLine >= line));
        return prohibited
            ? QualityFailure(
                testCase,
                subject,
                R3QualityCodes.ProhibitedFinding)
            : Passed(testCase, subject);
    }

    private static R3QualityOutcome EvaluateContinuation(
        R3QualityCase testCase,
        R3QualitySubject subject,
        R3QualityContinuationExpectation expectation)
    {
        var marker = StrictUtf8.GetBytes(expectation.PriorOnlyMarker);
        if (!subject.HasPriorSession)
        {
            return Create(
                testCase,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.SubjectInvalid,
                "quality_prior_session_missing",
                findingCount: 0,
                toolCallCount: 0,
                terminalSha256: null);
        }

        if (!MatchesCaseContext(testCase, subject))
        {
            return SubjectMismatch(testCase);
        }

        if (subject.TrustedPolicy.AsSpan().IndexOf(marker) >= 0 ||
            !subject.FreshInputs!.MatchesManifest(expectation.FreshInputNames) ||
            subject.FreshInputs.Contains(marker) ||
            subject.ToolObservations.Any(item =>
                item.CanonicalResult.AsSpan().IndexOf(marker) >= 0))
        {
            return CompletedFailure(
                testCase,
                subject,
                "not_evaluated",
                "evaluator",
                R3QualityCodes.FreshInputInvalid);
        }

        var review = subject.Review!;
        if (review.Findings.Length != 0)
        {
            return QualityFailure(
                testCase,
                subject,
                R3QualityCodes.ProhibitedFinding);
        }

        if (!subject.ToolObservations.IsEmpty)
        {
            return QualityFailure(
                testCase,
                subject,
                R3QualityCodes.RequiredToolWrong);
        }

        var present = review.Summary.Contains(
            expectation.PriorOnlyMarker,
            StringComparison.Ordinal);
        return present
            ? Passed(testCase, subject)
            : QualityFailure(
                testCase,
                subject,
                R3QualityCodes.PriorFactMissing);
    }

    private static bool MatchesCaseContext(
        R3QualityCase testCase,
        R3QualitySubject subject) =>
        StringComparer.Ordinal.Equals(
            subject.CurrentReviewContext,
            testCase.InitialContext) &&
        StringComparer.Ordinal.Equals(
            subject.PriorReviewContext,
            testCase.ProcessOneContext);

    private static R3QualityOutcome SubjectMismatch(R3QualityCase testCase) =>
        Create(
            testCase,
            "not_evaluated",
            "evaluator",
            R3QualityCodes.SubjectInvalid,
            "quality_scope_mismatch",
            findingCount: 0,
            toolCallCount: 0,
            terminalSha256: null);

    private static R3QualityOutcome FailureOutcome(
        R3QualityCase testCase,
        R3QualitySubject subject)
    {
        var contract = subject.Kind switch
        {
            R3QualitySubjectKind.ProductFailure =>
                ("product", R3QualityCodes.ProductFailed),
            R3QualitySubjectKind.ProviderFailure =>
                ("provider", R3QualityCodes.ProviderFailed),
            R3QualitySubjectKind.ToolFailure =>
                ("tool", R3QualityCodes.ToolFailed),
            R3QualitySubjectKind.StateFailure =>
                ("state", R3QualityCodes.StateFailed),
            R3QualitySubjectKind.EvaluatorFailure =>
                ("evaluator", R3QualityCodes.EvaluatorFailed),
            _ => throw new InvalidOperationException("Invalid failure subject."),
        };
        return Create(
            testCase,
            "not_evaluated",
            contract.Item1,
            contract.Item2,
            subject.SourceCode,
            subject.FindingCount,
            subject.ToolCallCount,
            terminalSha256: null);
    }

    private static R3QualityOutcome Passed(
        R3QualityCase testCase,
        R3QualitySubject subject) =>
        Create(
            testCase,
            "passed",
            "quality",
            R3QualityCodes.Passed,
            sourceCode: null,
            subject.FindingCount,
            subject.ToolCallCount,
            subject.Review!.TerminalSha256);

    private static R3QualityOutcome QualityFailure(
        R3QualityCase testCase,
        R3QualitySubject subject,
        string code) =>
        CompletedFailure(testCase, subject, "failed", "quality", code);

    private static R3QualityOutcome CompletedFailure(
        R3QualityCase testCase,
        R3QualitySubject subject,
        string status,
        string classification,
        string code) =>
        Create(
            testCase,
            status,
            classification,
            code,
            sourceCode: null,
            subject.FindingCount,
            subject.ToolCallCount,
            subject.Review!.TerminalSha256);

    private static R3QualityOutcome Create(
        R3QualityCase testCase,
        string status,
        string classification,
        string code,
        string? sourceCode,
        int findingCount,
        int toolCallCount,
        string? terminalSha256) =>
        Create(
            testCase.Id,
            testCase.CaseSha256,
            status,
            classification,
            code,
            sourceCode,
            findingCount,
            toolCallCount,
            terminalSha256);

    private static R3QualityOutcome Create(
        string caseId,
        string caseSha256,
        string status,
        string classification,
        string code,
        string? sourceCode,
        int findingCount,
        int toolCallCount,
        string? terminalSha256)
    {
        if (!R3QualityOutcome.TryCreate(
                caseId,
                caseSha256,
                status,
                classification,
                code,
                sourceCode,
                findingCount,
                toolCallCount,
                terminalSha256,
                out var outcome))
        {
            throw new InvalidOperationException("Invalid quality outcome combination.");
        }

        return outcome!;
    }

    private static string? SafeSourceCode(string? value) =>
        AgentValueDomains.IsIdentifier(value) ? value : null;
}
