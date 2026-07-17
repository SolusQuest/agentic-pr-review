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
    internal Rfc8785CanonicalizationException(Rfc8785RejectionReason reason, string message, string? path = null)
        : base(message)
    {
        Reason = reason;
        Path = path;
    }

    internal Rfc8785RejectionReason Reason { get; }

    /// <summary>Internal traversal path of the offending value, when known.</summary>
    internal string? Path { get; }
}
