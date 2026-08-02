using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed partial class AgentCapabilityArchitectureTests
{
    [Fact]
    public void LiveAgentVerifierOwnsOnlyTheApprovedTransportSeam()
    {
        var fixtureTypes = typeof(LiveAgentVerifierProfile).Assembly.GetTypes();
        var forbiddenInterfaces = new[]
        {
            typeof(IProjectChatClient),
            typeof(IMinimalChatBackend),
            typeof(IDeepSeekTransport),
        };
        Assert.DoesNotContain(
            fixtureTypes,
            type => forbiddenInterfaces.Any(contract =>
                type != contract && contract.IsAssignableFrom(type)));

        var factory = Assert.Single(fixtureTypes, type =>
            type is { IsInterface: false, IsAbstract: false } &&
            typeof(IR3LiveAgentTransportFactory).IsAssignableFrom(type));
        Assert.Equal(typeof(VerifierTransportFactory), factory);

        var seamCalls = fixtureTypes
            .SelectMany(type => DeclaredExecutableMembers(type)
                .SelectMany(method => ResolveMethodBodyMembers(method)
                    .OfType<MethodInfo>()
                    .Where(called => called.DeclaringType ==
                            typeof(DeepSeekTransport) &&
                        called.Name is "CreateHandler" or "CreateForTesting")
                    .Select(called => (Caller: method, Called: called))))
            .ToArray();
        Assert.Equal(2, seamCalls.Length);
        Assert.All(seamCalls, call =>
        {
            Assert.Equal(typeof(VerifierTransportFactory),
                call.Caller.DeclaringType);
            Assert.Equal(nameof(VerifierTransportFactory.Create),
                call.Caller.Name);
        });
    }

    [Fact]
    public void LiveAgentVerifierCannotRecomposeOrUseTheSingleShotPath()
    {
        var fixtureTypes = typeof(LiveAgentVerifierProfile).Assembly.GetTypes();
        var forbiddenConstruction = new HashSet<Type>
        {
            typeof(R3LiveAgentApplication),
            typeof(AgentLoop),
            typeof(RestrictedStateService),
            typeof(LiveAgentStateCommitCoordinator),
        };
        var constructed = fixtureTypes
            .SelectMany(DeclaredExecutableMembers)
            .SelectMany(ResolveMethodBodyMembers)
            .OfType<ConstructorInfo>()
            .Where(constructor => constructor.DeclaringType is { } type &&
                forbiddenConstruction.Contains(type))
            .ToArray();
        Assert.Empty(constructed);

        var oldPath = new HashSet<Type>
        {
            typeof(LiveRuntimeApplication),
            typeof(ILiveProviderExecutor),
            typeof(DeepSeekLiveProviderExecutor),
        };
        var referencedOldPath = fixtureTypes
            .SelectMany(type => ReferencedTypes(type)
                .Concat(DeclaredExecutableMembers(type)
                    .SelectMany(ResolveMethodBodyMembers)
                    .Select(member => member as Type ?? member.DeclaringType)
                    .OfType<Type>()))
            .SelectMany(ExpandTypeGraph)
            .Where(oldPath.Contains)
            .Distinct()
            .ToArray();
        Assert.Empty(referencedOldPath);
    }

    [Fact]
    public void LiveAgentVerifierStoresNoCredentialOrSecret()
    {
        var fields = typeof(LiveAgentVerifierProfile).Assembly.GetTypes()
            .SelectMany(type => type.GetFields(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.DeclaredOnly));

        Assert.DoesNotContain(fields, field =>
            field.FieldType == typeof(DeepSeekCredential) ||
            field.Name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
            field.Name.Contains("apiKey", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class LiveAgentVerifierContractTests
{
    [Fact]
    public void LiveAgentVerifierGoldenIsStablePublicEvidence()
    {
        var bytes = File.ReadAllBytes(Fixture("replacement-record.json.golden"));
        Assert.Equal((byte)'\n', bytes[^1]);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.Equal(
            [
                "kind",
                "status",
                "quality_cases",
                "tool_contracts",
                "continuation",
                "negative_matrix_sha256",
                "canary_matrix_sha256",
                "single_shot_independent",
            ],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(
            AgentToolRegistry.Definitions.Select(definition => definition.Name),
            root.GetProperty("tool_contracts")
                .EnumerateArray()
                .Select(value => value.GetString()));

        var continuation = root.GetProperty("continuation");
        Assert.Equal(
            "07c30a0221c921d9f8274602f89d881007a9d9e641071748e438c2f7634f73a9",
            continuation.GetProperty("seed_identity_sha256").GetString());
        Assert.Equal(
            "generation_0_to_1_verified_ahead",
            continuation.GetProperty("transition").GetString());
        Assert.True(continuation.GetProperty("two_fresh_processes").GetBoolean());
        Assert.True(
            continuation.GetProperty("restored_first_request_exact").GetBoolean());
        Assert.True(
            continuation.GetProperty("random_prior_fact_private").GetBoolean());
        Assert.True(root.GetProperty("single_shot_independent").GetBoolean());

        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain("APR111_RANDOM_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("prior_fact_sha256", text, StringComparison.Ordinal);
        Assert.DoesNotContain("first_request_sha256", text, StringComparison.Ordinal);
        Assert.DoesNotContain("terminal_sha256", text, StringComparison.Ordinal);
        Assert.DoesNotContain("accepted_session_sha256", text, StringComparison.Ordinal);
        Assert.DoesNotContain("accepted_envelope_sha256", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LiveAgentVerifierNegativeMatrixIsExact()
    {
        Assert.Equal(
            [
                "case\tphase\tstate_expectation\tstable_code",
                "outer-authorization-denied\tpre_activation\tno_advance\tstate_access_denied",
                "inner-authorization-denied\tpre_transport\tno_advance\tstate_access_denied",
                "provider-http-failure\tpre_commit\tno_advance\tagent_chat_failed",
                "provider-malformed-response\tpre_commit\tno_advance\tagent_response_invalid",
                "tool-arguments-invalid\tpre_commit\tno_advance\tagent_tool_arguments_invalid",
                "terminal-ungrounded\tpre_commit\tno_advance\tagent_terminal_invalid",
                "transition-from-head-invalid\tpre_activation\tprior_unchanged\tsession_transition_rejected",
                "quality-failed-after-commit\tpost_commit\taccepted_preserved\tr3_fresh_process_transport_proof_failed",
                "public-result-canary\tpost_commit\taccepted_preserved\tr3_fresh_process_transport_proof_failed",
                "replacement-write-failed\tpost_commit\taccepted_preserved\tr3_fresh_process_output_failed",
            ],
            File.ReadAllLines(Fixture("negative-cases.tsv")));
    }

    [Fact]
    public void LiveAgentVerifierNegativeMatrixRowsHaveExecutableRegressions()
    {
        var executable = typeof(LiveAgentVerifierContractTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(
                BindingFlags.Instance |
                BindingFlags.Static |
                BindingFlags.Public |
                BindingFlags.NonPublic))
            .Select(method => method.Name)
            .ToHashSet(StringComparer.Ordinal);
        var rows = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["outer-authorization-denied"] =
                "LiveAgentVerifierAuthorizationDenialHasZeroActivation",
            ["inner-authorization-denied"] =
                "LiveAgentVerifierInnerAuthorizationDenialPrecedesNetworkActivation",
            ["provider-http-failure"] =
                "LiveAgentVerifierProviderHttpFailuresHaveNoPublishableResult",
            ["provider-malformed-response"] =
                "LiveAgentVerifierMalformedProviderResponsesAreResponseInvalid",
            ["tool-arguments-invalid"] =
                "LiveAgentVerifierInvalidToolArgumentsStopBeforeDispatch",
            ["terminal-ungrounded"] =
                "LiveAgentVerifierUngroundedTerminalIsRejected",
            ["transition-from-head-invalid"] =
                "LiveAgentVerifierInvalidTransitionStopsBeforeProviderConstruction",
            ["quality-failed-after-commit"] =
                "LiveAgentVerifierQualityFailureAfterCommitPreservesAcceptedTruth",
            ["public-result-canary"] =
                "LiveAgentVerifierWireOracleBindsTheCompleteToolContracts",
            ["replacement-write-failed"] =
                "LiveAgentVerifierReplacementWriteFailurePreservesPriorEvidence",
        };
        var manifestCases = File.ReadAllLines(Fixture("negative-cases.tsv"))
            .Skip(1)
            .Select(line => line.Split('\t')[0])
            .ToArray();

        Assert.Equal(rows.Keys.Order(StringComparer.Ordinal),
            manifestCases.Order(StringComparer.Ordinal));
        Assert.All(rows.Values, name => Assert.Contains(name, executable));
    }

    [Fact]
    public void LiveAgentVerifierWireOracleBindsTheCompleteToolContracts()
    {
        var request = new MinimalChatRequest(
            [new MinimalChatMessage(
                "user",
                [new MinimalChatContent(
                    "text",
                    null,
                    null,
                    "review",
                    null,
                    null,
                    null,
                    0,
                    0)])],
            AgentToolRegistry.Definitions
                .Select(definition => new MinimalChatTool(
                    definition.Name,
                    definition.Description,
                    definition.SchemaJson))
                .ToArray(),
            Continuation: null,
            ThinkingRequired: true);
        var written = DeepSeekRequestWriter.Write(request);
        Assert.Equal(DeepSeekRequestWriteOutcome.Success, written.Outcome);
        var body = written.Body.AsSpan().ToArray();
        VerifierWireOracle.ValidateBody(body, []);

        var missingProperty = JsonNode.Parse(body)!.AsObject();
        Assert.True(missingProperty.Remove("max_tokens"));
        var shape = Assert.Throws<VerifierWireException>(() =>
            VerifierWireOracle.ValidateBody(
                Encoding.UTF8.GetBytes(missingProperty.ToJsonString()),
                []));
        Assert.Equal("wire_body_shape_invalid", shape.Code);

        var changedTool = JsonNode.Parse(body)!.AsObject();
        changedTool["tools"]![0]!["function"]!["description"] = "changed";
        var contract = Assert.Throws<VerifierWireException>(() =>
            VerifierWireOracle.ValidateBody(
                Encoding.UTF8.GetBytes(changedTool.ToJsonString()),
                []));
        Assert.Equal("wire_tool_contract_invalid", contract.Code);

        var changedMessage = JsonNode.Parse(body)!.AsObject();
        changedMessage["messages"]![0]!["content"] =
            "APR111_GITHUB_CANARY";
        var canary = Assert.Throws<VerifierWireException>(() =>
            VerifierWireOracle.ValidateBody(
                Encoding.UTF8.GetBytes(changedMessage.ToJsonString()),
                []));
        Assert.Equal("wire_canary_invalid", canary.Code);

        var randomFreshInput = Assert.Throws<VerifierWireException>(() =>
            VerifierWireOracle.ValidateBody(
                body,
                [Encoding.UTF8.GetBytes(
                    "APR111_RANDOM_0123456789abcdef0123456789abcdef" +
                    "0123456789abcdef0123456789abcdef")]));
        Assert.Equal("wire_canary_invalid", randomFreshInput.Code);
    }

    [Fact]
    public async Task LiveAgentVerifierReplacementWriteFailurePreservesPriorEvidence()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            string.Concat("apr111-write-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(Path.Join(root, "receipts"));
        var output = Path.Join(root, "replacement.json");
        var prior = "prior-evidence"u8.ToArray();
        File.WriteAllBytes(output, prior);
        var fact = new string('f', 64);
        var seedIdentity = new string('e', 64);
        var qualities = new[]
        {
            Quality("must-find-read-diff", new string('1', 64), 1, 2),
            Quality("must-not-find-safe-line", new string('2', 64), 0, 3),
            Quality("continuation-prior-only", new string('3', 64), 0, 1),
        };
        WriteReceipt(root, "must-find.json", Receipt(
            "MustFind", 0, 3, new string('a', 64), null, qualities[0]));
        WriteReceipt(root, "must-not-find.json", Receipt(
            "MustNotFind", 0, 4, new string('b', 64), null, qualities[1]));
        WriteReceipt(root, "seed.json", Receipt(
            "ContinuationSeed",
            0,
            2,
            new string('c', 64),
            fact,
            null,
            seedIdentity));
        WriteReceipt(root, "restore.json", Receipt(
            "ContinuationRestore",
            1,
            2,
            new string('d', 64),
            fact,
            qualities[2],
            firstRequestSha256: new string('9', 64)));

        try
        {
            var exitCode = await AgenticPrReview.Runtime
                .LiveAgentVerifierFixture.Program.Main(
            [
                "aggregate",
                "--root",
                root,
                "--corpus",
                Corpus(),
                "--output",
                output,
            ]);

            Assert.Equal(1, exitCode);
            Assert.Equal(prior, File.ReadAllBytes(output));
            Assert.Equal(
                nameof(IOException),
                File.ReadAllText(Path.Join(root, "private", "failure.code")));
            Assert.Equal(4, Directory.GetFiles(
                Path.Join(root, "receipts"),
                "*.json").Length);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LiveAgentVerifierContinuationSeedIsDistinctAndRuntimeRandom()
    {
        var corpusBytes = File.ReadAllBytes(Corpus());
        Assert.True(R3QualityCorpusParser.TryParse(
            corpusBytes,
            out var corpus,
            out var diagnostic),
            diagnostic?.ToString());
        var testCase = Assert.Single(
            corpus!.Cases,
            item => item.Kind == R3QualityCaseKind.Continuation);
        var first = new VerifierProviderScript(
            VerifierScenario.ContinuationSeed,
            testCase);
        var second = new VerifierProviderScript(
            VerifierScenario.ContinuationSeed,
            testCase);
        Assert.NotNull(first.PriorFactSha256);
        Assert.NotNull(second.PriorFactSha256);
        Assert.NotEqual(first.PriorFactSha256, second.PriorFactSha256);
        Assert.DoesNotContain(
            "APR111_RANDOM_",
            Encoding.UTF8.GetString(corpusBytes),
            StringComparison.Ordinal);

        var root = Path.Join(
            Path.GetTempPath(),
            string.Concat("apr111-unit-", Guid.NewGuid().ToString("N")));
        try
        {
            Assert.True(FreshProcessMaterializer.TryMaterialize(
                VerifierScenario.ContinuationSeed,
                root,
                corpusBytes,
                out var seed));
            Assert.Equal(new string('4', 40), seed!.ReviewedIdentity.BaseSha);
            Assert.Equal(new string('5', 40), seed.ReviewedIdentity.HeadSha);
            Assert.Equal(new string('5', 40), testCase.ReviewedIdentity.BaseSha);
            Assert.Equal(new string('6', 40), testCase.ReviewedIdentity.HeadSha);
            Assert.NotEqual(
                seed.ReviewedIdentity,
                testCase.ReviewedIdentity);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void LiveAgentVerifierTransportProofIsFailClosed()
    {
        var terminal = new string('a', 64);
        var passed = new VerifierWireProof(
            true,
            1,
            1,
            null,
            terminal,
            null,
            null);
        Assert.True(passed.IsSatisfiedBy(terminal));
        Assert.False(passed.IsSatisfiedBy(new string('b', 64)));
        Assert.False((passed with { FactoryCreateCount = 0 })
            .IsSatisfiedBy(terminal));
        Assert.False((passed with { Succeeded = false })
            .IsSatisfiedBy(terminal));
        Assert.False(VerifierWireProof.Empty.IsSatisfiedBy(null));
    }

    private static string Fixture(string name) => Path.Join(
        AppContext.BaseDirectory,
        "fixtures",
        "agent",
        "r3-live-agent",
        name);

    private static string Corpus() => Path.Join(
        AppContext.BaseDirectory,
        "fixtures",
        "agent",
        "r3-quality",
        "corpus.json");

    private static VerifierQualityProjection Quality(
        string id,
        string caseSha256,
        int findingCount,
        int toolCallCount) => new(
        id,
        caseSha256,
        "passed",
        "quality",
        R3QualityCodes.Passed,
        findingCount,
        toolCallCount,
        TerminalPresent: true,
        ExpectedCaseBound: true);

    private static VerifierPhaseReceipt Receipt(
        string scenario,
        long generation,
        int providerRequests,
        string invocationIdentitySha256,
        string? priorFactSha256,
        VerifierQualityProjection? quality,
        string? seedIdentitySha256 = null,
        string? firstRequestSha256 = null) => new(
        scenario,
        "passed",
        R3LiveAgentCodes.Completed,
        generation,
        generation == 0 ? "same_head" : "verified_ahead",
        ModelCalls: providerRequests,
        ToolCalls: quality?.ToolCallCount ?? 1,
        ProviderRequests: providerRequests,
        WireValid: true,
        WireFailureCode: null,
        CommitDelegatedOnce: true,
        HandoffReady: true,
        FirstRequestSha256: firstRequestSha256,
        TerminalSha256: new string('8', 64),
        PriorFactSha256: priorFactSha256,
        InvocationIdentitySha256: invocationIdentitySha256,
        SeedIdentitySha256: seedIdentitySha256,
        Quality: quality);

    private static void WriteReceipt(
        string root,
        string name,
        VerifierPhaseReceipt receipt) => File.WriteAllBytes(
        Path.Join(root, "receipts", name),
        VerifierReceiptCodec.Write(receipt));
}
