using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Prefix;

namespace AgenticPrReview.Runtime;

/// <summary>
/// The process-side M4 live seam. Provider execution is selected from the
/// validated invocation context; transaction binding and four-output publication
/// remain runtime-owned.
/// </summary>
internal static class LiveRuntimeApplication
{
    private const string ZeroHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static async Task<int> RunAsync(
        string[] args,
        IRuntimeFileSystem fileSystem,
        SchemaContracts schemas,
        TextWriter stderr,
        ILiveProviderExecutorFactory? executorFactory = null)
    {
        try
        {
            var invocation = ParseInvocation(args, fileSystem);
            return await ExecuteAsync(invocation, fileSystem, schemas, stderr, executorFactory);
        }
        catch (RuntimeFailure failure)
        {
            await WriteDiagnosticAsync(stderr, failure.Code, failure.Message);
            return failure.ExitCode;
        }
        catch (ProviderFailureException failure)
        {
            await WriteDiagnosticAsync(stderr, failure.Code, "Provider invocation failed.");
            return failure.ExitCode;
        }
        catch (Exception ex) when (!IsFatalException(ex))
        {
            await WriteDiagnosticAsync(stderr, "APR_RUNTIME_INTERNAL", "Live runtime execution failed.");
            return 20;
        }
    }

