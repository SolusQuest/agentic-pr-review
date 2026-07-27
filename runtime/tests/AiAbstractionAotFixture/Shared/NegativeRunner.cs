namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class NegativeRunner
{
    private static readonly IReadOnlyDictionary<string, string> ExpectedCodes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["unknown-tool"] = "APR_AI_TOOL_NAME",
            ["unknown-argument-field"] = "APR_AI_TOOL_ARGUMENT",
            ["duplicate-argument-field"] = "APR_AI_TOOL_ARGUMENT",
            ["malformed-argument"] = "APR_AI_TOOL_ARGUMENT",
            ["oversized-tool-result"] = "APR_AI_TOOL_RESULT_OVERSIZED",
            ["response-ordering"] = "APR_AI_ORDERING",
            ["continuation-missing"] = "APR_AI_ORDERING",
            ["continuation-altered"] = "APR_AI_CONTINUATION",
            ["continuation-oversized"] = "APR_AI_CONTINUATION_OVERSIZED",
            ["continuation-misplaced"] = "APR_AI_ORDERING",
            ["continuation-wrong-framing"] = "APR_AI_CONTINUATION",
            ["continuation-wrong-association"] = "APR_AI_CONTINUATION",
            ["continuation-wrong-role"] = "APR_AI_PROVIDER_ROLE",
            ["wrong-tool-result-id"] = "APR_AI_ASSOCIATION",
            ["wrong-candidate"] = "APR_AI_RESTORE_BINDING",
            ["wrong-provider"] = "APR_AI_RESTORE_BINDING",
            ["wrong-model"] = "APR_AI_RESTORE_BINDING",
            ["wrong-adapter"] = "APR_AI_RESTORE_BINDING",
            ["wrong-session"] = "APR_AI_RESTORE_BINDING",
            ["candidate-object-state"] = "APR_AI_SERIALIZATION",
            ["cancellation"] = "APR_AI_CANCELLED",
            ["before-evidence-commit"] = "APR_AI_EVIDENCE",
            ["before-state-commit"] = "APR_AI_STATE_COMMIT",
        };

    internal static async Task<int> RunAsync(CommandLine command)
    {
        var scenario = command.Required("scenario");
        if (!ExpectedCodes.TryGetValue(scenario, out var expected))
        {
            throw new FixtureFailure("APR_AI_SCENARIO");
        }
        var statePath = command.Required("state");
        var evidencePath = command.Required("evidence");
        EnsureAbsent(statePath);
        EnsureAbsent(evidencePath);

        try
        {
            await ExecuteAsync(command, scenario);
        }
        catch (OperationCanceledException)
        {
            AssertFailure("APR_AI_CANCELLED", expected, statePath);
            return Success(scenario);
        }
        catch (FixtureFailure failure)
        {
            AssertFailure(failure.Code, expected, statePath);
            return Success(scenario);
        }

        throw new FixtureFailure("APR_AI_NEGATIVE_ACCEPTED");
    }

    private static async Task ExecuteAsync(CommandLine command, string scenario)
    {
        var firstInput = FixtureJson.ReadFirstInput(command.Required("first-fixture"));
        var firstScenario = IsFirstPhaseScenario(scenario) ? scenario : "happy";
        var firstHarness = CandidateFactory.Create(FixturePhase.First, firstScenario);
        using var cancellation = new CancellationTokenSource();
        if (scenario == "cancellation")
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(50));
        }
        var first = await FixtureRunner.RunFirstAsync(
            firstInput,
            firstHarness,
            firstScenario,
            command.Many("canary"),
            cancellation.Token);

        if (scenario == "candidate-object-state")
        {
            throw new FixtureFailure("APR_AI_SERIALIZATION");
        }
        if (scenario is "before-evidence-commit" or "before-state-commit")
        {
            OutputCommitter.CommitFirst(
                first.State,
                first.Evidence,
                command.Required("state"),
                command.Required("evidence"),
                scenario);
            return;
        }
        if (IsFirstPhaseScenario(scenario))
        {
            return;
        }

        var resumeInput = FixtureJson.ReadResumeInput(
            command.Required("resume-fixture"));
        var resumeHarness = CandidateFactory.Create(FixturePhase.Resume, "happy");
        var state = Mutate(first.State, scenario);
        _ = await FixtureRunner.RunResumeAsync(
            resumeInput,
            state,
            resumeHarness,
            command.Many("canary"),
            CancellationToken.None);
    }

    private static bool IsFirstPhaseScenario(string scenario) => scenario is
        "unknown-tool" or
        "unknown-argument-field" or
        "duplicate-argument-field" or
        "malformed-argument" or
        "oversized-tool-result" or
        "response-ordering" or
        "continuation-missing" or
        "continuation-altered" or
        "continuation-oversized" or
        "continuation-misplaced" or
        "continuation-wrong-framing" or
        "continuation-wrong-association" or
        "continuation-wrong-role" or
        "cancellation";

    private static ProofState Mutate(ProofState state, string scenario) =>
        scenario switch
        {
            "wrong-tool-result-id" => state with
            {
                Records = state.Records.Select((record, index) =>
                    record.Kind == "tool_result" && index > 0
                        ? record with { CallId = "call-unknown-999" }
                        : record).ToArray(),
            },
            "wrong-candidate" => state with { Candidate = "OtherCandidate" },
            "wrong-provider" => state with { ProviderId = "other-provider" },
            "wrong-model" => state with { ModelId = "other-model" },
            "wrong-adapter" => state with { AdapterId = "other-adapter" },
            "wrong-session" => state with { SessionId = "other-session" },
            _ => state,
        };

    private static void AssertFailure(
        string actual,
        string expected,
        string statePath)
    {
        if (actual != expected)
        {
            throw new FixtureFailure("APR_AI_NEGATIVE_CODE");
        }
        if (File.Exists(statePath) || Directory.Exists(statePath))
        {
            throw new FixtureFailure("APR_AI_STATE_PARTIAL");
        }
    }

    private static int Success(string scenario)
    {
        Console.WriteLine($"APR_AI_NEGATIVE_OK {scenario}");
        return 0;
    }

    private static void EnsureAbsent(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new FixtureFailure("APR_AI_OUTPUT_EXISTS");
        }
    }
}
