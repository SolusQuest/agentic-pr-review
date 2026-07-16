using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Ledger;

internal static class LedgerCanonicalizer
{
    internal static ImmutableArray<byte> SerializeCanonical(LedgerModel model)
    {
        var writer = new CanonicalJsonWriter(4096);
        writer.WriteObjectStart();

        writer.WriteProperty("header");
        WriteHeader(ref writer, model.Header);

        writer.WriteProperty("prefixContractVersion");
        writer.WriteNumber(model.PrefixContractVersion);

        writer.WriteProperty("records");
        writer.WriteArrayStart();
        for (var i = 0; i < model.Records.Length; i++)
        {
            if (i > 0) writer.WriteComma();
            WriteRecord(ref writer, model.Records[i]);
        }
        writer.WriteArrayEnd();

        writer.WriteProperty("schemaVersion");
        writer.WriteNumber(model.SchemaVersion);

        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> SerializeRecord(LedgerRecord record)
    {
        var writer = new CanonicalJsonWriter(1024);
        WriteRecord(ref writer, record);
        return writer.ToImmutableArray();
    }

    internal static ImmutableArray<byte> SerializeCacheContractIdentity(ExpectedIdentities identities)
    {
        var writer = new CanonicalJsonWriter(512);
        writer.WriteObjectStart();

        writer.WriteProperty("adapterId");
        writer.WriteString(identities.AdapterId);

        writer.WriteProperty("cacheConfigId");
        writer.WriteString(identities.CacheConfigId);

        writer.WriteProperty("modelId");
        writer.WriteString(identities.ModelId);

        writer.WriteProperty("policyId");
        writer.WriteString(identities.PolicyId);

        writer.WriteProperty("providerId");
        writer.WriteString(identities.ProviderId);

        writer.WriteProperty("templateId");
        writer.WriteString(identities.TemplateId);

        writer.WriteProperty("toolDefinitionId");
        writer.WriteString(identities.ToolDefinitionId);

        writer.WriteObjectEnd();
        return writer.ToImmutableArray();
    }

    internal static string ComputeCacheContractDigest(ExpectedIdentities identities)
    {
        var bytes = SerializeCacheContractIdentity(identities);
        var hash = SHA256.HashData(bytes.AsSpan());
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteHeader(ref CanonicalJsonWriter writer, LedgerHeader header)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("adapterId");
        writer.WriteString(header.AdapterId);

        writer.WriteProperty("cacheConfigId");
        writer.WriteString(header.CacheConfigId);

        writer.WriteProperty("headRepository");
        writer.WriteString(header.HeadRepository);

        writer.WriteProperty("kind");
        writer.WriteString(header.Kind);

        writer.WriteProperty("ledgerEpoch");
        writer.WriteString(header.LedgerEpoch);

        writer.WriteProperty("modelId");
        writer.WriteString(header.ModelId);

        writer.WriteProperty("policyId");
        writer.WriteString(header.PolicyId);

        if (header.PredecessorLedgerEpoch is not null)
        {
            writer.WriteProperty("predecessorLedgerEpoch");
            writer.WriteString(header.PredecessorLedgerEpoch);
        }

        if (header.PredecessorLedgerSha256 is not null)
        {
            writer.WriteProperty("predecessorLedgerSha256");
            writer.WriteString(header.PredecessorLedgerSha256);
        }

        if (header.PredecessorManifestSha256 is not null)
        {
            writer.WriteProperty("predecessorManifestSha256");
            writer.WriteString(header.PredecessorManifestSha256);
        }

        if (header.PredecessorStateGeneration.HasValue)
        {
            writer.WriteProperty("predecessorStateGeneration");
            writer.WriteNumber(header.PredecessorStateGeneration.Value);
        }

        writer.WriteProperty("providerId");
        writer.WriteString(header.ProviderId);

        writer.WriteProperty("pullRequest");
        writer.WriteNumber(header.PullRequest);

        if (header.RecoveryReason is not null)
        {
            writer.WriteProperty("recoveryReason");
            writer.WriteString(header.RecoveryReason);
        }

        writer.WriteProperty("repository");
        writer.WriteString(header.Repository);

        if (header.ResetReason is not null)
        {
            writer.WriteProperty("resetReason");
            writer.WriteString(header.ResetReason);
        }

        writer.WriteProperty("sessionEpoch");
        writer.WriteString(header.SessionEpoch);

        writer.WriteProperty("stateGeneration");
        writer.WriteNumber(header.StateGeneration);

        writer.WriteProperty("templateId");
        writer.WriteString(header.TemplateId);

        writer.WriteProperty("toolDefinitionId");
        writer.WriteString(header.ToolDefinitionId);

        writer.WriteProperty("trustedExecutionDomain");
        writer.WriteString(header.TrustedExecutionDomain);

        writer.WriteProperty("workflowIdentity");
        writer.WriteString(header.WorkflowIdentity);

        writer.WriteObjectEnd();
    }

    private static void WriteRecord(ref CanonicalJsonWriter writer, LedgerRecord record)
    {
        writer.WriteObjectStart();

        writer.WriteProperty("interactionId");
        writer.WriteString(record.InteractionId);

        writer.WriteProperty("interactionOrdinal");
        writer.WriteNumber(record.InteractionOrdinal);

        writer.WriteProperty("role");
        writer.WriteString(record.Role);

        if (record is ReviewContextRecord ctx)
        {
            writer.WriteProperty("cacheContractDigest");
            writer.WriteString(ctx.CacheContractDigest);

            writer.WriteProperty("changedFiles");
            writer.WriteArrayStart();
            for (var i = 0; i < ctx.ChangedFiles.Length; i++)
            {
                if (i > 0) writer.WriteComma();
                WriteChangedFile(ref writer, ctx.ChangedFiles[i]);
            }
            writer.WriteArrayEnd();

            writer.WriteProperty("reviewedBaseSha");
            writer.WriteString(ctx.ReviewedBaseSha);

            writer.WriteProperty("reviewedHeadSha");
            writer.WriteString(ctx.ReviewedHeadSha);

            writer.WriteProperty("subjectDigest");
            writer.WriteString(ctx.SubjectDigest);
        }
        else if (record is ReviewOutcomeRecord outcome)
        {
            writer.WriteProperty("findings");
            writer.WriteArrayStart();
            for (var i = 0; i < outcome.Findings.Length; i++)
            {
                if (i > 0) writer.WriteComma();
                WriteFinding(ref writer, outcome.Findings[i]);
            }
            writer.WriteArrayEnd();

            writer.WriteProperty("limitations");
            writer.WriteArrayStart();
            for (var i = 0; i < outcome.Limitations.Length; i++)
            {
                if (i > 0) writer.WriteComma();
                writer.WriteString(outcome.Limitations[i]);
            }
            writer.WriteArrayEnd();

            writer.WriteProperty("summary");
            writer.WriteString(outcome.Summary);
        }

        writer.WriteObjectEnd();
    }

    private static void WriteChangedFile(ref CanonicalJsonWriter writer, LedgerChangedFile file)
    {
        writer.WriteObjectStart();

        writer.WriteProperty("additions");
        writer.WriteNumber(file.Additions);

        writer.WriteProperty("changes");
        writer.WriteNumber(file.Changes);

        writer.WriteProperty("deletions");
        writer.WriteNumber(file.Deletions);

        writer.WriteProperty("path");
        writer.WriteString(file.Path);

        if (file.Patch is not null)
        {
            writer.WriteProperty("patch");
            WritePatch(ref writer, file.Patch);
        }

        if (file.PreviousPath is not null)
        {
            writer.WriteProperty("previousPath");
            writer.WriteString(file.PreviousPath);
        }

        writer.WriteProperty("status");
        writer.WriteString(file.Status);

        writer.WriteObjectEnd();
    }

    private static void WritePatch(ref CanonicalJsonWriter writer, LedgerBoundedPatch patch)
    {
        writer.WriteObjectStart();

        writer.WriteProperty("maxChars");
        writer.WriteNumber(patch.MaxChars);

        writer.WriteProperty("sha256");
        writer.WriteString(patch.Sha256);

        writer.WriteProperty("truncated");
        writer.WriteBoolean(patch.Truncated);

        writer.WriteObjectEnd();
    }

    private static void WriteFinding(ref CanonicalJsonWriter writer, LedgerFinding finding)
    {
        writer.WriteObjectStart();

        writer.WriteProperty("body");
        writer.WriteString(finding.Body);

        writer.WriteProperty("category");
        writer.WriteString(finding.Category);

        writer.WriteProperty("confidence");
        writer.WriteString(finding.Confidence);

        writer.WriteProperty("endLine");
        if (finding.EndLine.HasValue)
        {
            writer.WriteNumber(finding.EndLine.Value);
        }
        else
        {
            writer.WriteNull();
        }

        if (finding.Evidence is not null)
        {
            writer.WriteProperty("evidence");
            writer.WriteString(finding.Evidence);
        }

        if (finding.InlinePreference is not null)
        {
            writer.WriteProperty("inlinePreference");
            writer.WriteString(finding.InlinePreference);
        }

        writer.WriteProperty("path");
        if (finding.Path is not null)
        {
            writer.WriteString(finding.Path);
        }
        else
        {
            writer.WriteNull();
        }

        writer.WriteProperty("severity");
        writer.WriteString(finding.Severity);

        writer.WriteProperty("startLine");
        if (finding.StartLine.HasValue)
        {
            writer.WriteNumber(finding.StartLine.Value);
        }
        else
        {
            writer.WriteNull();
        }

        if (finding.SuggestedAction is not null)
        {
            writer.WriteProperty("suggestedAction");
            writer.WriteString(finding.SuggestedAction);
        }

        writer.WriteProperty("title");
        writer.WriteString(finding.Title);

        writer.WriteObjectEnd();
    }
}

internal struct CanonicalJsonWriter
{
    private readonly ArrayBufferWriter<byte> _writer;
    private bool _needsComma;

