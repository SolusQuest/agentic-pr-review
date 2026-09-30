using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Reporting;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6ProspectivePipelineTests
{
    private const string TitleCanary = "APR311_TITLE_9a4f7de2b1c849e4";
    private const string MessageCanary = "APR311_MESSAGE_fbd832c763e14d1a";
    private const string SummaryCanary = "APR311_SUMMARY_37eac419d88347f0";
    private const string ReasoningCanary = "APR311_REASONING_64bb1ac927fe4d89";
    private const string SourceCanary = "APR311_SOURCE_a3fdf04e71784c22";
    private const string EvidencePathCanary = "src/APR311_EVIDENCE_58c63e190fba4a01.cs";
    private const string LocalPathCanary = "C:\\APR311_LOCAL_77d189ed350a48cb\\secret.txt";
    private const string AnnotationCanary = "APR311_ANNOTATION_00a3ac57571f4d8d";
    private const string ExceptionCanary = "APR311_EXCEPTION_e8fbc6e559c84c9a";
    private const string CredentialCanary = "APR311_CREDENTIAL_266bd85e88ea4ab9";

    private static readonly string[] PrivateCanaries =
    [
        TitleCanary, MessageCanary, SummaryCanary, ReasoningCanary,
        SourceCanary, EvidencePathCanary, LocalPathCanary,
        AnnotationCanary, ExceptionCanary, CredentialCanary,
    ];

    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r6",
        "quality-sandbox");

    [Theory]
    [InlineData(5, 5, "exact")]
    [InlineData(5, 6, "bounded_context")]
    [InlineData(5, 7, "bounded_context")]
    [InlineData(4, 6, "bounded_context")]
    [InlineData(3, 5, "bounded_context")]
    [InlineData(4, 7, "invalid")]
    [InlineData(3, 6, "invalid")]
    [InlineData(1, 7, "invalid")]
    [InlineData(6, 7, "invalid")]
    public void FrozenUploadCitationRequiresCompactReturnedAnchor(int start, int end, string expected)
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var identity = fixture.Runs.Single(run => run.Input.CaseId == "repository-rule")
            .Input.ReviewedIdentity.Runtime;
        var observations = ImmutableArray.Create(
            new EvaluationObservation(AgentToolRegistry.ReadFileName,
                new AgentObservation("upload", identity,
                    ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                        "src/Upload.cs", Enumerable.Range(1, 7).ToImmutableHashSet()))),
            new EvaluationObservation(AgentToolRegistry.ReadFileName,
                new AgentObservation("rule", identity,
                    ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                        "rules/review.md", Enumerable.Range(1, 5).ToImmutableHashSet()))));
        var finding = new AgentFinding("high", "bounded synthetic finding", "cause",
            [new AgentEvidence("upload", "src/Upload.cs", start, end)]);
        Assert.Equal(expected, R6ProspectiveAssessment.KnownCitationClass(
            "repository-token-log", finding, observations));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            "repository-token-log", finding with { Severity = "medium" }, observations));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            "repository-token-log", finding, observations.RemoveAt(1)));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            "repository-token-log", finding with
            { Evidence = [new AgentEvidence("upload", "src/Caller.cs", start, end)] }, observations));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            "repository-token-log", finding with
            { Evidence = [new AgentEvidence("not-returned", "src/Upload.cs", start, end)] }, observations));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            "repository-token-log", finding, observations.Select(item => item with
            { Tool = AgentToolRegistry.ReadDiffName }).ToImmutableArray()));
    }

    [Fact]
    public void V2WiderCitationStillRequiresReturnedAnchorAndDeclaredCausalReads()
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var identity = fixture.Runs.Single(run => run.Input.CaseId == "repository-rule")
            .Input.ReviewedIdentity.Runtime;
        var upload = new EvaluationObservation(AgentToolRegistry.ReadFileName,
            new AgentObservation("upload", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                    "src/Upload.cs", Enumerable.Range(1, 7).ToImmutableHashSet())));
        var rule = new EvaluationObservation(AgentToolRegistry.ReadFileName,
            new AgentObservation("rule", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                    "rules/review.md", Enumerable.Range(1, 5).ToImmutableHashSet())));
        ImmutableArray<EvaluationObservation> observed = [upload, rule];
        var finding = new AgentFinding("medium", "reviewed wider citation",
            "The token reaches the stderr logger against the returned rule.",
            [new AgentEvidence("upload", "src/Upload.cs", 1, 7)]);
        Assert.Equal("reviewed_context", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding, observed, false, 0));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding, [upload], false, 0));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding, observed.Select(item => item with
            { Tool = AgentToolRegistry.ReadDiffName }).ToImmutableArray(), false, 0));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding with { Evidence =
                [new AgentEvidence("missing", "src/Upload.cs", 1, 7)] },
            observed, false, 0));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding, observed, false, 1));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding with { Evidence =
                [new AgentEvidence("upload", "src/Caller.cs", 1, 7)] },
            observed, false, 0));
        Assert.Equal("reviewed_context", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding, [upload with
            { Tool = AgentToolRegistry.ReadDiffName }, rule], true, 0));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "repository-token-log", finding, [upload with
            { Tool = AgentToolRegistry.ReadDiffName }], true, 0));
        Assert.False(R6ProspectiveAssessment.ValidV2SemanticAssessments(true,
            "unresolved", "relevant"));
        Assert.False(R6ProspectiveAssessment.ValidV2SemanticAssessments(true,
            "justified", "unresolved"));
    }

    [Theory]
    [InlineData(false, false, 1, 1, true)]
    [InlineData(true, false, 1, 1, false)]
    [InlineData(false, true, 1, 1, false)]
    [InlineData(false, false, 2, 2, false)]
    public void V3DiffMetadataMustProveWholeUntruncatedPatch(bool sourceTruncated,
        bool pageTruncated, int requestedStart, int returnedStart, bool expected)
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var identity = fixture.Runs[0].Input.ReviewedIdentity.Runtime;
        var json = JsonSerializer.SerializeToUtf8Bytes(new
        {
            status = "ok",
            source_truncated = sourceTruncated,
            truncated = pageTruncated,
            requested_start_hunk = requestedStart,
            returned_start_hunk = returnedStart,
        });
        var captured = new R3QualityToolObservation("call", AgentToolRegistry.ReadDiffName,
            [], ImmutableArray.CreateRange(json), new AgentObservation("changed", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                    "src/client.ts", [1, 2])));
        Assert.Equal(expected, EvaluationObservation.From(captured).CompleteDiff);
    }

    [Fact]
    public void V3EquivalentChangedReadDoesNotWaiveUnchangedSupportOrIdentity()
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var identity = fixture.Runs.Single(run => run.Input.CaseId == "ts-defect")
            .Input.ReviewedIdentity.Runtime;
        var changed = new EvaluationObservation(AgentToolRegistry.ReadDiffName,
            new AgentObservation("changed", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                    "src/client.ts", [1, 2])), true);
        var support = new EvaluationObservation(AgentToolRegistry.ReadFileName,
            new AgentObservation("support", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                    "src/config.ts", [1, 2])));
        var client = new RequiredObservation(AgentToolRegistry.ReadFileName, null,
            new("src/client.ts", 2, 2));
        var config = new RequiredObservation(AgentToolRegistry.ReadFileName, null,
            new("src/config.ts", 1, 2));
        Assert.True(R6ProspectiveAssessment.EquivalentChangedRead(client, [changed], identity));
        Assert.False(R6ProspectiveAssessment.EquivalentChangedRead(config, [changed], identity));
        Assert.False(R6ProspectiveAssessment.EquivalentChangedRead(client,
            [changed with { CompleteDiff = false }], identity));
        Assert.False(R6ProspectiveAssessment.EquivalentChangedRead(client,
            [changed with { Tool = AgentToolRegistry.SearchTextName }], identity));
        Assert.False(R6ProspectiveAssessment.EquivalentChangedRead(client,
            [changed with { Observation = changed.Observation with
            { Identity = identity with { ReviewTarget = identity.ReviewTarget + 1 } } }], identity));
        Assert.False(R6ProspectiveAssessment.EquivalentChangedRead(client,
            [changed with { Observation = changed.Observation with
            { ReturnedLines = ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(
                "src/client.ts", [1]) } }], identity));
        var finding = new AgentFinding("medium", "zero is lost", "truthy fallback",
            [new AgentEvidence("changed", "src/client.ts", 2, 2)]);
        Assert.Equal("exact", R6ProspectiveAssessment.ReviewedCitationClass(
            "ts-zero-timeout", finding, [changed, support], false, 0, true));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "ts-zero-timeout", finding, [changed], false, 0, true));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "ts-zero-timeout", finding, [changed with { CompleteDiff = false }, support],
            false, 0, true));
        Assert.Equal("invalid", R6ProspectiveAssessment.ReviewedCitationClass(
            "ts-zero-timeout", finding, [changed, support], false, 0));
    }

    [Theory]
    [InlineData("cs-null-deref", "src/Caller.cs", 5, 5, "src/Lookup.cs", 5, 5, "high", "exact")]
    [InlineData("ts-zero-timeout", "src/client.ts", 2, 2, "src/config.ts", 1, 2,
        "medium", "exact")]
    [InlineData("repository-token-log", "src/Upload.cs", 5, 6, "rules/review.md", 3, 3,
        "high", "bounded_context")]
    public void AuthoredOffFocusAcceptsGroundedDiffAnchorWithReturnedSupportingRead(
        string group, string changedPath, int start, int end, string supportingPath,
        int supportingStart, int supportingEnd, string severity, string expectedClass)
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture);
        var identity = fixture.Runs.Single(run => run.Input.CaseId == "ts-safe")
            .Input.ReviewedIdentity.Runtime;
        var changed = new EvaluationObservation(AgentToolRegistry.ReadDiffName,
            new AgentObservation("changed", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(changedPath,
                    Enumerable.Range(start, end - start + 1).ToImmutableHashSet())));
        var support = new EvaluationObservation(AgentToolRegistry.ReadFileName,
            new AgentObservation("support", identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add(supportingPath,
                    Enumerable.Range(supportingStart, supportingEnd - supportingStart + 1)
                        .ToImmutableHashSet())));
        var observations = ImmutableArray.Create(changed, support);
        var finding = new AgentFinding(severity, "independently confirmed", "causal explanation",
            [new AgentEvidence("changed", changedPath, start, end)]);

        Assert.Equal(expectedClass, R6ProspectiveAssessment.KnownCitationClass(
            group, finding, observations, offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding, observations));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding, observations.RemoveAt(1), offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding, observations.SetItem(1, support with
            { Tool = AgentToolRegistry.ReadDiffName }), offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding, observations.SetItem(0, changed with
            { Tool = AgentToolRegistry.SearchTextName }), offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding with { Severity = severity == "high" ? "medium" : "high" },
            observations, offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding with { Evidence = [new AgentEvidence("changed", supportingPath,
                start, end)] }, observations, offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding with { Evidence = [new AgentEvidence("not-returned", changedPath,
                start, end)] }, observations, offFocus: true));
        Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
            group, finding with { Evidence = [new AgentEvidence("changed", changedPath,
                Math.Max(1, start - 3), end + 3)] }, observations, offFocus: true));
        if (end > start)
            Assert.Equal("invalid", R6ProspectiveAssessment.KnownCitationClass(
                group, finding with { Evidence = [new AgentEvidence("changed", changedPath,
                    start, start)] }, observations, offFocus: true));
    }

    [Fact]
    public void MixedSafeLineUsesAlwaysResolveToAccusation()
    {
        var both = new AgentFinding("high", "synthetic", "cause",
        [
            new AgentEvidence("cs", "src/SafeCaller.cs", 5, 5),
            new AgentEvidence("ts", "src/safe-client.ts", 2, 2),
        ]);
        Assert.Equal(R6ProspectiveAssessment.Comparison,
            R6ProspectiveAssessment.DeriveSafeLineRole(both, ["comparison", "comparison"]));
        Assert.Equal(R6ProspectiveAssessment.Accusation,
            R6ProspectiveAssessment.DeriveSafeLineRole(both, ["comparison", "accusation"]));
        Assert.Equal(R6ProspectiveAssessment.Accusation,
            R6ProspectiveAssessment.DeriveSafeLineRole(both, ["accusation", "comparison"]));
        Assert.Null(R6ProspectiveAssessment.DeriveSafeLineRole(both, ["comparison"]));
        Assert.Null(R6ProspectiveAssessment.DeriveSafeLineRole(both, ["none", "comparison"]));
    }

    [Fact]
    public void V2SafeLineReviewBindsEveryUseToItsEvidenceOrdinal()
    {
        var finding = new AgentFinding("low", "synthetic", "cause",
        [
            new AgentEvidence("cs", "src/SafeCaller.cs", 5, 5),
            new AgentEvidence("lookup", "src/Lookup.cs", 5, 5),
            new AgentEvidence("ts", "src/safe-client.ts", 2, 2),
        ]);
        ImmutableArray<R6ProspectiveSafeUse> uses =
        [
            new(0, "accusation", "confirmed_other_issue"),
            new(2, "comparison", "comparison_only"),
        ];
        Assert.True(R6ProspectiveAssessment.ValidV2SafeLineAssessments(finding,
            ["accusation", "comparison"], uses));
        Assert.False(R6ProspectiveAssessment.ValidV2SafeLineAssessments(finding,
            ["accusation", "comparison"], uses.Reverse().ToImmutableArray()));
        Assert.False(R6ProspectiveAssessment.ValidV2SafeLineAssessments(finding,
            ["comparison", "accusation"], uses));
        Assert.False(R6ProspectiveAssessment.ValidV2SafeLineAssessments(finding,
            ["accusation"], uses));
        Assert.False(R6ProspectiveAssessment.ValidV2SafeLineAssessments(finding,
            ["accusation", "comparison"], uses.RemoveAt(1)));
        Assert.False(R6ProspectiveAssessment.ValidV2SafeLineAssessments(finding,
            ["accusation", "comparison"], uses.SetItem(1,
                new(2, "comparison", "confirmed_other_issue"))));
    }

    [Fact]
    public void V2PublicFindingRequiresCompleteConsistentProtectedPropertyReview()
    {
        var rubric = new LivePlanRubric(R6ProspectiveRubric.V2Id,
            R6ProspectiveRubric.V2Sha256);
        var group = R6ProspectiveAssessment.PublicGroupId("run", "run-redundant-trim");
        var other = new R6ProspectiveFindingReceipt(0, "true_off_focus", null,
            "run", group, "reviewed_other", "accusation", "confirmed_off_focus",
            [new(0, "accusation", "confirmed_other_issue"),
             new(2, "accusation", "confirmed_other_issue")], 0,
            "justified", "relevant");
        Assert.True(R6ProspectiveQualityGate.ValidFindingShape(other, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other, 1, 0));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { SafeLineAssessments = [new(2, "accusation", "confirmed_other_issue"),
            new(0, "accusation", "confirmed_other_issue")] }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { SafeLineAssessments = null }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { SafeLineRole = "comparison" }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { AnchorEvidenceOrdinal = null }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { AnchorEvidenceOrdinal = AgentLimits.EvidencePerFinding }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { SafeLineAssessments = [new(AgentLimits.EvidencePerFinding,
            "accusation", "confirmed_other_issue")] }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { SeverityAssessment = null }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { AnchorAssessment = "unresolved" }, 1, 0, rubric));
        Assert.False(R6ProspectiveQualityGate.ValidFindingShape(other with
        { SafeLineAssessments = [new(0, "accusation", "confirmed_other_issue"),
            new(2, "accusation", "protected_property_accusation")] }, 1, 0, rubric));
        var unsafeFinding = other with
        {
            SafeLineAssessments = [new(0, "accusation", "protected_property_accusation")],
            Reason = "safe_line_accusation",
        };
        Assert.True(R6ProspectiveQualityGate.ValidFindingShape(unsafeFinding, 1, 0,
            rubric));
    }

    [Fact]
    public async Task OmittedSelectorKeepsOldPlanAndSummaryShape()
    {
        using var plan = new PlanFile(document => document.Remove("rubric"));
        var admitted = LivePlanAdmission.Load(plan.Path, false, CancellationToken.None);
        Assert.Null(admitted.Rubric);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false,
            new LiveOptions { WriteLine = lines.Add }, CancellationToken.None);
        Assert.Null(result.Summary.ProspectiveQualityCandidate);
        Assert.DoesNotContain("prospective_", lines[^1], StringComparison.Ordinal);
        Assert.DoesNotContain("\"rubric\"", Encoding.UTF8.GetString(
            UsageJournalJson.Write(result.Journal)), StringComparison.Ordinal);
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
    }

    [Fact]
    public async Task SelectedKeylessRunProducesClosedReadbackAndCannotClaimLiveCredit()
    {
        using var plan = new PlanFile();
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false,
            new LiveOptions { WriteLine = lines.Add }, CancellationToken.None);

        Assert.Equal(5, result.Attempted);
        Assert.Equal(5, result.Completed);
        Assert.Equal(7, lines.Count);
        Assert.Equal("not_evaluable", result.Summary.ProspectiveQualityCandidate?.Status);
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var read = R6ProspectiveReportReader.Read(bytes);
        Assert.Equal("not_evaluable", read.Status);
        Assert.Equal("keyless_run", read.Reason);
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")).Status);
        Assert.DoesNotContain("reasoning", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);

        var summary = JsonNode.Parse(lines[^1])!.AsObject();
        summary["prospective_quality_candidate"]!["status"] = "candidate_pass";
        lines[^1] = summary.ToJsonString();
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
    }

    [Fact]
    public async Task V2KeylessReviewBindsClosedReceiptsAndPreservesPrivateGroupContext()
    {
        using var plan = new PlanFile(v2: true);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true, v2: true);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, result.Completed);
        Assert.Equal("adjudicated", result.Summary.AdjudicationStatus);
        Assert.Equal(R6ProspectiveRubric.V2Id, result.Summary.ProspectiveRubric?.Id);
        Assert.Equal("not_evaluable",
            result.Summary.ProspectiveQualityCandidate?.CostKnowledgeStatus);
        Assert.True(input.PriorPacketsVisible);
        Assert.Contains("reuse one group id for the same cause", prompts.ToString(),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(input.Root));
        foreach (var receipt in result.Summary.ProspectiveCaseReceipts!.Value)
        {
            Assert.Equal(R6ProspectiveRubric.V2Id, receipt.RubricId);
            foreach (var finding in receipt.Findings)
            {
                Assert.NotNull(finding.SafeLineAssessments);
                Assert.NotNull(finding.AnchorEvidenceOrdinal);
            }
        }
        var report = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(report).Status);
        Assert.Equal("keyless_run", R6ProspectiveReportReader.Read(report).Reason);
        foreach (var canary in PrivateCanaries)
            Assert.DoesNotContain(canary, Encoding.UTF8.GetString(report),
                StringComparison.Ordinal);
    }

    [Fact]
    public async Task V3KeylessFiveCaseControlBindsNewRubricWithoutLiveCredit()
    {
        using var plan = new PlanFile(v3: true);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true, v2: true,
            v3: true);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, result.Completed);
        Assert.Equal("adjudicated", result.Summary.AdjudicationStatus);
        Assert.Equal(R6ProspectiveRubric.V3Id, result.Summary.ProspectiveRubric?.Id);
        var receipts = Assert.IsType<ImmutableArray<R6ProspectiveCaseReceipt>>(
            result.Summary.ProspectiveCaseReceipts);
        Assert.All(receipts,
            receipt => Assert.Equal(R6ProspectiveRubric.V3Id, receipt.RubricId));
        var report = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        var readback = R6ProspectiveReportReader.Read(report);
        Assert.Equal("not_evaluable", readback.Status);
        Assert.Equal("keyless_run", readback.Reason);
        var receipt = receipts[2];
        Assert.Equal("undetermined", receipt.Attribution?.Confidence);
        Assert.Equal("receipt_shape_invalid", R6ProspectiveReportReader.Read(WriteReport(
            result.Summary with
            {
                ProspectiveCaseReceipts = receipts.SetItem(
                    2, receipt with { Attribution = null }),
            }, result.Outcomes)).Reason);
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public void V3AttributionRecordsMixedOrUncertainCauseWithoutGuessing()
    {
        Assert.True(R6ProspectiveAttribution.Undetermined.Valid(3));
        var mixed = new R6ProspectiveAttribution(
            ["harness", "test_contract"], "probable", "returned_observation", [0, 2]);
        Assert.True(mixed.Valid(3));
        Assert.True(new R6ProspectiveAttribution(["model_behavior"], "confirmed",
            "independent_review", []).Valid(0));
        Assert.True(new R6ProspectiveAttribution(["provider_transport"], "probable",
            "provider_receipt", []).Valid(0));
        Assert.False((mixed with { Causes = ["test_contract", "harness"] }).Valid(3));
        Assert.False((mixed with { ObservationOrdinals = [2, 0] }).Valid(3));
        Assert.False((mixed with { ObservationOrdinals = [0, 3] }).Valid(3));
        Assert.False((mixed with { Basis = "insufficient" }).Valid(3));
        Assert.False((mixed with { Causes = [] }).Valid(3));
        Assert.False((R6ProspectiveAttribution.Undetermined with
        { Confidence = "confirmed" }).Valid(3));
    }

    [Fact]
    public async Task V3ReviewerMustExplicitlyChooseAttributionEvenWhenUndetermined()
    {
        using var plan = new PlanFile(v3: true);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true, v2: true);
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("review_incomplete", result.Summary.AdjudicationStatus);
        Assert.Equal(0, result.Summary.AiAdjudicatedCases);
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public async Task V3SyntheticLiveCandidateAndStrictReadbackBindAttribution()
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile(v3: true);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, v3: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = new FakeSecret("APR317_SYNTHETIC_ONLY"),
            TransportFactory = new FakeFactory(_ =>
                new ReplayTransport(runs[next++].Script, ReplayFault.None)),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("candidate_pass", result.Summary.ProspectiveQualityCandidate?.Status);
        Assert.Equal(3, result.Summary.ProspectiveQualityCandidate?.ExpectedCredits);
        var report = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        Assert.Equal("candidate_pass", R6ProspectiveReportReader.Read(report).Status);
        var receipts = Assert.IsType<ImmutableArray<R6ProspectiveCaseReceipt>>(
            result.Summary.ProspectiveCaseReceipts);
        var receipt = receipts[2];
        Assert.Equal("receipt_shape_invalid", R6ProspectiveReportReader.Read(WriteReport(
            result.Summary with
            {
                ProspectiveCaseReceipts = receipts.SetItem(
                    2, receipt with
                    {
                        Attribution = new(["model_behavior"], "confirmed",
                            "returned_observation", [int.MaxValue]),
                    }),
            }, result.Outcomes)).Reason);
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public async Task V3ReviewsChangedFileFromCompleteDiffWhileV2KeepsOldReadRule()
    {
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var ts = runs.Single(run => run.Input.CaseId == "ts-defect");
        var discovery = WithTsChangedDiff(ts.Script, null);
        using var v3Plan = new PlanFile(v3: true);
        using var firstPrompts = new StringWriter();
        using var firstInput = new PacketReviewInput(firstPrompts, approveExpected: true,
            v2: true, v3: true);
        await LiveRunner.RunAsync(v3Plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "ts-defect"
                ? discovery : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(firstInput, firstPrompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        var packet = JsonNode.Parse(firstInput.PacketsByCase["ts-defect"])!.AsObject();
        var diffId = packet["returned_observations"]!.AsArray().Single(item =>
            item!["tool"]!.GetValue<string>() == AgentToolRegistry.ReadDiffName &&
            item["returned_lines"]!.AsObject().ContainsKey("src/client.ts"))!
            ["observation_id"]!.GetValue<string>();
        var reviewed = WithTsChangedDiff(ts.Script, diffId);
        using var v3Prompts = new StringWriter();
        using var v3Input = new PacketReviewInput(v3Prompts, approveExpected: true,
            v2: true, v3: true);
        var v3 = await LiveRunner.RunAsync(v3Plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "ts-defect"
                ? reviewed : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(v3Input, v3Prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(AssertionStatus.Passed, v3.Outcomes[2].EvidenceStatus);
        var v3Receipts = Assert.IsType<ImmutableArray<R6ProspectiveCaseReceipt>>(
            v3.Summary.ProspectiveCaseReceipts);
        Assert.Equal("assessed", v3Receipts[2].Status);
        Assert.Equal("expected", Assert.Single(v3Receipts[2]
            .Findings).Verdict);

        using var v2Plan = new PlanFile(v2: true);
        var v2 = await LiveRunner.RunAsync(v2Plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "ts-defect"
                ? reviewed : run.Script, ReplayFault.None),
        }, CancellationToken.None);
        Assert.Equal(AssertionStatus.Failed, v2.Outcomes[2].EvidenceStatus);
        Assert.Equal(EvaluationCode.RequiredObservationMissing, v2.Outcomes[2].Code);
    }

    [Fact]
    public async Task V2ReviewsActualGroundedSafeLineSuggestionWithoutReclassifyingAccusations()
    {
        using var plan = new PlanFile(v2: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var safeRun = runs.Single(run => run.Input.CaseId == "cs-safe");
        var discoveryScript = WithSafeTrimDiscovery(safeRun.Script);
        using var discoveryPrompts = new StringWriter();
        using var discoveryInput = new PacketReviewInput(discoveryPrompts,
            approveExpected: true, v2: true);
        await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "cs-safe"
                ? discoveryScript : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(discoveryInput,
                discoveryPrompts, R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        var packet = JsonNode.Parse(discoveryInput.PacketsByCase["cs-safe"])!.AsObject();
        var observations = packet["returned_observations"]!.AsArray();
        string Observation(string tool, string path) => observations.Single(item =>
            item!["tool"]!.GetValue<string>() == tool &&
            item["returned_lines"]!.AsObject().ContainsKey(path))!["observation_id"]!
            .GetValue<string>();
        var safeFile = Observation(AgentToolRegistry.ReadFileName, "src/SafeCaller.cs");
        var lookup = Observation(AgentToolRegistry.ReadFileName, "src/Lookup.cs");
        var safeDiff = Observation(AgentToolRegistry.ReadDiffName, "src/SafeCaller.cs");
        var reviewedScript = WithSafeTrimFinding(discoveryScript, safeFile, lookup, safeDiff);

        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, approveSafeTrim: true);
        var lines = new List<string>();
        var keyless = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "cs-safe"
                ? reviewedScript : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, keyless.Completed);
        Assert.Equal("adjudicated", keyless.Summary.AdjudicationStatus);
        var safeFinding = Assert.Single(keyless.Summary.ProspectiveCaseReceipts!.Value[1]
            .Findings);
        Assert.Equal("true_off_focus", safeFinding.Verdict);
        Assert.Equal("accusation", safeFinding.SafeLineRole);
        Assert.Equal("confirmed_off_focus", safeFinding.Reason);
        Assert.Equal(2, safeFinding.SafeLineAssessments?.Length);
        Assert.Equal("reviewed_other", safeFinding.CitationClass);
        Assert.Equal(EvaluationCode.ProhibitedFinding, keyless.Outcomes[1].Code);
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.False(Directory.Exists(input.Root));

        if (!IsCleanSource()) return;
        using var livePrompts = new StringWriter();
        using var liveInput = new PacketReviewInput(livePrompts, approveExpected: true,
            v2: true, approveSafeTrim: true);
        var liveLines = new List<string>();
        var next = 0;
        var live = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = liveLines.Add,
            SecretSource = new FakeSecret("APR317_SYNTHETIC_ONLY"),
            TransportFactory = new FakeFactory(_ =>
            {
                var run = runs[next++];
                return new ReplayTransport(run.Input.CaseId == "cs-safe"
                    ? reviewedScript : run.Script, ReplayFault.None);
            }),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(liveInput,
                livePrompts, R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("candidate_pass", live.Summary.ProspectiveQualityCandidate?.Status);
        Assert.Equal(3, live.Summary.ProspectiveQualityCandidate?.ExpectedCredits);
        Assert.Equal(1, live.Summary.ProspectiveQualityCandidate?.TrueOffFocusFindings);
        Assert.Equal("failed", live.Summary.ProspectiveQualityCandidate?.LegacyStructuralStatus);
        Assert.Equal("passed", live.Summary.ProspectiveQualityCandidate?.SafetyStatus);
        Assert.Equal("known", live.Summary.ProspectiveQualityCandidate?.CostKnowledgeStatus);
        Assert.Equal("candidate_pass", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', liveLines) + "\n")).Status);
        var receipts = live.Summary.ProspectiveCaseReceipts!.Value;
        var safeReceipt = receipts[1];
        var forged = live.Summary with
        {
            ProspectiveCaseReceipts = receipts.SetItem(1, safeReceipt with
            {
                Findings = safeReceipt.Findings.SetItem(0,
                    safeReceipt.Findings[0] with { SafeLineAssessments = null }),
            }),
        };
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(
            WriteReport(forged, live.Outcomes)).Status);
        var impossibleOrdinal = live.Summary with
        {
            ProspectiveCaseReceipts = receipts.SetItem(1, safeReceipt with
            {
                Findings = safeReceipt.Findings.SetItem(0,
                    safeReceipt.Findings[0] with
                    { AnchorEvidenceOrdinal = AgentLimits.EvidencePerFinding }),
            }),
        };
        Assert.Equal("receipt_shape_invalid", R6ProspectiveReportReader.Read(
            WriteReport(impossibleOrdinal, live.Outcomes)).Reason);
        var impossibleSafeUse = live.Summary with
        {
            ProspectiveCaseReceipts = receipts.SetItem(1, safeReceipt with
            {
                Findings = safeReceipt.Findings.SetItem(0,
                    safeReceipt.Findings[0] with
                    {
                        SafeLineAssessments = safeReceipt.Findings[0]
                            .SafeLineAssessments!.Value.SetItem(1,
                                new(AgentLimits.EvidencePerFinding, "accusation",
                                    "confirmed_other_issue")),
                    }),
            }),
        };
        Assert.Equal("receipt_shape_invalid", R6ProspectiveReportReader.Read(
            WriteReport(impossibleSafeUse, live.Outcomes)).Reason);
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(WriteReport(
            live.Summary with { ProspectiveQualityCandidate =
                live.Summary.ProspectiveQualityCandidate! with
                { CostKnowledgeStatus = "unknown" } }, live.Outcomes)).Status);
        Assert.False(Directory.Exists(liveInput.Root));
    }

    [Theory]
    [InlineData("stop\n")]
    [InlineData("")]
    public async Task V2StoppedReviewIsExplicitlyIncomplete(string command)
    {
        using var plan = new PlanFile(v2: true);
        using var prompts = new StringWriter();
        using var input = new StringReader(command);
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("review_incomplete", result.Summary.AdjudicationStatus);
        Assert.Equal(0, result.Summary.AiAdjudicatedCases);
    }

    [Fact]
    public async Task V2ReviewsGroundedTypeScriptDefaultSuggestionThatPreservesZero()
    {
        using var plan = new PlanFile(v2: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var safeRun = runs.Single(run => run.Input.CaseId == "ts-safe");
        using var discoveryPrompts = new StringWriter();
        using var discoveryInput = new PacketReviewInput(discoveryPrompts,
            approveExpected: true, v2: true);
        await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            ProspectiveReviewer = new R6ProspectiveAdjudicator(discoveryInput,
                discoveryPrompts, R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        var packet = JsonNode.Parse(discoveryInput.PacketsByCase["ts-safe"])!.AsObject();
        var safeRead = packet["returned_observations"]!.AsArray().Single(item =>
            item!["tool"]!.GetValue<string>() == AgentToolRegistry.ReadFileName &&
            item["returned_lines"]!.AsObject().ContainsKey("src/safe-client.ts"))!
            ["observation_id"]!.GetValue<string>();
        var reviewedScript = WithTsDefaultFinding(safeRun.Script, safeRead);

        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, approveTsDefault: true);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "ts-safe"
                ? reviewedScript : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, result.Completed);
        Assert.Equal("adjudicated", result.Summary.AdjudicationStatus);
        var finding = Assert.Single(result.Summary.ProspectiveCaseReceipts!.Value[3].Findings);
        Assert.Equal("true_off_focus", finding.Verdict);
        Assert.Equal("reviewed_other", finding.CitationClass);
        Assert.Equal("accusation", finding.SafeLineRole);
        Assert.Equal("confirmed_off_focus", finding.Reason);
        Assert.Equal(EvaluationCode.ProhibitedFinding, result.Outcomes[3].Code);
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.False(Directory.Exists(input.Root));
    }

    [Theory]
    [InlineData(false, "within_case_duplicate")]
    [InlineData(true, "all_gates_passed")]
    public async Task V2MechanicalDuplicateRoutesToReviewedCausalGroups(
        bool distinctCause, string expectedReason)
    {
        using var plan = new PlanFile(v2: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var duplicate = WithMechanicalDuplicate(runs.Single(run =>
            run.Input.CaseId == "cs-defect").Script);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, approveDistinctDuplicate: distinctCause);
        var keyless = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "cs-defect"
                ? duplicate : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(EvaluationCode.DuplicateObservation, keyless.Outcomes[0].Code);
        Assert.Equal(1, keyless.Outcomes[0].DuplicateObservations);
        Assert.Equal("adjudicated", keyless.Summary.AdjudicationStatus);
        Assert.False(Directory.Exists(input.Root));

        if (!IsCleanSource()) return;
        var next = 0;
        using var livePrompts = new StringWriter();
        using var liveInput = new PacketReviewInput(livePrompts, approveExpected: true,
            v2: true, approveDistinctDuplicate: distinctCause);
        var lines = new List<string>();
        var live = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = new FakeSecret("APR317_DUPLICATE_SYNTHETIC_ONLY"),
            TransportFactory = new FakeFactory(_ =>
            {
                var run = runs[next++];
                return new ReplayTransport(run.Input.CaseId == "cs-defect"
                    ? duplicate : run.Script, ReplayFault.None);
            }),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(liveInput,
                livePrompts, R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(expectedReason, live.Summary.ProspectiveQualityCandidate?.Reason);
        Assert.Equal(expectedReason, R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Reason);
        Assert.False(Directory.Exists(liveInput.Root));
    }

    [Fact]
    public async Task V3ConfirmedDuplicateIsRecordedAsUsabilityCost()
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile(v3: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var duplicate = WithMechanicalDuplicate(runs.Single(run =>
            run.Input.CaseId == "cs-defect").Script);
        var next = 0;
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, v3: true);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = new FakeSecret("APR317_DUPLICATE_SYNTHETIC_ONLY"),
            TransportFactory = new FakeFactory(_ =>
            {
                var run = runs[next++];
                return new ReplayTransport(run.Input.CaseId == "cs-defect"
                    ? duplicate : run.Script, ReplayFault.None);
            }),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(EvaluationCode.DuplicateObservation, result.Outcomes[0].Code);
        Assert.Equal("candidate_pass", result.Summary.ProspectiveQualityCandidate?.Status);
        Assert.Equal("cost_recorded", result.Summary.ProspectiveQualityCandidate?.UsabilityStatus);
        Assert.Equal("candidate_pass", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.False(Directory.Exists(input.Root));
    }

    [Theory]
    [InlineData("v2_retry", "adjudicated", 5)]
    [InlineData("v2_null_row", "adjudicated", 5)]
    [InlineData("v2_retry_fail", "review_incomplete", 0)]
    public async Task V2CorrectionIsBoundedToTheSamePrivateCase(
        string corruption, string expectedStatus, int acceptedCases)
    {
        using var plan = new PlanFile(v2: true);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, corruption: corruption);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, result.Completed);
        Assert.Equal(expectedStatus, result.Summary.AdjudicationStatus);
        Assert.Equal(acceptedCases, result.Summary.AiAdjudicatedCases);
        Assert.Equal(2, input.PromptCaseIndices.Count(index => index == 0));
        Assert.False(Directory.Exists(input.Root));
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
    }

    [Fact]
    public async Task V2BoundSemanticRejectionCannotBeRewrittenOnRetry()
    {
        using var plan = new PlanFile(v2: true);
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            v2: true, corruption: "v2_semantic_rejected");
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, result.Completed);
        Assert.Equal("review_incomplete", result.Summary.AdjudicationStatus);
        Assert.Single(input.PromptCaseIndices);
        Assert.Equal(0, input.PromptCaseIndices[0]);
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public async Task SelectedPaidRunRequiresTrustedReviewerBeforeCredentialAccess()
    {
        // Execute admission requires the committed source marker. The clean
        // corrected head and CI exercise this branch; dirty edits cannot.
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        var secret = new FakeSecret("APR311_UNUSED_KEY_CANARY");
        var created = 0;
        var error = await Assert.ThrowsAsync<LivePlanRejected>(() => LiveRunner.RunAsync(
            plan.Path, true, new LiveOptions
            {
                SecretSource = secret,
                TransportFactory = new FakeFactory(_ =>
                {
                    created++;
                    throw new InvalidOperationException("transport_must_not_be_created");
                }),
            }, CancellationToken.None));
        Assert.Equal(LiveAdmissionCode.InvalidPlan, error.Code);
        Assert.Equal(0, secret.Taken);
        Assert.Equal(0, created);
    }

    [Fact]
    public async Task ProspectiveReviewerUsesPrivatePacketAndEmitsFiveExecutionBoundReceipts()
    {
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(5, result.Completed);
        Assert.Equal("adjudicated", result.Summary.AdjudicationStatus);
        Assert.Equal("cleaned", result.Summary.Cleanup);
        Assert.Equal(5, result.Summary.AiAdjudicatedCases);
        Assert.Equal(5, result.Summary.ProspectiveCaseReceipts?.Length);
        Assert.Equal(5, result.Summary.ProspectiveRecoveryReceipts?.Length);
        Assert.False(Directory.Exists(input.Root));
        Assert.All(result.Summary.ProspectiveCaseReceipts!.Value,
            row => Assert.Equal("assessed", row.Status));
        Assert.Contains("returned_observations", input.LastPacket);
        var publicBytes = Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n");
        Assert.DoesNotContain("returned_observations", Encoding.UTF8.GetString(publicBytes));
        using (var packet = JsonDocument.Parse(input.LastPacket))
        {
            var message = packet.RootElement.GetProperty("findings")[0]
                .GetProperty("message").GetString()!;
            Assert.DoesNotContain(message, Encoding.UTF8.GetString(publicBytes),
                StringComparison.Ordinal);
        }
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(publicBytes).Status);
    }

    [Fact]
    public async Task SelectedCaseKeepsItsReadsWhileAuthoredOffFocusUsesReturnedDiff()
    {
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            approveOffFocusDiff: true);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            DryRunTransport = run => new ReplayTransport(run.Input.CaseId == "ts-safe"
                ? WithOffFocusUploadDiff(run.Script) : run.Script, ReplayFault.None),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);

        Assert.Equal(5, result.Completed);
        Assert.Equal("adjudicated", result.Summary.AdjudicationStatus);
        Assert.Equal("cleaned", result.Summary.Cleanup);
        var receipt = Assert.IsType<ImmutableArray<R6ProspectiveCaseReceipt>>(
            result.Summary.ProspectiveCaseReceipts)[3];
        var finding = Assert.Single(receipt.Findings);
        Assert.Equal("true_off_focus", finding.Verdict);
        Assert.Equal("repository-token-log", finding.DefectGroupId);
        Assert.Equal("bounded_context", finding.CitationClass);
        Assert.Equal("confirmed_off_focus", finding.Reason);
        Assert.Equal("none", finding.SafeLineRole);

        using var packet = JsonDocument.Parse(Assert.IsType<string>(input.OffFocusPacket));
        var returned = packet.RootElement.GetProperty("returned_observations")
            .EnumerateArray().ToArray();
        Assert.Contains(returned, observation => observation.GetProperty("tool").GetString() ==
            AgentToolRegistry.ReadDiffName && observation.GetProperty("returned_lines")
                .TryGetProperty("src/Upload.cs", out _));
        Assert.DoesNotContain(returned, observation => observation.GetProperty("tool").GetString() ==
            AgentToolRegistry.ReadFileName && observation.GetProperty("returned_lines")
                .TryGetProperty("src/Upload.cs", out _));
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public async Task CleanSyntheticLiveRunRecomputesAuthoredOffFocusDiffCredit()
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            approveOffFocusDiff: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = new FakeSecret("APR314_SYNTHETIC_CREDENTIAL_CANARY"),
            TransportFactory = new FakeFactory(_ =>
            {
                var run = runs[next++];
                return new ReplayTransport(run.Input.CaseId == "ts-safe"
                    ? WithOffFocusUploadDiff(run.Script) : run.Script, ReplayFault.None);
            }),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);

        Assert.Equal(5, result.Completed);
        var gate = Assert.IsType<R6ProspectiveGateResult>(
            result.Summary.ProspectiveQualityCandidate);
        Assert.Equal("candidate_pass", gate.Status);
        Assert.Equal(3, gate.ExpectedCredits);
        Assert.Equal(1, gate.TrueOffFocusFindings);
        Assert.Equal(1, gate.CrossCaseRepeats);
        Assert.Equal("passed", gate.LegacyStructuralStatus);
        Assert.Equal("passed", gate.ProspectiveSemanticStatus);
        Assert.Equal("passed", gate.SafetyStatus);
        Assert.Equal("cost_recorded", gate.UsabilityStatus);
        var read = R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n"));
        Assert.Equal("candidate_pass", read.Status);
        Assert.Equal(gate, read.Recomputed);
        Assert.DoesNotContain("APR314_SYNTHETIC_CREDENTIAL_CANARY", string.Join('\n', lines));
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public async Task OffFocusDiffCannotReplaceScheduledCaseRequiredRead()
    {
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            approveOffFocusDiff: true);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            DryRunTransport = run =>
            {
                if (run.Input.CaseId != "ts-safe")
                    return new ReplayTransport(run.Script, ReplayFault.None);
                var script = WithOffFocusUploadDiff(run.Script);
                return new ReplayTransport(script with
                {
                    Turns = script.Turns.Select(turn => turn with
                    {
                        ToolCalls = turn.ToolCalls.Select(call =>
                            call.Name == AgentToolRegistry.ReadFileName &&
                            call.ArgumentsJson.Contains("src/safe-client.ts", StringComparison.Ordinal)
                                ? call with
                                {
                                    Name = AgentToolRegistry.ReadDiffName,
                                    ArgumentsJson = "{\"path\":\"src/safe-client.ts\"}",
                                }
                                : call).ToImmutableArray(),
                    }).ToImmutableArray(),
                }, ReplayFault.None);
            },
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);

        Assert.Equal(5, result.Completed);
        Assert.Equal("input_invalid", result.Summary.AdjudicationStatus);
        Assert.Equal("pending", result.Summary.ProspectiveCaseReceipts!.Value[3].Status);
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.False(Directory.Exists(input.Root));
    }

    [Fact]
    public void RunLocalDefectIdsAreOpaqueInPublicReceipts()
    {
        const string privateCanary = "run-apr311privategroupcanary73829f43";
        var emitted = R6ProspectiveAssessment.PublicGroupId("run", privateCanary);
        Assert.NotNull(emitted);
        Assert.StartsWith("run-", emitted);
        Assert.DoesNotContain(privateCanary, emitted, StringComparison.Ordinal);
        Assert.Equal(emitted, R6ProspectiveAssessment.PublicGroupId("run", privateCanary));
        Assert.NotEqual(emitted, R6ProspectiveAssessment.PublicGroupId("run",
            "run-apr311differentgroupcanary73829f43"));
    }

    [Fact]
    public void RecoveryErrorCannotBecomeAdmittedObservationOrFindingEvidence()
    {
        var error = new AgentToolErrorEvent("rejected", AgentToolRegistry.ListFilesName,
            "error-hash", []);
        ImmutableArray<AgentLogicalEvent> events =
        [
            error,
            new AgentToolResultEvent("later", AgentToolRegistry.ReadFileName,
                "ordinary-observation", "ordinary-hash", []),
        ];
        Assert.True(R6ProspectiveRecoveryAudit.ErrorExcludedFromEvidence(events,
            ["ordinary-observation"], ["ordinary-observation"],
            ["ordinary-observation"], error));
        Assert.False(R6ProspectiveRecoveryAudit.ErrorExcludedFromEvidence(events,
            ["error-observation"], ["ordinary-observation"],
            ["ordinary-observation"], error));
        Assert.False(R6ProspectiveRecoveryAudit.ErrorExcludedFromEvidence(events,
            ["ordinary-observation"], ["ordinary-observation"],
            ["error-observation"], error));
        Assert.False(R6ProspectiveRecoveryAudit.ErrorExcludedFromEvidence(
            [error, new AgentToolResultEvent("later", AgentToolRegistry.ReadFileName,
                "ordinary-observation", "error-hash", [])],
            ["ordinary-observation"], ["ordinary-observation"],
            ["ordinary-observation"], error));
    }

    [Fact]
    public void MalformedRejectedEventGetsBoundedBlockedReceiptBeforeLegacyProjection()
    {
        var testCase = Assert.IsType<AdmittedReplayFixture>(
            ReplayAdmission.Load(Corpus).Fixture).Runs[0].Expected;
        ImmutableArray<AgentLogicalEvent> events =
        [
            new AgentRecoveryToolCallEvent("rejected", AgentToolRegistry.ListFilesName,
                new string('a', 64), [], true),
            new AgentToolErrorEvent("different-call", AgentToolRegistry.ListFilesName,
                new string('b', 64), []),
        ];
        var outcome = AgentRunOutcome.Failure("tool_arguments_invalid", 1, 0, events);
        Assert.Throws<InvalidOperationException>(() =>
            LiveRecoveryDiagnostic.Capture(0, outcome));
        var capture = R6ProspectiveRecoveryAudit.Capture(0, testCase, outcome, null);
        Assert.Empty(capture.CanonicalDiagnostics);
        Assert.Equal(1, capture.Receipt.ObservedCount);
        Assert.Equal("blocked", capture.Receipt.Status);
        Assert.Equal("error_pair_invalid", capture.Receipt.Reason);
        Assert.True(R6ProspectiveRecoveryAudit.ValidPublicBinding(capture.Receipt, 0));
        Assert.False(R6ProspectiveRecoveryAudit.ValidPublicBinding(
            capture.Receipt with { Status = "qualified" }, 0));
    }

    [Theory]
    [InlineData("origin")]
    [InlineData("execution_sha256")]
    [InlineData("rubric_sha256")]
    public async Task StaleOrSelfAssertedReviewAuthorityIsRejected(string corruption)
    {
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, corruption: corruption);
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add,
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("input_invalid", result.Summary.AdjudicationStatus);
        var receipts = Assert.IsType<ImmutableArray<R6ProspectiveCaseReceipt>>(
            result.Summary.ProspectiveCaseReceipts);
        Assert.Equal(5, receipts.Length);
        Assert.All(receipts,
            receipt => Assert.Equal("pending", receipt.Status));
        Assert.Equal("cleaned", result.Summary.Cleanup);
        Assert.False(Directory.Exists(input.Root));
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
    }

    [Fact]
    public async Task CleanKeylessTransportProvesLiveGateWithoutProviderCalls()
    {
        // The evaluator embeds a dirty-source marker at build time. This case
        // runs on a committed checkout in CI and in the exact-head local gate.
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var secret = new FakeSecret("APR311_SYNTHETIC_CREDENTIAL_CANARY");
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = secret,
            TransportFactory = new FakeFactory(_ => new ReplayTransport(runs[next++].Script,
                ReplayFault.None)),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal(1, secret.Taken);
        Assert.Equal(5, result.Completed);
        Assert.Equal("candidate_pass", result.Summary.ProspectiveQualityCandidate?.Status);
        var baselineGate = Assert.IsType<R6ProspectiveGateResult>(
            result.Summary.ProspectiveQualityCandidate);
        Assert.Equal(3, baselineGate.ExpectedCredits);
        Assert.Equal("passed", baselineGate.LegacyStructuralStatus);
        Assert.Equal("passed", baselineGate.ProspectiveSemanticStatus);
        Assert.Equal("passed", baselineGate.SafetyStatus);
        Assert.Equal("clean", baselineGate.UsabilityStatus);
        Assert.Equal(5, result.Summary.ProspectiveCaseReceipts?.Length);
        Assert.Equal("candidate_pass", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.DoesNotContain("APR311_SYNTHETIC_CREDENTIAL_CANARY", string.Join('\n', lines));

        void Check(LiveRunSummary summary, System.Collections.Immutable.ImmutableArray<EvaluationOutcome> rows,
            string status, string reason)
        {
            var gate = Assert.IsType<R6ProspectiveGateResult>(
                R6ProspectiveQualityGate.Evaluate(summary, rows));
            Assert.Equal(status, gate.Status);
            Assert.Equal(reason, gate.Reason);
            var read = R6ProspectiveReportReader.Read(WriteReport(summary with
            { ProspectiveQualityCandidate = gate }, rows));
            Assert.Equal(status, read.Status);
            Assert.Equal(reason, read.Reason);
        }

        void RejectBinding(LiveRunSummary summary,
            ImmutableArray<EvaluationOutcome> rows)
        {
            Assert.Equal("plan_binding_invalid",
                R6ProspectiveQualityGate.Evaluate(summary, rows)?.Reason);
            var read = R6ProspectiveReportReader.Read(WriteReport(summary, rows));
            Assert.Equal("rejected", read.Status);
            Assert.Equal("plan_binding_invalid", read.Reason);
        }

        var baseline = result.Summary;
        var outcomes = result.Outcomes;
        using var secondPrompts = new StringWriter();
        using var secondInput = new PacketReviewInput(secondPrompts, approveExpected: true);
        var secondIndex = 0;
        var second = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = _ => { },
            SecretSource = new FakeSecret("APR311_SECOND_SYNTHETIC_CREDENTIAL"),
            TransportFactory = new FakeFactory(_ => new ReplayTransport(
                runs[secondIndex++].Script, ReplayFault.None)),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(secondInput,
                secondPrompts, R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("candidate_pass", second.Summary.ProspectiveQualityCandidate?.Status);
        var firstJournal = Assert.IsType<UsageJournalDocument>(baseline.UsageJournal);
        var secondJournal = Assert.IsType<UsageJournalDocument>(second.Summary.UsageJournal);
        Assert.Equal(firstJournal.Provenance.PlanSha256,
            secondJournal.Provenance.PlanSha256);
        Assert.NotEqual(firstJournal.Provenance.CampaignId,
            secondJournal.Provenance.CampaignId);
        Assert.NotEqual(firstJournal.Attempts[0].EvaluationAttemptSha256,
            secondJournal.Attempts[0].EvaluationAttemptSha256);
        Assert.NotNull(UsageJournal.Admit(secondJournal));
        var mixedSummary = baseline with { UsageJournal = secondJournal };
        RejectBinding(mixedSummary, outcomes);

        var knownCallIndex = Enumerable.Range(0, firstJournal.Calls.Length)
            .First(index => firstJournal.Calls[index].UsageStatus == "known");
        var knownCall = firstJournal.Calls[knownCallIndex];
        var usage = Assert.IsType<UsageJournalUsage>(knownCall.Usage);
        var changedCache = usage.Cache is { } cache ? cache with
        { UncachedInputTokens = cache.UncachedInputTokens + 1 } : null;
        var changedCalls = firstJournal.Calls.SetItem(knownCallIndex, knownCall with
        { Usage = usage with { InputTokens = usage.InputTokens + 1,
            CombinedTokens = usage.CombinedTokens + 1, Cache = changedCache } });
        var changedJournal = firstJournal with
        { Calls = changedCalls, Totals = UsageJournal.Totals(firstJournal.Attempts,
            changedCalls) };
        Assert.NotNull(UsageJournal.Admit(changedJournal));
        var inconsistentUsage = baseline with { UsageJournal = changedJournal };
        RejectBinding(inconsistentUsage, outcomes);
        RejectBinding(baseline with
        { KnownInputTokens = baseline.KnownInputTokens + 1 }, outcomes);
        RejectBinding(baseline with
        { ReservedInputTokens = baseline.ReservedInputTokens + 1 }, outcomes);
        RejectBinding(baseline with
        { ActualProviderCalls = baseline.ActualProviderCalls + 1 }, outcomes);

        RejectBinding(baseline with { UsageUnknownCalls = 1 }, outcomes);
        Check(baseline with { AccountingViolation = true }, outcomes,
            "blocked", "population_ineligible");
        var dirtySource = baseline with { SourceClean = false };
        var dirtyGate = R6ProspectiveQualityGate.Evaluate(dirtySource, outcomes)!;
        Assert.Equal("plan_binding_invalid", dirtyGate.Reason);
        var dirtyRead = R6ProspectiveReportReader.Read(WriteReport(dirtySource with
        { ProspectiveQualityCandidate = dirtyGate }, outcomes));
        Assert.Equal("rejected", dirtyRead.Status);
        Assert.Equal("plan_binding_invalid", dirtyRead.Reason);
        Check(baseline with { Cleanup = "cleanup_failed" }, outcomes,
            "blocked", "population_ineligible");
        RejectBinding(baseline with { Completed = 4, Failed = 1 }, outcomes);
        Check(baseline with { AgentDiagnostics = [new LiveAgentDiagnostic(4,
            "response_invalid", 1, 0)] }, outcomes,
            "blocked", "population_ineligible");
        var missingRecovery = baseline with { ProspectiveRecoveryReceipts = [] };
        var missingRecoveryGate = R6ProspectiveQualityGate.Evaluate(missingRecovery, outcomes)!;
        Assert.Equal("recovery_count_invalid", missingRecoveryGate.Reason);
        var missingRecoveryRead = R6ProspectiveReportReader.Read(WriteReport(missingRecovery with
        { ProspectiveQualityCandidate = missingRecoveryGate }, outcomes));
        Assert.Equal("rejected", missingRecoveryRead.Status);
        Assert.Equal("receipt_shape_invalid", missingRecoveryRead.Reason);

        var evidenceFailed = outcomes[4] with
        {
            EvidenceStatus = AssertionStatus.Failed,
            ScenarioStatus = AssertionStatus.NotEvaluated,
            ModelStatus = ModelObservationStatus.NotEvaluated,
            Code = EvaluationCode.RequiredObservationMissing,
            StructuralMatches = 0,
            StructurallyMissingDefects = 0,
            AdjudicatedTrue = 0,
            AdjudicatedFalse = 0,
            AdjudicatedDefects = 0,
            UnadjudicatedFindings = 0,
        };
        Check(baseline, outcomes.SetItem(4, evidenceFailed),
            "blocked", "case_binding_invalid");
        var agentFailed = outcomes[4] with
        {
            ExecutionSha256 = null,
            ExecutionStatus = EvaluationStatus.Failed,
            EvidenceStatus = AssertionStatus.NotEvaluated,
            ScenarioStatus = AssertionStatus.NotEvaluated,
            ModelStatus = ModelObservationStatus.NotEvaluated,
            Code = EvaluationCode.ExecutionFailed,
            FailureSource = EvaluationFailureSource.Agent,
            FailureKind = EvaluationFailureKind.MalformedOutput,
            FindingCount = 0,
            ToolObservationCount = 0,
            ExpectedDefects = 0,
            StructuralMatches = 0,
            StructurallyMissingDefects = 0,
            DuplicateObservations = 0,
            ProhibitedObservations = 0,
            AdjudicatedTrue = 0,
            AdjudicatedFalse = 0,
            AdjudicatedDefects = 0,
            UnadjudicatedFindings = 0,
        };
        var failedReceipts = Assert.IsType<ImmutableArray<R6ProspectiveCaseReceipt>>(
            baseline.ProspectiveCaseReceipts);
        var failedRecovery = Assert.IsType<ImmutableArray<R6ProspectiveRecoveryReceipt>>(
            baseline.ProspectiveRecoveryReceipts);
        RejectBinding(baseline with
        {
            ProspectiveCaseReceipts = failedReceipts.SetItem(4, failedReceipts[4] with
            { ExecutionSha256 = null, Origin = "none", Status = "pending",
                FindingRowCount = 0, Findings = [] }),
            ProspectiveRecoveryReceipts = failedRecovery.SetItem(4, failedRecovery[4] with
            { ExecutionSha256 = null, Status = "blocked", Reason = "capture_missing" }),
        }, outcomes.SetItem(4, agentFailed));

        var receipts = Assert.IsType<System.Collections.Immutable.ImmutableArray<R6ProspectiveCaseReceipt>>(
            baseline.ProspectiveCaseReceipts);
        var repoReceipt = receipts[4];
        var repoFinding = Assert.Single(repoReceipt.Findings);
        var unresolved = repoFinding with { Verdict = "unresolved", Reason = "review_pending",
            ExpectedDefectId = null, DefectGroupScope = null, DefectGroupId = null,
            CitationClass = "invalid" };
        var unresolvedReceipts = receipts.SetItem(4, repoReceipt with { Findings = [unresolved] });
        Check(baseline with { ProspectiveCaseReceipts = unresolvedReceipts }, outcomes,
            "blocked", "finding_ineligible");

        var falseFinding = repoFinding with { Verdict = "false_unsafe", Reason = "review_rejected",
            DefectGroupScope = null, DefectGroupId = null, ExpectedDefectId = null,
            CitationClass = "invalid" };
        var falseReceipts = receipts.SetItem(4, repoReceipt with { Findings = [falseFinding] });
        Check(baseline with { ProspectiveCaseReceipts = falseReceipts }, outcomes,
            "blocked", "finding_ineligible");

        var duplicateReceipts = receipts.SetItem(4, repoReceipt with
        { FindingRowCount = 2, Findings = [repoFinding, repoFinding with { FindingOrdinal = 1 }] });
        Check(baseline with { ProspectiveCaseReceipts = duplicateReceipts },
            outcomes.SetItem(4, outcomes[4] with { FindingCount = 2, UnadjudicatedFindings = 2 }),
            "blocked", "within_case_duplicate");

        var boundedLegacy = outcomes.SetItem(4, outcomes[4] with
        {
            Code = EvaluationCode.ExpectedFindingMissing,
            ScenarioStatus = AssertionStatus.Failed,
            StructuralMatches = 0,
            StructurallyMissingDefects = 1,
        });
        var boundedReceipts = receipts.SetItem(4, repoReceipt with
        { Findings = [repoFinding with { CitationClass = "bounded_context" }] });
        Check(baseline with { ProspectiveCaseReceipts = boundedReceipts }, boundedLegacy,
            "candidate_pass", "all_gates_passed");
        var boundedGate = R6ProspectiveQualityGate.Evaluate(
            baseline with { ProspectiveCaseReceipts = boundedReceipts }, boundedLegacy)!;
        Assert.Equal("failed", boundedGate.LegacyStructuralStatus);
        Assert.Equal("passed", boundedGate.ProspectiveSemanticStatus);

        var offFocus = new R6ProspectiveFindingReceipt(0, "true_off_focus", null,
            "authored", "repository-token-log", "exact", "none", "confirmed_off_focus");
        var repeatedReceipts = receipts.SetItem(1, receipts[1] with
        { FindingRowCount = 1, Findings = [offFocus] });
        var repeatedOutcomes = outcomes.SetItem(1, outcomes[1] with
        { FindingCount = 1, UnadjudicatedFindings = 1,
            ModelStatus = ModelObservationStatus.Unadjudicated });
        Check(baseline with { ProspectiveCaseReceipts = repeatedReceipts }, repeatedOutcomes,
            "candidate_pass", "all_gates_passed");
        var repeatedGate = R6ProspectiveQualityGate.Evaluate(
            baseline with { ProspectiveCaseReceipts = repeatedReceipts }, repeatedOutcomes)!;
        Assert.Equal(1, repeatedGate.CrossCaseRepeats);
        Assert.Equal("cost_recorded", repeatedGate.UsabilityStatus);
        Assert.Equal("passed", repeatedGate.SafetyStatus);

        var laterFalse = new R6ProspectiveFindingReceipt(0, "false_unsafe", null,
            null, null, "invalid", "none", "review_rejected");
        var blockedButMeasured = repeatedReceipts.SetItem(3, receipts[3] with
        { FindingRowCount = 1, Findings = [laterFalse] });
        var blockedButMeasuredOutcomes = repeatedOutcomes.SetItem(3,
            outcomes[3] with { FindingCount = 1, UnadjudicatedFindings = 1,
                ModelStatus = ModelObservationStatus.Unadjudicated });
        var measuredSummary = baseline with
        { ProspectiveCaseReceipts = blockedButMeasured };
        Check(measuredSummary, blockedButMeasuredOutcomes,
            "blocked", "finding_ineligible");
        var measuredGate = R6ProspectiveQualityGate.Evaluate(
            measuredSummary, blockedButMeasuredOutcomes)!;
        Assert.Equal(3, measuredGate.ExpectedCredits);
        Assert.Equal(1, measuredGate.TrueOffFocusFindings);
        Assert.Equal(1, measuredGate.CrossCaseRepeats);
        Assert.Equal(5, measuredGate.AiAdjudicatedCases);
        Assert.Equal("cost_recorded", measuredGate.UsabilityStatus);
        Assert.Equal("failed", measuredGate.SafetyStatus);
        var malformedLater = blockedButMeasured.SetItem(4, receipts[4] with
        { Findings = [repoFinding with { DefectGroupId = "forged-group" }] });
        var malformedSummary = baseline with
        { ProspectiveCaseReceipts = malformedLater };
        var malformedClaim = R6ProspectiveQualityGate.Evaluate(
            malformedSummary, blockedButMeasuredOutcomes)!;
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(
            WriteReport(malformedSummary with
            { ProspectiveQualityCandidate = malformedClaim },
                blockedButMeasuredOutcomes)).Status);

        foreach (var safeIndex in new[] { 1, 3 })
        {
            var accused = offFocus with { SafeLineRole = "accusation",
                Reason = "safe_line_accusation" };
            var accusedReceipts = receipts.SetItem(safeIndex, receipts[safeIndex] with
            { FindingRowCount = 1, Findings = [accused] });
            var accusedRows = outcomes.SetItem(safeIndex, outcomes[safeIndex] with
            { FindingCount = 1, UnadjudicatedFindings = 1,
                ModelStatus = ModelObservationStatus.Unadjudicated });
            Check(baseline with { ProspectiveCaseReceipts = accusedReceipts }, accusedRows,
                "blocked", "finding_ineligible");
            Assert.Equal("failed", R6ProspectiveQualityGate.Evaluate(
                baseline with { ProspectiveCaseReceipts = accusedReceipts },
                accusedRows)?.SafetyStatus);
        }

        var compared = offFocus with { SafeLineRole = "comparison" };
        var comparedReceipts = receipts.SetItem(1, receipts[1] with
        { FindingRowCount = 1, Findings = [compared] });
        var comparedRows = outcomes.SetItem(1, outcomes[1] with
        {
            FindingCount = 1, UnadjudicatedFindings = 1,
            ModelStatus = ModelObservationStatus.Unadjudicated,
            Code = EvaluationCode.ProhibitedFinding,
            ScenarioStatus = AssertionStatus.Failed,
            ProhibitedObservations = 1,
        });
        Check(baseline with { ProspectiveCaseReceipts = comparedReceipts }, comparedRows,
            "candidate_pass", "all_gates_passed");
        var comparedGate = R6ProspectiveQualityGate.Evaluate(
            baseline with { ProspectiveCaseReceipts = comparedReceipts }, comparedRows)!;
        Assert.Equal("failed", comparedGate.LegacyStructuralStatus);
        Assert.Equal("passed", comparedGate.SafetyStatus);

        // An unexplained legacy safe-line overlap must not publish a passing
        // safety dimension, even when the public finding row is well formed.
        foreach (var safeIndex in new[] { 1, 3 })
        {
            var unexplainedReceipts = receipts.SetItem(safeIndex,
                receipts[safeIndex] with { FindingRowCount = 1, Findings = [offFocus] });
            var unexplainedRows = outcomes.SetItem(safeIndex,
                outcomes[safeIndex] with
                {
                    FindingCount = 1,
                    UnadjudicatedFindings = 1,
                    ModelStatus = ModelObservationStatus.Unadjudicated,
                    Code = EvaluationCode.ProhibitedFinding,
                    ScenarioStatus = AssertionStatus.Failed,
                    ProhibitedObservations = 1,
                });
            var unexplainedSummary = baseline with
            { ProspectiveCaseReceipts = unexplainedReceipts };
            Check(unexplainedSummary, unexplainedRows,
                "blocked", "prohibited_evidence_unexplained");
            var unexplainedGate = Assert.IsType<R6ProspectiveGateResult>(
                R6ProspectiveQualityGate.Evaluate(unexplainedSummary, unexplainedRows));
            Assert.Equal("passed", unexplainedGate.ProspectiveSemanticStatus);
            Assert.Equal("failed", unexplainedGate.SafetyStatus);
            var read = R6ProspectiveReportReader.Read(WriteReport(
                unexplainedSummary with { ProspectiveQualityCandidate = unexplainedGate },
                unexplainedRows));
            Assert.Equal("failed", read.Recomputed?.SafetyStatus);
        }

        var recovery = new LiveRecoveryDiagnostic(0, 0, AgentToolRegistry.ListFilesName,
            LiveRecoveryDiagnostic.ArgumentsInvalid);
        var recoveryReceipts = Assert.IsType<ImmutableArray<R6ProspectiveRecoveryReceipt>>(
            baseline.ProspectiveRecoveryReceipts);
        var attested = recoveryReceipts.SetItem(0, recoveryReceipts[0] with
        { ObservedCount = 1, Status = "qualified", Reason = "qualified" });
        Check(baseline with { RecoveryDiagnostics = [recovery],
            ProspectiveRecoveryReceipts = attested }, outcomes,
            "candidate_pass", "all_gates_passed");
        var recoveredGate = R6ProspectiveQualityGate.Evaluate(baseline with
        { RecoveryDiagnostics = [recovery], ProspectiveRecoveryReceipts = attested }, outcomes)!;
        Assert.Equal(1, recoveredGate.QualifiedRecoveries);
        Assert.Equal("cost_recorded", recoveredGate.UsabilityStatus);
        Check(baseline with { RecoveryDiagnostics = [recovery],
            ProspectiveRecoveryReceipts = attested.SetItem(0, attested[0] with
            { Status = "blocked", Reason = "followup_missing" }) }, outcomes,
            "blocked", "recovery_ineligible");
        var malformedPair = recoveryReceipts.SetItem(0, recoveryReceipts[0] with
        { ObservedCount = 1, Status = "blocked", Reason = "error_pair_invalid" });
        Check(baseline with { ProspectiveRecoveryReceipts = malformedPair }, outcomes,
            "blocked", "recovery_ineligible");
        Check(baseline with { RecoveryDiagnostics = [recovery with { ScheduleIndex = 5 }] },
            outcomes, "blocked", "recovery_count_invalid");
        var secondRecovery = recovery with { ScheduleIndex = 1 };
        var twoAttested = attested.SetItem(1, attested[1] with
        { ObservedCount = 1, Status = "qualified", Reason = "qualified" });
        var extraRecovery = baseline with { RecoveryDiagnostics = [recovery,
            secondRecovery], ProspectiveRecoveryReceipts = twoAttested };
        Assert.Equal("recovery_count_invalid", R6ProspectiveQualityGate.Evaluate(
            extraRecovery, outcomes)?.Reason);
        var extraClaim = R6ProspectiveQualityGate.Evaluate(extraRecovery, outcomes)!;
        var extraRead = R6ProspectiveReportReader.Read(WriteReport(extraRecovery with
        { ProspectiveQualityCandidate = extraClaim }, outcomes));
        Assert.Equal("blocked", extraRead.Status);
        Assert.Equal("recovery_count_invalid", extraRead.Reason);
    }

    [Fact]
    public async Task ReviewerCanDenyCausalCreditDespiteCompactGroundedCitation()
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            corruption: "false_explanation");
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = _ => { },
            SecretSource = new FakeSecret("APR311_SYNTHETIC_CREDENTIAL_CANARY"),
            TransportFactory = new FakeFactory(_ => new ReplayTransport(runs[next++].Script,
                ReplayFault.None)),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("adjudicated", result.Summary.AdjudicationStatus);
        Assert.Equal("finding_ineligible", result.Summary.ProspectiveQualityCandidate?.Reason);
        Assert.Equal("failed", result.Summary.ProspectiveQualityCandidate?.SafetyStatus);
        Assert.Equal("false_unsafe", result.Summary.ProspectiveCaseReceipts!.Value[0]
            .Findings[0].Verdict);
    }

    [Theory]
    [InlineData("run_local_context", "assessed", "reviewed_other")]
    [InlineData("wrong_authored_context", "pending", null)]
    public async Task ContextualKnownAnchorDoesNotAssignAnotherDefectGroup(
        string annotation, string expectedStatus, string? expectedCitation)
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, corruption: annotation);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = _ => { },
            SecretSource = new FakeSecret("APR311_SYNTHETIC_CREDENTIAL_CANARY"),
            TransportFactory = new FakeFactory(_ =>
            {
                var run = runs[next++];
                return new ReplayTransport(run.Input.CaseId == "cs-defect"
                    ? WithContextEvidence(run.Script) : run.Script, ReplayFault.None);
            }),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);

        Assert.Equal(expectedStatus, result.Summary.ProspectiveCaseReceipts!.Value[0].Status);
        if (expectedCitation is not null)
        {
            var finding = result.Summary.ProspectiveCaseReceipts.Value[0].Findings[0];
            Assert.Equal("true_off_focus", finding.Verdict);
            Assert.Equal("run", finding.DefectGroupScope);
            Assert.StartsWith("run-", finding.DefectGroupId, StringComparison.Ordinal);
            Assert.Equal(expectedCitation, finding.CitationClass);
        }
        else Assert.Equal("input_invalid", result.Summary.AdjudicationStatus);
    }

    [Fact]
    public async Task PublicProjectionAndVerifierExcludeDistinctPrivateCanariesOnCandidatePass()
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            injectPacketCanaries: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = new FakeSecret(CredentialCanary),
            TransportFactory = new FakeFactory(_ => new ReplayTransport(
                WithModelCanaries(runs[next++].Script), ReplayFault.None)),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("candidate_pass", result.Summary.ProspectiveQualityCandidate?.Status);
        Assert.Contains(SourceCanary, input.LastPacket);
        Assert.Equal(LocalPathCanary, JsonNode.Parse(input.LastPacket)!["local_path_probe"]!
            .GetValue<string>());
        Assert.False(Directory.Exists(input.Root));
        AssertPublicProjection(lines, "r6_prospective_candidate_pass all_gates_passed", 0);
    }

    [Theory]
    [InlineData("annotation")]
    [InlineData("exception")]
    [InlineData("evidence_path")]
    public async Task PublicProjectionAndVerifierExcludePrivateCanariesOnBlockedRun(string failure)
    {
        if (!IsCleanSource()) return;
        using var plan = new PlanFile();
        using var prompts = new StringWriter();
        using var input = new PacketReviewInput(prompts, approveExpected: true,
            corruption: failure == "annotation" ? "private_canary" :
                failure == "exception" ? "exception_canary" : null,
            injectPacketCanaries: true);
        var runs = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).Runs;
        var next = 0;
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            WriteLine = lines.Add,
            SecretSource = new FakeSecret(CredentialCanary),
            TransportFactory = new FakeFactory(_ =>
            {
                var run = runs[next++];
                return new ReplayTransport(WithModelCanaries(run.Script,
                    invalidEvidencePath: failure == "evidence_path" &&
                        run.Input.CaseId == "repository-rule"), ReplayFault.None);
            }),
            ProspectiveReviewer = new R6ProspectiveAdjudicator(input, prompts,
                R6ProspectiveAssessment.AiOrigin),
        }, CancellationToken.None);
        Assert.Equal("blocked", result.Summary.ProspectiveQualityCandidate?.Status);
        Assert.False(Directory.Exists(input.Root));
        AssertPublicProjection(lines,
            "r6_prospective_blocked population_ineligible", 1);
    }

    private static ReplayScript WithModelCanaries(ReplayScript script,
        bool invalidEvidencePath = false) => script with
    {
        Turns = script.Turns.Select((turn, index) => turn with
        {
            ReasoningContent = index == 0 ? ReasoningCanary : turn.ReasoningContent,
            ToolCalls = turn.ToolCalls.Select(call =>
            {
                if (call.Name != "finish_review") return call;
                var terminal = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                terminal["summary"] = SummaryCanary;
                foreach (var finding in terminal["findings"]!.AsArray())
                {
                    finding!["title"] = TitleCanary;
                    finding["message"] = MessageCanary;
                    if (invalidEvidencePath)
                        finding["evidence"]![0]!["path"] = EvidencePathCanary;
                }
                return call with { ArgumentsJson = terminal.ToJsonString() };
            }).ToImmutableArray(),
        }).ToImmutableArray(),
    };

    private static ReplayScript WithSafeTrimDiscovery(ReplayScript script)
    {
        var terminal = script.Turns[script.Turns.Length - 1];
        return script with
        {
            Turns = script.Turns.RemoveAt(script.Turns.Length - 1)
                .Add(new ReplayScriptTurn([new ReplayToolCall("safe-diff",
                    AgentToolRegistry.ReadDiffName,
                    "{\"path\":\"src/SafeCaller.cs\"}")], ""))
                .Add(terminal),
        };
    }

    private static ReplayScript WithTsChangedDiff(ReplayScript script, string? observationId) =>
        script with
        {
            Turns = script.Turns.Select(turn => turn with
            {
                ToolCalls = turn.ToolCalls.Select(call =>
                {
                    if (call.Name == AgentToolRegistry.ReadFileName &&
                        call.ArgumentsJson.Contains("src/client.ts", StringComparison.Ordinal))
                        return call with
                        {
                            Name = AgentToolRegistry.ReadDiffName,
                            ArgumentsJson = "{\"path\":\"src/client.ts\"}",
                        };
                    if (call.Name != AgentToolRegistry.FinishReviewName) return call;
                    var terminal = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                    if (observationId is null) terminal["findings"] = new JsonArray();
                    else terminal["findings"]![0]!["evidence"]![0]!["observation_id"] =
                        observationId;
                    return call with { ArgumentsJson = terminal.ToJsonString() };
                }).ToImmutableArray(),
            }).ToImmutableArray(),
        };

    private static ReplayScript WithSafeTrimFinding(ReplayScript script,
        string safeFile, string lookup, string safeDiff) => script with
    {
        Turns = script.Turns.Select(turn => turn with
        {
            ToolCalls = turn.ToolCalls.Select(call =>
            {
                if (call.Name != AgentToolRegistry.FinishReviewName) return call;
                var terminal = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                terminal["findings"] = new JsonArray(new JsonObject
                {
                    ["severity"] = "low",
                    ["title"] = "Caller repeats an already completed trim",
                    ["message"] = "Lookup.Find trims every non-null result. The caller-side trim is redundant; the nullable fallback remains safe.",
                    ["evidence"] = new JsonArray(
                        new JsonObject
                        {
                            ["observation_id"] = safeFile,
                            ["path"] = "src/SafeCaller.cs",
                            ["start_line"] = 5,
                            ["end_line"] = 5,
                        },
                        new JsonObject
                        {
                            ["observation_id"] = lookup,
                            ["path"] = "src/Lookup.cs",
                            ["start_line"] = 5,
                            ["end_line"] = 5,
                        },
                        new JsonObject
                        {
                            ["observation_id"] = safeDiff,
                            ["path"] = "src/SafeCaller.cs",
                            ["start_line"] = 5,
                            ["end_line"] = 5,
                        }),
                });
                return call with { ArgumentsJson = terminal.ToJsonString() };
            }).ToImmutableArray(),
        }).ToImmutableArray(),
    };

    private static ReplayScript WithTsDefaultFinding(ReplayScript script,
        string safeRead) => script with
    {
        Turns = script.Turns.Select(turn => turn with
        {
            ToolCalls = turn.ToolCalls.Select(call =>
            {
                if (call.Name != AgentToolRegistry.FinishReviewName) return call;
                var terminal = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                terminal["findings"] = new JsonArray(new JsonObject
                {
                    ["severity"] = "low",
                    ["title"] = "Centralize the fallback timeout value",
                    ["message"] = "Move the literal 3000 into a named default constant, preserving ?? so an explicit timeoutMs of 0 remains meaningful.",
                    ["evidence"] = new JsonArray(new JsonObject
                    {
                        ["observation_id"] = safeRead,
                        ["path"] = "src/safe-client.ts",
                        ["start_line"] = 2,
                        ["end_line"] = 2,
                    }),
                });
                return call with { ArgumentsJson = terminal.ToJsonString() };
            }).ToImmutableArray(),
        }).ToImmutableArray(),
    };

    // A synthetic structural overlap, not an assertion that the second
    // finding is genuinely severe or independent in a future model run.
    private static ReplayScript WithMechanicalDuplicate(ReplayScript script) => script with
    {
        Turns = script.Turns.Select(turn => turn with
        {
            ToolCalls = turn.ToolCalls.Select(call =>
            {
                if (call.Name != AgentToolRegistry.FinishReviewName) return call;
                var terminal = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                var second = terminal["findings"]![0]!.DeepClone();
                second["title"] = "Synthetic second causal hypothesis";
                second["message"] = "An independent reviewer must decide whether this is a distinct causal issue on the same returned line.";
                terminal["findings"]!.AsArray().Add(second);
                return call with { ArgumentsJson = terminal.ToJsonString() };
            }).ToImmutableArray(),
        }).ToImmutableArray(),
    };

    private static ReplayScript WithOffFocusUploadDiff(ReplayScript script)
    {
        var terminal = script.Turns[^1] with
        {
            ToolCalls = script.Turns[^1].ToolCalls.Select(call =>
            {
                var review = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                review["findings"] = new JsonArray(new JsonObject
                {
                    ["severity"] = "high",
                    ["title"] = "Synthetic off-focus token log",
                    ["message"] = "Upload passes its token to stderr against the repository rule.",
                    ["evidence"] = new JsonArray(new JsonObject
                    {
                        ["observation_id"] =
                            "5870f083ee1dc9c29e794d6f090827aa1fe92b541081d8494d32c37a46f4cafe",
                        ["path"] = "src/Upload.cs",
                        ["start_line"] = 5,
                        ["end_line"] = 6,
                    }),
                });
                return call with { ArgumentsJson = review.ToJsonString() };
            }).ToImmutableArray(),
        };
        return script with
        {
            Turns = script.Turns.RemoveAt(script.Turns.Length - 1)
                .Add(new ReplayScriptTurn([new ReplayToolCall("off-diff",
                    AgentToolRegistry.ReadDiffName, "{\"path\":\"src/Upload.cs\"}")], ""))
                .Add(new ReplayScriptTurn([new ReplayToolCall("off-rule",
                    AgentToolRegistry.ReadFileName,
                    "{\"path\":\"rules/review.md\",\"start_line\":1,\"line_count\":20}")], ""))
                .Add(terminal),
        };
    }

    // Mechanical grouping test: the synthetic second line is not a claim that
    // this fixture contains another real defect. The independent reviewer owns
    // that causal judgment; the parser must not infer it from contextual overlap.
    private static ReplayScript WithContextEvidence(ReplayScript script) => script with
    {
        Turns = script.Turns.Select(turn => turn with
        {
            ToolCalls = turn.ToolCalls.Select(call =>
            {
                if (call.Name != "finish_review") return call;
                var terminal = JsonNode.Parse(call.ArgumentsJson)!.AsObject();
                var evidence = terminal["findings"]![0]!["evidence"]!.AsArray();
                var context = evidence[0]!.DeepClone();
                context["start_line"] = 4;
                context["end_line"] = 4;
                evidence.Add(context);
                return call with { ArgumentsJson = terminal.ToJsonString() };
            }).ToImmutableArray(),
        }).ToImmutableArray(),
    };

    private static void AssertPublicProjection(IReadOnlyList<string> lines,
        string expectedVerifierLine, int expectedExit)
    {
        Assert.Equal(7, lines.Count);
        var jsonl = string.Join('\n', lines) + "\n";
        foreach (var canary in PrivateCanaries)
            Assert.DoesNotContain(canary, jsonl, StringComparison.Ordinal);
        var summary = JsonNode.Parse(lines[^1])!.AsObject();
        var cases = summary["prospective_case_receipts"]!.AsArray();
        var recoveries = summary["prospective_recovery_receipts"]!.AsArray();
        Assert.Equal(5, cases.Count);
        Assert.Equal(5, recoveries.Count);
        AssertKeys(cases[0]!.AsObject(), "rubric_id", "rubric_sha256",
            "schedule_index", "case_id", "corpus_sha256", "case_sha256",
            "configuration_sha256", "execution_sha256", "origin", "status",
            "finding_row_count", "findings");
        AssertKeys(recoveries[0]!.AsObject(), "rubric_id", "rubric_sha256",
            "schedule_index", "case_sha256", "configuration_sha256",
            "execution_sha256", "observed_count", "status", "reason");
        AssertKeys(summary["prospective_quality_candidate"]!.AsObject(),
            "status", "reason", "expected_credits", "true_off_focus_findings",
            "cross_case_repeats", "unique_defect_groups", "qualified_recoveries",
            "ai_adjudicated_cases", "human_confirmed_cases",
            "legacy_structural_status", "prospective_semantic_status",
            "safety_status", "usability_status");
        foreach (var caseRow in cases)
            foreach (var finding in caseRow!["findings"]!.AsArray())
                AssertKeys(finding!.AsObject(), "finding_ordinal", "verdict",
                    "expected_defect_id", "defect_group_scope", "defect_group_id",
                    "citation_class", "safe_line_role", "reason");
        var path = System.IO.Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, jsonl);
            using var output = new StringWriter();
            Assert.Equal(expectedExit, R6ProspectiveReportReader.Invoke(path, output));
            Assert.Equal(expectedVerifierLine, output.ToString().Trim());
            foreach (var canary in PrivateCanaries)
                Assert.DoesNotContain(canary, output.ToString(), StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }

    private static void AssertKeys(JsonObject value, params string[] expected) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal),
            value.Select(property => property.Key).Order(StringComparer.Ordinal));

    private static byte[] WriteReport(LiveRunSummary summary,
        System.Collections.Immutable.ImmutableArray<EvaluationOutcome> outcomes)
    {
        var rows = outcomes.Select(EvaluationJson.Write).ToArray();
        var report = EvaluationReport.Create(rows.Select(row =>
            (ReadOnlyMemory<byte>)row).ToImmutableArray());
        Assert.True(report.Succeeded);
        var aggregate = EvaluationReportJson.Write(report.Value);
        Assert.True(aggregate.Succeeded);
        return Encoding.UTF8.GetBytes(string.Join('\n', rows.Select(Encoding.UTF8.GetString)
            .Append(Encoding.UTF8.GetString(aggregate.Value!))
            .Append(JsonSerializer.Serialize(summary, LiveJsonContext.Default.LiveRunSummary))) + "\n");
    }

    [Theory]
    [InlineData("wrong_corpus")]
    [InlineData("wrong_order")]
    [InlineData("wrong_id")]
    [InlineData("wrong_digest")]
    public void InvalidSelectionRejectedByPlanAdmission(string mutation)
    {
        using var plan = new PlanFile(document =>
        {
            switch (mutation)
            {
                case "wrong_corpus": document["corpus"]!["sha256"] = new string('0', 64); break;
                case "wrong_order": document["schedule"] = new JsonArray(document["schedule"]!
                    .AsArray().Reverse().Select(item => item!.DeepClone()).ToArray()); break;
                case "wrong_id": document["rubric"]!["id"] = "unknown"; break;
                case "wrong_digest": document["rubric"]!["sha256"] = new string('0', 64); break;
            }
        });
        var error = Assert.Throws<LivePlanRejected>(() =>
            LivePlanAdmission.Load(plan.Path, false, CancellationToken.None));
        Assert.Equal(LiveAdmissionCode.InvalidPlan, error.Code);
    }

    [Theory]
    [InlineData("transport-blocked-370cfbd.jsonl", "19ed7d1edda26c0760214a8b8a90a427db3bd9574c6d45401c59cae55ed5599e")]
    [InlineData("live-868775.jsonl", "760616d13f1bcc4ecb65c5797a91dc6af81983a1ee15c659c69e34cf49a4c24c")]
    [InlineData("live-64k-04f72fd.jsonl", "f7926cb69a462e07f26331c76ee582a5c018f7d4a63d73bcdf545fec448e391d")]
    public void HistoricalReportsStayImmutableAndCannotProvideProspectiveCredit(string name, string sha)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r6",
            "quality-observation", name);
        var bytes = File.ReadAllBytes(path);
        Assert.Equal(sha, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(bytes).Status);
    }

    private sealed class PlanFile : IDisposable
    {
        private readonly string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r6-rubric-" +
            Guid.NewGuid().ToString("N"));
        internal string Path { get; }

        internal PlanFile(Action<JsonObject>? edit = null, bool v2 = false, bool v3 = false)
        {
            Assert.False(v2 && v3);
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "plan.json");
            Assert.Equal(0, R5CaseVerifier.MakeLivePlan(Corpus, Path).Item1);
            var document = JsonNode.Parse(File.ReadAllText(Path))!.AsObject();
            document["rubric"] = new JsonObject
            {
                ["id"] = v3 ? R6ProspectiveRubric.V3Id :
                    v2 ? R6ProspectiveRubric.V2Id : R6ProspectiveRubric.Id,
                ["sha256"] = v3 ? R6ProspectiveRubric.V3Sha256 :
                    v2 ? R6ProspectiveRubric.V2Sha256 : R6ProspectiveRubric.Sha256,
            };
            edit?.Invoke(document);
            File.WriteAllText(Path, document.ToJsonString());
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class PacketReviewInput(StringWriter prompts, bool approveExpected = false,
        string? corruption = null, bool injectPacketCanaries = false,
        bool approveOffFocusDiff = false, bool v2 = false,
        bool approveSafeTrim = false, bool approveTsDefault = false,
        bool approveDistinctDuplicate = false, bool v3 = false) : TextReader
    {
        private int promptCount;
        private int position;
        private const string Command = "accept\n";
        private JsonNode? originalFirstFinding;
        internal string? Root { get; private set; }
        internal string LastPacket { get; private set; } = "";
        internal string? OffFocusPacket { get; private set; }
        internal Dictionary<string, string> PacketsByCase { get; } = new(StringComparer.Ordinal);
        internal bool PriorPacketsVisible { get; private set; }
        internal List<int> PromptCaseIndices { get; } = [];

        public override ValueTask<int> ReadAsync(Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = prompts.ToString().Split('\n');
            var current = lines.Count(line => line.StartsWith("r6_review_case ",
                StringComparison.Ordinal));
            if (current > promptCount)
            {
                if (corruption == "exception_canary")
                    throw new IOException(ExceptionCanary);
                promptCount = current;
                position = 0;
                Root ??= lines[0].TrimEnd('\r').Replace("r6_review_private_directory ", "",
                    StringComparison.Ordinal);
                var latest = lines.Last(line => line.StartsWith("r6_review_case ",
                    StringComparison.Ordinal));
                var caseIndex = int.Parse(latest.Split(' ')[1],
                    System.Globalization.CultureInfo.InvariantCulture);
                Assert.InRange(caseIndex, 0, 4);
                PromptCaseIndices.Add(caseIndex);
                var packetPath = System.IO.Path.Join(Root,
                    v2 ? "case-" + caseIndex + ".json" : "review.json");
                LastPacket = File.ReadAllText(packetPath);
                if (v2 && caseIndex > 0)
                    PriorPacketsVisible |= File.Exists(System.IO.Path.Join(Root,
                        "case-" + (caseIndex - 1) + ".json"));
                if (injectPacketCanaries)
                {
                    var privatePacket = JsonNode.Parse(LastPacket)!.AsObject();
                    var source = privatePacket["source"]!.AsObject();
                    var sourceName = source.First().Key;
                    source[sourceName] = source[sourceName]!.GetValue<string>() + SourceCanary;
                    privatePacket["local_path_probe"] = LocalPathCanary;
                    File.WriteAllText(packetPath, privatePacket.ToJsonString());
                    LastPacket = File.ReadAllText(packetPath);
                }
                var annotationPath = System.IO.Path.Join(Root,
                    v2 ? "case-" + caseIndex + "-annotation.json" : "annotation.json");
                var annotation = JsonNode.Parse(File.ReadAllText(annotationPath))!.AsObject();
                if (v3) annotation["attribution"] = new JsonObject
                {
                    ["causes"] = new JsonArray(),
                    ["confidence"] = "undetermined",
                    ["basis"] = "insufficient",
                    ["observation_ordinals"] = new JsonArray(),
                };
                if (current == 2 && corruption == "v2_null_row")
                    annotation["findings"]![0] = originalFirstFinding!.DeepClone();
                using var packet = JsonDocument.Parse(LastPacket);
                var findings = packet.RootElement.GetProperty("findings");
                var caseId = packet.RootElement.GetProperty("case").GetProperty("id").GetString();
                if (caseId is not null) PacketsByCase[caseId] = LastPacket;
                if (caseId == "ts-safe" && approveOffFocusDiff) OffFocusPacket = LastPacket;
                for (var index = 0; index < findings.GetArrayLength(); index++)
                {
                    var uses = new JsonArray();
                    var assessments = new JsonArray();
                    var evidenceRows = findings[index].GetProperty("evidence").EnumerateArray()
                        .ToArray();
                    for (var evidenceOrdinal = 0; evidenceOrdinal < evidenceRows.Length;
                        evidenceOrdinal++)
                    {
                        var evidence = evidenceRows[evidenceOrdinal];
                        var path = evidence.GetProperty("path").GetString();
                        var start = evidence.GetProperty("start_line").GetInt32();
                        var end = evidence.GetProperty("end_line").GetInt32();
                        if (path == "src/SafeCaller.cs" && start <= 5 && end >= 5 ||
                            path == "src/safe-client.ts" && start <= 2 && end >= 2)
                        {
                            var accusation = v2 && (approveSafeTrim && caseId == "cs-safe" ||
                                approveTsDefault && caseId == "ts-safe");
                            uses.Add(accusation ? "accusation" : "comparison");
                            if (v2) assessments.Add(new JsonObject
                            {
                                ["evidence_ordinal"] = evidenceOrdinal,
                                ["use"] = accusation ? "accusation" : "comparison",
                                ["assessment"] = accusation ? "confirmed_other_issue" :
                                    "comparison_only",
                            });
                        }
                    }
                    annotation["findings"]![index]!["safe_line_uses"] = uses;
                    if (v2)
                        annotation["findings"]![index]!["safe_line_assessments"] = assessments;
                    if (approveExpected)
                    {
                        var group = caseId switch
                        {
                            "cs-defect" => "cs-null-deref",
                            "ts-defect" => "ts-zero-timeout",
                            "repository-rule" => "repository-token-log",
                            _ => null,
                        };
                        if (group is not null)
                        {
                            annotation["findings"]![index]!["verdict"] = "expected";
                            annotation["findings"]![index]!["expected_defect_id"] = "defect";
                            annotation["findings"]![index]!["defect_group_scope"] = "authored";
                            annotation["findings"]![index]!["defect_group_id"] = group;
                            if (v2) annotation["findings"]![index]!["anchor_evidence_ordinal"] = 0;
                        }
                    }
                    if (v2 && approveSafeTrim && caseId == "cs-safe")
                    {
                        annotation["findings"]![index]!["verdict"] = "true_off_focus";
                        annotation["findings"]![index]!["defect_group_scope"] = "run";
                        annotation["findings"]![index]!["defect_group_id"] =
                            "run-redundant-caller-trim";
                        annotation["findings"]![index]!["anchor_evidence_ordinal"] = 0;
                    }
                    if (v2 && approveTsDefault && caseId == "ts-safe")
                    {
                        annotation["findings"]![index]!["verdict"] = "true_off_focus";
                        annotation["findings"]![index]!["defect_group_scope"] = "run";
                        annotation["findings"]![index]!["defect_group_id"] =
                            "run-named-timeout-default";
                        annotation["findings"]![index]!["anchor_evidence_ordinal"] = 0;
                    }
                    if (v2 && approveDistinctDuplicate && caseId == "cs-defect" &&
                        index == 1)
                    {
                        annotation["findings"]![index]!["verdict"] = "true_off_focus";
                        annotation["findings"]![index]!["expected_defect_id"] = null;
                        annotation["findings"]![index]!["defect_group_scope"] = "run";
                        annotation["findings"]![index]!["defect_group_id"] =
                            "run-independent-caller-cause";
                        annotation["findings"]![index]!["anchor_evidence_ordinal"] = 0;
                    }
                    if (approveOffFocusDiff && caseId == "ts-safe")
                    {
                        annotation["findings"]![index]!["verdict"] = "true_off_focus";
                        annotation["findings"]![index]!["defect_group_scope"] = "authored";
                        annotation["findings"]![index]!["defect_group_id"] =
                            "repository-token-log";
                        if (v2) annotation["findings"]![index]!["anchor_evidence_ordinal"] = 0;
                    }
                    if (v2)
                    {
                        var trueFinding = annotation["findings"]![index]!["verdict"]!
                            .GetValue<string>() is "expected" or "true_off_focus";
                        annotation["findings"]![index]!["severity_assessment"] =
                            trueFinding ? "justified" : "unresolved";
                        annotation["findings"]![index]!["anchor_assessment"] =
                            trueFinding ? "relevant" : "unresolved";
                    }
                }
                if (current == 1)
                {
                    if (corruption == "origin") annotation["origin"] = "human-confirmed";
                    else if (corruption == "private_canary")
                        annotation["private_note"] = AnnotationCanary;
                    else if (corruption is "execution_sha256" or "rubric_sha256")
                        annotation[corruption] = new string('0', 64);
                    else if (corruption is "v2_retry" or "v2_retry_fail")
                        annotation["rubric_sha256"] = new string('0', 64);
                    else if (corruption == "v2_null_row" && findings.GetArrayLength() > 0)
                    {
                        originalFirstFinding = annotation["findings"]![0]!.DeepClone();
                        annotation["findings"]![0] = null;
                    }
                    else if (corruption == "v2_semantic_rejected" &&
                        findings.GetArrayLength() > 0)
                    {
                        var row = annotation["findings"]![0]!;
                        row["verdict"] = "true_off_focus";
                        row["expected_defect_id"] = null;
                        row["defect_group_scope"] = "authored";
                        row["defect_group_id"] = "ts-zero-timeout";
                    }
                    else if (corruption == "false_explanation" && findings.GetArrayLength() > 0)
                    {
                        var row = annotation["findings"]![0]!;
                        row["verdict"] = "false_unsafe";
                        row["expected_defect_id"] = null;
                        row["defect_group_scope"] = null;
                        row["defect_group_id"] = null;
                    }
                    else if (corruption is "run_local_context" or "wrong_authored_context" &&
                        findings.GetArrayLength() > 0)
                    {
                        var row = annotation["findings"]![0]!;
                        row["verdict"] = "true_off_focus";
                        row["defect_group_scope"] = corruption == "run_local_context"
                            ? "run" : "authored";
                        row["defect_group_id"] = corruption == "run_local_context"
                            ? "run-contextual-novel-claim" : "ts-zero-timeout";
                    }
                }
                if (current == 2 && corruption == "v2_retry")
                    annotation["rubric_sha256"] = R6ProspectiveRubric.V2Sha256;
                File.WriteAllText(annotationPath, annotation.ToJsonString());
            }
            if (position == Command.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = Command[position++];
            return ValueTask.FromResult(1);
        }
    }

    private sealed class FakeSecret(string value) : ILiveSecretSource
    {
        internal int Taken { get; private set; }
        public string? TakeProviderCredential() { Taken++; return value; }
    }

    private sealed class FakeFactory(Func<DeepSeekCredential, IDeepSeekTransport> create)
        : ILiveTransportFactory
    {
        public IDeepSeekTransport Create(DeepSeekCredential credential) => create(credential);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsCleanSource() => EvaluationSource.Clean;
}
