using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using Json.Schema;

namespace AgenticPrReview.Runtime.Ledger;

public static class LedgerParser
{
    public const int LedgerRawByteLimit = 524_288;
    public const int LedgerCanonicalByteLimit = 262_144;
    public const int LedgerJsonMaxDepth = 64;
    public const int LedgerJsonMaxArrayLength = 4_096;
    public const int LedgerJsonMaxPropertyCount = 512;

    public static ParseOutcome ParseAndValidate(ReadOnlySpan<byte> bytes)
    {
        // Fail-closed guard: the public parser must never surface an unhandled
        // exception for any JSON input. Every stage below classifies its own
        // failures; this catch converts anything that escaped that analysis into the
        // generic schema-stage fallback code (the mapper's own fallback bucket)
        // instead of crashing the caller. The message is a fixed literal so no
        // exception detail (which may echo untrusted input) leaks into diagnostics.
        try
        {
            return ParseAndValidateCore(bytes);
        }
        catch (Exception)
        {
            return new ParseOutcome(null, ImmutableArray.Create(
                CreateDiagnostic(LedgerDiagnosticCodes.SchemaViolation, "Ledger validation failed unexpectedly.")));
        }
    }

    private static ParseOutcome ParseAndValidateCore(ReadOnlySpan<byte> bytes)
    {
        var rawDiagnostic = RunRawTransportChecks(bytes);
        if (rawDiagnostic is not null)
        {
            return new ParseOutcome(null, ImmutableArray.Create(rawDiagnostic));
        }

        // The Unicode-safety stage runs over the lenient tree built from the raw
        // bytes: JsonDocument/JsonElement cannot materialize property names that
        // contain unpaired UTF-16 surrogate escapes, and the traversal must sort
        // every key (valid or not) at its exact unsigned UTF-16 ordinal position.
        var unicodeDiagnostic = LedgerSafePath.ScanForUnicodeViolation(bytes);
        if (unicodeDiagnostic is not null)
        {
            return new ParseOutcome(null, ImmutableArray.Create(unicodeDiagnostic));
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = LedgerJsonMaxDepth });
        }
        catch (JsonException ex)
        {
            return new ParseOutcome(null, ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.InvalidJson, ex.Message)));
        }

        using (document)
        {
            var versionDiagnostic = RunVersionRouting(document.RootElement);
            if (versionDiagnostic is not null)
            {
                return new ParseOutcome(null, ImmutableArray.Create(versionDiagnostic));
            }

            var schemaResults = SchemaContracts.Load(typeof(LedgerParser).Assembly).GetSchema(SchemaKind.Ledger).Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
            if (!schemaResults.IsValid)
            {
                var schemaDiagnostics = LedgerSchemaMapper.Map(document, schemaResults);
                return new ParseOutcome(null, schemaDiagnostics);
            }

            LedgerModel model;
            ImmutableArray<byte> canonicalBytes;
            try
            {
                model = BuildModel(document.RootElement);
                canonicalBytes = LedgerCanonicalizer.SerializeCanonical(model);
            }
            catch (LedgerCanonicalizationException ex)
            {
                return new ParseOutcome(null, ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.NonCanonical, ex.Message)));
            }

            var structuralDiagnostic = RunStructuralBounds(model, canonicalBytes);
            if (structuralDiagnostic is not null)
            {
                return new ParseOutcome(null, ImmutableArray.Create(structuralDiagnostic));
            }

            var semanticDiagnostics = RunSemanticInvariants(model);
            if (!semanticDiagnostics.IsEmpty)
            {
                return new ParseOutcome(null, semanticDiagnostics);
            }

            if (!canonicalBytes.AsSpan().SequenceEqual(bytes))
            {
                return new ParseOutcome(null, ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.NonCanonical, "Ledger bytes are not in canonical form.")));
            }

            var ledger = new ValidatedLedger(model, canonicalBytes);
            return new ParseOutcome(ledger, ImmutableArray<LedgerDiagnostic>.Empty);
        }
    }

    private static LedgerDiagnostic? RunRawTransportChecks(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > LedgerRawByteLimit)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.RawByteLimitExceeded, $"Raw ledger bytes ({bytes.Length}) exceed {LedgerRawByteLimit}.");
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.InvalidUtf8, "Ledger bytes begin with a UTF-8 BOM.");
        }

        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.InvalidUtf8, "Ledger bytes are not valid UTF-8.");
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = LedgerRawByteLimit
        });

        var stack = new Stack<Container>();
        var depth = 0;
        LedgerDiagnostic? duplicateDiagnostic = null;
        LedgerDiagnostic? depthDiagnostic = null;
        LedgerDiagnostic? arrayLengthDiagnostic = null;
        LedgerDiagnostic? propertyCountDiagnostic = null;

        try
        {
            while (reader.Read())
            {
                var token = reader.TokenType;

                if (token == JsonTokenType.PropertyName)
                {
                    if (stack.Count == 0 || stack.Peek().Kind != ContainerKind.Object)
                    {
                        return CreateDiagnostic(LedgerDiagnosticCodes.InvalidJson, "Property name outside object.");
                    }

                    var obj = stack.Peek();
                    // Decode the name into its exact UTF-16 code-unit sequence
                    // (unpaired surrogates preserved) and deduplicate under Ordinal
                    // equality. Raw-span byte equality would miss escape-case variants
                    // of the same name (e.g. D800 vs d800), and System.Text.Json
                    // materialization throws on lone-surrogate names; the decoded
                    // sequence handles both. Names with invalid Unicode still reach
                    // the Unicode-safety stage for <invalid-utf16>/<invalid-nul>
                    // classification when they are not duplicates.
                    var name = LedgerRawJsonDecoder.DecodeStringTokenContent(PropertyNameContent(ref reader));
                    if (!obj.PropertyNames.Add(name))
                    {
                        duplicateDiagnostic ??= CreateDiagnostic(LedgerDiagnosticCodes.DuplicateJsonProperty, "Duplicate JSON property.");
                    }

                    if (obj.PropertyNames.Count > LedgerJsonMaxPropertyCount)
                    {
                        propertyCountDiagnostic ??= CreateDiagnostic(LedgerDiagnosticCodes.JsonPropertyCountExceeded, $"Object property count exceeds {LedgerJsonMaxPropertyCount}.");
                    }

                    continue;
                }

                if (stack.Count > 0 && stack.Peek().Kind == ContainerKind.Array &&
                    token != JsonTokenType.EndObject && token != JsonTokenType.EndArray)
                {
                    var arr = stack.Peek();
                    arr.ArrayCount++;
                    if (arr.ArrayCount > LedgerJsonMaxArrayLength)
                    {
                        arrayLengthDiagnostic ??= CreateDiagnostic(LedgerDiagnosticCodes.JsonArrayLengthExceeded, $"Array length exceeds {LedgerJsonMaxArrayLength}.");
                    }
                }

                switch (token)
                {
                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        depth++;
                        if (depth > LedgerJsonMaxDepth)
                        {
                            depthDiagnostic ??= CreateDiagnostic(LedgerDiagnosticCodes.JsonDepthExceeded, $"JSON depth exceeds {LedgerJsonMaxDepth}.");
                        }

                        stack.Push(new Container(token == JsonTokenType.StartObject ? ContainerKind.Object : ContainerKind.Array));
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (stack.Count == 0)
                        {
                            return CreateDiagnostic(LedgerDiagnosticCodes.InvalidJson, "Unbalanced JSON structure.");
                        }

                        stack.Pop();
                        depth--;
                        break;
                }
            }
        }
        catch (JsonException ex)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.InvalidJson, ex.Message);
        }

        if (stack.Count != 0)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.InvalidJson, "Unbalanced JSON structure.");
        }

        return duplicateDiagnostic ?? depthDiagnostic ?? arrayLengthDiagnostic ?? propertyCountDiagnostic;
    }

    private static LedgerDiagnostic? RunVersionRouting(JsonElement root)
    {
        if (root.TryGetProperty("schemaVersion", out var schemaVersion) &&
            schemaVersion.ValueKind == JsonValueKind.Number &&
            schemaVersion.TryGetInt64(out var schemaVersionValue) &&
            schemaVersionValue != 1)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.UnsupportedSchemaVersion, $"schemaVersion {schemaVersionValue} is not supported.");
        }

        if (root.TryGetProperty("prefixContractVersion", out var prefixVersion) &&
            prefixVersion.ValueKind == JsonValueKind.Number &&
            prefixVersion.TryGetInt64(out var prefixVersionValue) &&
            prefixVersionValue != 1)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.UnsupportedPrefixContractVersion, $"prefixContractVersion {prefixVersionValue} is not supported.");
        }

        return null;
    }

    // Returns the raw content span of the current property-name token (the bytes
    // between the surrounding quotes, escapes intact). The reader is span-based, so
    // ValueSpan is always available; the sequence branch is defensive only.
    private static ReadOnlySpan<byte> PropertyNameContent(ref Utf8JsonReader reader)
    {
        return reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan;
    }

    private static LedgerDiagnostic? RunStructuralBounds(LedgerModel model, ImmutableArray<byte> canonicalBytes)
    {
        if (canonicalBytes.Length > LedgerCanonicalByteLimit)
        {
            return CreateDiagnostic(LedgerDiagnosticCodes.CanonicalByteLimitExceeded, $"Canonical ledger bytes ({canonicalBytes.Length}) exceed {LedgerCanonicalByteLimit}.");
        }

        var header = model.Header;
        var identityStrings = new[]
        {
            header.Repository,
            header.HeadRepository,
            header.WorkflowIdentity,
            header.TrustedExecutionDomain,
            header.ProviderId,
            header.ModelId,
            header.AdapterId,
            header.TemplateId,
            header.PolicyId,
            header.ToolDefinitionId,
            header.CacheConfigId
        };

        foreach (var value in identityStrings)
        {
            var byteCount = Encoding.UTF8.GetByteCount(value);
            if (byteCount > 256)
            {
                return CreateDiagnostic(LedgerDiagnosticCodes.IdentityByteLengthExceeded, $"Identity string exceeds 256 UTF-8 bytes.");
            }
        }

        foreach (var value in identityStrings)
        {
            foreach (var c in value)
            {
                if (c < 0x20 || c == 0x7f)
                {
                    return CreateDiagnostic(LedgerDiagnosticCodes.ControlCharacterInIdentity, "Identity string contains a control character.");
                }
            }
        }

        return null;
    }

    internal static ImmutableArray<LedgerDiagnostic> RunSemanticInvariants(LedgerModel model)
    {
        var records = model.Records;
        if (records.Length % 2 != 0)
        {
            return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.RecordsLengthNotEven, $"Records length {records.Length} is not even."));
        }

        var pairCount = records.Length / 2;
        for (var i = 0; i < pairCount; i++)
        {
            var context = records[i * 2];
            var outcome = records[i * 2 + 1];

            if (context is not ReviewContextRecord || outcome is not ReviewOutcomeRecord)
            {
                return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.PairOrderMismatch, $"Record pair {i} does not have review_context followed by review_outcome."));
            }
        }

        var seenTuples = new HashSet<(string Role, long Ordinal)>();
        var ordinalCounts = new Dictionary<long, int>();
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            var tuple = (record.Role, record.InteractionOrdinal);
            if (!seenTuples.Add(tuple))
            {
                return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.DuplicateInteraction, $"Duplicate interaction (role='{record.Role}', ordinal={record.InteractionOrdinal})."));
            }

            ordinalCounts[record.InteractionOrdinal] = ordinalCounts.GetValueOrDefault(record.InteractionOrdinal) + 1;
        }

        foreach (var count in ordinalCounts.Values)
        {
            if (count != 2)
            {
                return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.DuplicateInteraction, "Interaction ordinal does not appear exactly twice."));
            }
        }

        for (var i = 0; i < pairCount; i++)
        {
            var context = records[i * 2];
            var outcome = records[i * 2 + 1];

            if (context.InteractionOrdinal != i || outcome.InteractionOrdinal != i)
            {
                return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.OrdinalGap, $"Record pair {i} has ordinal mismatch."));
            }

            if (context.InteractionId != outcome.InteractionId)
            {
                return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.PairInteractionIdMismatch, $"Record pair {i} has mismatched interactionId."));
            }
        }

        for (var i = 0; i < records.Length; i++)
        {
            if (records[i] is not ReviewOutcomeRecord outcome)
            {
                continue;
            }

            foreach (var finding in outcome.Findings)
            {
                var hasStart = finding.StartLine.HasValue;
                var hasEnd = finding.EndLine.HasValue;
                if (hasStart != hasEnd)
                {
                    return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.FindingLocationMismatch, $"Finding has mismatched startLine/endLine presence."));
                }

                if (hasStart && string.IsNullOrEmpty(finding.Path))
                {
                    return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.FindingLocationMissingPath, "Finding with line range is missing path."));
                }

                if (finding.StartLine is { } start && finding.EndLine is { } end && start > end)
                {
                    return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.FindingLineRangeInvalid, "Finding startLine is greater than endLine."));
                }
            }
        }

        var identities = new ExpectedIdentities(
            model.Header.Repository,
            model.Header.HeadRepository,
            model.Header.PullRequest,
            model.Header.WorkflowIdentity,
            model.Header.TrustedExecutionDomain,
            model.Header.ProviderId,
            model.Header.ModelId,
            model.Header.AdapterId,
            model.Header.TemplateId,
            model.Header.PolicyId,
            model.Header.ToolDefinitionId,
            model.Header.CacheConfigId);

        var expectedDigest = LedgerCanonicalizer.ComputeCacheContractDigest(identities);
        for (var i = 0; i < records.Length; i++)
        {
            if (records[i] is ReviewContextRecord context && context.CacheContractDigest != expectedDigest)
            {
                return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.DigestMismatch, $"Record {i} cacheContractDigest does not match header-derived digest."));
            }
        }

        if (model.Header.ModelId == "latest")
        {
            return ImmutableArray.Create(CreateDiagnostic(LedgerDiagnosticCodes.ModelAliasLiteral, "modelId is the floating alias 'latest'."));
        }

        return ImmutableArray<LedgerDiagnostic>.Empty;
    }

    // Materializes a schema-valid integer slot. JsonElement.TryGetInt64 only accepts
    // integer-form tokens, but JSON Schema draft-07 numeric equality also accepts
    // mathematical integers written with a fraction or exponent (1.0, 1e0, 0e0); those
    // are read via decimal, whose equality/truncation semantics are exact (no binary
    // floating-point rounding). Every ledger integer slot is range-bounded by the
    // schema (const 0/1, 0..1_000_000, or 1..2_147_483_647), far inside decimal's
    // range, so a schema-valid value always converts exactly; the non-integer-form raw
    // bytes then fail the canonical byte comparison as ledger_non_canonical.
    private static long GetInteger(JsonElement element)
    {
        if (element.TryGetInt64(out var value))
        {
            return value;
        }

        if (!element.TryGetDecimal(out var asDecimal) || asDecimal != Math.Truncate(asDecimal) ||
            asDecimal < long.MinValue || asDecimal > long.MaxValue)
        {
            // Unreachable for schema-valid input (all integer slots are bounded);
            // the ParseAndValidate top-level catch converts this to a fail-closed
            // diagnostic instead of an unhandled exception.
            throw new InvalidOperationException("Schema-valid integer is not exactly representable as Int64.");
        }

        return (long)asDecimal;
    }

    private static LedgerModel BuildModel(JsonElement root)
    {
        return new LedgerModel
        {
            SchemaVersion = (int)GetInteger(root.GetProperty("schemaVersion")),
            PrefixContractVersion = (int)GetInteger(root.GetProperty("prefixContractVersion")),
            Header = BuildHeader(root.GetProperty("header")),
            Records = BuildRecords(root.GetProperty("records"))
        };
    }

    private static LedgerHeader BuildHeader(JsonElement element)
    {
        return new LedgerHeader
        {
            Kind = element.GetProperty("kind").GetString()!,
            SessionEpoch = element.GetProperty("sessionEpoch").GetString()!,
            LedgerEpoch = element.GetProperty("ledgerEpoch").GetString()!,
            StateGeneration = GetInteger(element.GetProperty("stateGeneration")),
            PredecessorLedgerSha256 = element.GetProperty("predecessorLedgerSha256").GetString()!,
            Repository = element.GetProperty("repository").GetString()!,
            HeadRepository = element.GetProperty("headRepository").GetString()!,
            PullRequest = (int)GetInteger(element.GetProperty("pullRequest")),
            WorkflowIdentity = element.GetProperty("workflowIdentity").GetString()!,
            TrustedExecutionDomain = element.GetProperty("trustedExecutionDomain").GetString()!,
            ProviderId = element.GetProperty("providerId").GetString()!,
            ModelId = element.GetProperty("modelId").GetString()!,
            AdapterId = element.GetProperty("adapterId").GetString()!,
            TemplateId = element.GetProperty("templateId").GetString()!,
            PolicyId = element.GetProperty("policyId").GetString()!,
            ToolDefinitionId = element.GetProperty("toolDefinitionId").GetString()!,
            CacheConfigId = element.GetProperty("cacheConfigId").GetString()!,
            PredecessorLedgerEpoch = GetOptionalString(element, "predecessorLedgerEpoch"),
            PredecessorStateGeneration = GetOptionalLong(element, "predecessorStateGeneration"),
            PredecessorManifestSha256 = GetOptionalString(element, "predecessorManifestSha256"),
            ResetReason = GetOptionalString(element, "resetReason"),
            RecoveryReason = GetOptionalString(element, "recoveryReason")
        };
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null)
        {
            return property.GetString();
        }

        return null;
    }

    private static long? GetOptionalLong(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind != JsonValueKind.Null)
        {
            return GetInteger(property);
        }

        return null;
    }

    private static ImmutableArray<LedgerRecord> BuildRecords(JsonElement element)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerRecord>();
        foreach (var item in element.EnumerateArray())
        {
            builder.Add(BuildRecord(item));
        }

        return builder.ToImmutable();
    }

    private static LedgerRecord BuildRecord(JsonElement element)
    {
        var role = element.GetProperty("role").GetString()!;
        if (role == "review_context")
        {
            return new ReviewContextRecord
            {
                Role = role,
                InteractionId = element.GetProperty("interactionId").GetString()!,
                InteractionOrdinal = GetInteger(element.GetProperty("interactionOrdinal")),
                SubjectDigest = element.GetProperty("subjectDigest").GetString()!,
                CacheContractDigest = element.GetProperty("cacheContractDigest").GetString()!,
                ReviewedHeadSha = element.GetProperty("reviewedHeadSha").GetString()!,
                ReviewedBaseSha = element.GetProperty("reviewedBaseSha").GetString()!,
                ChangedFiles = BuildChangedFiles(element.GetProperty("changedFiles"))
            };
        }

        return new ReviewOutcomeRecord
        {
            Role = role,
            InteractionId = element.GetProperty("interactionId").GetString()!,
            InteractionOrdinal = GetInteger(element.GetProperty("interactionOrdinal")),
            Summary = element.GetProperty("summary").GetString()!,
            Findings = BuildFindings(element.GetProperty("findings")),
            Limitations = BuildLimitations(element.GetProperty("limitations"))
        };
    }

    private static ImmutableArray<LedgerChangedFile> BuildChangedFiles(JsonElement element)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerChangedFile>();
        foreach (var item in element.EnumerateArray())
        {
            var file = new LedgerChangedFile
            {
                Path = item.GetProperty("path").GetString()!,
                Status = item.GetProperty("status").GetString()!,
                Additions = GetInteger(item.GetProperty("additions")),
                Deletions = GetInteger(item.GetProperty("deletions")),
                Changes = GetInteger(item.GetProperty("changes")),
                PreviousPath = GetOptionalString(item, "previousPath"),
                Patch = TryBuildPatch(item)
            };

            builder.Add(file);
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<LedgerFinding> BuildFindings(JsonElement element)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerFinding>();
        foreach (var item in element.EnumerateArray())
        {
            var finding = new LedgerFinding
            {
                Severity = item.GetProperty("severity").GetString()!,
                Confidence = item.GetProperty("confidence").GetString()!,
                Category = item.GetProperty("category").GetString()!,
                Title = item.GetProperty("title").GetString()!,
                Body = item.GetProperty("body").GetString()!,
                Path = item.GetProperty("path").ValueKind == JsonValueKind.Null ? null : item.GetProperty("path").GetString(),
                StartLine = item.GetProperty("startLine").ValueKind == JsonValueKind.Null ? null : GetInteger(item.GetProperty("startLine")),
                EndLine = item.GetProperty("endLine").ValueKind == JsonValueKind.Null ? null : GetInteger(item.GetProperty("endLine")),
                Evidence = GetOptionalString(item, "evidence"),
                SuggestedAction = GetOptionalString(item, "suggestedAction"),
                InlinePreference = GetOptionalString(item, "inlinePreference")
            };

            builder.Add(finding);
        }

        return builder.ToImmutable();
    }

    private static LedgerBoundedPatch? TryBuildPatch(JsonElement item)
    {
        if (!item.TryGetProperty("patch", out var patch) || patch.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return new LedgerBoundedPatch
        {
            Sha256 = patch.GetProperty("sha256").GetString()!,
            Truncated = patch.GetProperty("truncated").GetBoolean(),
            MaxChars = GetInteger(patch.GetProperty("maxChars"))
        };
    }

    private static ImmutableArray<string> BuildLimitations(JsonElement element)
    {
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in element.EnumerateArray())
        {
            builder.Add(item.GetString()!);
        }

        return builder.ToImmutable();
    }

    private static LedgerDiagnostic CreateDiagnostic(string code, string message)
    {
        return new LedgerDiagnostic { Code = code, Message = LedgerSafePath.TruncateDiagnosticMessage(message) };
    }

    private enum ContainerKind { Object, Array }

    private sealed class Container
    {
        public Container(ContainerKind kind)
        {
            Kind = kind;
            PropertyNames = new HashSet<string>(StringComparer.Ordinal);
        }

        public ContainerKind Kind { get; }
        public HashSet<string> PropertyNames { get; }
        public int ArrayCount { get; set; }
    }
}
