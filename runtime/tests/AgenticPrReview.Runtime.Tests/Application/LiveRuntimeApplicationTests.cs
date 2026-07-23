using System.Collections.Immutable;
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
    [Theory]
    [InlineData(2L, 0L, "hit")]
    [InlineData(0L, 2L, "miss")]
    [InlineData(1L, 1L, "partial")]
    public async Task InjectedDeepSeekProviderPublishesFourOutputsAndUsageOracle(
        long hit,
        long miss,
        string cacheStatus)
    {
        using var fixture = LiveFixture.Create(hit, miss, cacheStatus);
        var application = fixture.Application(new InjectedFactory(fail: false, hit, miss, cacheStatus));
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
        Assert.Equal("deepseek", metadata.RootElement.GetProperty("selectedProviderId").GetString());
        Assert.Equal(cacheStatus, metadata.RootElement.GetProperty("cacheStatus").GetString());
        Assert.Equal(1, metadata.RootElement.GetProperty("normalizedUsage").GetProperty("attempts").GetArrayLength());
        var aggregate = metadata.RootElement.GetProperty("normalizedUsage").GetProperty("aggregate");
        Assert.Equal(1, aggregate.GetProperty("requestCount").GetInt32());
        Assert.Equal(1, aggregate.GetProperty("attemptCount").GetInt32());
        Assert.Null(aggregate.GetProperty("cacheWriteInputTokens").GetString());
        Assert.Equal("partial", metadata.RootElement.GetProperty("telemetryCompleteness").GetProperty("usage").GetString());

        var ledger = LedgerParser.ParseAndValidate(await File.ReadAllBytesAsync(fixture.CandidateLedgerPath));
        Assert.NotNull(ledger.Ledger);
        Assert.Equal(2, ledger.Ledger!.Model.Records.Length);
    }

    [Fact]
    public async Task InjectedProviderFailureLeavesAllSuccessfulSidecarsAbsent()
    {
        using var fixture = LiveFixture.Create();
        var application = fixture.Application(new InjectedFactory(true, 0, 2, "miss"));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(fixture.Arguments, stdout, stderr);

        Assert.True(exitCode == 30, stderr.ToString());
        Assert.Empty(stdout.ToString());
        Assert.StartsWith("APR_PROVIDER_TIMEOUT:", stderr.ToString(), StringComparison.Ordinal);
        Assert.All(fixture.OutputPaths, path => Assert.False(File.Exists(path), path));
    }

    private sealed class InjectedFactory(bool fail, long hit, long miss, string cacheStatus) : ILiveProviderExecutorFactory
    {
        public ILiveProviderExecutor Create(string providerMode, ProviderRequestPlan plan, ExpectedIdentities identities) =>
            fail ? new FailingExecutor() : new DeepSeekObservationExecutor(hit, miss, cacheStatus);
    }

    private sealed class DeepSeekObservationExecutor(long hit, long miss, string cacheStatus) : ILiveProviderExecutor
    {
        public Task<ProviderExecutionObservation> ExecuteAsync(
            ProviderRequestPlan plan,
            ExpectedIdentities identities,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ProviderExecutionObservation(
                identities.ProviderId,
                identities.ProviderId,
                identities.ModelId,
                identities.AdapterId,
                new JsonObject { ["mode"] = "standard", ["aggregate"] = "eligible", ["statelessProof"] = null },
                cacheStatus,
                Usage(hit, miss, cacheStatus),
                RetryObservations(),
                [],
                new JsonObject { ["usage"] = "partial", ["cache"] = "complete", ["statelessProof"] = "notApplicable", ["aggregate"] = "partial" },
                "deepseek transaction",
                ImmutableArray<LedgerFinding>.Empty,
                ["Injected provider observation."],
                "live-provider"));

        private static JsonObject Usage(long hit, long miss, string cacheStatus)
        {
            var attempt = new JsonObject
            {
                ["requestOrdinal"] = 0, ["attemptOrdinal"] = 0, ["outcome"] = "succeeded",
                ["capability"] = "eligible", ["cacheStatus"] = cacheStatus, ["usageCompleteness"] = "partial",
                ["totalInputTokens"] = 2, ["uncachedInputTokens"] = miss, ["cacheWriteInputTokens"] = null,
                ["cacheReadInputTokens"] = hit, ["outputTokens"] = 1, ["attemptErrorCodes"] = new JsonArray(),
            };
            var request = new JsonObject
            {
                ["requestOrdinal"] = 0, ["capability"] = "eligible", ["cacheStatus"] = cacheStatus,
                ["usageCompleteness"] = "partial", ["totalInputTokens"] = 2, ["uncachedInputTokens"] = miss,
                ["cacheWriteInputTokens"] = null, ["cacheReadInputTokens"] = hit, ["outputTokens"] = 1,
            };
            return new JsonObject
            {
                ["attempts"] = new JsonArray(attempt), ["requests"] = new JsonArray(request),
                ["aggregate"] = new JsonObject
                {
                    ["totalInputTokens"] = 2, ["uncachedInputTokens"] = miss, ["cacheWriteInputTokens"] = null,
                    ["cacheReadInputTokens"] = hit, ["outputTokens"] = 1, ["requestCount"] = 1, ["attemptCount"] = 1,
                },
            };
        }

        private static JsonObject RetryObservations() => new()
        {
            ["requests"] = new JsonArray(new JsonObject
            {
                ["requestOrdinal"] = 0, ["attemptCount"] = 1, ["succeededCount"] = 1,
                ["failedCount"] = 0, ["cancelledCount"] = 0,
            }),
            ["aggregate"] = new JsonObject
            {
                ["requestCount"] = 1, ["attemptCount"] = 1, ["succeededCount"] = 1,
                ["failedCount"] = 0, ["cancelledCount"] = 0,
            },
        };
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

        public static LiveFixture Create(long hit = 0, long miss = 0, string cacheStatus = "unknown")
        {
            var root = Path.Combine(Path.GetTempPath(), $"apr-live-transaction-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            var inputPath = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "cases", "bootstrap", "input.json");
            var inputNode = JsonNode.Parse(File.ReadAllBytes(inputPath))!.AsObject();
            inputNode["host"]!["options"]!["maxFindings"] = 7;
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

            var template = JsonNode.Parse("{\"definition\":{\"role\":\"system\",\"text\":\"You are a precise code reviewer.\"},\"schemaVersion\":1,\"templateVersion\":3}")!;
            var policy = JsonNode.Parse("{\"constraints\":{\"maxFindings\":7,\"tone\":\"strict\"},\"instructions\":\"Review the delta carefully and return only the requested structured result.\",\"policyVersion\":2,\"schemaVersion\":1}")!;
            var tools = JsonNode.Parse("{\"definitions\":[{\"description\":\"Submit the structured review.\",\"inputSchema\":{\"properties\":{\"summary\":{\"type\":\"string\"}},\"required\":[\"summary\"],\"type\":\"object\"},\"name\":\"submit_review\",\"policyMetadata\":{\"risk\":\"low\"}}],\"schemaVersion\":1,\"toolsetVersion\":1}")!;
            var cacheConfig = JsonNode.Parse("{\"cacheConfigVersion\":1,\"eligibility\":\"automatic\",\"markerPolicy\":\"none\",\"schemaVersion\":1,\"statelessMode\":false}")!;
            var adapter = JsonNode.Parse("{\"adapterBuildVersion\":\"deepseek-openai-chat-v1\",\"capabilityProfileVersion\":1,\"requestContractSha256\":\"312f55d0038a4bcefb26703158edcf196bdb1e6a458c6ec88f3f08b1211f0356\",\"schemaVersion\":2}")!;
            using var templateDocument = JsonDocument.Parse(template.ToJsonString());
            using var policyDocument = JsonDocument.Parse(policy.ToJsonString());
            using var toolsDocument = JsonDocument.Parse(tools.ToJsonString());
            using var cacheConfigDocument = JsonDocument.Parse(cacheConfig.ToJsonString());
            using var adapterDocument = JsonDocument.Parse(adapter.ToJsonString());
            var identities = new ExpectedIdentities(
                "acme/widgets", "acme/widgets", 42, "workflow", "trusted",
                "deepseek", "deepseek-v4-flash",
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
                ["providerMode"] = "live",
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