    internal CanonicalJsonWriter(int initialCapacity)
    {
        _writer = new ArrayBufferWriter<byte>(initialCapacity);
        _needsComma = false;
    }

    internal ImmutableArray<byte> ToImmutableArray() => _writer.WrittenSpan.ToArray().ToImmutableArray();

    internal void WriteObjectStart()
    {
        _writer.Write("{"u8);
        _needsComma = false;
    }

    internal void WriteObjectEnd()
    {
        _writer.Write("}"u8);
        _needsComma = true;
    }

    internal void WriteArrayStart()
    {
        _writer.Write("["u8);
        _needsComma = false;
    }

    internal void WriteArrayEnd()
    {
        _writer.Write("]"u8);
        _needsComma = true;
    }

    internal void WriteComma() => _writer.Write(","u8);
    internal void WriteNull() { _writer.Write("null"u8); _needsComma = true; }
    internal void WriteBoolean(bool value) { _writer.Write(value ? "true"u8 : "false"u8); _needsComma = true; }

    internal void WriteProperty(string name)
    {
        if (_needsComma)
        {
            _writer.Write(","u8);
        }
        WriteEscapedString(name);
        _writer.Write(":"u8);
        _needsComma = false;
    }

    internal void WriteNumber(long value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        _writer.Write(Encoding.UTF8.GetBytes(text));
        _needsComma = true;
    }

