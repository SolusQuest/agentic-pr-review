namespace AgenticPrReview.Runtime.Prefix;

/// <summary>Frozen implementation-parameter bounds (issue #50, D11).</summary>
internal static class PrefixBounds
{
    public const int MaxLogicalSegmentPayloadBytes = 262_144;
    public const long MaxLogicalStableStreamBytes = 1_048_576;
    public const long MaxLogicalDynamicStreamBytes = 262_144;
    public const int MaxProviderBlockWrapperBytes = 64;
    public const long MaxProviderBlockPayloadBytes = 2L * MaxLogicalSegmentPayloadBytes + MaxProviderBlockWrapperBytes;
    public const long MaxProviderStableStreamBytes = 2L * MaxLogicalStableStreamBytes + MaxStableSegments * (MaxProviderBlockWrapperBytes + 4L);
    public const long MaxProviderDynamicStreamBytes = 4L + MaxProviderBlockPayloadBytes;
    public const int MaxEnvelopeCanonicalBytes = 262_144;
    public const int MaxStableSegments = 67;
    public const int MaxToolDefinitions = 64;
    public const int MaxEnvelopeJsonDepth = 64;
    public const int MaxEnvelopeObjectProperties = 256;
    public const int MaxEnvelopeArrayItems = 1_024;
    public const int MaxIdentityUtf8Bytes = 256;
    public const long MaxInteractionOrdinal = 1_000_000;
}