    private static async Task<int> ExecuteAsync(
        LiveInvocation invocation,
        IRuntimeFileSystem fileSystem,
        SchemaContracts schemas,
        TextWriter stderr,
        ILiveProviderExecutorFactory? executorFactory)
    {
        var inputBytes = await ReadRequiredAsync(fileSystem, invocation.InputPath, "APR_INPUT_READ_FAILED");
        var contextBytes = await ReadRequiredAsync(fileSystem, invocation.ContextPath, "APR_LIVE_CONTEXT_READ_FAILED");
        if (contextBytes.Length > 2_097_152)
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SCHEMA_INVALID", "Live context exceeds its byte cap.");
        if (contextBytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_BOM", "Live context must not contain a UTF-8 BOM.");
        try { _ = new UTF8Encoding(false, true).GetString(contextBytes); }
        catch (DecoderFallbackException)
        {
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_INVALID_UTF8", "Live context is not valid UTF-8.");
        }

        using var inputDocument = ParseJson(inputBytes, "APR_INPUT_JSON_INVALID", "Input is not valid JSON.");
        using var contextDocument = ParseJson(contextBytes, "APR_LIVE_CONTEXT_JSON_INVALID", "Live context is not valid JSON.");
        if (HasDuplicateJsonProperties(contextBytes))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_DUPLICATE_PROPERTY", "Live context contains a duplicate property.");
        if (!contextDocument.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
            !IsMathematicalInteger(schemaVersion, 1))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_VERSION", "Live context schema version is unsupported.");
        if (ContainsLoneSurrogate(contextBytes))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_UNICODE", "Live context contains an unpaired surrogate.");
        if (!schemas.IsValid(SchemaKind.LiveContext, contextDocument.RootElement))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SCHEMA_INVALID", "Live context does not satisfy LiveRuntimeInvocationContextV1.");
        if (!ValidateContextSemanticDomains(contextDocument.RootElement))
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SEMANTIC_INVALID", "Live context identity or lifecycle domains are invalid.");
        var providerMode = contextDocument.RootElement.GetProperty("providerMode").GetString();
        if (providerMode is not ("synthetic" or "live"))
            throw new RuntimeFailure(10, "APR_LIVE_PROVIDER_UNAVAILABLE", "The live runtime provider mode is unsupported.");

        if (!schemas.IsValid(SchemaKind.Input, inputDocument.RootElement))
            throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");
        var input = JsonSerializer.Deserialize(inputDocument.RootElement, RuntimeJsonContext.Default.ReviewInput)
            ?? throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");
        if (!StringComparer.Ordinal.Equals(input.Host.Review.RuntimeProvider, "test"))
            throw new RuntimeFailure(10, "APR_INPUT_RUNTIME_PROVIDER_INVALID", "The #55 live seam requires the host test runtime provider.");
        if (input.RequestedRuntimeVersion is not null &&
            !StringComparer.Ordinal.Equals(input.RequestedRuntimeVersion, RuntimeApplication.RuntimeVersion))
            throw new RuntimeFailure(10, "APR_RUNTIME_VERSION_MISMATCH", "Requested runtime version does not match this binary.");

        var inputHash = Sha256(inputBytes);
        var currentInteraction = contextDocument.RootElement.GetProperty("currentInteraction");
        if (!StringComparer.Ordinal.Equals(inputHash, currentInteraction.GetProperty("consumedInputSha256").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context consumed-input hash does not match input bytes.");

        var identities = ReadIdentities(contextDocument.RootElement.GetProperty("stateKey"), contextDocument.RootElement.GetProperty("cacheContractIdentity"));
        var producingRun = contextDocument.RootElement.GetProperty("producingRun");
        var producingRunId = producingRun.GetProperty("producingRunId").GetString()!;
        var producingRunAttempt = NumberInt32(producingRun.GetProperty("runAttempt"), "APR_LIVE_CONTEXT_SCHEMA_INVALID", 1, int.MaxValue);
        var expectedCacheContractDigest = LedgerCanonicalizer.ComputeCacheContractDigest(identities);
        if (!StringComparer.Ordinal.Equals(expectedCacheContractDigest, currentInteraction.GetProperty("cacheContractDigest").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context cache-contract digest does not match the identity fields.");
        string expectedSubjectDigest;
        try
        {
            using var subjectDocument = JsonDocument.Parse(inputDocument.RootElement.GetProperty("subject").GetRawText());
            var canonicalSubject = JsonElementCanonicalizer.Canonicalize(subjectDocument.RootElement, int.MaxValue, int.MaxValue, int.MaxValue, long.MaxValue, out _);
            var tag = Encoding.UTF8.GetBytes("agentic-pr-review/review-subject/v1");
            var framed = new byte[tag.Length + 1 + canonicalSubject.Length];
            tag.CopyTo(framed, 0);
            framed[tag.Length] = 0;
            canonicalSubject.CopyTo(framed, tag.Length + 1);
            expectedSubjectDigest = Sha256(framed);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            throw new RuntimeFailure(10, "APR_LIVE_CONTEXT_SCHEMA_INVALID", "Review subject could not be canonicalized.");
        }
        if (!StringComparer.Ordinal.Equals(expectedSubjectDigest, currentInteraction.GetProperty("subjectDigest").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context subject digest does not match ReviewInputV1.subject.");
        var transition = ReadTransition(contextDocument.RootElement, identities);
        var transitionKind = contextDocument.RootElement.GetProperty("transition").GetProperty("kind").GetString();
        var rootTransition = transitionKind is "bootstrap" or "recovery_root";
        if (rootTransition != (invocation.PredecessorLedgerPath is null))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Predecessor path presence does not match the transition kind.");
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
            NumberInt64(currentInteraction.GetProperty("interactionOrdinal"), "APR_LIVE_CONTEXT_SCHEMA_INVALID", 0, 1_000_000));
        if (derivedInteraction.InteractionId is null || !StringComparer.Ordinal.Equals(derivedInteraction.InteractionId, currentInteraction.GetProperty("interactionId").GetString()))
            throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Context interaction id does not match the host facts.");

        var interaction = new InteractionIdentity(
            currentInteraction.GetProperty("interactionId").GetString() ?? throw new RuntimeFailure(10, "APR_LIVE_TRANSITION_INVALID", "Interaction id is missing."),
            NumberInt64(currentInteraction.GetProperty("interactionOrdinal"), "APR_LIVE_CONTEXT_SCHEMA_INVALID", 0, 1_000_000));
        var source = new ValidatedContextSource
        {
            SubjectDigest = currentInteraction.GetProperty("subjectDigest").GetString() ?? ZeroHash,
            ReviewedHeadSha = input.Host.Review.HeadSha,
            ReviewedBaseSha = input.Host.Review.BaseSha,
            ChangedFiles = ReadChangedFiles(input.Subject.ChangedFiles),
            CurrentEvidence = new CurrentReviewEvidence
            {
                Subject = BuildCurrentSubject(input.Subject.PullRequest),
                Files = input.Subject.ChangedFiles.Select(file => new CurrentEvidenceFile
                {
                    Path = file.Path,
                    Patch = file.Patch?.Text,
                }).ToImmutableArray(),
            },
        };
        var contextOutcome = LedgerBuilder.BuildReviewContext(source, identities, interaction);
        if (contextOutcome.Value is null)
            throw new RuntimeFailure(20, "APR_CANDIDATE_LEDGER_SELF_VALIDATION_FAILED", "Candidate context record failed self-validation.");
        var envelopes = contextDocument.RootElement.GetProperty("cacheContractEnvelopes");
        var sessionEpoch = contextDocument.RootElement.GetProperty("sessionEpoch").GetString()!;
        var prefixOutcome = PrefixMaterializer.Materialize(new PrefixMaterializationInput(
            predecessor is null || transition is BootstrapTransition or RecoveryRootTransition
                ? MaterializationHistory.BootstrapHistory.Instance
                : transition is ContinuationTransition
                    ? new MaterializationHistory.ContinuationHistory(predecessor!)
                    : new MaterializationHistory.ResetHistory(predecessor!),
            source,
            interaction,
            identities,
            sessionEpoch,
            new RawCacheContractEnvelopes(
                envelopes.GetProperty("template"), envelopes.GetProperty("policy"),
                envelopes.GetProperty("tools"), envelopes.GetProperty("cacheConfig"),
                envelopes.GetProperty("adapter"))));
        if (prefixOutcome.Value is null)
            throw new RuntimeFailure(20, "APR_LIVE_CONTEXT_SEMANTIC_INVALID", "Cache-contract envelopes failed independent validation.");

        var requestPlan = ProviderRequestPlan.From(
            prefixOutcome.Value,
            identities,
            ReadMaxFindings(input.Host.Options),
            envelopes.GetProperty("adapter").TryGetProperty("requestContractSha256", out var requestContractSha256)
                ? requestContractSha256.GetString()
                : null);
        var executor = (executorFactory ?? new DefaultLiveProviderExecutorFactory()).Create(providerMode);
        var observation = await executor.ExecuteAsync(requestPlan, identities);
        var result = new ReviewResult(
            1,
            RuntimeApplication.RuntimeVersion,
            inputHash,
            observation.Summary,
            observation.Findings.Select(ToRuntimeFinding).ToArray(),
            observation.Limitations,
            [],
            [],
            new ReviewTraceReference(null, ZeroHash));
        var trace = new ReviewTrace(1, RuntimeApplication.RuntimeVersion, inputHash, observation.Mode, null, [], [], []);
        var traceBytes = RuntimeJson.SerializeTrace(trace);
        var resultWithTrace = result with { Trace = new ReviewTraceReference(null, Sha256(traceBytes)) };
        var resultBytes = RuntimeJson.SerializeResult(resultWithTrace);
        using (var resultDocument = ParseJson(resultBytes, "APR_RESULT_JSON_INVALID", "Live result is not valid JSON."))
        using (var traceDocument = ParseJson(traceBytes, "APR_TRACE_JSON_INVALID", "Live trace is not valid JSON."))
        {
            if (!schemas.IsValid(SchemaKind.Result, resultDocument.RootElement) ||
                !SemanticValidation.HasValidFindingLocations(resultDocument.RootElement) ||
                !schemas.IsValid(SchemaKind.Trace, traceDocument.RootElement))
                throw new RuntimeFailure(20, "APR_LIVE_OUTPUT_SELF_VALIDATION_FAILED", "Live result or trace failed self-validation.");
        }

        var outcomeSource = new ValidatedOutcomeSource
        {
            Summary = resultWithTrace.Summary,
            Findings = observation.Findings,
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
            Sha256(candidateLedgerBytes),
            producingRunId,
            producingRunAttempt,
            prefixOutcome.Value.LogicalPrefixSha256,
            prefixOutcome.Value.PrefixSha256,
            observation);
        using (var metadataDocument = ParseJson(metadataBytes, "APR_METADATA_JSON_INVALID", "Live provider metadata is not valid JSON."))
        {
            if (!schemas.IsValid(SchemaKind.ProviderRunMetadata, metadataDocument.RootElement) ||
                !HasValidMetadataSemantics(metadataDocument.RootElement, observation, providerMode))
                throw new RuntimeFailure(20, "APR_LIVE_OUTPUT_SELF_VALIDATION_FAILED", "Live provider metadata failed schema validation.");
        }

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            foreach (var file in staged)
            {
                await TryDeleteAsync(fileSystem, file.TempPath);
            }
            if (providerMode == "live")
                throw new ProviderFailureException("APR_PROVIDER_PERSISTENCE", 40);
            throw new RuntimeFailure(40, "APR_CANDIDATE_LEDGER_WRITE_FAILED", "Live output files could not be committed.");
        }
    }

    private static async Task<StagedFile> StageAsync(IRuntimeFileSystem fileSystem, string path, byte[] bytes)
    {
        try { return await fileSystem.StageAsync(path, bytes); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException("stage failed", ex);
        }
    }

    private static async Task<byte[]> ReadRequiredAsync(IRuntimeFileSystem fileSystem, string path, string code)
    {
        try { return await fileSystem.ReadAllBytesAsync(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new RuntimeFailure(40, code, "Required live invocation file could not be read.");
        }
    }

    private static JsonDocument ParseJson(byte[] bytes, string code, string message)
    {
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw new RuntimeFailure(10, code, message); }
    }

    private static bool HasDuplicateJsonProperties(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        var scopes = new Stack<HashSet<string>>();
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;
                case JsonTokenType.EndObject:
                    scopes.Pop();
                    break;
                case JsonTokenType.PropertyName:
                    if (!scopes.Peek().Add(DecodeJsonStringToken(reader))) return true;
                    break;
            }
        }
        return false;
    }

    private static bool ContainsLoneSurrogate(ReadOnlySpan<byte> bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if ((reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName) &&
                ContainsLoneSurrogate(DecodeJsonStringToken(reader))) return true;
        }
        return false;
    }

    private static bool ContainsLoneSurrogate(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current is >= '\uD800' and <= '\uDBFF')
            {
                if (index + 1 >= value.Length || value[index + 1] is < '\uDC00' or > '\uDFFF') return true;
                index++;
            }
            else if (current is >= '\uDC00' and <= '\uDFFF') return true;
        }
        return false;
    }

