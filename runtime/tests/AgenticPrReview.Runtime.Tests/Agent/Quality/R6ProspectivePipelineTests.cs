using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R6ProspectivePipelineTests
{
    private static string Corpus => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r6",
        "quality-sandbox");

    [Theory]
    [InlineData(5, 5, "exact")]
    [InlineData(5, 6, "bounded_context")]
    [InlineData(5, 7, "bounded_context")]
    [InlineData(4, 6, "bounded_context")]
    [InlineData(3, 5, "bounded_context")]
    [InlineData(4, 7, "invalid")]
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
        Assert.DoesNotContain("reasoning", Encoding.UTF8.GetString(bytes), StringComparison.OrdinalIgnoreCase);

        var summary = JsonNode.Parse(lines[^1])!.AsObject();
        summary["prospective_quality_candidate"]!["status"] = "candidate_pass";
        lines[^1] = summary.ToJsonString();
        Assert.Equal("rejected", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
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
        Assert.Equal("not_evaluable", R6ProspectiveReportReader.Read(publicBytes).Status);
    }

    [Fact]
    public async Task CleanKeylessTransportProvesLiveGateWithoutProviderCalls()
    {
        // The evaluator embeds a dirty-source marker at build time. This case
        // runs on a committed checkout in CI and in the exact-head local gate.
        if (!EvaluationSource.Clean) return;
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
        Assert.Equal(3, result.Summary.ProspectiveQualityCandidate.ExpectedCredits);
        Assert.Equal(5, result.Summary.ProspectiveCaseReceipts?.Length);
        Assert.Equal("candidate_pass", R6ProspectiveReportReader.Read(
            Encoding.UTF8.GetBytes(string.Join('\n', lines) + "\n")).Status);
        Assert.DoesNotContain("APR311_SYNTHETIC_CREDENTIAL_CANARY", string.Join('\n', lines));
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

        internal PlanFile(Action<JsonObject>? edit = null)
        {
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "plan.json");
            Assert.Equal(0, R5CaseVerifier.MakeLivePlan(Corpus, Path).Item1);
            var document = JsonNode.Parse(File.ReadAllText(Path))!.AsObject();
            document["rubric"] = new JsonObject
            {
                ["id"] = R6ProspectiveRubric.Id,
                ["sha256"] = R6ProspectiveRubric.Sha256,
            };
            edit?.Invoke(document);
            File.WriteAllText(Path, document.ToJsonString());
        }

        public void Dispose() => Directory.Delete(root, recursive: true);
    }

    private sealed class PacketReviewInput(StringWriter prompts, bool approveExpected = false) : TextReader
    {
        private int promptCount;
        private int position;
        private const string Command = "accept\n";
        internal string? Root { get; private set; }
        internal string LastPacket { get; private set; } = "";

        public override ValueTask<int> ReadAsync(Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lines = prompts.ToString().Split('\n');
            var current = lines.Count(line => line.StartsWith("r6_review_case ",
                StringComparison.Ordinal));
            if (current > promptCount)
            {
                promptCount = current;
                position = 0;
                Root ??= lines[0].TrimEnd('\r').Replace("r6_review_private_directory ", "",
                    StringComparison.Ordinal);
                LastPacket = File.ReadAllText(System.IO.Path.Combine(Root, "review.json"));
                var annotationPath = System.IO.Path.Combine(Root, "annotation.json");
                var annotation = JsonNode.Parse(File.ReadAllText(annotationPath))!.AsObject();
                using var packet = JsonDocument.Parse(LastPacket);
                var findings = packet.RootElement.GetProperty("findings");
                var caseId = packet.RootElement.GetProperty("case").GetProperty("id").GetString();
                for (var index = 0; index < findings.GetArrayLength(); index++)
                {
                    var uses = new JsonArray();
                    foreach (var evidence in findings[index].GetProperty("evidence").EnumerateArray())
                    {
                        var path = evidence.GetProperty("path").GetString();
                        var start = evidence.GetProperty("start_line").GetInt32();
                        var end = evidence.GetProperty("end_line").GetInt32();
                        if (path == "src/SafeCaller.cs" && start <= 5 && end >= 5 ||
                            path == "src/safe-client.ts" && start <= 2 && end >= 2)
                            uses.Add("comparison");
                    }
                    annotation["findings"]![index]!["safe_line_uses"] = uses;
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
                        }
                    }
                }
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
}
