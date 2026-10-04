namespace AgenticPrReview.Runtime.Agent.Chat;

// Only the adapter's closed transport classifier may emit this signal. It carries
// no HTTP types, provider body, headers, credentials or original exception.
internal sealed class ProjectChatRetryException : Exception
{
    internal ProjectChatRetryException(TimeSpan? retryAfter = null)
        : base("Transient provider attempt failed.")
    {
        if (retryAfter < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retryAfter));
        RetryAfter = retryAfter;
    }

    internal TimeSpan? RetryAfter { get; }
}
