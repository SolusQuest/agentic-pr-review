using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Prefix;

namespace AgenticPrReview.Runtime;

/// <summary>
/// The process-side M4 live seam. Provider execution is deliberately synthetic in
/// #55; the transaction binding and four-output publication remain runtime-owned.
/// </summary>
internal static class LiveRuntimeApplication
{
    private const string Summary = "Synthetic live runtime completed without findings.";
    private const string Limitation = "No live provider was invoked.";
    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static async Task<int> RunAsync(
        string[] args,
        IRuntimeFileSystem fileSystem,
        SchemaContracts schemas,
        TextWriter stderr)
    {
        try
        {
            var invocation = ParseInvocation(args, fileSystem);
            return await ExecuteAsync(invocation, fileSystem, schemas, stderr);
        }
        catch (RuntimeFailure failure)
        {
            await WriteDiagnosticAsync(stderr, failure.Code, failure.Message);
            return failure.ExitCode;
        }
        catch
        {
            await WriteDiagnosticAsync(stderr, "APR_RUNTIME_INTERNAL", "Live runtime execution failed.");
            return 20;
        }
    }

    private static async Task<int> ExecuteAsync(
        LiveInvocation invocation,
        IRuntimeFileSystem fileSystem,
        SchemaContracts schemas,
        TextWriter stderr)
    {
        var inputBytes = await ReadRequiredAsync(fileSystem, invocation.InputPath, "APR_INPUT_READ_FAILED");
        var contextBytes = await ReadRequiredAsync(fileSystem, invocation.ContextPath, "APR_LIVE_CONTEXT_READ_FAILED");
        if (contextBytes.Length > 2_097_152)
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SCHEMA_INVALID", "Live context exceeds its byte cap.");

        using var inputDocument = ParseJson(inputBytes, "APR_INPUT_JSON_INVALID", "Input is not valid JSON.");
        using var contextDocument = ParseJson(contextBytes, "APR_LIVE_CONTEXT_JSON_INVALID", "Live context is not valid JSON.");
        if (!schemas.IsValid(SchemaKind.LiveContext, contextDocument.RootElement))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SCHEMA_INVALID", "Live context does not satisfy LiveRuntimeInvocationContextV1.");

        if (!schemas.IsValid(SchemaKind.Input, inputDocument.RootElement))
            throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");
        var input = JsonSerializer.Deserialize(inputDocument.RootElement, RuntimeJsonContext.Default.ReviewInput)
            ?? throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");

        var inputHash = Sha256(inputBytes);
        var currentInteraction = contextDocument.RootElement.GetProperty("currentInteraction");
        if (!StringComparer.Ordinal.Equals(inputHash, currentInteraction.GetProperty("consumedInputSha256").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context consumed-input hash does not match input bytes.");

        var identities = ReadIdentities(contextDocument.RootElement.GetProperty("stateKey"), contextDocument.RootElement.GetProperty("cacheContractIdentity"));
        var expectedCacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(identities);
        if (!StringComparer.Ordinal.Equals(expectedCacheContractDigest, currentInteraction.GetProperty("cacheContractDigest").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context cache-contract digest does not match the identity fields.");
        string expectedSubjectDigest;
        try
        {
            var canonicalSubject = JsonElementCanonicalizer.Canonicalize(inputDocument.RootElement.GetProperty("subject"), 64, 512, 4_096, long.MaxValue, out _);
            var tag = Encoding.UTF8.GetBytes("agentic-pr-review/review-subject/v1");
            var framed = new byte[tag.Length + 1 + canonicalSubject.Length];
            tag.CopyTo(framed, 0);
            framed[tag.Length] = 0;
            canonicalSubject.CopyTo(framed, tag.Length + 1);
            expectedSubjectDigest = Sha256(framed);
        }
        catch
        {
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SCHEMA_INVALID", "Review subject could not be canonicalized.");
        }
        if (!StringComparer.Ordinal.Equals(expectedSubjectDigest, currentInteraction.GetProperty("subjectDigest").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context subject digest does not match ReviewInputV1.subject.");
        var transition = ReadTransition(contextDocument.RootElement, identities);
        ValidatedLedger? predecessor = null;
        byte[] predecessorBytes = [];
        if (invocation.PredecessorLedgerPath is not null)
        {
            predecessorBytes = await ReadRequiredAsync(fileSystem, invocation.PredecessorLedgerPath, "APR_PREDECESSOR_LEDGER_READ_FAILED");
            var parsed = LedgerParser.ParseAndValidate(predecessorBytes);
            if (parsed.Ledger is null)
                throw new RuntimeFailure(10, "APR_PREDECESSOR_LEDGER_INVALID", "Predecessor ledger failed validation.");
            predecessor = parsed.Ledger;
        }

        var derivedInteraction = InteractionIdDeriver.Derive(
            predecessor is null ? PredecessorLedgerReference.Bootstrap.Instance : new PredecessorLedgerReference.LedgerHash(Sha256(predecessorBytes)),
            inputHash,
            input.Host.Review.HeadSha,
            currentInteraction.GetProperty("interactionOrdinal").GetInt64());
        if (derivedInteraction.InteractionId is null || !StringComparer.Ordinal.Equals(derivedInteraction.InteractionId, currentInteraction.GetProperty("interactionId").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context interaction id does not match the host facts.");

        var interaction = new InteractionIdentity(
            currentInteraction.GetProperty("interactionId").GetString() ?? throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Interaction id is missing."),
            currentInteraction.GetProperty("interactionOrdinal").GetInt64());
        var source = new ValidatedContextSource
        {
            SubjectDigest = currentInteraction.GetProperty("subjectDigest").GetString() ?? ZeroHash,
            ReviewedHeadSha = input.Host.Review.HeadSha,
            ReviewedBaseSha = input.Host.Review.BaseSha,
            ChangedFiles = ReadChangedFiles(input.Subject.ChangedFiles)
        };
        var contextOutcome = LedgerBuilder.BuildReviewContext(source, identities, interaction);
        if (contextOutcome.Value is null)
            throw new RuntimeFailure(20, "APR_CANDIDATE_LEDGER_SELF_VALIDATION_FAILED", "Candidate context record failed self-validation.");

        var result = new ReviewResult(
            1,
            GetRuntimeVersion(),
            inputHash,
            Summary,
            [],
            [Limitation],
            [],
            [],
            new ReviewTraceReference(null, ZeroHash));
        var trace = new ReviewTrace(1, GetRuntimeVersion(), inputHash, "live-provider", null, [], [], []);
        var traceBytes = RuntimeJson.SerializeTrace(trace);
        var resultWithTrace = result with { Trace = new ReviewTraceReference(null, Sha256(traceBytes)) };
        var resultBytes = RuntimeJson.SerializeResult(resultWithTrace);

        var outcomeSource = new ValidatedOutcomeSource
        {
            Summary = resultWithTrace.Summary,
            Findings = [],
            Limitations = resultWithTrace.Limitations.ToImmutableArray()
        };
        var outcome = LedgerBuilder.BuildReviewOutcome(outcomeSource, interaction);
        if (outcome.Value is null)
            throw new RuntimeFailure(20, "APR_CANDIDATE_LEDGER_SELF_VALIDATION_FAILED", "Candidate outcome record failed self-validation.");

        CandidateOutcome candidate = transition switch
        {
            BootstrapTransition bootstrap => LedgerBuilder.CreateBootstrap(bootstrap, contextOutcome.Value, outcome.Value),
            ContinuationTransition continuation when predecessor is not null => LedgerBuilder.AppendContinuation(continuation, predecessor, contextOutcome.Value, outcome.Value),
            ResetTransition reset when predecessor is not null => LedgerBuilder.CreateReset(reset, predecessor, contextOutcome.Value, outcome.Value),
            RecoveryRootTransition recovery => LedgerBuilder.CreateRecoveryRoot(recovery, contextOutcome.Value, outcome.Value),
            _ => new CandidateOutcome(null, ImmutableArray.Create(new LedgerDiagnostic { Code = LedgerDiagnosticCodes.TransitionKindMismatch, Message = "Transition and predecessor input do not agree." }))
        };
        if (candidate.Candidate is null)
            throw new RuntimeFailure(20, "APR_CANDIDATE_LEDGER_SELF_VALIDATION_FAILED", "Candidate ledger failed self-validation.");
        var candidateLedgerBytes = candidate.Candidate.CanonicalBytes.ToArray();
        var metadataBytes = BuildMetadata(
            identities,
            interaction,
            inputHash,
            Sha256(resultBytes),
            Sha256(traceBytes),
            predecessor is null ? "bootstrap" : Sha256(predecessorBytes),
            Sha256(candidateLedgerBytes));

        var staged = new List<StagedFile>();
        try
        {
            staged.Add(await StageAsync(fileSystem, invocation.TracePath, traceBytes));
            staged.Add(await StageAsync(fileSystem, invocation.OutputPath, resultBytes));
            staged.Add(await StageAsync(fileSystem, invocation.CandidateLedgerPath, candidateLedgerBytes));
            staged.Add(await StageAsync(fileSystem, invocation.ProviderRunMetadataPath, metadataBytes));

            foreach (var file in staged)
            {
                await fileSystem.CommitNoReplaceAsync(file);
            }
            return 0;
        }
        catch
        {
            foreach (var file in staged)
            {
                await TryDeleteAsync(fileSystem, file.TempPath);
            }
            throw new RuntimeFailure(40, "APR_CANDIDATE_LEDGER_WRITE_FAILED", "Live output files could not be committed.");
        }
    }

    private static async Task<StagedFile> StageAsync(IRuntimeFileSystem fileSystem, string path, byte[] bytes)
    {
        try { return await fileSystem.StageAsync(path, bytes); }
        catch { throw new IOException("stage failed"); }
    }

    private static async Task<byte[]> ReadRequiredAsync(IRuntimeFileSystem fileSystem, string path, string code)
    {
        try { return await fileSystem.ReadAllBytesAsync(path); }
        catch { throw new RuntimeFailure(40, code, "Required live invocation file could not be read."); }
    }

    private static JsonDocument ParseJson(byte[] bytes, string code, string message)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new RuntimeFailure(10, code, message); }
    }

    private static ExpectedIdentities ReadIdentities(JsonElement stateKey, JsonElement cache)
    {
        return new ExpectedIdentities(
            stateKey.GetProperty("repository").GetString()!,
            stateKey.GetProperty("headRepository").GetString()!,
            stateKey.GetProperty("pullRequest").GetInt32(),
            stateKey.GetProperty("workflowIdentity").GetString()!,
            stateKey.GetProperty("trustedExecutionDomain").GetString()!,
            cache.GetProperty("providerId").GetString()!,
            cache.GetProperty("modelId").GetString()!,
            cache.GetProperty("adapterId").GetString()!,
            cache.GetProperty("templateId").GetString()!,
            cache.GetProperty("policyId").GetString()!,
            cache.GetProperty("toolDefinitionId").GetString()!,
            cache.GetProperty("cacheConfigId").GetString()!);
    }

    private static ExpectedTransition ReadTransition(JsonElement root, ExpectedIdentities identities)
    {
        var generation = root.GetProperty("generation");
        var transition = root.GetProperty("transition");
        var kind = transition.GetProperty("kind").GetString();
        var sessionEpoch = root.GetProperty("sessionEpoch").GetString()!;
        var ledgerEpoch = generation.GetProperty("ledgerEpoch").GetString()!;
        var stateGeneration = generation.GetProperty("stateGeneration").GetInt64();
        return kind switch
        {
            "bootstrap" => new BootstrapTransition(identities, sessionEpoch, ledgerEpoch, stateGeneration),
            "continuation" => new ContinuationTransition(identities, sessionEpoch, ledgerEpoch,
                RequiredString(transition, "predecessorLedgerSha256"), RequiredString(transition, "predecessorLedgerEpoch"),
                RequiredInt64(transition, "predecessorStateGeneration"), stateGeneration),
            "reset" => new ResetTransition(identities, sessionEpoch, ledgerEpoch,
                RequiredString(transition, "predecessorLedgerSha256"), RequiredString(transition, "predecessorManifestSha256"),
                RequiredString(transition, "predecessorLedgerEpoch"), RequiredInt64(transition, "predecessorStateGeneration"), stateGeneration,
                RequiredString(transition, "reason")),
            "recovery_root" => new RecoveryRootTransition(identities, sessionEpoch, ledgerEpoch, stateGeneration, RequiredString(transition, "reason")),
            _ => throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Live transition kind is unsupported.")
        };
    }

    private static string RequiredString(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Live transition field is missing.");

    private static long RequiredInt64(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result)
            ? result
            : throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Live transition number is missing.");

    private static ImmutableArray<LedgerChangedFile> ReadChangedFiles(IEnumerable<RuntimeChangedFile> files)
    {
        return files.Select(file => new LedgerChangedFile
        {
            Path = file.Path,
            PreviousPath = file.PreviousPath,
            Status = file.Status,
            Additions = Number(file.Additions),
            Deletions = Number(file.Deletions),
            Changes = Number(file.Changes),
            Patch = file.Patch is null ? null : new LedgerBoundedPatch
            {
                Sha256 = file.Patch.Sha256,
                Truncated = file.Patch.Truncated,
                MaxChars = Number(file.Patch.MaxChars)
            }
        }).ToImmutableArray();
    }

    private static long Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number) ? number : 0;

    private static byte[] BuildMetadata(
        ExpectedIdentities identities,
        InteractionIdentity interaction,
        string inputHash,
        string resultHash,
        string traceHash,
        string predecessorHash,
        string candidateHash)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("selectedProviderId", identities.ProviderId);
            writer.WriteString("observedProviderId", identities.ProviderId);
            writer.WriteString("resolvedModelId", identities.ModelId);
            writer.WriteString("adapterId", identities.AdapterId);
            writer.WriteString("logicalPrefixSha256", ZeroHash);
            writer.WriteString("prefixSha256", ZeroHash);
            writer.WriteStartObject("capability");
            writer.WriteString("mode", "standard");
            writer.WriteString("aggregate", "eligible");
            writer.WriteNull("statelessProof");
            writer.WriteEndObject();
            writer.WriteString("cacheStatus", "miss");
            writer.WriteStartObject("normalizedUsage");
            writer.WriteStartArray("attempts"); writer.WriteEndArray();
            writer.WriteStartArray("requests"); writer.WriteEndArray();
            WriteEmptyAggregate(writer);
            writer.WriteEndObject();
            writer.WriteStartObject("retryObservations");
            writer.WriteStartArray("requests"); writer.WriteEndArray();
            writer.WriteStartObject("aggregate");
            writer.WriteNumber("requestCount", 0); writer.WriteNumber("attemptCount", 0);
            writer.WriteNumber("succeededCount", 0); writer.WriteNumber("failedCount", 0); writer.WriteNumber("cancelledCount", 0);
            writer.WriteEndObject(); writer.WriteEndObject();
            writer.WriteStartArray("errorCodes"); writer.WriteEndArray();
            writer.WriteStartObject("telemetryCompleteness");
            writer.WriteString("usage", "missing"); writer.WriteString("cache", "unknown"); writer.WriteString("statelessProof", "notApplicable"); writer.WriteString("aggregate", "missing");
            writer.WriteEndObject();
            writer.WriteString("producingRunId", "1"); writer.WriteNumber("runAttempt", 1);
            writer.WriteString("interactionId", interaction.InteractionId);
            writer.WriteString("consumedInputSha256", inputHash);
            writer.WriteString("resultSha256", resultHash);
            writer.WriteString("traceSha256", traceHash);
            writer.WriteString("predecessorLedgerSha256", predecessorHash);
            writer.WriteString("candidateLedgerSha256", candidateHash);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteEmptyAggregate(Utf8JsonWriter writer)
    {
        writer.WriteStartObject("aggregate");
        writer.WriteNull("totalInputTokens"); writer.WriteNull("uncachedInputTokens"); writer.WriteNull("cacheWriteInputTokens"); writer.WriteNull("cacheReadInputTokens"); writer.WriteNull("outputTokens");
        writer.WriteNumber("requestCount", 0); writer.WriteNumber("attemptCount", 0);
        writer.WriteEndObject();
    }

    private static LiveInvocation ParseInvocation(string[] args, IRuntimeFileSystem fileSystem)
    {
        if (args.Length < 2 || !StringComparer.Ordinal.Equals(args[0], "review-live"))
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Expected review-live --input --context --output --trace --candidate-ledger --provider-run-metadata.");
        string? input = null, context = null, predecessor = null, output = null, trace = null, candidate = null, metadata = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Every review-live flag requires one path.");
            var flag = args[index]; var value = args[index + 1];
            switch (flag)
            {
                case "--input" when input is null: input = value; break;
                case "--context" when context is null: context = value; break;
                case "--predecessor-ledger" when predecessor is null: predecessor = value; break;
                case "--output" when output is null: output = value; break;
                case "--trace" when trace is null: trace = value; break;
                case "--candidate-ledger" when candidate is null: candidate = value; break;
                case "--provider-run-metadata" when metadata is null: metadata = value; break;
                default: throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Unknown or duplicate review-live flag.");
            }
        }
        if (input is null || context is null || output is null || trace is null || candidate is null || metadata is null)
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Required review-live flags are missing.");
        var paths = new[] { input, context, predecessor, output, trace, candidate, metadata }.Where(path => path is not null).Select(path => Path.GetFullPath(path!)).ToArray();
        var parent = Path.GetDirectoryName(paths[0]);
        if (parent is null || new[] { input, context, predecessor, output, trace, candidate, metadata }.Where(path => path is not null).Any(path => !Path.IsPathFullyQualified(path!)) || paths.Any(path => !StringComparer.Ordinal.Equals(Path.GetDirectoryName(path), parent)))
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "All review-live paths must be direct children of one invocation directory.");
        if (Path.GetFileName(input) != "input.json" || Path.GetFileName(context) != "live-context.json" ||
            (predecessor is not null && Path.GetFileName(predecessor) != "predecessor-ledger.json") ||
            Path.GetFileName(output) != "result.json" || Path.GetFileName(trace) != "trace.json" ||
            Path.GetFileName(candidate) != "candidate-ledger.json" || Path.GetFileName(metadata) != "provider-run-metadata.json")
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "review-live paths must use the fixed invocation filenames.");
        if (new[] { output, trace, candidate, metadata }.Any(path => fileSystem.Exists(Path.GetFullPath(path!))))
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "review-live output paths must be absent before preflight.");
        if (predecessor is not null && !fileSystem.Exists(predecessor))
            throw new RuntimeFailure(40, "APR_PREDECESSOR_LEDGER_READ_FAILED", "Predecessor ledger is unavailable.");
        return new LiveInvocation(paths[0], paths[1], predecessor, paths[^4], paths[^3], paths[^2], paths[^1]);
    }

    private static string Sha256(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string GetRuntimeVersion() => typeof(LiveRuntimeApplication).Assembly.GetName().Version?.ToString() ?? "0.1.0-dev";
    private static Task WriteDiagnosticAsync(TextWriter stderr, string code, string message) => stderr.WriteLineAsync($"{code}: {message}");
    private static async Task TryDeleteAsync(IRuntimeFileSystem fileSystem, string path) { try { await fileSystem.DeleteIfExistsAsync(path); } catch { } }
}

internal sealed record LiveInvocation(
    string InputPath,
    string ContextPath,
    string? PredecessorLedgerPath,
    string OutputPath,
    string TracePath,
    string CandidateLedgerPath,
    string ProviderRunMetadataPath);
