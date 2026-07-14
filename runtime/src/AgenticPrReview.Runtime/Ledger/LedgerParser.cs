using System.Collections.Immutable;
using System.Text.Json;
using Json.Schema;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Parses a candidate ledger byte sequence and runs the full validation pipeline:
/// raw transport, version routing, schema (via mapper), structural bounds,
/// semantic invariants, and canonical form. Returns a <see cref="ValidatedLedger"/>
/// only on complete success.
/// </summary>
public static class LedgerParser
{
    public static ParseOutcome ParseAndValidate(ReadOnlySpan<byte> bytes)
    {
        // Copy input span into a persistent byte buffer to detach from caller-owned memory.
        var rawCopy = new byte[bytes.Length];
        bytes.CopyTo(rawCopy);

        // ------ Raw transport
        JsonDocument? document = null;
        var rawFailure = LedgerRawTransport.Validate(rawCopy, out document);
        if (rawFailure is not null) return new ParseOutcome(null, rawFailure);
        using var doc = document!;

        var root = doc.RootElement;

        // ------ Version routing
        var routingFailure = CheckVersionRouting(root);
        if (routingFailure is not null) return new ParseOutcome(null, routingFailure);

        // ------ Schema evaluation via mapper
        var schemas = SchemaContracts.Load(typeof(LedgerParser).Assembly);
        var evalResults = schemas.Evaluate(SchemaKind.Ledger, root);
        if (!evalResults.IsValid)
        {
            var mapped = LedgerSchemaMapper.Map(root, evalResults);
            return new ParseOutcome(null, mapped);
        }

        // ------ Deserialize into strongly-typed model
        LedgerModel model;
        try
        {
            model = LedgerDeserializer.Deserialize(root);
        }
        catch (LedgerDeserializationException ex)
        {
            return new ParseOutcome(null, LedgerDiagnosticMessages.Of(ex.Code));
        }

        // ------ Structural bounds (post-schema; identity byte lengths / control chars in identities)
        var boundsFailure = LedgerSemanticChecks.CheckIdentityBounds(model);
        if (boundsFailure is not null) return new ParseOutcome(null, boundsFailure);

        // ------ Semantic invariants
        var semanticFailure = LedgerSemanticChecks.CheckSemanticInvariants(model);
        if (semanticFailure is not null) return new ParseOutcome(null, semanticFailure);

        // ------ Canonical form
        var canonical = LedgerCanonicalizer.SerializeCanonical(model);
        if (canonical.Length > LedgerLimits.MaxCanonicalBytes)
        {
            return new ParseOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.CanonicalByteLimitExceeded));
        }
        if (!canonical.AsSpan().SequenceEqual(rawCopy))
        {
            return new ParseOutcome(null, LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.NonCanonical));
        }

        // ------ Success: hand back a defensive-copied ValidatedLedger
        var contentSha = LedgerCanonicalizer.ComputeSha256Hex(canonical);
        return new ParseOutcome(new ValidatedLedger(model, canonical, contentSha), null);
    }

    private static LedgerDiagnostic? CheckVersionRouting(JsonElement root)
    {
        if (!root.TryGetProperty("schemaVersion", out var sv) || sv.ValueKind != JsonValueKind.Number)
        {
            // Missing/invalid handled by schema stage.
            return null;
        }
        if (sv.TryGetInt32(out var svv))
        {
            if (svv != 1) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedSchemaVersion);
        }
        else
        {
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedSchemaVersion);
        }

        if (!root.TryGetProperty("prefixContractVersion", out var pcv) || pcv.ValueKind != JsonValueKind.Number)
        {
            return null;
        }
        if (pcv.TryGetInt32(out var pcvv))
        {
            if (pcvv != 1) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedPrefixContractVersion);
        }
        else
        {
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
        var records = ImmutableArray.CreateBuilder<LedgerRecord>();
        foreach (var el in root.GetProperty("records").EnumerateArray())
        {
            records.Add(DeserializeRecord(el));
        }
        return new LedgerModel(schemaVersion, prefixContractVersion, header, records.ToImmutable());
    }

    private static LedgerHeader DeserializeHeader(JsonElement e)
    {
        string? getOpt(string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? getOptInt(string name)
        {
            return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null;
        }

        return new LedgerHeader(
            Kind: e.GetProperty("kind").GetString()!,
            Repository: e.GetProperty("repository").GetString()!,
            HeadRepository: e.GetProperty("headRepository").GetString()!,
            PullRequest: e.GetProperty("pullRequest").GetInt32(),
            WorkflowIdentity: e.GetProperty("workflowIdentity").GetString()!,
            TrustedExecutionDomain: e.GetProperty("trustedExecutionDomain").GetString()!,
            SessionEpoch: e.GetProperty("sessionEpoch").GetString()!,
            ProviderId: e.GetProperty("providerId").GetString()!,
            ModelId: e.GetProperty("modelId").GetString()!,
            AdapterId: e.GetProperty("adapterId").GetString()!,
            TemplateId: e.GetProperty("templateId").GetString()!,
            PolicyId: e.GetProperty("policyId").GetString()!,
            ToolDefinitionId: e.GetProperty("toolDefinitionId").GetString()!,
            CacheConfigId: e.GetProperty("cacheConfigId").GetString()!,
            StateGeneration: e.GetProperty("stateGeneration").GetInt32(),
            LedgerEpoch: e.GetProperty("ledgerEpoch").GetInt32(),
            PredecessorLedgerSha256: e.GetProperty("predecessorLedgerSha256").GetString()!,
            PredecessorStateGeneration: getOptInt("predecessorStateGeneration"),
            PredecessorManifestSha256: getOpt("predecessorManifestSha256"),
            ResetReason: getOpt("resetReason"),
            RecoveryReason: getOpt("recoveryReason"));
    }

    private static LedgerRecord DeserializeRecord(JsonElement e)
    {
        var role = e.GetProperty("role").GetString();
        if (role == "review_context") return new LedgerRecord(DeserializeContext(e), null);
        if (role == "review_outcome") return new LedgerRecord(null, DeserializeOutcome(e));
        throw new LedgerDeserializationException(LedgerDiagnosticCodes.RecordRoleMismatch);
    }

    private static ReviewContextRecord DeserializeContext(JsonElement e)
    {
        var files = ImmutableArray.CreateBuilder<ChangedFileEntry>();
        foreach (var f in e.GetProperty("changedFiles").EnumerateArray())
        {
            files.Add(DeserializeChangedFile(f));
        }
        return new ReviewContextRecord(
            InteractionId: e.GetProperty("interactionId").GetString()!,
            InteractionOrdinal: e.GetProperty("interactionOrdinal").GetInt32(),
            ReviewedHeadSha: e.GetProperty("reviewedHeadSha").GetString()!,
            ReviewedBaseSha: e.GetProperty("reviewedBaseSha").GetString()!,
            SubjectDigest: e.GetProperty("subjectDigest").GetString()!,
            CacheContractDigest: e.GetProperty("cacheContractDigest").GetString()!,
            ChangedFiles: files.ToImmutable());
    }

    private static ChangedFileEntry DeserializeChangedFile(JsonElement e)
    {
        string? prev = e.TryGetProperty("previousPath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        ChangedFilePatch? patch = null;
        if (e.TryGetProperty("patch", out var pe) && pe.ValueKind == JsonValueKind.Object)
        {
            patch = new ChangedFilePatch(
                Sha256: pe.GetProperty("sha256").GetString()!,
                Truncated: pe.GetProperty("truncated").GetBoolean(),
                MaxChars: pe.GetProperty("maxChars").GetInt32());
        }
        return new ChangedFileEntry(
            Path: e.GetProperty("path").GetString()!,
            PreviousPath: prev,
            Status: e.GetProperty("status").GetString()!,
            Additions: e.GetProperty("additions").GetInt32(),
            Deletions: e.GetProperty("deletions").GetInt32(),
            Changes: e.GetProperty("changes").GetInt32(),
            Patch: patch);
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
        return new ReviewOutcomeRecord(
            InteractionId: e.GetProperty("interactionId").GetString()!,
            InteractionOrdinal: e.GetProperty("interactionOrdinal").GetInt32(),
            Summary: e.GetProperty("summary").GetString()!,
            Findings: findings.ToImmutable(),
            Limitations: lims.ToImmutable());
    }

    private static LedgerFinding DeserializeFinding(JsonElement e)
    {
        string? optStr(string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        string? nullableStr(string n) => e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() : null) : null;
        int? nullableInt(string n) => e.TryGetProperty(n, out var v) ? (v.ValueKind == JsonValueKind.Number ? v.GetInt32() : (int?)null) : null;

        return new LedgerFinding(
            Severity: e.GetProperty("severity").GetString()!,
            Confidence: e.GetProperty("confidence").GetString()!,
            Category: e.GetProperty("category").GetString()!,
            Title: e.GetProperty("title").GetString()!,
            Body: e.GetProperty("body").GetString()!,
            Path: nullableStr("path"),
            StartLine: nullableInt("startLine"),
            EndLine: nullableInt("endLine"),
            Evidence: optStr("evidence"),
            SuggestedAction: optStr("suggestedAction"),
            InlinePreference: optStr("inlinePreference"));
    }
}


