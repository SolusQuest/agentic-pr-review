using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Quality;

internal static class R3QualityFormat
{
    internal const string CorpusKind = "apr-r3-quality-corpus";
    internal const string OutcomeKind = "apr-r3-quality-outcome";
    internal const string CaseHashDomain = "apr.r3.quality.case";
    internal const string InvalidFixtureHashDomain = "apr.r3.quality.invalid-fixture";
    internal const int CaseCount = 3;
    internal const int MaximumCorpusBytes = 512 * 1024;
    internal const int MaximumFilesPerCase = 16;
    internal const int MaximumFileBytes = 64 * 1024;
    internal const int MaximumFreshInputs = 32;
    internal const int MaximumFreshInputBytes = 64 * 1024;
    internal const int MaximumFreshInputTotalBytes = 512 * 1024;
}

internal static class R3QualityCodes
{
    internal const string Passed = "r3_quality_passed";
    internal const string RequiredToolMissing = "r3_quality_required_tool_missing";
    internal const string RequiredToolWrong = "r3_quality_required_tool_wrong";
    internal const string RequiredObservationMissing =
        "r3_quality_required_observation_missing";
    internal const string ExpectedFindingMissing =
        "r3_quality_expected_finding_missing";
    internal const string ProhibitedFinding = "r3_quality_prohibited_finding";
    internal const string PriorFactMissing = "r3_quality_prior_fact_missing";
    internal const string FixtureInvalid = "r3_quality_fixture_invalid";
    internal const string InitialContextLeak = "r3_quality_initial_context_leak";
    internal const string FreshInputInvalid = "r3_quality_fresh_input_invalid";
    internal const string ObservationIsolationInvalid =
        "r3_quality_observation_isolation_invalid";
    internal const string SubjectInvalid = "r3_quality_subject_invalid";
    internal const string ProductFailed = "r3_quality_product_failed";
    internal const string ProviderFailed = "r3_quality_provider_failed";
    internal const string ToolFailed = "r3_quality_tool_failed";
    internal const string StateFailed = "r3_quality_state_failed";
    internal const string EvaluatorFailed = "r3_quality_evaluator_failed";
}

internal enum R3QualityCaseKind
{
    MustFind,
    MustNotFind,
    Continuation,
}

internal sealed record R3QualityRepositoryFile(string Path, string Content);

internal abstract record R3QualityExpectation;

internal sealed record R3QualityMustFindExpectation(
    string RequiredObservationId,
    ImmutableArray<byte> RequiredArguments,
    ImmutableArray<byte> RequiredResult,
    AgentEvidence Evidence,
    string ExpectedSeverity,
    string TargetMarker,
    string ExpectedFindingToken)
    : R3QualityExpectation;

internal sealed record R3QualityMustNotFindExpectation(
    string RequiredObservationId,
    ImmutableArray<byte> RequiredArguments,
    ImmutableArray<byte> RequiredResult,
    string Path,
    ImmutableArray<int> ProhibitedLines)
    : R3QualityExpectation;

internal sealed record R3QualityContinuationExpectation(
    string PriorOnlyMarker,
    ImmutableArray<string> FreshInputNames)
    : R3QualityExpectation;

internal sealed record R3QualityCase(
    string Id,
    string CaseSha256,
    R3QualityCaseKind Kind,
    ReviewedIdentity ReviewedIdentity,
    string InitialContext,
    string? ProcessOneContext,
    ImmutableArray<R3QualityRepositoryFile> Files,
    ReviewedChangedFile ChangedFile,
    ReviewedDiffSource DiffSource,
    R3QualityExpectation Expectation)
{
    public override string ToString() => "r3_quality_case";
}

internal sealed record R3QualityCorpus(ImmutableArray<R3QualityCase> Cases)
{
    public override string ToString() => "r3_quality_corpus";
}

internal sealed record R3QualityParseFailure(
    string CaseId,
    string CaseSha256,
    string? SourceCode)
{
    public override string ToString() => "r3_quality_parse_failure";
}

internal sealed record R3QualityFreshInput(
    string Name,
    ImmutableArray<byte> Bytes)
{
    public override string ToString() => "r3_quality_fresh_input";
}

internal sealed class R3QualityFreshProcessTwoInputSet
{
    private R3QualityFreshProcessTwoInputSet(
        ImmutableArray<R3QualityFreshInput> inputs,
        bool structurallyValid)
    {
        Inputs = inputs;
        StructurallyValid = structurallyValid;
    }

    internal ImmutableArray<R3QualityFreshInput> Inputs { get; }

    internal bool StructurallyValid { get; }

    internal static R3QualityFreshProcessTwoInputSet Capture(
        IEnumerable<(string Name, ReadOnlyMemory<byte> Bytes)> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var builder = ImmutableArray.CreateBuilder<R3QualityFreshInput>();
        var valid = true;
        long total = 0;
        foreach (var input in inputs)
        {
            if (builder.Count >= R3QualityFormat.MaximumFreshInputs + 1)
            {
                valid = false;
                break;
            }

            var length = input.Bytes.Length;
            total = checked(total + length);
            if (!RepositoryPath.IsValid(input.Name) ||
                length > R3QualityFormat.MaximumFreshInputBytes ||
                total > R3QualityFormat.MaximumFreshInputTotalBytes)
            {
                valid = false;
            }

            var copyLength = Math.Min(
                length,
                R3QualityFormat.MaximumFreshInputBytes + 1);
            builder.Add(new R3QualityFreshInput(
                input.Name,
                ImmutableArray.CreateRange(
                    input.Bytes.Span[..copyLength].ToArray())));
        }

        if (builder.Count > R3QualityFormat.MaximumFreshInputs)
        {
            valid = false;
        }

        return new R3QualityFreshProcessTwoInputSet(
            builder.ToImmutable(),
            valid);
    }