    internal void WriteString(string value)
    {
        WriteEscapedString(value);
        _needsComma = true;
    }

    private void WriteEscapedString(string value)
    {
        _writer.Write("\""u8);
        var utf16 = value.AsSpan();
        for (var i = 0; i < utf16.Length; i++)
        {
            var c = utf16[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < utf16.Length && char.IsLowSurrogate(utf16[i + 1]))
                {
                    var codepoint = char.ConvertToUtf32(c, utf16[i + 1]);
                    WriteUtf8Codepoint(codepoint);
                    i++;
                }
                else
                {
                    throw new LedgerCanonicalizationException("Unpaired UTF-16 surrogate encountered during canonicalization.");
                }
            }
            else if (char.IsLowSurrogate(c))
            {
                throw new LedgerCanonicalizationException("Unpaired UTF-16 surrogate encountered during canonicalization.");
            }
            else if (c == '\0')
            {
                throw new LedgerCanonicalizationException("U+0000 encountered during canonicalization.");
            }
            else
            {
                WriteEscapedCodepoint(c);
            }
        }
        _writer.Write("\""u8);
    }

    private void WriteEscapedCodepoint(char c)
    {
        switch (c)
        {
            case '"':
                _writer.Write("\\\""u8);
                return;
            case '\\':
                _writer.Write("\\\\"u8);
                return;
            case '\b':
                _writer.Write("\\b"u8);
                return;
            case '\f':
                _writer.Write("\\f"u8);
                return;
            case '\n':
                _writer.Write("\\n"u8);
                return;
            case '\r':
                _writer.Write("\\r"u8);
                return;
            case '\t':
                _writer.Write("\\t"u8);
                return;
        }

        if (c < 0x20)
        {
            _writer.Write(Encoding.UTF8.GetBytes($"\\u{(int)c:X4}"));
            return;
        }

        WriteUtf8Codepoint(c);
    }

    private void WriteUtf8Codepoint(int codepoint)
    {
        var chars = char.ConvertFromUtf32(codepoint);
        _writer.Write(Encoding.UTF8.GetBytes(chars));
    }
}

internal sealed class LedgerCanonicalizationException : Exception
{
    public LedgerCanonicalizationException(string message) : base(message)
    {
    }
}
