using System.Collections.Immutable;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Parses a candidate ledger byte sequence and runs the full validation
/// pipeline (raw transport → Unicode-safety → version routing → schema →
/// structural bounds → semantic invariants → canonical form). Each stage is
/// fail-fast under a fixed intra-stage precedence. A <see cref="ValidatedLedger"/>
/// is minted only on complete success.
/// </summary>
public static class LedgerParser
{
    public static ParseOutcome ParseAndValidate(ReadOnlySpan<byte> bytes)
    {
        var rawCopy = new byte[bytes.Length];
        bytes.CopyTo(rawCopy);

        // ------ Raw transport
        JsonDocument? document = null;
        var rawFailure = LedgerRawTransport.Validate(rawCopy, out document);
        if (rawFailure is not null) return SingleFailure(rawFailure);
        using var doc = document!;
        var root = doc.RootElement;

        // ------ Unicode-safety pre-scan (property names + string values).
        var unicodeFailure = LedgerUnicodeSafety.Scan(root);
        if (unicodeFailure is not null) return SingleFailure(unicodeFailure);

        // ------ Version routing
        var routingFailure = CheckVersionRouting(root);
        if (routingFailure is not null) return SingleFailure(routingFailure);

        // ------ Schema evaluation via mapper
        var schemas = SchemaContracts.Load(typeof(LedgerParser).Assembly);
        var evalResults = schemas.Evaluate(SchemaKind.Ledger, root);
        if (!evalResults.IsValid)
        {
            ImmutableArray<LedgerDiagnostic> mapped;
            try
            {
                mapped = ImmutableArray.Create(LedgerSchemaMapper.Map(root, evalResults));
            }
            catch (Exception)
            {
                mapped = ImmutableArray.Create(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
            }
            return new ParseOutcome(null, mapped);
        }

        // ------ Deserialize
        LedgerModel model;
        try
        {
            model = LedgerDeserializer.Deserialize(root);
        }
        catch (LedgerDeserializationException ex)
        {
            return SingleFailure(LedgerDiagnosticMessages.Of(ex.Code));
        }
        catch (Exception)
        {
            // JSON reader / conversion errors post-schema map to schema violation.
            return SingleFailure(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation));
        }

        // ------ Structural bounds (semantic): canonical byte cap, identity byte length, control chars in identity.
        var canonicalIm = LedgerCanonicalizer.SerializeCanonical(model);
        if (canonicalIm.Length > LedgerLimits.MaxCanonicalBytes)
            return SingleFailure(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.CanonicalByteLimitExceeded));

        var identityBoundsFailure = LedgerSemanticChecks.CheckIdentityBounds(model);
        if (identityBoundsFailure is not null) return SingleFailure(identityBoundsFailure);

        // ------ Semantic invariants (fixed order; see Issue #49 section 9).
        var semanticFailure = LedgerSemanticChecks.CheckSemanticInvariants(model);
        if (semanticFailure is not null) return SingleFailure(semanticFailure);

        // ------ Canonical form: bytes must equal SerializeCanonical(Parse(bytes)).
        var canonicalBytes = canonicalIm.ToArray();
        if (!canonicalBytes.AsSpan().SequenceEqual(rawCopy))
            return SingleFailure(LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.NonCanonical));

        var contentSha = LedgerCanonicalizer.ComputeSha256Hex(canonicalBytes);
        return new ParseOutcome(new ValidatedLedger(model, canonicalBytes, contentSha), ImmutableArray<LedgerDiagnostic>.Empty);
    }

    private static ParseOutcome SingleFailure(LedgerDiagnostic d)
        => new(null, ImmutableArray.Create(d));

    private static LedgerDiagnostic? CheckVersionRouting(JsonElement root)
    {
        if (root.TryGetProperty("schemaVersion", out var sv) && sv.ValueKind == JsonValueKind.Number)
        {
            if (!sv.TryGetInt32(out var svv) || svv != 1)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedSchemaVersion);
        }
        if (root.TryGetProperty("prefixContractVersion", out var pcv) && pcv.ValueKind == JsonValueKind.Number)
        {
            if (!pcv.TryGetInt32(out var pcvv) || pcvv != 1)
                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedPrefixContractVersion);
        }
        return null;
    }
}

internal sealed class LedgerDeserializationException : Exception
{
    public LedgerDeserializationException(string code) : base(code) { Code = code; }
    public string Code { get; }
}

internal static class LedgerDeserializer
{
    public static LedgerModel Deserialize(JsonElement root)
    {
        var schemaVersion = root.GetProperty("schemaVersion").GetInt32();
        var prefixContractVersion = root.GetProperty("prefixContractVersion").GetInt32();
        var header = DeserializeHeader(root.GetProperty("header"));
        var recordsBuilder = ImmutableArray.CreateBuilder<LedgerRecord>();
        foreach (var el in root.GetProperty("records").EnumerateArray())
        {
            recordsBuilder.Add(DeserializeRecord(el));
        }
        return new LedgerModel
        {
            SchemaVersion = schemaVersion,
            PrefixContractVersion = prefixContractVersion,
            Header = header,
            Records = recordsBuilder.ToImmutable(),
        };
    }

