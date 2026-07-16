using System.Buffers;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Ledger-scoped RFC 8785 canonical writer. Not a general arbitrary-JSON API;
/// only serializes the closed shapes owned by <see cref="LedgerModel"/>,
/// <see cref="LedgerRecord"/> subtypes, and the cache-contract identity object.
/// </summary>
internal static class LedgerCanonicalizer
{
    /// <summary>
    /// Full-ledger canonical bytes used by parser round-trip verification and
    /// by builder assembly.
    /// </summary>
    internal static ImmutableArray<byte> SerializeCanonical(LedgerModel model)
    {
        using var buffer = new PooledMemoryStream();
        WriteLedger(buffer, model);
        return ImmutableArray.Create(buffer.ToArray());
    }

    /// <summary>
    /// Canonical bytes of the cache-contract identity envelope
    /// <c>{adapterId, cacheConfigId, modelId, policyId, providerId, templateId, toolDefinitionId}</c>,
    /// used to compute <c>cacheContractDigest</c> during builder assembly.
    /// </summary>
    internal static ImmutableArray<byte> SerializeCacheContractIdentity(ExpectedIdentities identities)
    {
        using var buffer = new PooledMemoryStream();
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["adapterId"] = () => WriteString(buffer, identities.AdapterId),
            ["cacheConfigId"] = () => WriteString(buffer, identities.CacheConfigId),
            ["modelId"] = () => WriteString(buffer, identities.ModelId),
            ["policyId"] = () => WriteString(buffer, identities.PolicyId),
            ["providerId"] = () => WriteString(buffer, identities.ProviderId),
            ["templateId"] = () => WriteString(buffer, identities.TemplateId),
            ["toolDefinitionId"] = () => WriteString(buffer, identities.ToolDefinitionId),
        };
        WriteObject(buffer, props);
        return ImmutableArray.Create(buffer.ToArray());
    }

    /// <summary>
    /// Canonical bytes of a single record. Used by the continuation prefix
    /// invariant (same-index canonical-byte equality between predecessor and
    /// candidate records).
    /// </summary>
    internal static ImmutableArray<byte> SerializeRecord(LedgerRecord record)
    {
        using var buffer = new PooledMemoryStream();
        WriteRecord(buffer, record);
        return ImmutableArray.Create(buffer.ToArray());
    }

    /// <summary>
    /// Lowercase-hex SHA-256 over an arbitrary byte sequence, in the shared
    /// <c>Sha256Hex</c> format.
    /// </summary>
    internal static string ComputeSha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    // -----------------------------------------------------------------
    // Ledger writer

    private static void WriteLedger(PooledMemoryStream buffer, LedgerModel model)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["header"] = () => WriteHeader(buffer, model.Header),
            ["prefixContractVersion"] = () => WriteInt(buffer, model.PrefixContractVersion),
            ["records"] = () =>
            {
                buffer.WriteByte((byte)'[');
                for (var i = 0; i < model.Records.Length; i++)
                {
                    if (i > 0) buffer.WriteByte((byte)',');
                    WriteRecord(buffer, model.Records[i]);
                }
                buffer.WriteByte((byte)']');
            },
            ["schemaVersion"] = () => WriteInt(buffer, model.SchemaVersion),
        };
        WriteObject(buffer, props);
    }

    private static void WriteHeader(PooledMemoryStream buffer, LedgerHeader h)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["adapterId"] = () => WriteString(buffer, h.AdapterId),
            ["cacheConfigId"] = () => WriteString(buffer, h.CacheConfigId),
            ["headRepository"] = () => WriteString(buffer, h.HeadRepository),
            ["kind"] = () => WriteString(buffer, h.Kind),
            ["ledgerEpoch"] = () => WriteString(buffer, h.LedgerEpoch),
            ["modelId"] = () => WriteString(buffer, h.ModelId),
            ["policyId"] = () => WriteString(buffer, h.PolicyId),
            ["predecessorLedgerSha256"] = () => WriteString(buffer, h.PredecessorLedgerSha256),
            ["providerId"] = () => WriteString(buffer, h.ProviderId),
            ["pullRequest"] = () => WriteInt(buffer, h.PullRequest),
            ["repository"] = () => WriteString(buffer, h.Repository),
            ["sessionEpoch"] = () => WriteString(buffer, h.SessionEpoch),
            ["stateGeneration"] = () => WriteLong(buffer, h.StateGeneration),
            ["templateId"] = () => WriteString(buffer, h.TemplateId),
            ["toolDefinitionId"] = () => WriteString(buffer, h.ToolDefinitionId),
            ["trustedExecutionDomain"] = () => WriteString(buffer, h.TrustedExecutionDomain),
            ["workflowIdentity"] = () => WriteString(buffer, h.WorkflowIdentity),
        };

        if (h.PredecessorLedgerEpoch is string ple)
            props["predecessorLedgerEpoch"] = () => WriteString(buffer, ple);
        if (h.PredecessorStateGeneration is long psg)
            props["predecessorStateGeneration"] = () => WriteLong(buffer, psg);
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
        switch (record)
        {
            case ReviewContextRecord ctx:
                WriteReviewContext(buffer, ctx);
                return;
            case ReviewOutcomeRecord oc:
                WriteReviewOutcome(buffer, oc);
                return;
            default:
                throw new InvalidOperationException("LedgerRecord must be ReviewContextRecord or ReviewOutcomeRecord.");
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
            ["interactionOrdinal"] = () => WriteLong(buffer, ctx.InteractionOrdinal),
            ["reviewedBaseSha"] = () => WriteString(buffer, ctx.ReviewedBaseSha),
            ["reviewedHeadSha"] = () => WriteString(buffer, ctx.ReviewedHeadSha),
            ["role"] = () => WriteString(buffer, "review_context"),
            ["subjectDigest"] = () => WriteString(buffer, ctx.SubjectDigest),
        };
        WriteObject(buffer, props);
    }

    private static void WriteChangedFile(PooledMemoryStream buffer, LedgerChangedFile cf)
    {
        var props = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
        {
            ["additions"] = () => WriteLong(buffer, cf.Additions),
            ["changes"] = () => WriteLong(buffer, cf.Changes),
            ["deletions"] = () => WriteLong(buffer, cf.Deletions),
            ["path"] = () => WriteString(buffer, cf.Path),
            ["status"] = () => WriteString(buffer, cf.Status),
        };
        if (cf.PreviousPath is string pp)
            props["previousPath"] = () => WriteString(buffer, pp);
        if (cf.Patch is LedgerBoundedPatch patch)
        {
            props["patch"] = () =>
            {
                var patchProps = new SortedDictionary<string, Action>(LedgerPropertyNameComparer.Instance)
                {
                    ["maxChars"] = () => WriteLong(buffer, patch.MaxChars),
                    ["sha256"] = () => WriteString(buffer, patch.Sha256),
                    ["truncated"] = () => WriteBool(buffer, patch.Truncated),
                };
                WriteObject(buffer, patchProps);
            };
        }
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
            ["interactionOrdinal"] = () => WriteLong(buffer, oc.InteractionOrdinal),
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
            ["endLine"] = () => WriteNullableLong(buffer, f.EndLine),
            ["path"] = () => WriteNullableString(buffer, f.Path),
            ["severity"] = () => WriteString(buffer, f.Severity),
            ["startLine"] = () => WriteNullableLong(buffer, f.StartLine),
            ["title"] = () => WriteString(buffer, f.Title),
        };
        if (f.Evidence is string e) props["evidence"] = () => WriteString(buffer, e);
        if (f.SuggestedAction is string sa) props["suggestedAction"] = () => WriteString(buffer, sa);
        if (f.InlinePreference is string ip) props["inlinePreference"] = () => WriteString(buffer, ip);
        WriteObject(buffer, props);
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

    private static void WriteString(PooledMemoryStream buffer, string s)
    {
        buffer.WriteByte((byte)'"');
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
        if (s is null) buffer.WriteAscii("null");
        else WriteString(buffer, s);
    }

    private static void WriteInt(PooledMemoryStream buffer, int value)
        => buffer.WriteAscii(value.ToString(CultureInfo.InvariantCulture));

    private static void WriteLong(PooledMemoryStream buffer, long value)
        => buffer.WriteAscii(value.ToString(CultureInfo.InvariantCulture));

    private static void WriteNullableLong(PooledMemoryStream buffer, long? value)
    {
        if (value is long v) WriteLong(buffer, v);
        else buffer.WriteAscii("null");
    }

    private static void WriteBool(PooledMemoryStream buffer, bool value)
        => buffer.WriteAscii(value ? "true" : "false");

    private static void AppendUtf8CodePoint(PooledMemoryStream buffer, int cp)
    {
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
/// lexicographic order.
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
