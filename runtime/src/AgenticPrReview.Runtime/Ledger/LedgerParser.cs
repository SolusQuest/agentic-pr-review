using System.Buffers;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
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
        var rawDiagnostic = RunRawTransportChecks(bytes);
        if (rawDiagnostic is not null)
        {
            return new ParseOutcome(null, ImmutableArray.Create(rawDiagnostic));
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
            var unicodeDiagnostic = LedgerSafePath.ScanForUnicodeViolation(document.RootElement);
            if (unicodeDiagnostic is not null)
            {
                return new ParseOutcome(null, ImmutableArray.Create(unicodeDiagnostic));
            }

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
                    if (!TryGetPropertyName(ref reader, out var name, out var rawName))
                    {
                        // The name contains an unpaired UTF-16 surrogate escape and cannot
                        // be materialized by System.Text.Json; the Unicode-safety stage
                        // owns its classification (<invalid-utf16>). Duplicate detection
                        // falls back to raw-span byte equality, which is sound (identical
                        // raw bytes are identical names) though it may miss escape-case
                        // variants of the same name; those documents are still rejected
                        // at the Unicode stage.
                        if (!obj.AddRawPropertyName(rawName))
                        {
                            duplicateDiagnostic ??= CreateDiagnostic(LedgerDiagnosticCodes.DuplicateJsonProperty, "Duplicate JSON property.");
                        }
                    }
                    else if (!obj.PropertyNames.Add(name))
                    {
                        duplicateDiagnostic ??= CreateDiagnostic(LedgerDiagnosticCodes.DuplicateJsonProperty, "Duplicate JSON property.");
                    }

                    if (obj.PropertyNames.Count + obj.RawPropertyNameCount > LedgerJsonMaxPropertyCount)
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

    // Materializes a property name. System.Text.Json refuses to unescape a name
    // containing an unpaired UTF-16 surrogate (InvalidOperationException); in that case
    // the raw name bytes are returned for span-based duplicate detection and the name is
    // left to the Unicode-safety stage, which classifies it as <invalid-utf16>.
    private static bool TryGetPropertyName(ref Utf8JsonReader reader, out string name, out byte[] rawName)
    {
        try
        {
            name = reader.GetString()!;
            rawName = Array.Empty<byte>();
            return true;
        }
        catch (InvalidOperationException)
        {
            name = string.Empty;
            rawName = reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan.ToArray();
            return false;
        }
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

    private static LedgerModel BuildModel(JsonElement root)
    {
        return new LedgerModel
        {
            SchemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            PrefixContractVersion = root.GetProperty("prefixContractVersion").GetInt32(),
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
            StateGeneration = element.GetProperty("stateGeneration").GetInt64(),
            PredecessorLedgerSha256 = element.GetProperty("predecessorLedgerSha256").GetString()!,
            Repository = element.GetProperty("repository").GetString()!,
            HeadRepository = element.GetProperty("headRepository").GetString()!,
            PullRequest = element.GetProperty("pullRequest").GetInt32(),
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
            return property.GetInt64();
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
                InteractionOrdinal = element.GetProperty("interactionOrdinal").GetInt64(),
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
            InteractionOrdinal = element.GetProperty("interactionOrdinal").GetInt64(),
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
                Additions = item.GetProperty("additions").GetInt64(),
                Deletions = item.GetProperty("deletions").GetInt64(),
                Changes = item.GetProperty("changes").GetInt64(),
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
                StartLine = item.GetProperty("startLine").ValueKind == JsonValueKind.Null ? null : item.GetProperty("startLine").GetInt64(),
                EndLine = item.GetProperty("endLine").ValueKind == JsonValueKind.Null ? null : item.GetProperty("endLine").GetInt64(),
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
            MaxChars = patch.GetProperty("maxChars").GetInt64()
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
        private readonly List<byte[]> _rawPropertyNames = new();

        public Container(ContainerKind kind)
        {
            Kind = kind;
            PropertyNames = new HashSet<string>();
        }

        public ContainerKind Kind { get; }
        public HashSet<string> PropertyNames { get; }
        public int RawPropertyNameCount => _rawPropertyNames.Count;
        public int ArrayCount { get; set; }

        // Raw-byte duplicate detection for property names that cannot be materialized.
        public bool AddRawPropertyName(byte[] rawName)
        {
            foreach (var existing in _rawPropertyNames)
            {
                if (existing.AsSpan().SequenceEqual(rawName))
                {
                    return false;
                }
            }

            _rawPropertyNames.Add(rawName);
            return true;
        }
    }
}