    private static LedgerHeader DeserializeHeader(JsonElement e)
    {
        string? getStr(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        long? getLong(string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number) return null;
            if (v.TryGetInt64(out var vv)) return vv;
            return null;
        }

        return new LedgerHeader
        {
            Kind = e.GetProperty("kind").GetString()!,
            SessionEpoch = e.GetProperty("sessionEpoch").GetString()!,
            LedgerEpoch = e.GetProperty("ledgerEpoch").GetString()!,
            StateGeneration = e.GetProperty("stateGeneration").GetInt64(),
            PredecessorLedgerSha256 = e.GetProperty("predecessorLedgerSha256").GetString()!,
            PredecessorLedgerEpoch = getStr("predecessorLedgerEpoch"),
            PredecessorStateGeneration = getLong("predecessorStateGeneration"),
            PredecessorManifestSha256 = getStr("predecessorManifestSha256"),
            ResetReason = getStr("resetReason"),
            RecoveryReason = getStr("recoveryReason"),
            Repository = e.GetProperty("repository").GetString()!,
            HeadRepository = e.GetProperty("headRepository").GetString()!,
            PullRequest = e.GetProperty("pullRequest").GetInt32(),
            WorkflowIdentity = e.GetProperty("workflowIdentity").GetString()!,
            TrustedExecutionDomain = e.GetProperty("trustedExecutionDomain").GetString()!,
            ProviderId = e.GetProperty("providerId").GetString()!,
            ModelId = e.GetProperty("modelId").GetString()!,
            AdapterId = e.GetProperty("adapterId").GetString()!,
            TemplateId = e.GetProperty("templateId").GetString()!,
            PolicyId = e.GetProperty("policyId").GetString()!,
            ToolDefinitionId = e.GetProperty("toolDefinitionId").GetString()!,
            CacheConfigId = e.GetProperty("cacheConfigId").GetString()!,
        };
    }

    private static LedgerRecord DeserializeRecord(JsonElement e)
    {
        var role = e.GetProperty("role").GetString();
        if (role == "review_context") return DeserializeContext(e);
        if (role == "review_outcome") return DeserializeOutcome(e);
        throw new LedgerDeserializationException(LedgerDiagnosticCodes.RecordRoleMismatch);
    }

    private static ReviewContextRecord DeserializeContext(JsonElement e)
    {
        var files = ImmutableArray.CreateBuilder<LedgerChangedFile>();
        foreach (var f in e.GetProperty("changedFiles").EnumerateArray())
        {
            files.Add(DeserializeChangedFile(f));
        }
        return new ReviewContextRecord
        {
            Role = "review_context",
            InteractionId = e.GetProperty("interactionId").GetString()!,
            InteractionOrdinal = e.GetProperty("interactionOrdinal").GetInt64(),
            SubjectDigest = e.GetProperty("subjectDigest").GetString()!,
            CacheContractDigest = e.GetProperty("cacheContractDigest").GetString()!,
            ReviewedHeadSha = e.GetProperty("reviewedHeadSha").GetString()!,
            ReviewedBaseSha = e.GetProperty("reviewedBaseSha").GetString()!,
            ChangedFiles = files.ToImmutable(),
        };
    }

    private static LedgerChangedFile DeserializeChangedFile(JsonElement e)
    {
        string? prev = e.TryGetProperty("previousPath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        LedgerBoundedPatch? patch = null;
        if (e.TryGetProperty("patch", out var pe) && pe.ValueKind == JsonValueKind.Object)
        {
            patch = new LedgerBoundedPatch
            {
                Sha256 = pe.GetProperty("sha256").GetString()!,
                Truncated = pe.GetProperty("truncated").GetBoolean(),
                MaxChars = pe.GetProperty("maxChars").GetInt64(),
            };
        }
        return new LedgerChangedFile
        {
            Path = e.GetProperty("path").GetString()!,
            PreviousPath = prev,
            Status = e.GetProperty("status").GetString()!,
            Additions = e.GetProperty("additions").GetInt64(),
            Deletions = e.GetProperty("deletions").GetInt64(),
            Changes = e.GetProperty("changes").GetInt64(),
            Patch = patch,
        };
    }

    private static ReviewOutcomeRecord DeserializeOutcome(JsonElement e)
    {
        var findings = ImmutableArray.CreateBuilder<LedgerFinding>();
        foreach (var f in e.GetProperty("findings").EnumerateArray())
        {
            findings.Add(DeserializeFinding(f));
        }
        var lims = ImmutableArray.CreateBuilder<string>();
        foreach (var l in e.GetProperty("limitations").EnumerateArray())
        {
            lims.Add(l.GetString()!);
        }
        return new ReviewOutcomeRecord
        {
            Role = "review_outcome",
            InteractionId = e.GetProperty("interactionId").GetString()!,
            InteractionOrdinal = e.GetProperty("interactionOrdinal").GetInt64(),
            Summary = e.GetProperty("summary").GetString()!,
            Findings = findings.ToImmutable(),
            Limitations = lims.ToImmutable(),
        };
    }

    private static LedgerFinding DeserializeFinding(JsonElement e)
    {
        string? optStr(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? nullableStr(string n) => e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : null) : null;
        long? nullableLong(string n)
        {
            if (!e.TryGetProperty(n, out var v)) return null;
            if (v.ValueKind != JsonValueKind.Number) return null;
            return v.TryGetInt64(out var vv) ? vv : null;
        }

        return new LedgerFinding
        {
            Severity = e.GetProperty("severity").GetString()!,
            Confidence = e.GetProperty("confidence").GetString()!,
            Category = e.GetProperty("category").GetString()!,
            Title = e.GetProperty("title").GetString()!,
            Body = e.GetProperty("body").GetString()!,
            Path = nullableStr("path"),
            StartLine = nullableLong("startLine"),
            EndLine = nullableLong("endLine"),
            Evidence = optStr("evidence"),
            SuggestedAction = optStr("suggestedAction"),
            InlinePreference = optStr("inlinePreference"),
        };
    }
}