    private static bool ValidateContextSemanticDomains(JsonElement root)
    {
        var state = root.GetProperty("stateKey");
        var cache = root.GetProperty("cacheContractIdentity");
        var repositoryPattern = new System.Text.RegularExpressions.Regex("^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!ValidRepository(state.GetProperty("repository"), repositoryPattern) ||
            !ValidRepository(state.GetProperty("headRepository"), repositoryPattern)) return false;
        if (!new[] { "workflowIdentity", "trustedExecutionDomain" }
            .All(field => ValidUtf8Bound(state.GetProperty(field), 256))) return false;
        if (!new[] { "providerId", "modelId" }
            .All(field => ValidUtf8Bound(cache.GetProperty(field), 256))) return false;
        if (cache.GetProperty("modelId").GetString() == "latest") return false;
        var generation = root.GetProperty("generation");
        var stateGeneration = NumberInt64(generation.GetProperty("stateGeneration"), "APR_LIVE_CONTEXT_SEMANTIC_INVALID", 0, 1_000_000);
        var transition = root.GetProperty("transition");
        var kind = transition.GetProperty("kind").GetString();
        if (new[] { "sessionEpoch", "stateKey", "cacheContractIdentity", "generation", "transition", "currentInteraction", "providerMode", "producingRun" }
            .Any(field => ContainsForbiddenControl(root.GetProperty(field)))) return false;
        var currentInteraction = root.GetProperty("currentInteraction");
        if (kind is "bootstrap" or "recovery_root")
            return stateGeneration == 0 && NumberInt64(currentInteraction.GetProperty("interactionOrdinal"), "APR_LIVE_CONTEXT_SEMANTIC_INVALID", 0, 1_000_000) == 0;
        var predecessorGeneration = NumberInt64(transition.GetProperty("predecessorStateGeneration"), "APR_LIVE_CONTEXT_SEMANTIC_INVALID", 0, 1_000_000);
        return stateGeneration == predecessorGeneration + 1;
    }

