namespace AgenticPrReview.Runtime.Canonical;

internal enum Rfc8785RejectionReason
{
    NonFiniteNumber,
    UnpairedSurrogate,
    DuplicateProperty,
    DepthLimitExceeded,
    PropertyCountExceeded,
    ArrayLengthExceeded,
}

/// <summary>
/// Rejection raised by the general RFC 8785 writer / canonicalizer. Mapped to
/// prefix-contract diagnostics by callers; never escapes a public entry point.
/// </summary>
internal sealed class Rfc8785CanonicalizationException : Exception
{
    internal Rfc8785CanonicalizationException(Rfc8785RejectionReason reason, string message, IReadOnlyList<string>? segments = null)
        : base(message)
    {
        Reason = reason;
        Segments = segments;
    }

    internal Rfc8785RejectionReason Reason { get; }

    /// <summary>Raw path segments (property names / array indices) of the offending value, when known.</summary>
    internal IReadOnlyList<string>? Segments { get; }
}
