using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R3QualityEvaluatorTests
{
    [Fact]
    public async Task MustFindPassesOnlyWithRequiredObservationAndSemanticFinding()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var expectation = Assert.IsType<R3QualityMustFindExpectation>(
            testCase.Expectation);
        var finding = Finding(
            expectation.ExpectedSeverity,
            expectation.ExpectedFindingToken,
            "The synthetic null assertion can fail before Trim.",
            expectation.Evidence);
        var completed = await BootstrapAsync(
            testCase,
            [RequiredDiff(expectation)],
            "Synthetic review complete.",
            [finding]);

        var creation = R3QualitySubject.TryCreateCompleted(
            completed.Input,
            Fresh());
        var outcome = R3QualityEvaluator.Evaluate(testCase, creation);

        Assert.True(creation.Succeeded, creation.FailureCode);
        Assert.Equal("passed", outcome.Status);
        Assert.Equal("quality", outcome.Classification);
        Assert.Equal(R3QualityCodes.Passed, outcome.Code);
        Assert.Equal(1, outcome.FindingCount);
        Assert.Equal(1, outcome.ToolCallCount);
        Assert.NotNull(outcome.TerminalSha256);
    }

    [Fact]
    public async Task MustFindRejectsSubstitutedCurrentContextAndPriorSession()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var expectation = Assert.IsType<R3QualityMustFindExpectation>(
            testCase.Expectation);
        var substitutedContexts = new[]
        {
            "Different marker-free synthetic review context.",
            string.Concat(
                "Different synthetic review context containing ",
                expectation.TargetMarker),
        };
        foreach (var currentContext in substitutedContexts)
        {
            var substituted = await BootstrapAsync(
                testCase,
                [],
                "No finding.",
                [],
                currentContext);

            var substitutedOutcome = Evaluate(
                testCase,
                substituted.Input,
                Fresh());

            Assert.Equal("not_evaluated", substitutedOutcome.Status);
            Assert.Equal(R3QualityCodes.SubjectInvalid, substitutedOutcome.Code);
        }

        using var generationOne = await ContinuationAsync(
            testCase,
            "No finding.",
            string.Concat(
                "Unrelated predecessor history containing ",
                expectation.TargetMarker));
        var generationOneOutcome = Evaluate(
            testCase,
            generationOne.Input,
            Fresh());

        Assert.Equal("not_evaluated", generationOneOutcome.Status);
        Assert.Equal(R3QualityCodes.SubjectInvalid, generationOneOutcome.Code);
    }

    [Fact]
    public async Task LiveAgentVerifierMustFindNegativesRemainExecutable()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var expectation = Assert.IsType<R3QualityMustFindExpectation>(
            testCase.Expectation);
        var noTool = await BootstrapAsync(
            testCase,
            [],
            "No grounded finding.",
            []);
        var missing = Evaluate(testCase, noTool.Input, Fresh());
        Assert.Equal(R3QualityCodes.RequiredToolMissing, missing.Code);

        var wrongArguments = string.Concat(
            "{\"path\":\"",
            expectation.Evidence.Path,
            "\",\"start_line\":1,\"line_count\":5}");
        var wrongToolCase = testCase with
        {
            Files = testCase.Files.Select(file =>
                    StringComparer.Ordinal.Equals(
                        file.Path,
                        expectation.Evidence.Path)
                        ? file with
                        {
                            Content = file.Content.Replace(
                                "namespace SyntheticQuality;",
                                string.Concat(
                                    "namespace SyntheticQuality; // ",
                                    expectation.TargetMarker),
                                StringComparison.Ordinal),
                        }
                        : file)
                .ToImmutableArray(),
        };
        var wrongTool = await BootstrapAsync(
            wrongToolCase,
            [new ToolCall("read0", AgentToolRegistry.ReadFileName, wrongArguments)],
            "Observed through the wrong tool.",
            []);
        var wrong = Evaluate(wrongToolCase, wrongTool.Input, Fresh());
        Assert.Equal(R3QualityCodes.RequiredToolWrong, wrong.Code);

        var nonmatching = await BootstrapAsync(
            testCase,
            [new ToolCall(
                "diff0",
                AgentToolRegistry.ReadDiffName,
                string.Concat(
                    "{\"path\":\"",
                    expectation.Evidence.Path,
                    "\",\"start_hunk\":2,\"hunk_count\":20}"))],
            "Required page was not returned.",
            []);
        var observationMissing = Evaluate(testCase, nonmatching.Input, Fresh());
        Assert.Equal(
            R3QualityCodes.RequiredObservationMissing,
            observationMissing.Code);

        var duplicate = await BootstrapAsync(
            testCase,
            [
                RequiredDiff(expectation, "diff0"),
                RequiredDiff(expectation, "diff1"),
            ],
            "Duplicate observation isolation vector.",
            []);
        var isolation = Evaluate(testCase, duplicate.Input, Fresh());
        Assert.Equal("not_evaluated", isolation.Status);
        Assert.Equal("evaluator", isolation.Classification);
        Assert.Equal(
            R3QualityCodes.ObservationIsolationInvalid,
            isolation.Code);
    }

    [Fact]
    public async Task MustFindRejectsInitialLeakAndSemanticMismatchByPhase()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var expectation = Assert.IsType<R3QualityMustFindExpectation>(
            testCase.Expectation);
        var validFinding = Finding(
            expectation.ExpectedSeverity,
            expectation.ExpectedFindingToken,
            "Grounded synthetic defect.",
            expectation.Evidence);
        var leaked = await BootstrapAsync(
            testCase,
            [RequiredDiff(expectation)],
            "Complete.",
            [validFinding],
            initialContext: null,
            trustedPolicy: string.Concat(
                "Public synthetic quality policy with ",
                expectation.TargetMarker));
        var leakOutcome = Evaluate(testCase, leaked.Input, Fresh());
        Assert.Equal("not_evaluated", leakOutcome.Status);
        Assert.Equal(R3QualityCodes.InitialContextLeak, leakOutcome.Code);

        var mismatchedFinding = Finding(
            expectation.ExpectedSeverity,
            "Different synthetic allegation",
            "The required semantic token is intentionally absent.",
            expectation.Evidence);
        var mismatched = await BootstrapAsync(
            testCase,
            [RequiredDiff(expectation)],
            "Complete.",
            [mismatchedFinding]);
        var mismatchOutcome = Evaluate(testCase, mismatched.Input, Fresh());
        Assert.Equal("failed", mismatchOutcome.Status);
        Assert.Equal(
            R3QualityCodes.ExpectedFindingMissing,
            mismatchOutcome.Code);
    }

    [Fact]
    public async Task LiveAgentVerifierAlteredProductSubjectsRemainInvalid()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var expectation = Assert.IsType<R3QualityMustFindExpectation>(
            testCase.Expectation);
        var finding = Finding(
            expectation.ExpectedSeverity,
            expectation.ExpectedFindingToken,
            "Grounded synthetic defect.",
            expectation.Evidence);
        var completed = await BootstrapAsync(
            testCase,
            [RequiredDiff(expectation)],
            "Complete.",
            [finding]);

        var originalReview = completed.Input.Outcome.Review!;
        var detachedOutcome = completed.Input.Outcome with
        {
            Review = originalReview with { Findings = [] },
        };
        var detachedCreation = R3QualitySubject.TryCreateCompleted(
            completed.Input with { Outcome = detachedOutcome },
            Fresh());
        Assert.False(detachedCreation.Succeeded);
        Assert.Equal(
            R3QualityCodes.SubjectInvalid,
            R3QualityEvaluator.Evaluate(testCase, detachedCreation).Code);

        var events = completed.Input.Outcome.Events;
        var resultIndex = Enumerable.Range(0, events.Length).Single(index =>
            events[index] is AgentToolResultEvent);
        var result = Assert.IsType<AgentToolResultEvent>(events[resultIndex]);
        var altered = result.CanonicalResult.ToArray();
        altered[^1] = altered[^1] == (byte)'}' ? (byte)' ' : (byte)'}';
        var alteredOutcome = completed.Input.Outcome with
        {
            Events = events.SetItem(
                resultIndex,
                result with
                {
                    CanonicalResult = ImmutableArray.CreateRange(altered),
                }),
        };
        var alteredCreation = R3QualitySubject.TryCreateCompleted(
            completed.Input with { Outcome = alteredOutcome },
            Fresh());
        Assert.False(alteredCreation.Succeeded);
        Assert.Equal(
            R3QualityCodes.SubjectInvalid,
            R3QualityEvaluator.Evaluate(testCase, alteredCreation).Code);
    }

    [Fact]
    public async Task UngroundedIdentityPathAndRangeRemainProductFailures()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var expectation = Assert.IsType<R3QualityMustFindExpectation>(
            testCase.Expectation);
        AgentEvidence[] invalidEvidence =
        [
            expectation.Evidence with { ObservationId = new string('f', 64) },
            expectation.Evidence with { Path = "src/Other.cs" },
            expectation.Evidence with { EndLine = 6 },
        ];
        foreach (var evidence in invalidEvidence)
        {
            var trusted = Trusted(testCase);
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized));
            var run = new AgentRunRequest(
                testCase.ReviewedIdentity,
                materialized!.StablePlan,
                "quality-invalid-evidence",
                [.. materialized.ControlMessages, User(testCase.InitialContext)]);
            var outcome = await CompleteAsync(
                testCase,
                run,
                [RequiredDiff(expectation)],
                "Invalid evidence vector.",
                [Finding(
                    expectation.ExpectedSeverity,
                    expectation.ExpectedFindingToken,
                    "This finding is intentionally ungrounded.",
                    evidence)]);

            Assert.False(outcome.Succeeded);
            Assert.Equal(AgentFailureCodes.TerminalInvalid, outcome.Diagnostic!.Code);
            Assert.True(R3QualitySubject.TryCreateProductFailure(
                outcome.Diagnostic.Code,
                findingCount: 0,
                toolCallCount: 1,
                out var productFailure));
            var quality = R3QualityEvaluator.Evaluate(testCase, productFailure!);
            Assert.Equal("not_evaluated", quality.Status);
            Assert.Equal("product", quality.Classification);
            Assert.Equal(R3QualityCodes.ProductFailed, quality.Code);
        }
    }

    [Theory]
    [InlineData("critical")]
    [InlineData("high")]
    [InlineData("medium")]
    [InlineData("low")]
    public async Task MustNotRejectsPinnedScopeAtEverySeverity(string severity)
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[1];
        var expectation = Assert.IsType<R3QualityMustNotFindExpectation>(
            testCase.Expectation);
        var finding = Finding(
            severity,
            "Synthetic false positive",
            "This allegation targets the prohibited safe line.",
            new AgentEvidence(
                expectation.RequiredObservationId,
                expectation.Path,
                4,
                4));
        var completed = await BootstrapAsync(
            testCase,
            [RequiredDiff(expectation)],
            "Complete.",
            [finding]);

        var outcome = Evaluate(testCase, completed.Input, Fresh());

        Assert.Equal("failed", outcome.Status);
        Assert.Equal(R3QualityCodes.ProhibitedFinding, outcome.Code);
    }

    [Fact]
    public async Task LiveAgentVerifierMustNotNegativesRemainExecutable()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[1];
        var expectation = Assert.IsType<R3QualityMustNotFindExpectation>(
            testCase.Expectation);
        var readArguments = string.Concat(
            "{\"path\":\"",
            expectation.Path,
            "\",\"start_line\":4,\"line_count\":2}");
        var alternateObservation = await ObserveAsync(
            testCase,
            AgentToolRegistry.ReadFileName,
            readArguments);
        var finding = Finding(
            "low",
            "Alternate synthetic false positive",
            "A covering evidence range still includes the prohibited line.",
            new AgentEvidence(
                alternateObservation.ObservationId,
                expectation.Path,
                4,
                5));
        var completed = await BootstrapAsync(
            testCase,
            [new ToolCall("read0", AgentToolRegistry.ReadFileName, readArguments)],
            "Complete.",
            [finding]);

        var outcome = Evaluate(testCase, completed.Input, Fresh());

        Assert.Equal(R3QualityCodes.ProhibitedFinding, outcome.Code);
    }

    [Fact]
    public async Task MustNotAllowsNoFindingAndUnrelatedGroundedLine()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[1];
        var expectation = Assert.IsType<R3QualityMustNotFindExpectation>(
            testCase.Expectation);
        var clean = await BootstrapAsync(
            testCase,
            [],
            "No actionable defects.",
            []);
        Assert.Equal(
            R3QualityCodes.Passed,
            Evaluate(testCase, clean.Input, Fresh()).Code);

        var readArguments = string.Concat(
            "{\"path\":\"",
            expectation.Path,
            "\",\"start_line\":4,\"line_count\":2}");
        var observation = await ObserveAsync(
            testCase,
            AgentToolRegistry.ReadFileName,
            readArguments);
        var readOnly = await BootstrapAsync(
            testCase,
            [new ToolCall("read0", AgentToolRegistry.ReadFileName, readArguments)],
            "No actionable defects after reading the safe line.",
            []);
        Assert.Equal(
            R3QualityCodes.Passed,
            Evaluate(testCase, readOnly.Input, Fresh()).Code);

        var unrelated = Finding(
            "low",
            "Synthetic unrelated scope",
            "Only the closing brace is cited.",
            new AgentEvidence(
                observation.ObservationId,
                expectation.Path,
                5,
                5));
        var completed = await BootstrapAsync(
            testCase,
            [new ToolCall("read0", AgentToolRegistry.ReadFileName, readArguments)],
            "Complete.",
            [unrelated]);

        Assert.Equal(
            R3QualityCodes.Passed,
            Evaluate(testCase, completed.Input, Fresh()).Code);
    }

    [Fact]
    public async Task ContinuationRequiresPriorFactAndExactFreshInputSet()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[2];
        var expectation = Assert.IsType<R3QualityContinuationExpectation>(
            testCase.Expectation);
        using var completed = await ContinuationAsync(
            testCase,
            string.Concat("Restored fact: ", expectation.PriorOnlyMarker));
        var validFresh = Fresh(expectation.FreshInputNames.Select(name =>
            (name, string.Concat("synthetic input for ", name))).ToArray());

        var passed = Evaluate(testCase, completed.Input, validFresh);
        Assert.Equal(R3QualityCodes.Passed, passed.Code);

        var missing = Fresh(
            (expectation.FreshInputNames[0], "one"),
            (expectation.FreshInputNames[1], "two"));
        var duplicate = Fresh(
            (expectation.FreshInputNames[0], "one"),
            (expectation.FreshInputNames[0], "two"),
            (expectation.FreshInputNames[2], "three"));
        var reordered = Fresh(
            (expectation.FreshInputNames[1], "two"),
            (expectation.FreshInputNames[0], "one"),
            (expectation.FreshInputNames[2], "three"));
        var extra = Fresh(
            (expectation.FreshInputNames[0], "one"),
            (expectation.FreshInputNames[1], "two"),
            (expectation.FreshInputNames[2], "three"),
            ("unexpected.json", "extra"));
        var leaked = Fresh(
            (expectation.FreshInputNames[0], "one"),
            (expectation.FreshInputNames[1], expectation.PriorOnlyMarker),
            (expectation.FreshInputNames[2], "three"));
        foreach (var invalid in new[] { missing, duplicate, reordered, extra, leaked })
        {
            var outcome = Evaluate(testCase, completed.Input, invalid);
            Assert.Equal("not_evaluated", outcome.Status);
            Assert.Equal(R3QualityCodes.FreshInputInvalid, outcome.Code);
        }
    }

    [Fact]
    public async Task LiveAgentVerifierMissingPriorFactFailsQuality()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[2];
        var expectation = Assert.IsType<R3QualityContinuationExpectation>(
            testCase.Expectation);
        using var completed = await ContinuationAsync(
            testCase,
            "The restored response intentionally omits the prior-only fact.");
        var fresh = Fresh(expectation.FreshInputNames.Select(name =>
            (name, "synthetic current input")).ToArray());

        var outcome = Evaluate(testCase, completed.Input, fresh);

        Assert.Equal("failed", outcome.Status);
        Assert.Equal(R3QualityCodes.PriorFactMissing, outcome.Code);
    }

    [Fact]
    public async Task ContinuationRejectsFindingsEvenWhenGroundedAndFactBearing()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[2];
        var expectation = Assert.IsType<R3QualityContinuationExpectation>(
            testCase.Expectation);
        const string arguments =
            "{\"path\":\"src/RetryBudget.cs\"}";
        var observation = await ObserveAsync(
            testCase,
            AgentToolRegistry.ReadDiffName,
            arguments);
        var finding = Finding(
            "low",
            "Synthetic continuation finding",
            expectation.PriorOnlyMarker,
            new AgentEvidence(
                observation.ObservationId,
                "src/RetryBudget.cs",
                4,
                4));
        using var completed = await ContinuationAsync(
            testCase,
            string.Concat("Restored fact: ", expectation.PriorOnlyMarker),
            currentCalls:
            [
                new ToolCall(
                    "diff-current",
                    AgentToolRegistry.ReadDiffName,
                    arguments),
            ],
            currentFindings: [finding]);
        var fresh = Fresh(expectation.FreshInputNames.Select(name =>
            (name, "synthetic current input")).ToArray());

        var outcome = Evaluate(testCase, completed.Input, fresh);

        Assert.Equal("failed", outcome.Status);
        Assert.Equal(R3QualityCodes.ProhibitedFinding, outcome.Code);
    }

    [Fact]
    public async Task ContinuationRejectsUnrelatedPredecessorContext()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[2];
        var expectation = Assert.IsType<R3QualityContinuationExpectation>(
            testCase.Expectation);
        using var completed = await ContinuationAsync(
            testCase,
            string.Concat("Restored fact: ", expectation.PriorOnlyMarker),
            "Unrelated predecessor without the prior-only marker.");
        var fresh = Fresh(expectation.FreshInputNames.Select(name =>
            (name, "synthetic current input")).ToArray());

        var outcome = Evaluate(testCase, completed.Input, fresh);

        Assert.Equal("not_evaluated", outcome.Status);
        Assert.Equal(R3QualityCodes.SubjectInvalid, outcome.Code);
    }

    [Fact]
    public async Task ContinuationRejectsPriorMarkerInTrustedPolicy()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[2];
        var expectation = Assert.IsType<R3QualityContinuationExpectation>(
            testCase.Expectation);
        using var completed = await ContinuationAsync(
            testCase,
            string.Concat("Restored fact: ", expectation.PriorOnlyMarker),
            predecessorContextOverride: null,
            trustedPolicyOverride: string.Concat(
                "Public synthetic policy containing ",
                expectation.PriorOnlyMarker));
        var fresh = Fresh(expectation.FreshInputNames.Select(name =>
            (name, "synthetic current input")).ToArray());

        var outcome = Evaluate(testCase, completed.Input, fresh);

        Assert.Equal("not_evaluated", outcome.Status);
        Assert.Equal(R3QualityCodes.FreshInputInvalid, outcome.Code);
    }

    [Fact]
    public void TypedFailuresNeverMasqueradeAsQuality()
    {
        var testCase = R3QualityCorpusTests.ParseCorpus().Cases[0];
        var subjects = new List<(R3QualitySubject Subject, string Class, string Code)>();
        Assert.True(R3QualitySubject.TryCreateProductFailure(
            "agent_terminal_invalid", 0, 0, out var product));
        subjects.Add((product!, "product", R3QualityCodes.ProductFailed));
        Assert.True(R3QualitySubject.TryCreateProviderFailure(
            "agent_chat_failed", 0, 0, out var provider));
        subjects.Add((provider!, "provider", R3QualityCodes.ProviderFailed));
        Assert.True(R3QualitySubject.TryCreateToolFailure(
            "tool_io_failed", 0, 1, out var tool));
        subjects.Add((tool!, "tool", R3QualityCodes.ToolFailed));
        Assert.True(R3QualitySubject.TryCreateStateFailure(
            "session_record_invalid", 0, 1, out var state));
        subjects.Add((state!, "state", R3QualityCodes.StateFailed));
        Assert.True(R3QualitySubject.TryCreateEvaluatorFailure(
            "harness_failed", 0, 0, out var evaluator));
        subjects.Add((evaluator!, "evaluator", R3QualityCodes.EvaluatorFailed));

        foreach (var item in subjects)
        {
            var outcome = R3QualityEvaluator.Evaluate(testCase, item.Subject);
            Assert.Equal("not_evaluated", outcome.Status);
            Assert.Equal(item.Class, outcome.Classification);
            Assert.Equal(item.Code, outcome.Code);
            Assert.Null(outcome.TerminalSha256);
        }

        Assert.False(R3QualitySubject.TryCreateProviderFailure(
            "contains spaces", 0, 0, out _));
    }

    private static R3QualityOutcome Evaluate(
        R3QualityCase testCase,
        AgentSessionBuildInput input,
        R3QualityFreshProcessTwoInputSet freshInputs) =>
        R3QualityEvaluator.Evaluate(
            testCase,
            R3QualitySubject.TryCreateCompleted(input, freshInputs));

    private static async Task<CompletedInput> BootstrapAsync(
        R3QualityCase testCase,
        IReadOnlyList<ToolCall> calls,
        string summary,
        ImmutableArray<AgentFinding> findings,
        string? initialContext = null,
        string? trustedPolicy = null)
    {
        var trusted = Trusted(testCase, trustedPolicy);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(
            trusted,
            priorSessionSha256: null,
            out var materialized));
        var run = new AgentRunRequest(
            testCase.ReviewedIdentity,
            materialized!.StablePlan,
            "quality-session",
            [
                .. materialized.ControlMessages,
                User(initialContext ?? testCase.InitialContext),
            ]);
        var outcome = await CompleteAsync(testCase, run, calls, summary, findings);
        Assert.True(outcome.CompletedSessionEligible, outcome.Diagnostic?.Code);
        return new CompletedInput(
            new AgentSessionBuildInput(
                run,
                outcome,
                trusted,
                run.InitialMessages.Length - 1,
                NoContinuationCodec.Instance,
                Predecessor: null,
                AgentSessionHeadTransition.SameHead),
            trusted);
    }

    private static async Task<AgentRunOutcome> CompleteAsync(
        R3QualityCase testCase,
        AgentRunRequest run,
        IReadOnlyList<ToolCall> calls,
        string summary,
        ImmutableArray<AgentFinding> findings,
        string finishCallId = "finish0")
    {
        var responses = new Queue<ProjectChatResponse>();
        foreach (var call in calls)
        {
            responses.Enqueue(Response(new ProjectToolCallContent(
                call.CallId,
                call.Name,
                call.ArgumentsJson)));
        }

        responses.Enqueue(Response(new ProjectToolCallContent(
            finishCallId,
            AgentToolRegistry.FinishReviewName,
            Encoding.UTF8.GetString(
                AgentToolArguments.WriteFinishReview(summary, findings)))));
        using var repository = new SyntheticRepository(testCase);
        var snapshot = Snapshot(testCase, repository.Root);
        return await new AgentLoop(
            new QueueChatClient(responses),
            new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess()))
            .RunAsync(run, CancellationToken.None);
    }

    private static async Task<AgentObservation> ObserveAsync(
        R3QualityCase testCase,
        string name,
        string argumentsJson)
    {
        using var repository = new SyntheticRepository(testCase);
        var executor = new SnapshotToolExecutor(
            Snapshot(testCase, repository.Root),
            new VerifiedReviewedFileAccess());
        PreparedAgentToolCall prepared;
        if (name == AgentToolRegistry.ReadFileName &&
            AgentToolArguments.TryReadFile(argumentsJson, out var read))
        {
            prepared = new PreparedReadFileCall("read0", read!);
        }
        else if (name == AgentToolRegistry.ReadDiffName &&
            AgentToolArguments.TryReadDiff(argumentsJson, out var diff))
        {
            prepared = new PreparedReadDiffCall("diff0", diff!);
        }
        else
        {
            throw new InvalidOperationException("Unsupported observation helper.");
        }

        Assert.Null(executor.Preflight(prepared));
        var execution = await executor.ExecuteAsync(
            prepared,
            CancellationToken.None);
        Assert.True(execution.Succeeded, execution.FailureCode);
        return execution.Observation!;
    }

    private static async Task<ContinuationInput> ContinuationAsync(
        R3QualityCase testCase,
        string terminalSummary,
        string? predecessorContextOverride = null,
        string? trustedPolicyOverride = null,
        IReadOnlyList<ToolCall>? currentCalls = null,
        ImmutableArray<AgentFinding>? currentFindings = null)
    {
        var predecessorContext = predecessorContextOverride ??
            testCase.ProcessOneContext ??
            throw new InvalidOperationException(
                "A predecessor review context is required.");
        var generationZero = await BootstrapAsync(
            testCase,
            [],
            predecessorContext,
            [],
            predecessorContext,
            trustedPolicyOverride);
        var built = AgentSessionBuilder.Build(generationZero.Input);
        Assert.True(built.Succeeded, built.FailureCode);
        var artifact = built.Artifact!;
        var envelopeSha256 = new string('e', 64);
        var restore = AgentSessionRestorer.Restore(new AgentSessionRestoreInput(
            AgentSessionLocatorFamily.Current,
            AgentSessionRestoreIntent.Automatic,
            ExplicitReset: false,
            artifact.Plaintext,
            new AgentSessionAcceptedState(
                artifact.Document.Generation,
                artifact.SessionSha256,
                envelopeSha256,
                artifact.Document.ProducerBaseSha,
                artifact.Document.ProducerHeadSha,
                artifact.Document.PredecessorStateSha256),
            generationZero.Trusted,
            artifact.Document.SessionId,
            testCase.ReviewedIdentity,
            User(testCase.InitialContext),
            AgentSessionHeadTransition.SameHead,
            NoContinuationCodec.Instance));
        Assert.True(restore.Succeeded, restore.Code);
        var run = restore.RunRequest!;
        var outcome = await CompleteAsync(
            testCase,
            run,
            currentCalls ?? [],
            terminalSummary,
            currentFindings ?? [],
            finishCallId: "finish1");
        Assert.True(outcome.CompletedSessionEligible, outcome.Diagnostic?.Code);
        return new ContinuationInput(
            new AgentSessionBuildInput(
                run,
                outcome,
                generationZero.Trusted,
                run.InitialMessages.Length - 1,
                NoContinuationCodec.Instance,
                new AgentSessionPredecessor(
                    artifact.Plaintext,
                    artifact.SessionSha256,
                    envelopeSha256,
                    artifact.Document.Generation,
                    artifact.Document.ProducerBaseSha,
                    artifact.Document.ProducerHeadSha,
                    artifact.Document.PredecessorStateSha256),
                AgentSessionHeadTransition.SameHead),
            artifact,
            restore.Artifact);
    }

    private static R3QualityFreshProcessTwoInputSet Fresh(
        params (string Name, string Content)[] inputs) =>
        R3QualityFreshProcessTwoInputSet.Capture(inputs.Select(input =>
            (
                input.Name,
                (ReadOnlyMemory<byte>)Encoding.UTF8.GetBytes(input.Content))));

    private static ReviewedSnapshot Snapshot(
        R3QualityCase testCase,
        string root) =>
        new(
            testCase.ReviewedIdentity,
            root,
            testCase.Files.Select(file => file.Path),
            [testCase.ChangedFile],
            [testCase.DiffSource]);

    private static ToolCall RequiredDiff(
        R3QualityMustFindExpectation expectation,
        string callId = "diff0") =>
        new(
            callId,
            AgentToolRegistry.ReadDiffName,
            Encoding.UTF8.GetString(expectation.RequiredArguments.AsSpan()));

    private static ToolCall RequiredDiff(
        R3QualityMustNotFindExpectation expectation,
        string callId = "diff0") =>
        new(
            callId,
            AgentToolRegistry.ReadDiffName,
            Encoding.UTF8.GetString(expectation.RequiredArguments.AsSpan()));

    private static AgentFinding Finding(
        string severity,
        string title,
        string message,
        AgentEvidence evidence) =>
        new(severity, title, message, [evidence]);

    private static ProjectChatResponse Response(ProjectChatContent content) =>
        new(
            new ProjectChatMessage("assistant", [content]),
            new ProjectChatUsage(1, 1),
            CapturedResponseBodyBytes: 1);

    private static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    private static AgentSessionTrustedRequest Trusted(
        R3QualityCase testCase,
        string? trustedPolicy = null) =>
        new(
            testCase.ReviewedIdentity.RepositoryId,
            testCase.ReviewedIdentity.ReviewTarget,
            "r3-quality@synthetic",
            Encoding.UTF8.GetBytes(
                trustedPolicy ?? "public synthetic quality policy"),
            "quality-build",
            "quality-provider",
            "quality-model",
            "quality-adapter");

    private sealed record ToolCall(
        string CallId,
        string Name,
        string ArgumentsJson);

    private sealed record CompletedInput(
        AgentSessionBuildInput Input,
        AgentSessionTrustedRequest Trusted);

    private sealed class ContinuationInput(
        AgentSessionBuildInput input,
        AgentSessionArtifact predecessor,
        AgentSessionArtifact? restored) : IDisposable
    {
        internal AgentSessionBuildInput Input { get; } = input;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(predecessor.Plaintext);
            if (restored is not null &&
                !ReferenceEquals(restored.Plaintext, predecessor.Plaintext))
            {
                CryptographicOperations.ZeroMemory(restored.Plaintext);
            }
        }
    }

    private sealed class QueueChatClient(Queue<ProjectChatResponse> responses)
        : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(responses.Dequeue());
    }

    private sealed class NoContinuationCodec : IAgentContinuationCodec
    {
        internal static NoContinuationCodec Instance { get; } = new();

        public string CodecId => "r3-quality-test";

        public string CodecDiscriminator => "current-1";

        public bool TryEncode(
            AgentContinuationCodecValue value,
            out AgentContinuationEncodedPayload? payload)
        {
            payload = null;
            return false;
        }

        public bool TryDecode(
            string encoding,
            ReadOnlySpan<byte> payload,
            out AgentContinuationCodecValue? value)
        {
            value = null;
            return false;
        }
    }

    private sealed class SyntheticRepository : IDisposable
    {
        internal SyntheticRepository(R3QualityCase testCase)
        {
            Root = Path.Join(
                Path.GetTempPath(),
                "apr-r3-quality-evaluator",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var file in testCase.Files)
            {
                var fullPath = Path.Join(
                    Root,
                    file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(
                    fullPath,
                    file.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        internal string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