    private static bool ContainsForbiddenControl(JsonElement value)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(value.GetRawText()), isFinalBlock: true, state: default);
        while (reader.Read())
        {
            if (reader.TokenType is not (JsonTokenType.String or JsonTokenType.PropertyName)) continue;
            var text = DecodeJsonStringToken(reader);
            if (text.Any(character => character < '\u0020' || character == '\u007F')) return true;
        }
        return false;
    }

    private static string DecodeJsonStringToken(Utf8JsonReader reader)
    {
        var bytes = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
        var builder = new StringBuilder();
        var segmentStart = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != (byte)'\\') continue;
            if (index > segmentStart) builder.Append(new UTF8Encoding(false, true).GetString(bytes, segmentStart, index - segmentStart));
            if (++index >= bytes.Length) break;
            switch (bytes[index])
            {
                case (byte)'"': builder.Append('"'); break;
                case (byte)'\\': builder.Append('\\'); break;
                case (byte)'/': builder.Append('/'); break;
                case (byte)'b': builder.Append('\b'); break;
                case (byte)'f': builder.Append('\f'); break;
                case (byte)'n': builder.Append('\n'); break;
                case (byte)'r': builder.Append('\r'); break;
                case (byte)'t': builder.Append('\t'); break;
                case (byte)'u':
                    if (index + 4 >= bytes.Length) throw new JsonException();
                    var code = (char)((HexValue(bytes[index + 1]) << 12) | (HexValue(bytes[index + 2]) << 8) | (HexValue(bytes[index + 3]) << 4) | HexValue(bytes[index + 4]));
                    builder.Append(code);
                    index += 4;
                    break;
                default: throw new JsonException();
            }
            segmentStart = index + 1;
        }
        if (segmentStart < bytes.Length) builder.Append(new UTF8Encoding(false, true).GetString(bytes, segmentStart, bytes.Length - segmentStart));
        return builder.ToString();
    }

    private static int HexValue(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - (byte)'0',
        >= (byte)'a' and <= (byte)'f' => value - (byte)'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - (byte)'A' + 10,
        _ => throw new JsonException()
    };

    private static bool ValidRepository(JsonElement value, System.Text.RegularExpressions.Regex pattern) =>
        value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
        text.Length >= 3 && Encoding.UTF8.GetByteCount(text) <= 200 && pattern.IsMatch(text);

    private static bool ValidUtf8Bound(JsonElement value, int maximumBytes) =>
        value.ValueKind == JsonValueKind.String && Encoding.UTF8.GetByteCount(value.GetString() ?? string.Empty) <= maximumBytes;

    private static bool IsMathematicalInteger(JsonElement value, long expected)
    {
        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number) &&
            number == decimal.Truncate(number) && number == expected;
    }

    private static ExpectedIdentities ReadIdentities(JsonElement stateKey, JsonElement cache)
    {
        return new ExpectedIdentities(
            stateKey.GetProperty("repository").GetString()!,
            stateKey.GetProperty("headRepository").GetString()!,
            NumberInt32(stateKey.GetProperty("pullRequest"), "APR_LIVE_CONTEXT_SCHEMA_INVALID", 1, int.MaxValue),
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
        var stateGeneration = NumberInt64(generation.GetProperty("stateGeneration"), "APR_LIVE_CONTEXT_SCHEMA_INVALID", 0, 1_000_000);
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
        value.TryGetProperty(name, out var property)
            ? NumberInt64(property, "APR_LIVE_TRANSITION_INVALID", 0, 1_000_000)
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

    private static string BuildCurrentSubject(RuntimePullRequest pullRequest)
    {
        var subject = string.IsNullOrEmpty(pullRequest.Body)
            ? pullRequest.Title
            : $"{pullRequest.Title}\n\n{pullRequest.Body}";
        return subject.EnumerateRunes().Count() <= 4_000
            ? subject
            : string.Concat(subject.EnumerateRunes().Take(4_000).Select(rune => rune.ToString()));
    }

    private static int ReadMaxFindings(RuntimeOptions? options)
    {
        if (options?.MaxFindings is not JsonElement maxFindings ||
            maxFindings.ValueKind != JsonValueKind.Number ||
            !maxFindings.TryGetInt32(out var value))
            return 50;
        return Math.Clamp(value, 1, 50);
    }

    private static RuntimeFinding ToRuntimeFinding(LedgerFinding finding) => new(
        finding.Severity,
        finding.Confidence,
        finding.Category,
        finding.Title,
        finding.Body,
        finding.Path,
        finding.StartLine is null ? null : checked((int)finding.StartLine.Value),
        finding.EndLine is null ? null : checked((int)finding.EndLine.Value),
        finding.Evidence,
        finding.SuggestedAction,
        finding.InlinePreference);

    private static long Number(JsonElement value)
    {
        return NumberInt64(value, "APR_INPUT_SCHEMA_INVALID", 0, 1_000_000);
    }

    private static int NumberInt32(JsonElement value, string code, long minimum, long maximum) =>
        checked((int)NumberInt64(value, code, minimum, maximum));

    private static long NumberInt64(JsonElement value, string code, long minimum, long maximum)
    {
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDecimal(out var decimalValue) ||
            decimalValue != decimal.Truncate(decimalValue) ||
            decimalValue < minimum || decimalValue > maximum)
            throw new RuntimeFailure(10, code, "A live integer is not a bounded mathematical integer.");
        return (long)decimalValue;
    }

    private static byte[] BuildMetadata(
        ExpectedIdentities identities,
        InteractionIdentity interaction,
        string inputHash,
        string resultHash,
        string traceHash,
        string predecessorHash,
        string candidateHash,
        string producingRunId,
        int producingRunAttempt,
        string logicalPrefixSha256,
        string prefixSha256,
        ProviderExecutionObservation observation)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("selectedProviderId", observation.SelectedProviderId);
            writer.WriteString("observedProviderId", observation.ObservedProviderId);
            writer.WriteString("resolvedModelId", observation.ResolvedModelId);
            writer.WriteString("adapterId", observation.AdapterId);
            writer.WriteString("logicalPrefixSha256", logicalPrefixSha256);
            writer.WriteString("prefixSha256", prefixSha256);
            WriteNode(writer, "capability", observation.Capability);
            writer.WriteString("cacheStatus", observation.CacheStatus);
            WriteNode(writer, "normalizedUsage", observation.NormalizedUsage);
            WriteNode(writer, "retryObservations", observation.RetryObservations);
            writer.WritePropertyName("errorCodes");
            writer.WriteStartArray(); foreach (var code in observation.ErrorCodes) writer.WriteStringValue(code); writer.WriteEndArray();
            WriteNode(writer, "telemetryCompleteness", observation.TelemetryCompleteness);
            writer.WriteString("producingRunId", producingRunId); writer.WriteNumber("runAttempt", producingRunAttempt);
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

    private static void WriteNode(Utf8JsonWriter writer, string property, JsonObject node)
    {
        writer.WritePropertyName(property);
        node.WriteTo(writer);
    }

    private static bool HasValidMetadataSemantics(
        JsonElement metadata,
        ProviderExecutionObservation observation,
        string providerMode)
    {
        if (metadata.GetProperty("selectedProviderId").GetString() != observation.SelectedProviderId ||
            metadata.GetProperty("observedProviderId").GetString() != observation.ObservedProviderId ||
            metadata.GetProperty("resolvedModelId").GetString() != observation.ResolvedModelId ||
            metadata.GetProperty("adapterId").GetString() != observation.AdapterId ||
            metadata.GetProperty("errorCodes").GetArrayLength() != 0)
            return false;
        if (providerMode == "synthetic")
        {
            if (metadata.GetProperty("cacheStatus").GetString() != "unknown") return false;
            var capability = metadata.GetProperty("capability");
            if (capability.GetProperty("aggregate").GetString() != "unknown" ||
                capability.GetProperty("statelessProof").ValueKind != JsonValueKind.Null)
                return false;
            var usage = metadata.GetProperty("normalizedUsage");
            var aggregate = usage.GetProperty("aggregate");
            return usage.GetProperty("attempts").GetArrayLength() == 0 &&
                usage.GetProperty("requests").GetArrayLength() == 0 &&
                NumberInt64(aggregate.GetProperty("requestCount"), "APR_LIVE_OUTPUT_SELF_VALIDATION_FAILED", 0, 0) == 0 &&
                NumberInt64(aggregate.GetProperty("attemptCount"), "APR_LIVE_OUTPUT_SELF_VALIDATION_FAILED", 0, 0) == 0 &&
                metadata.GetProperty("retryObservations").GetProperty("requests").GetArrayLength() == 0 &&
                metadata.GetProperty("telemetryCompleteness").GetProperty("usage").GetString() == "missing" &&
                metadata.GetProperty("telemetryCompleteness").GetProperty("cache").GetString() == "missing" &&
                metadata.GetProperty("telemetryCompleteness").GetProperty("aggregate").GetString() == "missing";
        }
        var liveCapability = metadata.GetProperty("capability");
        if (liveCapability.GetProperty("aggregate").GetString() != "eligible" ||
            liveCapability.GetProperty("statelessProof").ValueKind != JsonValueKind.Null ||
            metadata.GetProperty("cacheStatus").GetString() != observation.CacheStatus)
            return false;
        var liveUsage = metadata.GetProperty("normalizedUsage");
        var liveAggregate = liveUsage.GetProperty("aggregate");
        return liveUsage.GetProperty("attempts").GetArrayLength() == 1 &&
            liveUsage.GetProperty("requests").GetArrayLength() == 1 &&
            NumberInt64(liveAggregate.GetProperty("requestCount"), "APR_LIVE_OUTPUT_SELF_VALIDATION_FAILED", 1, 1) == 1 &&
            NumberInt64(liveAggregate.GetProperty("attemptCount"), "APR_LIVE_OUTPUT_SELF_VALIDATION_FAILED", 1, 1) == 1 &&
            metadata.GetProperty("retryObservations").GetProperty("requests").GetArrayLength() == 1 &&
            metadata.GetProperty("telemetryCompleteness").GetProperty("usage").GetString() == "partial" &&
            metadata.GetProperty("telemetryCompleteness").GetProperty("cache").GetString() == "complete" &&
            metadata.GetProperty("telemetryCompleteness").GetProperty("aggregate").GetString() == "partial";
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
    private static bool IsFatalException(Exception ex) => ex is
        OutOfMemoryException or
        StackOverflowException or
        AccessViolationException or
        AppDomainUnloadedException or
        BadImageFormatException or
        CannotUnloadAppDomainException or
        InvalidProgramException or
        ThreadAbortException;
    private static Task WriteDiagnosticAsync(TextWriter stderr, string code, string message) => stderr.WriteLineAsync($"{code}: {message}");
    private static async Task TryDeleteAsync(IRuntimeFileSystem fileSystem, string path)
    {
        try
        {
            await fileSystem.DeleteIfExistsAsync(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Cleanup is best effort after a failed commit.
        }
    }
}

internal sealed record LiveInvocation(
    string InputPath,
    string ContextPath,
    string? PredecessorLedgerPath,
    string OutputPath,
    string TracePath,
    string CandidateLedgerPath,
    string ProviderRunMetadataPath);
