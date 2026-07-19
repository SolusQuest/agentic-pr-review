namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Bounds guards and checked arithmetic for stream framing. Exposed as
/// internal seams so guard-bound vectors can synthesize byte counts that no
/// legal mapped content can reach.
/// </summary>
internal static class PrefixGuards
{
    internal static PrefixDiagnostic? CheckSegmentPayload(long payloadBytes, string? causeCode = null)
    {
        return payloadBytes > PrefixBounds.MaxLogicalSegmentPayloadBytes
            ? PrefixDiagnostic.Create(PrefixDiagnosticCodes.SegmentTooLarge, causeCode: causeCode)
            : null;
    }

    internal static PrefixDiagnostic? CheckProviderBlockPayload(long payloadBytes)
    {
        return payloadBytes > PrefixBounds.MaxProviderBlockPayloadBytes
            ? PrefixDiagnostic.Create(PrefixDiagnosticCodes.SegmentTooLarge, causeCode: "provider-block")
            : null;
    }

    internal static PrefixDiagnostic? CheckStreamTotal(long totalBytes, long cap, string causeCode)
    {
        return totalBytes > cap
            ? PrefixDiagnostic.Create(PrefixDiagnosticCodes.StreamTooLarge, causeCode: causeCode)
            : null;
    }

    internal static PrefixDiagnostic? CheckLogicalStableTotal(long totalBytes) =>
        CheckStreamTotal(totalBytes, PrefixBounds.MaxLogicalStableStreamBytes, "logical-stable");

    internal static PrefixDiagnostic? CheckLogicalDynamicTotal(long totalBytes) =>
        CheckStreamTotal(totalBytes, PrefixBounds.MaxLogicalDynamicStreamBytes, "logical-dynamic");

    internal static PrefixDiagnostic? CheckProviderStableTotal(long totalBytes) =>
        CheckStreamTotal(totalBytes, PrefixBounds.MaxProviderStableStreamBytes, "provider-stable");

    internal static PrefixDiagnostic? CheckProviderDynamicTotal(long totalBytes) =>
        CheckStreamTotal(totalBytes, PrefixBounds.MaxProviderDynamicStreamBytes, "provider-dynamic");

    /// <summary>Checked addition; overflow maps to prefix_length_overflow.</summary>
    internal static bool TryCheckedTotal(long a, long b, out long total, out PrefixDiagnostic? overflow)
    {
        try
        {
            total = checked(a + b);
            overflow = null;
            return true;
        }
        catch (OverflowException)
        {
            total = 0;
            overflow = PrefixDiagnostic.Create(PrefixDiagnosticCodes.LengthOverflow);
            return false;
        }
    }
}
