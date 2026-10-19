using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.LiveAgentVerifierFixture;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

public sealed partial class AgentCapabilityArchitectureTests
{
    [Fact]
    public void LiveAgentVerifierOwnsOnlyTheApprovedTypedScriptSeam()
    {
        var fixtureTypes = typeof(LiveAgentVerifierProfile).Assembly.GetTypes();
        Assert.DoesNotContain(fixtureTypes, type => typeof(IMinimalChatBackend).IsAssignableFrom(type));
        Assert.Equal(typeof(VerifierHistoricalChat), Assert.Single(fixtureTypes, type =>
            type is { IsInterface: false, IsAbstract: false } && typeof(IProjectChatClient).IsAssignableFrom(type)));
        Assert.Equal(typeof(VerifierChatFactory), Assert.Single(fixtureTypes, type =>
            type is { IsInterface: false, IsAbstract: false } && typeof(IR3LiveAgentChatFactory).IsAssignableFrom(type)));
        var seamCalls = fixtureTypes.SelectMany(type => DeclaredExecutableMembers(type)
            .SelectMany(method => ResolveMethodBodyMembers(method).OfType<ConstructorInfo>()
                .Where(called => called.DeclaringType == typeof(VerifierHistoricalChat))
                .Select(called => (Caller: method, Called: called)))).ToArray();
        var call = Assert.Single(seamCalls);
        Assert.Equal(typeof(VerifierChatFactory), call.Caller.DeclaringType);
        Assert.Equal(nameof(VerifierChatFactory.Create), call.Caller.Name);
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

        var oldPath = new HashSet<string>(StringComparer.Ordinal)
        {
            "AgenticPrReview.Runtime.LiveRuntimeApplication",
            "AgenticPrReview.Runtime.ILiveProviderExecutor",
            "AgenticPrReview.Runtime.DeepSeekLiveProviderExecutor",
        };
        var referencedOldPath = fixtureTypes
            .SelectMany(type => ReferencedTypes(type)
                .Concat(DeclaredExecutableMembers(type)
                    .SelectMany(ResolveMethodBodyMembers)
                    .Select(member => member as Type ?? member.DeclaringType)
                    .OfType<Type>()))
            .SelectMany(ExpandTypeGraph)
            .Select(type => type.FullName)
            .Where(name => name is not null && oldPath.Contains(name))
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
        Assert.DoesNotContain("first_typed_request_sha256", text, StringComparison.Ordinal);
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
                "inner-authorization-denied\tpre_script\tno_advance\tstate_access_denied",
                "script-failure\tpre_commit\tno_advance\tagent_chat_failed",
                "script-invalid-response\tpre_commit\tno_advance\tagent_response_invalid",
                "tool-arguments-invalid\tpre_commit\tno_advance\tagent_chat_failed",
                "terminal-ungrounded\tpre_commit\tno_advance\tagent_terminal_invalid",
                "transition-from-head-invalid\tpre_activation\tprior_unchanged\tsession_transition_rejected",
                "lineage-authority-tampered\tpre_activation\tprior_unchanged\tstate_lineage_mismatch",
                "quality-failed-after-commit\tpost_commit\taccepted_preserved\tr3_fresh_process_transport_proof_failed",
                "public-result-canary\tpost_commit\taccepted_preserved\tr3_fresh_process_transport_proof_failed",
                "replacement-write-failed\tpost_commit\taccepted_preserved\tr3_fresh_process_output_failed",
            ],
            File.ReadAllLines(Fixture("negative-cases.tsv")));
    }

    [Fact]
    public void LiveAgentVerifierNegativeMatrixRowsHaveIntegratedScenarios()
    {
        var manifestRows = File.ReadAllLines(Fixture("negative-cases.tsv"))
            .Skip(1)
            .Select(line => line.Split('\t'))
            .ToArray();
        var integrated = Enum.GetValues<VerifierScenario>()
            .Where(VerifierScenarioDomain.IsNegative)
            .Select(VerifierScenarioDomain.Negative)
            .Select(item => new[]
            {
                item.Id,
                item.Phase,
                item.StateExpectation,
                item.StableCode,
            })
            .ToArray();

        Assert.Equal(
            integrated.Select(row => row[0]).Order(StringComparer.Ordinal),
            manifestRows.Take(integrated.Length)
                .Select(row => row[0])
                .Order(StringComparer.Ordinal));
        Assert.All(integrated, expected => Assert.Contains(
            manifestRows,
            actual => actual.SequenceEqual(expected, StringComparer.Ordinal)));
        Assert.Equal(
            [
                "replacement-write-failed",
                "post_commit",
                "accepted_preserved",
                LiveAgentFreshProcessCodes.OutputFailed,
            ],
            manifestRows[^1]);
    }

    [Fact]
    public void LiveAgentVerifierTypedOracleBindsTheCompleteToolContracts()
    {
        var request = new ProjectChatRequest([new("user", [new ProjectTextContent("review")])],
            AgentToolRegistry.Definitions.ToArray(), null, ThinkingRequired: true);
        VerifierLogicalOracle.Validate(request, []);
        Assert.Equal("typed_input_shape_invalid", Assert.Throws<VerifierScriptException>(() =>
            VerifierLogicalOracle.Validate(request with { ThinkingRequired = false }, [])).Code);
        var tools = request.Tools.ToArray(); tools[0] = tools[0] with { Description = "changed" };
        Assert.Equal("typed_tool_contract_invalid", Assert.Throws<VerifierScriptException>(() =>
            VerifierLogicalOracle.Validate(request with { Tools = tools }, [])).Code);
        Assert.Equal("typed_canary_invalid", Assert.Throws<VerifierScriptException>(() =>
            VerifierLogicalOracle.Validate(request with { Messages = [new("user", [new ProjectTextContent("APR111_GITHUB_CANARY")])] }, [])).Code);
        Assert.Equal("typed_canary_invalid", Assert.Throws<VerifierScriptException>(() =>
            VerifierLogicalOracle.Validate(request,
                [Encoding.UTF8.GetBytes("APR111_RANDOM_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")])).Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveAgentVerifierAggregateRejectsMissingOrFabricatedReceipts(
        bool fabricateReceipts)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            string.Concat("apr111-evidence-", Guid.NewGuid().ToString("N")));
        var receiptRoot = Path.Join(root, "receipts");
        Directory.CreateDirectory(Path.Join(receiptRoot, "negative"));
        var output = Path.Join(root, "replacement.json");
        var executionArtifact = typeof(AgenticPrReview.Runtime
            .LiveAgentVerifierFixture.Program).Assembly.Location;
        var buildPair = BuildPair(executionArtifact, executionArtifact);
        var buildPairManifest = Path.Join(root, "build-pair.json");
        var prior = "prior-evidence"u8.ToArray();
        File.WriteAllBytes(output, prior);
        File.WriteAllBytes(
            buildPairManifest,
            VerifierBuildPairDomain.Write(buildPair));
        if (fabricateReceipts)
        {
            foreach (var name in new[]
            {
                "must-find.json",
                "must-not-find.json",
                "seed.json",
                "restore.json",
                "canary.json",
                "architecture.json",
            })
            {
                File.WriteAllText(Path.Join(receiptRoot, name), "{}");
            }

            foreach (var line in File.ReadAllLines(Fixture("negative-cases.tsv"))
                .Skip(1))
            {
                File.WriteAllText(
                    Path.Join(
                        receiptRoot,
                        "negative",
                        string.Concat(line.Split('\t')[0], ".json")),
                    "{}");
            }
        }

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
                "--negative-manifest",
                Fixture("negative-cases.tsv"),
                "--canary-manifest",
                Fixture("canary-routes.tsv"),
                "--execution-kind",
                VerifierExecutionKinds.Framework,
                "--execution-artifact",
                executionArtifact,
                "--build-pair-manifest",
                buildPairManifest,
            ]);

            Assert.Equal(3, exitCode);
            Assert.Equal(prior, File.ReadAllBytes(output));
            Assert.DoesNotContain(
                "\"status\":\"passed\"",
                Encoding.UTF8.GetString(File.ReadAllBytes(output)),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LiveAgentVerifierBuildPairRejectsSubstitutedMembers()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            string.Concat("apr112-build-pair-", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        var executionArtifact = typeof(AgenticPrReview.Runtime
            .LiveAgentVerifierFixture.Program).Assembly.Location;
        var buildPair = BuildPair(executionArtifact, executionArtifact);
        var manifest = Path.Join(root, "build-pair.json");
        File.WriteAllBytes(manifest, VerifierBuildPairDomain.Write(buildPair));
        var output = Path.Join(root, "output.json");
        var command = new VerifierCommand(
            "aggregate",
            null,
            root,
            Corpus(),
            output,
            null,
            null,
            Fixture("negative-cases.tsv"),
            Fixture("canary-routes.tsv"),
            null,
            VerifierExecutionKinds.Framework,
            executionArtifact,
            manifest,
            null);

        try
        {
            Assert.True(VerifierBuildPairDomain.TryAdmit(
                command,
                out var admitted));
            Assert.Equal(buildPair, admitted);

            var substitutedExecutable = Path.Join(root, "substituted-runner.dll");
            File.WriteAllText(substitutedExecutable, "not-the-published-runner");
            Assert.False(VerifierBuildPairDomain.TryAdmit(
                command with { ExecutionArtifact = substitutedExecutable },
                out _));

            var receipt = VerifierArchitectureProof.Create(
                executionArtifact,
                buildPair,
                new string('8', 64));
            Assert.True(
                receipt.Passed,
                string.Join(
                    Environment.NewLine,
                    [
                        string.Join(
                            ':',
                            receipt.ForbiddenReferencesAbsent,
                            receipt.TypedFactoryCreationCalls,
                            receipt.TypedChatFactoryTypes,
                            receipt.ProfileTypes),
                        .. VerifierArchitectureProof
                            .DescribeTransportCallsForTesting(executionArtifact),
                    ]));
            Assert.Equal(buildPair.BuildPairSha256, receipt.BuildPairSha256);

            var substitutedAssembly = Path.Join(root, "substituted-input.dll");
            var bytes = File.ReadAllBytes(executionArtifact);
            bytes[^1] ^= 0xff;
            File.WriteAllBytes(substitutedAssembly, bytes);
            Assert.Throws<InvalidDataException>(() =>
                VerifierArchitectureProof.Create(
                    substitutedAssembly,
                    buildPair,
                    new string('9', 64)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void LiveAgentVerifierArchitectureRejectsMethodReferenceMutations()
    {
        var assemblyPath = typeof(AgenticPrReview.Runtime
            .LiveAgentVerifierFixture.Program).Assembly.Location;
        var calls = VerifierArchitectureProof
            .ReadTransportCallsForTesting(assemblyPath);
        Assert.Single(calls);
        Assert.All(calls, call =>
        {
            Assert.Equal(0, call.Caller.GenericParameterCount);
            Assert.Equal(0, call.Target.GenericParameterCount);
            Assert.Contains(" arity=0 ", call.Caller.Signature,
                StringComparison.Ordinal);
            Assert.Contains(" arity=0 ", call.Target.Signature,
                StringComparison.Ordinal);
        });
        Assert.True(
            VerifierArchitectureProof.TransportCallsValidForTesting(calls));

        var ldftn = VerifierArchitectureProof
            .ReadSyntheticTransportReferenceForTesting(
                assemblyPath,
                ".ctor",
                ILOpCode.Ldftn);
        Assert.Equal(ILOpCode.Ldftn, ldftn.OpCode);
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            [.. calls, new(calls[0].Caller, ldftn)]));

        var wrongAssembly = calls.ToArray();
        wrongAssembly[0] = wrongAssembly[0] with
        {
            Target = wrongAssembly[0].Target with
            {
                DeclaringType = wrongAssembly[0].Target.DeclaringType with
                {
                    AssemblyName = "Substituted.Runtime",
                },
            },
        };
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            wrongAssembly));

        var wrongSignature = calls.ToArray();
        wrongSignature[0] = wrongSignature[0] with
        {
            Target = wrongSignature[0].Target with
            {
                Signature = string.Concat(
                    wrongSignature[0].Target.Signature,
                    "<wrong-overload>"),
            },
        };
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            wrongSignature));

        var wrongCaller = calls.ToArray();
        wrongCaller[0] = wrongCaller[0] with
        {
            Caller = wrongCaller[0].Caller with { Name = "CreateElsewhere" },
        };
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            wrongCaller));

        var genericCaller = calls.ToArray();
        genericCaller[0] = genericCaller[0] with
        {
            Caller = genericCaller[0].Caller with
            {
                GenericParameterCount = 1,
                Signature = genericCaller[0].Caller.Signature.Replace(
                    " arity=0 ",
                    " arity=1 ",
                    StringComparison.Ordinal),
            },
        };
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            genericCaller));

        var genericTarget = calls.ToArray();
        genericTarget[0] = genericTarget[0] with
        {
            Target = genericTarget[0].Target with
            {
                GenericParameterCount = 1,
                Signature = genericTarget[0].Target.Signature.Replace(
                    " arity=0 ",
                    " arity=1 ",
                    StringComparison.Ordinal),
            },
        };
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            genericTarget));

        var invalidSpecification = calls.ToArray();
        invalidSpecification[0] = invalidSpecification[0] with
        {
            Target = invalidSpecification[0].Target with
            {
                IsMethodSpecification = true,
            },
        };
        Assert.False(VerifierArchitectureProof.TransportCallsValidForTesting(
            invalidSpecification));
    }

    [Fact]
    public void LiveAgentVerifierArchitectureCountsInheritedImplementations()
    {
        var assembly = typeof(LiveAgentVerifierContractTests).Assembly;
        var target = typeof(ArchitectureProfileProbe).FullName!;
        Assert.Equal(
            2,
            VerifierArchitectureProof.CountImplementationsForTesting(
                assembly.Location,
                assembly.GetName().Name!,
                target));
        Assert.Equal(
            0,
            VerifierArchitectureProof.CountImplementationsForTesting(
                assembly.Location,
                "Substituted.Runtime",
                target));
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
        var first = new VerifierAuthoredChatScript(
            VerifierScenario.ContinuationSeed,
            testCase);
        var second = new VerifierAuthoredChatScript(
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

    [Theory]
    [InlineData("valid", true)]
    [InlineData("repository_tool", false)]
    [InlineData("finding", false)]
    [InlineData("summary_missing", false)]
    public void ContinuationOnlyReviewAdmissionIsFailClosed(
        string variant,
        bool expected)
    {
        Assert.True(R3QualityCorpusParser.TryParse(
            File.ReadAllBytes(Corpus()),
            out var corpus,
            out var diagnostic),
            diagnostic?.ToString());
        var testCase = Assert.Single(
            corpus!.Cases,
            item => item.Kind == R3QualityCaseKind.Continuation);
        var expectation = Assert.IsType<R3QualityContinuationExpectation>(
            testCase.Expectation);
        var summary = variant == "summary_missing"
            ? "The prior fact was omitted."
            : string.Concat("Restored ", expectation.PriorOnlyMarker, ".");
        ImmutableArray<AgentFinding> findings = variant == "finding"
            ?
            [
                new AgentFinding(
                    "low",
                    "Unexpected continuation finding",
                    "Continuation is continuity-only.",
                    [
                        new AgentEvidence(
                            new string('a', 64),
                            "src/RetryBudget.cs",
                            1,
                            1),
                    ]),
            ]
            : [];
        var terminalBytes = Encoding.UTF8.GetBytes(
            variant == "finding"
                ? string.Concat(
                    "{\"summary\":",
                    JsonSerializer.Serialize(summary),
                    ",\"findings\":[{\"severity\":\"low\",",
                    "\"title\":\"Unexpected continuation finding\",",
                    "\"message\":\"Continuation is continuity-only.\",",
                    "\"evidence\":[{\"observation_id\":\"",
                    new string('a', 64),
                    "\",\"path\":\"src/RetryBudget.cs\",",
                    "\"start_line\":1,\"end_line\":1}]}]}")
                : string.Concat(
                    "{\"summary\":",
                    JsonSerializer.Serialize(summary),
                    ",\"findings\":[]}"));
        var terminalSha256 = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            terminalBytes);
        var events = ImmutableArray.CreateBuilder<AgentLogicalEvent>();
        if (variant == "repository_tool")
        {
            events.Add(new AgentToolCallEvent(
                "list0",
                AgentToolRegistry.ListChangedFilesName,
                AgentCanonical.HashRaw("{}"u8),
                ImmutableArray.CreateRange("{}"u8.ToArray())));
        }

        events.Add(new AgentToolCallEvent(
            "finish0",
            AgentToolRegistry.FinishReviewName,
            terminalSha256,
            ImmutableArray.CreateRange(terminalBytes)));
        events.Add(new AgentTerminalEvent(terminalSha256));
        var outcome = AgentRunOutcome.Success(
            new AgentTerminalReview(
                summary,
                findings,
                terminalSha256,
                terminalBytes),
            testCase.ReviewedIdentity,
            events.ToImmutable(),
            continuation: null);

        Assert.Equal(
            expected,
            VerifierCommitObserver.IsContinuationOnlyReview(
                outcome,
                testCase));
    }

    [Fact]
    public void LiveAgentVerifierTransportProofIsFailClosed()
    {
        var terminal = new string('a', 64);
        var passed = new VerifierTypedProof(
            true,
            1,
            1,
            null,
            terminal,
            null,
            null,
            false,
            false,
            null, TypedObservations: [new(1, new string('f', 64), "succeeded")]);
        Assert.True(passed.IsSatisfiedBy(terminal));
        Assert.False(passed.IsSatisfiedBy(new string('b', 64)));
        Assert.False((passed with { FactoryCreateCount = 0 })
            .IsSatisfiedBy(terminal));
        Assert.False((passed with { Succeeded = false })
            .IsSatisfiedBy(terminal));
        Assert.False(VerifierTypedProof.Empty.IsSatisfiedBy(null));
    }

    [Fact]
    public void LiveAgentVerifierAcceptedIdentityMutationsFailClosed()
    {
        var session = new string('1', 64);
        var envelope = new string('2', 64);
        var lineage = new string('3', 64);
        var buildPair = TestBuildPair();
        var valid = new VerifierNegativeReceipt(
            "apr-r3-historical-typed-negative-receipt-v1",
            "quality-failed-after-commit",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.TransportProofFailed,
            LiveAgentFreshProcessCodes.TransportProofFailed,
            new string('4', 64),
            new string('5', 64),
            null,
            lineage,
            0,
            session,
            envelope,
            lineage,
            0,
            session,
            envelope,
            lineage,
            ActivationCount: 1,
            SyntheticCalls: 3,
            CommitDelegationCount: 1,
            HandoffReady: false,
            AcceptedTruthPreserved: true,
            Passed: true,
            ProcessInstanceSha256: new string('6', 64),
            ExecutionKind: buildPair.ExecutionKind,
            ExecutionArtifactSha256: buildPair.ExecutionArtifactSha256,
            ArchitectureAssemblySha256:
                buildPair.ArchitectureAssemblySha256,
            BuildPairSha256: buildPair.BuildPairSha256,
            TypedObservations: Enumerable.Range(1, 3).Select(i => new VerifierTypedObservation(i, new string('f', 64), "succeeded")).ToArray());
        Assert.True(VerifierEvidence.NegativeValidForTesting(valid));
        Assert.True(VerifierEvidence.BuildPairValidForTesting(
            buildPair,
            valid));

        var mutations = new[]
        {
            valid with { AcceptedSessionSha256 = null },
            valid with { AcceptedSessionSha256 = new string('7', 64) },
            valid with { AcceptedEnvelopeSha256 = null },
            valid with { AcceptedEnvelopeSha256 = new string('7', 64) },
            valid with { AcceptedLineageSha256 = new string('7', 64) },
            valid with
            {
                CanonicalLineageSessionSha256 = new string('7', 64),
            },
            valid with
            {
                CanonicalLineageEnvelopeSha256 = new string('7', 64),
            },
            valid with { CanonicalLineageSha256 = new string('7', 64) },
            valid with { BuildPairSha256 = new string('7', 64) },
        };
        Assert.All(mutations, receipt => Assert.False(
            VerifierEvidence.NegativeValidForTesting(receipt)));
    }

    [Fact]
    public void LiveAgentVerifierReplacementPublicationProofFailsClosed()
    {
        var session = new string('1', 64);
        var envelope = new string('2', 64);
        var lineage = new string('3', 64);
        var stateBefore = new string('4', 64);
        var stateAfter = new string('5', 64);
        var lineageBefore = new string('6', 64);
        var result = new string('7', 64);
        var buildPair = TestBuildPair();
        var valid = new VerifierNegativeReceipt(
            "apr-r3-historical-typed-negative-receipt-v1",
            "replacement-write-failed",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.OutputFailed,
            LiveAgentFreshProcessCodes.OutputFailed,
            stateBefore,
            stateAfter,
            lineageBefore,
            lineage,
            1,
            session,
            envelope,
            lineage,
            1,
            session,
            envelope,
            lineage,
            ActivationCount: 1,
            SyntheticCalls: 1,
            CommitDelegationCount: 1,
            HandoffReady: false,
            AcceptedTruthPreserved: true,
            Passed: true,
            ProcessInstanceSha256: new string('8', 64),
            ResultBeforeSha256: result,
            ResultAfterSha256: result,
            ResultPublicationAttempts: 1,
            ExecutionKind: buildPair.ExecutionKind,
            ExecutionArtifactSha256: buildPair.ExecutionArtifactSha256,
            ArchitectureAssemblySha256:
                buildPair.ArchitectureAssemblySha256,
            BuildPairSha256: buildPair.BuildPairSha256, TypedObservations: [new(1, new string('f', 64), "succeeded")]);
        Assert.True(VerifierEvidence.ReplacementNegativeValidForTesting(valid));

        var mutations = new[]
        {
            valid with { StateAfterSha256 = stateBefore },
            valid with { LineageAfterSha256 = lineageBefore },
            valid with { ActivationCount = 0 },
            valid with { SyntheticCalls = 0 },
            valid with { CommitDelegationCount = 0 },
            valid with { ResultBeforeSha256 = null },
            valid with { ResultAfterSha256 = new string('9', 64) },
            valid with { ResultPublicationAttempts = 0 },
        };
        Assert.All(mutations, receipt => Assert.False(
            VerifierEvidence.ReplacementNegativeValidForTesting(receipt)));
    }

    [Fact]
    public void LiveAgentVerifierMaterializedPhaseCreatesPrivateObserverParent()
    {
        var root = Path.Join(Path.GetTempPath(), "apr-r8-materialized-observer-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            Assert.True(FreshProcessMaterializer.TryMaterialize(VerifierScenario.MustFind, root,
                File.ReadAllBytes(Corpus()), out var materialized));
            Assert.NotNull(materialized);
            var parent = Path.Join(root, "private");
            var receipt = Path.Join(parent, "phase.json");
            var bytes = VerifierReceiptCodec.Write(new VerifierTypedObservationSnapshot("MustFind", new('a', 64), new('b', 64), []));
            VerifierTypedObservationFiles.WriteNew(receipt, bytes);
            Assert.Equal(bytes, VerifierTypedObservationFiles.Read(receipt));
            if (!OperatingSystem.IsWindows())
            {
                var privateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
                Assert.Equal(privateMode, File.GetUnixFileMode(parent));
                File.SetUnixFileMode(parent, privateMode | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                Assert.Throws<InvalidDataException>(() => VerifierTypedObservationFiles.WriteNew(Path.Join(parent, "unsafe.json"), bytes));
                Assert.False(File.Exists(VerifierTypedObservationFiles.PathFor(Path.Join(parent, "unsafe.json"))));
                File.SetUnixFileMode(parent, privateMode);
                var sidecar = VerifierTypedObservationFiles.PathFor(receipt);
                File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
                Assert.Throws<InvalidDataException>(() => VerifierTypedObservationFiles.Read(receipt));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void ControlledTypedObserverRejectsCoordinatedReceiptAndTraceForgeries()
    {
        var root = Path.Join(Path.GetTempPath(), "apr-r8-typed-observer-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var path = Path.Join(root, "phase.json");
            var owner = new VerifierTypedObservationOwner();
            var request = new ProjectChatRequest([new("user", [new ProjectTextContent("fixed authored observation input")])],
                AgentToolRegistry.Definitions.ToArray(), null, ThinkingRequired: true);
            var ordinal = owner.Begin(request); owner.Complete(ordinal, "succeeded");
            var first = owner.Snapshot[0].RequestSha256;
            var receipt = new VerifierPhaseReceipt("ContinuationRestore", "passed", R3LiveAgentCodes.Completed, 1, "verified_ahead",
                1, 1, 1, true, null, true, true, first, new('a', 64), new('b', 64), new('c', 64), null,
                new('d', 64), new('e', 64), new('f', 64), true, new('1', 64), true, true, null,
                ProcessInstanceSha256: new('2', 64), BuildPairSha256: new('3', 64), TypedObservations: owner.Snapshot);
            var actual = new VerifierTypedObservationSnapshot(receipt.Scenario, receipt.ProcessInstanceSha256,
                receipt.BuildPairSha256, owner.Snapshot);
            VerifierTypedObservationFiles.WriteNew(path, VerifierReceiptCodec.Write(actual));
            var sidecar = VerifierTypedObservationFiles.PathFor(path);
            var original = File.ReadAllBytes(sidecar);
            void Write(VerifierPhaseReceipt value) => File.WriteAllBytes(path, VerifierReceiptCodec.Write(value));
            Write(receipt); Assert.True(VerifierEvidence.ControlledPhaseValidForTesting(path));
            foreach (var mutation in new[]
            {
                receipt with { FirstTypedRequestSha256 = new('4', 64) },
                receipt with { FirstTypedRequestSha256 = new('4', 64), TypedObservations = [owner.Snapshot[0] with { RequestSha256 = new('4', 64) }] },
                receipt with { SyntheticCalls = 2 }, receipt with { Scenario = "MustFind" },
                receipt with { ProcessInstanceSha256 = new('5', 64) }, receipt with { BuildPairSha256 = new('6', 64) },
                receipt with { ObservationDomain = "provider-wire" },
            }) { Write(mutation); Assert.False(VerifierEvidence.ControlledPhaseValidForTesting(path)); }
            Write(receipt);
            foreach (var mutation in new[] { "unknown", "phase", "build", "duplicate", "oversize", "missing" })
            {
                var node = JsonNode.Parse(original)!;
                if (mutation == "unknown") node["wire_valid"] = true;
                else if (mutation == "phase") node["scenario"] = "MustFind";
                else if (mutation == "build") node["build_pair_sha256"] = new string('6', 64);
                else if (mutation == "duplicate") node["typed_observations"]![0]!["ordinal"] = 0;
                if (mutation == "missing") File.Delete(sidecar);
                else File.WriteAllBytes(sidecar, mutation == "oversize" ? new byte[VerifierTypedObservationFiles.MaximumBytes + 1] : Encoding.UTF8.GetBytes(node.ToJsonString()));
                Assert.False(VerifierEvidence.ControlledPhaseValidForTesting(path));
                File.WriteAllBytes(sidecar, original);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var wrongReceipt = JsonNode.Parse(VerifierReceiptCodec.Write(receipt))!;
            wrongReceipt["wire_valid"] = true; File.WriteAllText(path, wrongReceipt.ToJsonString());
            Assert.False(VerifierEvidence.ControlledPhaseValidForTesting(path));
            Write(receipt);
            if (!OperatingSystem.IsWindows())
            {
                var target = Path.Join(root, "observer-target"); File.WriteAllBytes(target, original);
                File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                File.Delete(sidecar); File.CreateSymbolicLink(sidecar, target);
                Assert.False(VerifierEvidence.ControlledPhaseValidForTesting(path));
                File.Delete(sidecar); File.WriteAllBytes(sidecar, original);
                File.SetUnixFileMode(sidecar, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var actualParent = Path.Join(root, "actual-parent");
                Directory.CreateDirectory(actualParent, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                var actualReceipt = Path.Join(actualParent, "phase.json");
                File.WriteAllBytes(actualReceipt, VerifierReceiptCodec.Write(receipt));
                VerifierTypedObservationFiles.WriteNew(actualReceipt, original);
                var linkedParent = Path.Join(root, "linked-parent"); Directory.CreateSymbolicLink(linkedParent, actualParent);
                Assert.False(VerifierEvidence.ControlledPhaseValidForTesting(Path.Join(linkedParent, "phase.json")));
                Directory.Delete(linkedParent);
            }
            Assert.Throws<IOException>(() => VerifierTypedObservationFiles.WriteNew(path, original));
            Assert.Equal(original, File.ReadAllBytes(sidecar));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string Fixture(string name) => Path.Join(
        AppContext.BaseDirectory,
        "fixtures",
        "agent",
        "r3-live-agent",
        "cr1-r8-typed",
        name);

    private static string Corpus() => Path.Join(
        AppContext.BaseDirectory,
        "fixtures",
        "agent",
        "r3-quality",
        "corpus.json");

    private static VerifierBuildPair BuildPair(
        string executionArtifact,
        string architectureAssembly)
    {
        var executionSha256 = LiveAgentFreshProcessDomain.RawSha256(
            File.ReadAllBytes(executionArtifact));
        var architectureSha256 = LiveAgentFreshProcessDomain.RawSha256(
            File.ReadAllBytes(architectureAssembly));
        return new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            executionSha256,
            architectureSha256,
            VerifierBuildPairDomain.ComputeSha256(
                VerifierExecutionKinds.Framework,
                executionSha256,
                architectureSha256));
    }

    private static VerifierBuildPair TestBuildPair()
    {
        var execution = new string('a', 64);
        var architecture = new string('b', 64);
        return new VerifierBuildPair(
            VerifierExecutionKinds.Framework,
            execution,
            architecture,
            VerifierBuildPairDomain.ComputeSha256(
                VerifierExecutionKinds.Framework,
                execution,
                architecture));
    }

    private interface ArchitectureProfileProbe
    {
    }

    private abstract class ArchitectureProfileProbeBase :
        ArchitectureProfileProbe
    {
    }

    private abstract class ArchitectureProfileProbeMiddle :
        ArchitectureProfileProbeBase
    {
    }

    private sealed class ArchitectureProfileProbeDerived :
        ArchitectureProfileProbeMiddle
    {
    }

    private sealed class ArchitectureProfileProbeDirect :
        ArchitectureProfileProbe
    {
    }
}
