using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Ledger-schema RFC 8785 canonical writer. This is not a general
/// arbitrary-JSON API; it serializes only the closed shapes of
/// ProviderSessionLedgerV1 and its two digest envelopes.
/// </summary>
public static class LedgerCanonicalizer
{
    public static byte[] SerializeCanonical(LedgerModel model)
    {
        using var buffer = new PooledMemoryStream();
        WriteLedger(buffer, model);
        return buffer.ToArray();
    }

    /// <summary>
    /// Canonicalize a small envelope for digest preimages. Only handles the
    /// shapes used by <see cref="LedgerDigests"/>: objects whose values are
    /// strings, non-negative integers, or nested envelope objects. Property
    /// order is derived from the input dictionary insertion order after being
    /// sorted by unsigned UTF-16 code-unit ordering.
    /// </summary>
    public static byte[] SerializeEnvelope(IReadOnlyDictionary<string, object> envelope)
    {
        using var buffer = new PooledMemoryStream();
        WriteEnvelope(buffer, envelope);
        return buffer.ToArray();
    }

    /// <summary>
    /// Compute the lowercase-hex SHA-256 of a byte sequence.
    /// </summary>
    public static string ComputeSha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // -----------------------------------------------------------------
    // Ledger writer

    private static void WriteLedger(PooledMemoryStream buffer, LedgerModel model)
    {
        // Top-level order (canonical UTF-16 lex): header, prefixContractVersion, records, schemaVersion
        buffer.WriteByte((byte)'{');
        WriteProperty(buffer, "header", () => WriteHeader(buffer, model.Header));
        buffer.WriteByte((byte)',');
        WriteProperty(buffer, "prefixContractVersion", () => WriteInt(buffer, model.PrefixContractVersion));
        buffer.WriteByte((byte)',');
        WriteProperty(buffer, "records", () =>
        {
            buffer.WriteByte((byte)'[');
            for (var i = 0; i < model.Records.Length; i++)
            {
                if (i > 0) buffer.WriteByte((byte)',');
                WriteRecord(buffer, model.Records[i]);
            }
            buffer.WriteByte((byte)']');
        });
        buffer.WriteByte((byte)',');
        WriteProperty(buffer, "schemaVersion", () => WriteInt(buffer, model.SchemaVersion));
        buffer.WriteByte((byte)'}');
    }

    private static void WriteHeader(PooledMemoryStream buffer, LedgerHeader h)
    {
        // Collect present properties. Ordering will be applied by CanonicalPropertyList.
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["adapterId"] = () => WriteString(buffer, h.AdapterId),
            ["cacheConfigId"] = () => WriteString(buffer, h.CacheConfigId),
            ["headRepository"] = () => WriteString(buffer, h.HeadRepository),
            ["kind"] = () => WriteString(buffer, h.Kind),
            ["ledgerEpoch"] = () => WriteInt(buffer, h.LedgerEpoch),
            ["modelId"] = () => WriteString(buffer, h.ModelId),
            ["policyId"] = () => WriteString(buffer, h.PolicyId),
            ["predecessorLedgerSha256"] = () => WriteString(buffer, h.PredecessorLedgerSha256),
            ["providerId"] = () => WriteString(buffer, h.ProviderId),
            ["pullRequest"] = () => WriteInt(buffer, h.PullRequest),
            ["repository"] = () => WriteString(buffer, h.Repository),
            ["sessionEpoch"] = () => WriteString(buffer, h.SessionEpoch),
            ["stateGeneration"] = () => WriteInt(buffer, h.StateGeneration),
            ["templateId"] = () => WriteString(buffer, h.TemplateId),
            ["toolDefinitionId"] = () => WriteString(buffer, h.ToolDefinitionId),
            ["trustedExecutionDomain"] = () => WriteString(buffer, h.TrustedExecutionDomain),
            ["workflowIdentity"] = () => WriteString(buffer, h.WorkflowIdentity),
        };
        if (h.PredecessorStateGeneration is int psg)
            props["predecessorStateGeneration"] = () => WriteInt(buffer, psg);
        if (h.PredecessorManifestSha256 is string pms)
            props["predecessorManifestSha256"] = () => WriteString(buffer, pms);
        if (h.ResetReason is string rr)
            props["resetReason"] = () => WriteString(buffer, rr);
        if (h.RecoveryReason is string rvr)
            props["recoveryReason"] = () => WriteString(buffer, rvr);

