using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;

namespace AgenticPrReview.Runtime.Tests;

public sealed class LiveRuntimeApplicationTests
{
    [Fact]
    public async Task InjectedProviderPublishesFourOutputsAndZeroAttemptOracle()
    {
        using var fixture = LiveFixture.Create();
        var application = fixture.Application(new InjectedFactory(fail: false));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(fixture.Arguments, stdout, stderr);

        Assert.True(exitCode == 0, stderr.ToString());
        Assert.Empty(stdout.ToString());
        Assert.Empty(stderr.ToString());
        Assert.All(fixture.OutputPaths, path => Assert.True(File.Exists(path), path));

        using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.ResultPath));
        using var trace = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.TracePath));
        using var metadata = JsonDocument.Parse(await File.ReadAllBytesAsync(fixture.MetadataPath));
        Assert.Equal(1, result.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal(1, trace.RootElement.GetProperty("protocolVersion").GetInt32());
        Assert.Equal("synthetic", metadata.RootElement.GetProperty("selectedProviderId").GetString());
        Assert.Equal(0, metadata.RootElement.GetProperty("normalizedUsage").GetProperty("attempts").GetArrayLength());
        Assert.Equal(0, metadata.RootElement.GetProperty("normalizedUsage").GetProperty("aggregate").GetProperty("requestCount").GetInt32());
        Assert.Equal("missing", metadata.RootElement.GetProperty("telemetryCompleteness").GetProperty("aggregate").GetString());

        var ledger = LedgerParser.ParseAndValidate(await File.ReadAllBytesAsync(fixture.CandidateLedgerPath));
        Assert.NotNull(ledger.Ledger);
        Assert.Equal(2, ledger.Ledger!.Model.Records.Length);
    }

    [Fact]
    public async Task InjectedProviderFailureLeavesAllSuccessfulSidecarsAbsent()
    {
        using var fixture = LiveFixture.Create();
        var application = fixture.Application(new InjectedFactory(fail: true));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(fixture.Arguments, stdout, stderr);

        Assert.True(exitCode == 30, stderr.ToString());
        Assert.Empty(stdout.ToString());
        Assert.StartsWith("APR_PROVIDER_TIMEOUT:", stderr.ToString(), StringComparison.Ordinal);
        Assert.All(fixture.OutputPaths, path => Assert.False(File.Exists(path), path));
    }

    private sealed class InjectedFactory(bool fail) : ILiveProviderExecutorFactory
    {
        public ILiveProviderExecutor Create(string providerMode, ProviderRequestPlan plan, ExpectedIdentities identities) =>
            fail ? new FailingExecutor() : new SyntheticLiveProviderExecutor();
    }

    private sealed class FailingExecutor : ILiveProviderExecutor
    {
        public Task<ProviderExecutionObservation> ExecuteAsync(
            ProviderRequestPlan plan,
            ExpectedIdentities identities,
            CancellationToken cancellationToken = default) =>
            Task.FromException<ProviderExecutionObservation>(new ProviderFailureException("APR_PROVIDER_TIMEOUT", 30));
    }

    private sealed class LiveFixture : IDisposable
    {
        private readonly string root;

        private LiveFixture(string root, string[] arguments)
        {
            this.root = root;
            Arguments = arguments;
        }

        public string[] Arguments { get; }
        public string ResultPath => Path.Combine(root, "result.json");
        public string TracePath => Path.Combine(root, "trace.json");
        public string CandidateLedgerPath => Path.Combine(root, "candidate-ledger.json");
        public string MetadataPath => Path.Combine(root, "provider-run-metadata.json");
        public IReadOnlyList<string> OutputPaths => [TracePath, ResultPath, CandidateLedgerPath, MetadataPath];

        public RuntimeApplication Application(ILiveProviderExecutorFactory factory) =>
            new(fileSystem: null, executor: null, schemas: null, liveExecutorFactory: factory);

        public void Dispose()
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        public static LiveFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"apr-live-transaction-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var inputPath = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "cases", "bootstrap", "input.json");
            var inputNode = JsonNode.Parse(File.ReadAllBytes(inputPath))!.AsObject();
            foreach (var file in inputNode["subject"]!["changedFiles"]!.AsArray())
            {
                var patch = file!["patch"]?.AsObject();
                if (patch is not null)
                    patch["sha256"] = Sha256(Encoding.UTF8.GetBytes(patch["text"]!.GetValue<string>()));
            }
            var inputBytes = Encoding.UTF8.GetBytes(inputNode.ToJsonString());
            using var inputDocument = JsonDocument.Parse(inputBytes);
            var input = JsonSerializer.Deserialize(inputDocument.RootElement, RuntimeJsonContext.Default.ReviewInput)
                ?? throw new InvalidOperationException("bootstrap fixture is not a ReviewInputV1");

            var template = JsonNode.Parse("{\"definition\":{},\"schemaVersion\":1,\"templateVersion\":1}")!;
            var policy = JsonNode.Parse("{\"constraints\":{},\"instructions\":\"\",\"policyVersion\":1,\"schemaVersion\":1}")!;
            var tools = JsonNode.Parse("{\"definitions\":[],\"schemaVersion\":1,\"toolsetVersion\":1}")!;
            var cacheConfig = JsonNode.Parse("{\"cacheConfigVersion\":1,\"eligibility\":\"unknown\",\"markerPolicy\":\"none\",\"schemaVersion\":1,\"statelessMode\":false}")!;
            var adapter = JsonNode.Parse("{\"adapterBuildVersion\":\"test\",\"capabilityProfileVersion\":1,\"schemaVersion\":1}")!;
            using var templateDocument = JsonDocument.Parse(template.ToJsonString());
            using var policyDocument = JsonDocument.Parse(policy.ToJsonString());
            using var toolsDocument = JsonDocument.Parse(tools.ToJsonString());
            using var cacheConfigDocument = JsonDocument.Parse(cacheConfig.ToJsonString());
            using var adapterDocument = JsonDocument.Parse(adapter.ToJsonString());
            var identities = new ExpectedIdentities(
                "acme/widgets", "acme/widgets", 42, "workflow", "trusted",
                "synthetic", "synthetic-model",
                RequiredDigest(CacheContractDigests.ComputeAdapterId(adapterDocument.RootElement)),
                RequiredDigest(CacheContractDigests.ComputeTemplateId(templateDocument.RootElement)),
                RequiredDigest(CacheContractDigests.ComputePolicyId(policyDocument.RootElement)),
                RequiredDigest(CacheContractDigests.ComputeToolDefinitionId(toolsDocument.RootElement)),
                RequiredDigest(CacheContractDigests.ComputeCacheConfigId(cacheConfigDocument.RootElement)));
            var inputHash = Sha256(inputBytes);
            var subjectDigest = SubjectDigest(inputDocument.RootElement.GetProperty("subject"));
            var interaction = InteractionIdDeriver.Derive(
                PredecessorLedgerReference.Bootstrap.Instance,
                inputHash,
                input.Host.Review.HeadSha,
                0);
            var interactionId = interaction.InteractionId ?? throw new InvalidOperationException("interaction id did not derive");
            var sessionEpoch = "S00000000000000000000A";
            var context = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["stateKey"] = new JsonObject
                {
                    ["namespace"] = "m4-ledger-v2",
                    ["repository"] = "acme/widgets",
                    ["headRepository"] = "acme/widgets",
                    ["pullRequest"] = 42,
                    ["workflowIdentity"] = "workflow",
                    ["trustedExecutionDomain"] = "trusted",
                },
                ["sessionEpoch"] = sessionEpoch,
                ["cacheContractIdentity"] = new JsonObject
                {
                    ["ledgerSchemaVersion"] = 1,
                    ["prefixContractVersion"] = 1,
                    ["providerId"] = identities.ProviderId,
                    ["modelId"] = identities.ModelId,
                    ["adapterId"] = identities.AdapterId,
                    ["templateId"] = identities.TemplateId,
                    ["policyId"] = identities.PolicyId,
                    ["toolDefinitionId"] = identities.ToolDefinitionId,
                    ["cacheConfigId"] = identities.CacheConfigId,
                },
                ["generation"] = new JsonObject { ["stateGeneration"] = 0, ["ledgerEpoch"] = sessionEpoch },
                ["transition"] = new JsonObject
                {
                    ["kind"] = "bootstrap",
                    ["reason"] = "new_session",
                    ["predecessorLedgerSha256"] = "bootstrap",
                    ["predecessorManifestSha256"] = "bootstrap",
                },
                ["currentInteraction"] = new JsonObject
                {
                    ["interactionId"] = interactionId,
                    ["interactionOrdinal"] = 0,
                    ["consumedInputSha256"] = inputHash,
                    ["subjectDigest"] = subjectDigest,
                    ["cacheContractDigest"] = LedgerCanonicalizer.ComputeCacheContractDigest(identities),
                },
                ["cacheContractEnvelopes"] = new JsonObject
                {
                    ["template"] = template,
                    ["policy"] = policy,
                    ["tools"] = tools,
                    ["cacheConfig"] = cacheConfig,
                    ["adapter"] = adapter,
                },
                ["providerMode"] = "synthetic",
                ["producingRun"] = new JsonObject { ["producingRunId"] = "1", ["runAttempt"] = 1 },
            };
            var inputFile = Path.Combine(root, "input.json");
            var contextFile = Path.Combine(root, "live-context.json");
            File.WriteAllBytes(inputFile, inputBytes);
            File.WriteAllText(contextFile, context.ToJsonString());
            return new LiveFixture(root, [
                "review-live", "--input", inputFile, "--context", contextFile,
                "--output", Path.Combine(root, "result.json"),
                "--trace", Path.Combine(root, "trace.json"),
                "--candidate-ledger", Path.Combine(root, "candidate-ledger.json"),
                "--provider-run-metadata", Path.Combine(root, "provider-run-metadata.json"),
            ]);
        }

        private static string RequiredDigest(DigestOutcome outcome) =>
            outcome.Digest ?? throw new InvalidOperationException("test envelope digest did not derive");

        private static string SubjectDigest(JsonElement subject)
        {
            var canonical = JsonElementCanonicalizer.Canonicalize(subject, int.MaxValue, int.MaxValue, int.MaxValue, long.MaxValue, out _).ToArray();
            var tag = Encoding.UTF8.GetBytes("agentic-pr-review/review-subject/v1");
            var framed = new byte[tag.Length + 1 + canonical.Length];
            tag.CopyTo(framed, 0);
            canonical.CopyTo(framed, tag.Length + 1);
            return Sha256(framed);
        }

        private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
