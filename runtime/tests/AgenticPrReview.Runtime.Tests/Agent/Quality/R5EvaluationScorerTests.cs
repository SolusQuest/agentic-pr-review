using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R5EvaluationScorerTests
{
    [Fact]
    public async Task ExecutableSelfTestChecksEveryNamedPositiveAndNegative()
    {
        var result = await EvaluationSelfTest.RunAsync();
        Assert.True(result.Passed);
        Assert.Equal(16, result.Outcomes.Length);
        Assert.Contains(result.Outcomes, o => o.Code == EvaluationCode.Scored);
        foreach (var code in new[] { EvaluationCode.SubjectInvalid, EvaluationCode.WrongSnapshot,
            EvaluationCode.RequiredObservationMissing, EvaluationCode.ExpectedFindingMissing,
            EvaluationCode.ProhibitedFinding, EvaluationCode.RequiredToolMissing,
            EvaluationCode.DuplicateObservation, EvaluationCode.AdjudicationInvalid })
            Assert.Contains(result.Outcomes, o => o.Code == code);
    }

    [Theory]
    [InlineData("deterministic")]
    [InlineData("live")]
    public async Task ProseAloneNeverEstablishesSemanticTruthOrReusesOldAnnotation(string mode)
    {
        var first = await EvaluationSelfTest.CreateAsync(mode: mode);
        var changed = await EvaluationSelfTest.CreateAsync(prose: "Arbitrary opposite claim", mode: mode);
        var original = first.Admit()!;
        var current = changed.Admit()!;
        var before = EvaluationScorer.Evaluate(first.Case, original);
        var after = EvaluationScorer.Evaluate(changed.Case, current);
        Assert.Equal(1, before.StructuralMatches);
        Assert.Equal(before.StructuralMatches, after.StructuralMatches);
        Assert.Equal(ModelObservationStatus.Unadjudicated, after.ModelStatus);
        Assert.Equal(0, after.AdjudicatedTrue);
        Assert.Equal(1, after.UnadjudicatedFindings);
        Assert.NotEqual(original.ExecutionSha256, current.ExecutionSha256);
        var stale = EvaluationSelfTest.Annotation(first.Case, original, new FindingAdjudication(0, "confirmed", "defect-a"));
        Assert.Equal(EvaluationCode.AdjudicationInvalid, EvaluationScorer.Evaluate(changed.Case, current, stale).Code);
    }

    [Fact]
    public async Task AnnotationIsBoundToEveryIdentityAndCannotOverrideFailedEvidence()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var subject = fixture.Admit()!;
        var annotation = EvaluationSelfTest.Annotation(fixture.Case, subject, new FindingAdjudication(0, "confirmed", "defect-a"));
        Assert.Equal(1, EvaluationScorer.Evaluate(fixture.Case, subject, annotation).AdjudicatedDefects);
        var other = new string('d', 64);
        foreach (var stale in new[]
        {
            annotation with { CorpusSha256 = other }, annotation with { CaseSha256 = other },
            annotation with { ConfigurationSha256 = other }, annotation with { ExecutionSha256 = other },
            annotation with { Findings = [new(1, "confirmed", "defect-a")] },
            annotation with { Findings = [new(0, "confirmed", "unknown-defect")] },
            annotation with { Findings = [new(0, "confirmed", "defect-a"), new(0, "rejected", null)] },
            annotation with { Findings = [new(0, "rejected", "defect-a")] },
        }) Assert.Equal(EvaluationCode.AdjudicationInvalid, EvaluationScorer.Evaluate(fixture.Case, subject, stale).Code);
        var wrong = EvaluationCase.Admit(fixture.Case.Input with
        { ReviewedIdentity = fixture.Case.Input.ReviewedIdentity with { HeadSha = new string('9', 40) } })!;
        var boundToWrongCase = EvaluationSelfTest.Annotation(wrong, subject, new FindingAdjudication(0, "confirmed", "defect-a"));
        var outcome = EvaluationScorer.Evaluate(wrong, subject, boundToWrongCase);
        Assert.Equal(EvaluationCode.WrongSnapshot, outcome.Code);
        Assert.Equal(ModelObservationStatus.NotEvaluated, outcome.ModelStatus);
        Assert.Equal(0, outcome.AdjudicatedTrue);
    }

    [Fact]
    public async Task OneFindingCannotCreditTwoDefectsAndTwoFindingsCannotCreditOneDefectTwice()
    {
        var single = await EvaluationSelfTest.CreateAsync();
        var spec = single.Case.Input;
        var twoDefects = EvaluationCase.Admit(spec with { Defects = [spec.Defects[0], spec.Defects[0] with { Id = "defect-b" }] })!;
        var outcome = EvaluationScorer.Evaluate(twoDefects, single.Admit());
        Assert.Equal(1, outcome.StructuralMatches);
        Assert.Equal(1, outcome.StructurallyMissingDefects);
        var reversed = EvaluationCase.Admit(twoDefects.Input with { Defects = twoDefects.Input.Defects.Reverse().ToImmutableArray() })!;
        Assert.Equal(outcome.StructuralMatches, EvaluationScorer.Evaluate(reversed, single.Admit()).StructuralMatches);
        var pair = await EvaluationSelfTest.CreateAsync(findingCount: 2);
        var subject = pair.Admit()!;
        var duplicate = EvaluationSelfTest.Annotation(pair.Case, subject,
            new FindingAdjudication(0, "confirmed", "defect-a"), new FindingAdjudication(1, "confirmed", "defect-a"));
        Assert.Equal(EvaluationCode.AdjudicationInvalid, EvaluationScorer.Evaluate(pair.Case, subject, duplicate).Code);
        Assert.Equal(1, EvaluationScorer.Evaluate(pair.Case, subject).DuplicateObservations);
    }

    [Fact]
    public async Task MaximumMatchingReassignsAmbiguousFirstFindingAndIsPermutationInvariant()
    {
        var fixture = await EvaluationSelfTest.CreateAsync(findingFactory: observation =>
        [
            new("high", "Both locations", "First candidate", [new(observation, EvaluationSelfTest.SourcePath, 1, 1), new(observation, EvaluationSelfTest.SourcePath, 2, 2)]),
            new("high", "Only first location", "Second candidate", [new(observation, EvaluationSelfTest.SourcePath, 1, 1)]),
        ]);
        var spec = fixture.Case.Input;
        var two = EvaluationCase.Admit(spec with
        { Defects = [spec.Defects[0], spec.Defects[0] with { Id = "defect-b", StartLine = 2, EndLine = 2 }] })!;
        var result = EvaluationScorer.Evaluate(two, fixture.Admit());
        Assert.Equal(2, result.StructuralMatches);
        Assert.Equal(0, result.StructurallyMissingDefects);
        Assert.Equal(0, result.AdjudicatedDefects);
        var reversed = await EvaluationSelfTest.CreateAsync(findingFactory: _ => fixture.Input.Outcome.Review!.Findings.Reverse().ToImmutableArray());
        Assert.Equal(result.StructuralMatches, EvaluationScorer.Evaluate(two, reversed.Admit()).StructuralMatches);
    }

    [Fact]
    public async Task ExecutionBindingChangesWithRunSourceModeConfigurationAndStablePolicy()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var baseline = fixture.Admit()!;
        var rebuilt = (await EvaluationSelfTest.CreateAsync(buildId: "another-build")).Admit()!;
        Assert.Equal(baseline.ConfigurationSha256, rebuilt.ConfigurationSha256);
        Assert.NotEqual(baseline.ExecutionSha256, rebuilt.ExecutionSha256);
        foreach (var run in new[]
        {
            fixture.Run with { RunId = "another-run" }, fixture.Run with { Mode = "live" },
            fixture.Run with { SourceCommit = new string('a', 40) }, fixture.Run with { SourceTree = new string('b', 40) },
            fixture.Run with { SourceClean = !fixture.Run.SourceClean },
            fixture.Run with { ProviderConfigurationSha256 = new string('c', 64) },
        }) Assert.NotEqual(baseline.ExecutionSha256, EvaluationSubject.Admit(fixture.Input, run)!.ExecutionSha256);
        Assert.Null(EvaluationSubject.Admit(fixture.Input with { Run = null! }, fixture.Run));
        Assert.Null(EvaluationSubject.Admit(fixture.Input with { Outcome = null! }, fixture.Run));
    }

    [Fact]
    public async Task CandidateAdmissionRejectsDetachedReviewChangedObservationAndWrongRun()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        Assert.NotNull(fixture.Admit());
        var original = fixture.Input.Outcome;
        var detached = original with { Review = original.Review! with { Findings = [] } };
        Assert.Null(EvaluationSubject.Admit(fixture.Input with { Outcome = detached }, fixture.Run));
        var events = original.Events.Select(e => e is AgentToolResultEvent result
            ? result with { ObservationId = new string('e', 64) } : e).ToImmutableArray();
        Assert.Null(EvaluationSubject.Admit(fixture.Input with { Outcome = original with { Events = events } }, fixture.Run));
        Assert.Null(EvaluationSubject.Admit(fixture.Input with
        { Run = fixture.Input.Run with { ReviewedIdentity = fixture.Input.Run.ReviewedIdentity with { HeadSha = new string('8', 40) } } }, fixture.Run));
        Assert.Null(EvaluationSubject.Admit(fixture.Input, fixture.Run with { RunId = "../private" }));
    }

    [Fact]
    public async Task CompletedAssertionFailuresRetainExecutionStatusAndCannotBeRescued()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var subject = fixture.Admit()!;
        var noTool = await EvaluationSelfTest.CreateAsync(findingCount: 0, includeTool: false);
        var wrongScope = EvaluationCase.Admit(fixture.Case.Input with
        { ReviewedIdentity = fixture.Case.Input.ReviewedIdentity with { HeadSha = new string('9', 40) } })!;
        var wrongObservation = EvaluationCase.Admit(fixture.Case.Input with
        { RequiredObservations = [new(AgentToolRegistry.ReadFileName, new string('e', 64))] })!;
        foreach (var (testCase, completed, expected) in new[]
        {
            (noTool.Case, noTool.Admit()!, EvaluationCode.RequiredToolMissing),
            (wrongScope, subject, EvaluationCode.WrongSnapshot),
            (wrongObservation, subject, EvaluationCode.RequiredObservationMissing),
        })
        {
            var annotation = EvaluationSelfTest.Annotation(testCase, completed);
            var result = EvaluationScorer.Evaluate(testCase, completed, annotation);
            Assert.Equal(expected, result.Code);
            Assert.Equal(EvaluationStatus.Completed, result.ExecutionStatus);
            Assert.Equal(AssertionStatus.Failed, result.EvidenceStatus);
            Assert.Equal(AssertionStatus.NotEvaluated, result.ScenarioStatus);
            Assert.Equal(EvaluationFailureSource.None, result.FailureSource);
            Assert.Equal(EvaluationFailureKind.None, result.FailureKind);
            Assert.Equal(ModelObservationStatus.NotEvaluated, result.ModelStatus);
            Assert.Equal(completed.ExecutionSha256, result.ExecutionSha256);
            Assert.Equal(completed.Attempt.AttemptSha256, result.AttemptSha256);
        }
        var stale = EvaluationSelfTest.Annotation(fixture.Case, subject) with { ExecutionSha256 = new string('d', 64) };
        var invalidAnnotation = EvaluationScorer.Evaluate(fixture.Case, subject, stale);
        Assert.Equal(EvaluationStatus.Completed, invalidAnnotation.ExecutionStatus);
        Assert.Equal(EvaluationFailureSource.Evaluator, invalidAnnotation.FailureSource);
        Assert.Equal(AssertionStatus.Passed, invalidAnnotation.EvidenceStatus);
        Assert.Equal(ModelObservationStatus.NotEvaluated, invalidAnnotation.ModelStatus);
    }

    [Fact]
    public async Task ConfigurationCohortIgnoresCaseHistoryAndSourceButBindsActualSettings()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var baseline = fixture.Admit()!;
        var changedRequest = (await EvaluationSelfTest.CreateAsync(reviewContext: "Different request only")).Admit()!;
        Assert.Equal(baseline.ConfigurationSha256, changedRequest.ConfigurationSha256);
        Assert.NotEqual(baseline.ExecutionSha256, changedRequest.ExecutionSha256);
        var otherCase = await EvaluationSelfTest.CreateAsync(reviewContext: "A different authored case " + EvaluationSelfTest.Canary,
            reviewedIdentity: EvaluationSelfTest.Identity with { RepositoryId = "another/repository", ReviewTarget = 242 },
            buildId: "other-build");
        var otherSubject = otherCase.Admit()!;
        Assert.Equal(baseline.ConfigurationSha256, otherSubject.ConfigurationSha256);
        Assert.NotEqual(fixture.Case.Sha256, otherCase.Case.Sha256);
        Assert.NotEqual(baseline.ExecutionSha256, otherSubject.ExecutionSha256);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(fixture.Input.TrustedRequest,
            new string('a', 64), out var restored));
        Assert.Equal(baseline.ConfigurationSha256,
            EvaluationAttempt.ConfigurationIdentity(restored!.StablePlan, fixture.Run));
        foreach (var changed in new[]
        {
            await EvaluationSelfTest.CreateAsync(policy: "Changed trusted policy"),
            await EvaluationSelfTest.CreateAsync(provider: "another-provider"),
            await EvaluationSelfTest.CreateAsync(model: "another-model"),
            await EvaluationSelfTest.CreateAsync(adapter: "another-adapter"),
            await EvaluationSelfTest.CreateAsync(mode: "live"),
        }) Assert.NotEqual(baseline.ConfigurationSha256, changed.Admit()!.ConfigurationSha256);
        var plan = fixture.Input.Run.StablePlan;
        foreach (var changed in new[] { plan with { ToolsetSha256 = new string('a', 64) },
            plan with { LimitsSha256 = new string('b', 64) } })
            Assert.NotEqual(baseline.ConfigurationSha256, EvaluationAttempt.ConfigurationIdentity(changed, fixture.Run));
        Assert.NotEqual(baseline.ConfigurationSha256, EvaluationAttempt.Admit(fixture.Input.TrustedRequest,
            fixture.Run with { ProviderConfigurationSha256 = new string('f', 64) })!.ConfigurationSha256);
        // Provider/model IDs allow UTF-8, so delimiter-containing tuples must stay distinct.
        Assert.NotEqual(EvaluationAttempt.Admit(fixture.Input.TrustedRequest with { ProviderId = "a\0b", ModelId = "c" }, fixture.Run)!.ConfigurationSha256,
            EvaluationAttempt.Admit(fixture.Input.TrustedRequest with { ProviderId = "a", ModelId = "b\0c" }, fixture.Run)!.ConfigurationSha256);
    }

    [Fact]
    public async Task KnownFailuresKeepAttemptConfigurationSourceAndModeBeforeCompletion()
    {
        var fixture = await EvaluationSelfTest.CreateAsync(providerFailure: EvaluationSelfTest.Canary);
        Assert.False(fixture.Input.Outcome.Succeeded);
        var failure = EvaluationFailure.FromAgentOutcome(fixture.Input.Outcome);
        var baseline = EvaluationScorer.Failure(fixture.Case, failure, fixture.Attempt);
        Assert.Equal(fixture.Run.SourceCommit, baseline.SourceCommit);
        Assert.Equal(fixture.Run.SourceTree, baseline.SourceTree);
        Assert.Equal(fixture.Run.SourceClean, baseline.SourceClean);
        Assert.Equal(fixture.Run.Mode, baseline.Mode);
        Assert.Equal(fixture.Attempt.ConfigurationSha256, baseline.ConfigurationSha256);
        Assert.Equal(fixture.Attempt.AttemptSha256, baseline.AttemptSha256);
        Assert.Null(baseline.ExecutionSha256);
        Assert.Equal(ModelObservationStatus.NotEvaluated, baseline.ModelStatus);
        foreach (var run in new[] { fixture.Run with { RunId = "retry" }, fixture.Run with { Mode = "live" },
            fixture.Run with { ProviderConfigurationSha256 = new string('d', 64) },
            fixture.Run with { SourceCommit = new string('a', 40) }, fixture.Run with { SourceTree = new string('b', 40) },
            fixture.Run with { SourceClean = !fixture.Run.SourceClean } })
        {
            var attempt = EvaluationAttempt.Admit(fixture.Input.TrustedRequest, run)!;
            var result = EvaluationScorer.Failure(fixture.Case, failure, attempt);
            Assert.NotEqual(baseline.AttemptSha256, result.AttemptSha256);
            Assert.Equal(attempt.ConfigurationSha256, result.ConfigurationSha256);
            Assert.Equal(run.Mode, result.Mode);
            Assert.Equal(run.SourceCommit, result.SourceCommit);
            Assert.Null(result.ExecutionSha256);
        }
        var detached = EvaluationScorer.Failure(fixture.Case, EvaluationFailure.Invalid);
        Assert.Null(detached.AttemptSha256);
        Assert.Null(detached.ConfigurationSha256);
        Assert.Null(detached.SourceCommit);
        Assert.Null(EvaluationAttempt.Admit(null, fixture.Run));
        Assert.Null(EvaluationAttempt.Admit(fixture.Input.TrustedRequest, fixture.Run with { Mode = "unknown" }));
        Assert.Null(EvaluationAttempt.Admit(fixture.Input.TrustedRequest with { TrustedPolicyBytes = [0xff] }, fixture.Run));
        Assert.Equal("evaluation_attempt", fixture.Attempt.ToString());
        Assert.DoesNotContain(EvaluationSelfTest.Canary, Encoding.UTF8.GetString(EvaluationJson.Write(baseline)));
    }

    [Fact]
    public async Task NoncompletedFailuresRemainDistinctWithoutGuessingUnknownCauses()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var malformed = await EvaluationSelfTest.CreateAsync(ungrounded: true);
        var tool = await EvaluationSelfTest.CreateAsync(toolFailure: AgentFailureCodes.ToolIoFailed);
        var genericChatFailure = await EvaluationSelfTest.CreateAsync(providerFailure: EvaluationSelfTest.Canary);
        var failedState = AgentSessionBuilder.Build(fixture.Input with { CurrentReviewContextIndex = -1 });
        var failures = new[]
        {
            (EvaluationFailure.FromProviderTransport(new HttpRequestException(EvaluationSelfTest.Canary)), EvaluationFailureSource.Provider, EvaluationFailureKind.ProviderCall),
            (EvaluationFailure.FromAgentOutcome(malformed.Input.Outcome), EvaluationFailureSource.Agent, EvaluationFailureKind.MalformedOutput),
            (EvaluationFailure.FromAgentOutcome(tool.Input.Outcome), EvaluationFailureSource.Tool, EvaluationFailureKind.ToolOperation),
            (EvaluationFailure.FromSessionBuild(failedState), EvaluationFailureSource.HostState, EvaluationFailureKind.StateAdmission),
            (EvaluationFailure.Invalid, EvaluationFailureSource.Evaluator, EvaluationFailureKind.InvalidInput),
            (EvaluationFailure.FromAgentOutcome(genericChatFailure.Input.Outcome), EvaluationFailureSource.Unknown, EvaluationFailureKind.Unknown),
        };
        foreach (var (failure, source, kind) in failures)
        {
            var result = EvaluationScorer.Failure(fixture.Case, failure, fixture.Attempt);
            Assert.Equal(fixture.Attempt.AttemptSha256, result.AttemptSha256);
            Assert.Equal(fixture.Attempt.ConfigurationSha256, result.ConfigurationSha256);
            Assert.Equal(source, result.FailureSource);
            Assert.Equal(kind, result.FailureKind);
            Assert.Equal(ModelObservationStatus.NotEvaluated, result.ModelStatus);
            Assert.Equal(AssertionStatus.NotEvaluated, result.ScenarioStatus);
            Assert.NotEqual(EvaluationStatus.Completed, result.ExecutionStatus);
            Assert.DoesNotContain(EvaluationSelfTest.Canary, Encoding.UTF8.GetString(EvaluationJson.Write(result)));
        }
    }

    [Fact]
    public async Task ClosedJsonRejectsUnknownDuplicateMissingNullMalformedAndOversizedInput()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var bytes = EvaluationJson.Write(fixture.Case.Input);
        var json = Encoding.UTF8.GetString(bytes);
        Assert.NotNull(EvaluationJson.ReadCase(bytes));
        Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes(json.Insert(1, "\"unknown\":true,"))));
        Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes(json.Insert(1, "\"id\":\"duplicate\","))));
        Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes(json.Replace("\"id\":\"synthetic-scorer\",", "", StringComparison.Ordinal))));
        Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes(json.Replace("\"defects\":[", "\"defects\":null,\"wrong\":[", StringComparison.Ordinal))));
        Assert.Null(EvaluationJson.ReadCase("null"u8));
        Assert.Null(EvaluationJson.ReadCase([0xff]));
        Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes("{\"id\":\"\\ud800\"}")));
        Assert.Null(EvaluationJson.ReadCase(new byte[EvaluationLimits.InputBytes + 1]));
        Assert.Null(EvaluationJson.ReadCase(Encoding.UTF8.GetBytes(new string('[', EvaluationLimits.Depth + 1) + "0" + new string(']', EvaluationLimits.Depth + 1))));
        var maxBytes = new byte[EvaluationLimits.InputBytes];
        bytes.CopyTo(maxBytes, 0);
        Array.Fill(maxBytes, (byte)' ', bytes.Length, maxBytes.Length - bytes.Length);
        Assert.NotNull(EvaluationJson.ReadCase(maxBytes));
        Assert.False(EvaluationJsonContext.Default.Options.AllowDuplicateProperties);
    }

    [Fact]
    public async Task ContractBoundsAndContentIdentitiesAreEnforced()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var spec = fixture.Case.Input;
        var maximum = spec with { Defects = Enumerable.Range(0, AgentLimits.Findings)
            .Select(i => spec.Defects[0] with { Id = "defect-" + i }).ToImmutableArray() };
        Assert.NotNull(EvaluationCase.Admit(maximum));
        Assert.Null(EvaluationCase.Admit(maximum with { Defects = maximum.Defects.Add(spec.Defects[0]) }));
        Assert.NotNull(EvaluationCase.Admit(spec with { Id = new string('a', EvaluationLimits.Identifier) }));
        Assert.Null(EvaluationCase.Admit(spec with { Id = new string('a', EvaluationLimits.Identifier + 1) }));
        Assert.Null(EvaluationCase.Admit(spec with { Defects = [spec.Defects[0] with { Path = "../secret" }] }));
        Assert.Null(EvaluationCase.Admit(spec with { RequiredObservations = [new("arbitrary-tool", new string('a', 64))] }));
        Assert.Null(EvaluationCase.Admit(spec with { Defects = default }));
        Assert.Null(EvaluationCase.Admit(spec with { Defects = [null!] }));
        var changed = EvaluationCase.Admit(spec with { Defects = [spec.Defects[0] with { Severity = "low" }] })!;
        Assert.NotEqual(fixture.Case.Sha256, changed.Sha256);
        var runBytes = EvaluationJson.Write(fixture.Run);
        Assert.NotNull(EvaluationJson.ReadRun(runBytes));
        Assert.Null(EvaluationJson.ReadRun(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(runBytes).Insert(1, "\"mode\":\"live\","))));
        var subject = fixture.Admit()!;
        var annotation = EvaluationSelfTest.Annotation(fixture.Case, subject, new FindingAdjudication(0, "rejected", null));
        Assert.NotNull(EvaluationJson.ReadAdjudication(EvaluationJson.Write(annotation)));
        Assert.Null(EvaluationJson.ReadAdjudication(EvaluationJson.Write(annotation with { Findings = [new(-1, "confirmed", null)] })));
    }

    [Fact]
    public async Task ReportsContainOnlyBoundedProjectionAndNoPrivateCandidateOrToolBytes()
    {
        var fixture = await EvaluationSelfTest.CreateAsync();
        var subject = fixture.Admit()!;
        var outcome = EvaluationScorer.Evaluate(fixture.Case, subject);
        var json = Encoding.UTF8.GetString(EvaluationJson.Write(outcome));
        foreach (var privateText in new[] { EvaluationSelfTest.Canary, "Synthetic candidate", EvaluationSelfTest.SourcePath,
            "return value", "synthetic-provider", "synthetic-session", "Review synthetic" })
            Assert.DoesNotContain(privateText, json);
        Assert.True(json.Length < 4096);
        Assert.Equal("evaluation_subject", subject.ToString());
        Assert.Equal("evaluation_outcome", outcome.ToString());
        Assert.Equal(outcome, EvaluationScorer.Evaluate(fixture.Case, subject));
    }
}