        WriteObject(buffer, props);
    }

    private static void WriteRecord(PooledMemoryStream buffer, LedgerRecord record)
    {
        if (record.Context is ReviewContextRecord ctx)
        {
            WriteReviewContext(buffer, ctx);
        }
        else if (record.Outcome is ReviewOutcomeRecord oc)
        {
            WriteReviewOutcome(buffer, oc);
        }
        else
        {
            throw new InvalidOperationException("LedgerRecord must carry Context or Outcome.");
        }
    }

    private static void WriteReviewContext(PooledMemoryStream buffer, ReviewContextRecord ctx)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["cacheContractDigest"] = () => WriteString(buffer, ctx.CacheContractDigest),
            ["changedFiles"] = () =>
            {
                buffer.WriteByte((byte)'[');
                for (var i = 0; i < ctx.ChangedFiles.Length; i++)
                {
                    if (i > 0) buffer.WriteByte((byte)',');
                    WriteChangedFile(buffer, ctx.ChangedFiles[i]);
                }
                buffer.WriteByte((byte)']');
            },
            ["interactionId"] = () => WriteString(buffer, ctx.InteractionId),
            ["interactionOrdinal"] = () => WriteInt(buffer, ctx.InteractionOrdinal),
            ["reviewedBaseSha"] = () => WriteString(buffer, ctx.ReviewedBaseSha),
            ["reviewedHeadSha"] = () => WriteString(buffer, ctx.ReviewedHeadSha),
            ["role"] = () => WriteString(buffer, "review_context"),
            ["subjectDigest"] = () => WriteString(buffer, ctx.SubjectDigest),
        };
        WriteObject(buffer, props);
    }

    private static void WriteChangedFile(PooledMemoryStream buffer, ChangedFileEntry cf)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["additions"] = () => WriteInt(buffer, cf.Additions),
            ["changes"] = () => WriteInt(buffer, cf.Changes),
            ["deletions"] = () => WriteInt(buffer, cf.Deletions),
            ["path"] = () => WriteString(buffer, cf.Path),
            ["status"] = () => WriteString(buffer, cf.Status),
        };
        if (cf.PreviousPath is string pp)
            props["previousPath"] = () => WriteString(buffer, pp);
        if (cf.Patch is ChangedFilePatch patch)
            props["patch"] = () =>
            {
                var patchProps = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
                {
                    ["maxChars"] = () => WriteInt(buffer, patch.MaxChars),
                    ["sha256"] = () => WriteString(buffer, patch.Sha256),
                    ["truncated"] = () => WriteBool(buffer, patch.Truncated),
                };
                WriteObject(buffer, patchProps);
            };
        WriteObject(buffer, props);
    }

    private static void WriteReviewOutcome(PooledMemoryStream buffer, ReviewOutcomeRecord oc)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["findings"] = () =>
            {
                buffer.WriteByte((byte)'[');
                for (var i = 0; i < oc.Findings.Length; i++)
                {
                    if (i > 0) buffer.WriteByte((byte)',');
                    WriteFinding(buffer, oc.Findings[i]);
                }
                buffer.WriteByte((byte)']');
            },
            ["interactionId"] = () => WriteString(buffer, oc.InteractionId),
            ["interactionOrdinal"] = () => WriteInt(buffer, oc.InteractionOrdinal),
            ["limitations"] = () =>
            {
                buffer.WriteByte((byte)'[');
                for (var i = 0; i < oc.Limitations.Length; i++)
                {
                    if (i > 0) buffer.WriteByte((byte)',');
                    WriteString(buffer, oc.Limitations[i]);
                }
                buffer.WriteByte((byte)']');
            },
            ["role"] = () => WriteString(buffer, "review_outcome"),
            ["summary"] = () => WriteString(buffer, oc.Summary),
        };
        WriteObject(buffer, props);
    }

    private static void WriteFinding(PooledMemoryStream buffer, LedgerFinding f)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["body"] = () => WriteString(buffer, f.Body),
            ["category"] = () => WriteString(buffer, f.Category),
            ["confidence"] = () => WriteString(buffer, f.Confidence),
            ["endLine"] = () => WriteNullableInt(buffer, f.EndLine),
            ["path"] = () => WriteNullableString(buffer, f.Path),
            ["severity"] = () => WriteString(buffer, f.Severity),
            ["startLine"] = () => WriteNullableInt(buffer, f.StartLine),
            ["title"] = () => WriteString(buffer, f.Title),
        };
        if (f.Evidence is string e) props["evidence"] = () => WriteString(buffer, e);
        if (f.SuggestedAction is string sa) props["suggestedAction"] = () => WriteString(buffer, sa);
        if (f.InlinePreference is string ip) props["inlinePreference"] = () => WriteString(buffer, ip);
        WriteObject(buffer, props);
    }

    // -----------------------------------------------------------------
    // Envelope writer (for digest preimages)

    private static void WriteEnvelope(PooledMemoryStream buffer, IReadOnlyDictionary<string, object> envelope)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance);
        foreach (var kv in envelope)
        {
            var value = kv.Value;
            props[kv.Key] = () => WriteEnvelopeValue(buffer, value);
        }
        WriteObject(buffer, props);
    }

    private static void WriteEnvelopeValue(PooledMemoryStream buffer, object value)
    {
        switch (value)
        {
            case string s: WriteString(buffer, s); break;
            case int i: WriteInt(buffer, i); break;
            case long l: WriteLong(buffer, l); break;
            case bool b: WriteBool(buffer, b); break;
            case IReadOnlyDictionary<string, object> nested: WriteEnvelope(buffer, nested); break;
            default: throw new InvalidOperationException("Unsupported envelope value type: " + value.GetType());
        }
    }

    // -----------------------------------------------------------------
    // Low-level

    private static void WriteObject(PooledMemoryStream buffer, SortedDictionary<string, Action> props)
    {
        buffer.WriteByte((byte)'{');
        var first = true;
        foreach (var kv in props)
        {
            if (!first) buffer.WriteByte((byte)',');
            first = false;
            WriteString(buffer, kv.Key);
            buffer.WriteByte((byte)':');
            kv.Value();
        }
        buffer.WriteByte((byte)'}');
    }

    private static void WriteProperty(PooledMemoryStream buffer, string name, Action writeValue)
    {
        WriteString(buffer, name);
        buffer.WriteByte((byte)':');
        writeValue();
    }

    private static void WriteString(PooledMemoryStream buffer, string s)
    {
        buffer.WriteByte((byte)'"');
        // Escape per RFC 8785: only ", \, and code points below 0x20.
        var i = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (ch == '"') { buffer.WriteAscii("\\\""); i++; continue; }
            if (ch == '\\') { buffer.WriteAscii("\\\\"); i++; continue; }
            if (ch < 0x20)
            {
                buffer.WriteAscii(ch switch
                {
                    '\b' => "\\b",
                    '\f' => "\\f",
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\t' => "\\t",
                    _ => $"\\u{(int)ch:x4}",
                });
                i++;
                continue;
            }

            // Handle surrogate pair or single BMP char, encoded as UTF-8.
            int codepoint;
            int consumed;
            if (char.IsHighSurrogate(ch) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                codepoint = char.ConvertToUtf32(ch, s[i + 1]);
                consumed = 2;
            }
            else if (char.IsSurrogate(ch))
            {
                throw new InvalidOperationException("Lone surrogate encountered when writing canonical ledger JSON.");
            }
            else
            {
                codepoint = ch;
                consumed = 1;
            }

            AppendUtf8CodePoint(buffer, codepoint);
            i += consumed;
        }
        buffer.WriteByte((byte)'"');
    }

    private static void WriteNullableString(PooledMemoryStream buffer, string? s)
    {
        if (s is null)
        {
            buffer.WriteAscii("null");
        }
        else
        {
            WriteString(buffer, s);
        }
    }

    private static void WriteInt(PooledMemoryStream buffer, int value)
    {
        buffer.WriteAscii(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteLong(PooledMemoryStream buffer, long value)
    {
        buffer.WriteAscii(value.ToString(CultureInfo.InvariantCulture));
    }

    private static void WriteNullableInt(PooledMemoryStream buffer, int? value)
    {
        if (value is int v) WriteInt(buffer, v);
        else buffer.WriteAscii("null");
    }

    private static void WriteBool(PooledMemoryStream buffer, bool value)
    {
        buffer.WriteAscii(value ? "true" : "false");
    }

    private static void AppendUtf8CodePoint(PooledMemoryStream buffer, int cp)
    {
        // RFC 8785 requires literal UTF-8 emission for characters >= 0x20 (except the
        // two escaped characters " and \). Encode inline to avoid string allocations.
        if (cp <= 0x7F)
        {
            buffer.WriteByte((byte)cp);
        }
        else if (cp <= 0x7FF)
        {
            buffer.WriteByte((byte)(0xC0 | (cp >> 6)));
            buffer.WriteByte((byte)(0x80 | (cp & 0x3F)));
        }
        else if (cp <= 0xFFFF)
        {
            buffer.WriteByte((byte)(0xE0 | (cp >> 12)));
            buffer.WriteByte((byte)(0x80 | ((cp >> 6) & 0x3F)));
            buffer.WriteByte((byte)(0x80 | (cp & 0x3F)));
        }
        else
        {
            buffer.WriteByte((byte)(0xF0 | (cp >> 18)));
            buffer.WriteByte((byte)(0x80 | ((cp >> 12) & 0x3F)));
            buffer.WriteByte((byte)(0x80 | ((cp >> 6) & 0x3F)));
            buffer.WriteByte((byte)(0x80 | (cp & 0x3F)));
        }
    }
}

/// <summary>
/// RFC 8785 property-name ordering: sort by unsigned UTF-16 code-unit
/// lexicographic order. Not the same as ordinal / codepoint order for
/// supplementary-plane characters.
/// </summary>
internal sealed class LedgerPropertyNameComparer : IComparer<string>
{
    public static readonly LedgerPropertyNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;
        var n = Math.Min(x.Length, y.Length);
        for (var i = 0; i < n; i++)
        {
            int cmp = x[i] - y[i];
            if (cmp != 0) return cmp;
        }
        return x.Length - y.Length;
    }
}