    internal bool MatchesManifest(ImmutableArray<string> expectedNames)
    {
        if (!StructurallyValid || Inputs.Length != expectedNames.Length)
        {
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Inputs.Length; index++)
        {
            if (!names.Add(Inputs[index].Name) ||
                !StringComparer.Ordinal.Equals(
                    Inputs[index].Name,
                    expectedNames[index]))
            {
                return false;
            }
        }

        return true;
    }

    internal bool Contains(ReadOnlySpan<byte> marker)
    {
        foreach (var input in Inputs)
        {
            if (input.Bytes.AsSpan().IndexOf(marker) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    public override string ToString() => "r3_quality_fresh_process_two_input_set";
}

internal sealed class R3QualityOutcome
{
    private R3QualityOutcome(
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
        CaseId = caseId;
        CaseSha256 = caseSha256;
        Status = status;
        Classification = classification;
        Code = code;
        SourceCode = sourceCode;
        FindingCount = findingCount;
        ToolCallCount = toolCallCount;
        TerminalSha256 = terminalSha256;
        CanonicalBytes = WriteCanonical();
    }

    internal string CaseId { get; }

    internal string CaseSha256 { get; }

    internal string Status { get; }

    internal string Classification { get; }

    internal string Code { get; }

    internal string? SourceCode { get; }

    internal int FindingCount { get; }

    internal int ToolCallCount { get; }

    internal string? TerminalSha256 { get; }

    internal byte[] CanonicalBytes { get; }

    internal static bool TryCreate(
        string caseId,
        string caseSha256,
        string status,
        string classification,
        string code,
        string? sourceCode,
        int findingCount,
        int toolCallCount,
        string? terminalSha256,
        out R3QualityOutcome? outcome)
    {
        outcome = null;
        if (!AgentValueDomains.IsIdentifier(caseId) ||
            !IsLowerHex(caseSha256, 64) ||
            findingCount is < 0 or > AgentLimits.Findings ||
            toolCallCount is < 0 or > AgentLimits.ToolCalls ||
            sourceCode is not null && !AgentValueDomains.IsIdentifier(sourceCode) ||
            terminalSha256 is not null && !IsLowerHex(terminalSha256, 64) ||
            !CombinationIsValid(
                status,
                classification,
                code,
                sourceCode,
                findingCount,
                toolCallCount,
                terminalSha256))
        {
            return false;
        }

        outcome = new R3QualityOutcome(
            caseId,
            caseSha256,
            status,
            classification,
            code,
            sourceCode,
            findingCount,
            toolCallCount,
            terminalSha256);
        return true;
    }

    private static bool CombinationIsValid(
        string status,
        string classification,
        string code,
        string? sourceCode,
        int findingCount,
        int toolCallCount,
        string? terminalSha256)
    {
        if (classification == "quality")
        {
            return sourceCode is null &&
                terminalSha256 is not null &&
                (code == R3QualityCodes.Passed && status == "passed" ||
                    IsQualityFailure(code) && status == "failed");
        }

        if (status != "not_evaluated")
        {
            return false;
        }

        if (IsIsolationFailure(code))
        {
            return classification == "evaluator" &&
                sourceCode is null &&
                terminalSha256 is not null;
        }

        if (code is R3QualityCodes.FixtureInvalid or R3QualityCodes.SubjectInvalid)
        {
            return classification == "evaluator" &&
                terminalSha256 is null &&
                findingCount == 0 &&
                toolCallCount == 0;
        }

        return terminalSha256 is null &&
            sourceCode is not null &&
            code switch
            {
                R3QualityCodes.ProductFailed => classification == "product",
                R3QualityCodes.ProviderFailed => classification == "provider",
                R3QualityCodes.ToolFailed => classification == "tool",
                R3QualityCodes.StateFailed => classification == "state",
                R3QualityCodes.EvaluatorFailed => classification == "evaluator",
                _ => false,
            };
    }

    private static bool IsQualityFailure(string code) =>
        code is R3QualityCodes.RequiredToolMissing or
            R3QualityCodes.RequiredToolWrong or
            R3QualityCodes.RequiredObservationMissing or
            R3QualityCodes.ExpectedFindingMissing or
            R3QualityCodes.ProhibitedFinding or
            R3QualityCodes.PriorFactMissing;

    private static bool IsIsolationFailure(string code) =>
        code is R3QualityCodes.InitialContextLeak or
            R3QualityCodes.FreshInputInvalid or
            R3QualityCodes.ObservationIsolationInvalid;

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private byte[] WriteCanonical()
    {
        var writer = new Rfc8785Writer(512);
        writer.WriteObjectStart();
        writer.WriteProperty("kind");
        writer.WriteString(R3QualityFormat.OutcomeKind);
        writer.WriteProperty("case_id");
        writer.WriteString(CaseId);
        writer.WriteProperty("case_sha256");
        writer.WriteString(CaseSha256);
        writer.WriteProperty("status");
        writer.WriteString(Status);
        writer.WriteProperty("classification");
        writer.WriteString(Classification);
        writer.WriteProperty("code");
        writer.WriteString(Code);
        writer.WriteProperty("source_code");
        WriteNullableString(ref writer, SourceCode);
        writer.WriteProperty("finding_count");
        writer.WriteNumber(FindingCount);
        writer.WriteProperty("tool_call_count");
        writer.WriteNumber(ToolCallCount);
        writer.WriteProperty("terminal_sha256");
        WriteNullableString(ref writer, TerminalSha256);
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    private static void WriteNullableString(
        ref Rfc8785Writer writer,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(value);
        }
    }

    public override string ToString() => "r3_quality_outcome";
}