internal sealed class PooledMemoryStream : IDisposable
{
    private byte[] buffer = ArrayPool<byte>.Shared.Rent(4096);
    private int position;

    public void WriteByte(byte value)
    {
        EnsureCapacity(1);
        this.buffer[this.position++] = value;
    }

    public void WriteAscii(string value)
    {
        EnsureCapacity(value.Length);
        for (var i = 0; i < value.Length; i++)
        {
            this.buffer[this.position + i] = (byte)value[i];
        }
        this.position += value.Length;
    }

    public byte[] ToArray()
    {
        var result = new byte[this.position];
        Buffer.BlockCopy(this.buffer, 0, result, 0, this.position);
        return result;
    }

    public void Dispose()
    {
        if (this.buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(this.buffer);
            this.buffer = Array.Empty<byte>();
        }
    }

    private void EnsureCapacity(int extra)
    {
        if (this.position + extra <= this.buffer.Length) return;
        var newSize = Math.Max(this.buffer.Length * 2, this.position + extra);
        var newBuf = ArrayPool<byte>.Shared.Rent(newSize);
        Buffer.BlockCopy(this.buffer, 0, newBuf, 0, this.position);
        ArrayPool<byte>.Shared.Return(this.buffer);
        this.buffer = newBuf;
    }
}
