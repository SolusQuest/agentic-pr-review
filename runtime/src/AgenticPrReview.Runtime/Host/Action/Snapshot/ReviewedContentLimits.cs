namespace AgenticPrReview.Runtime.ActionHost.Snapshot;

internal static class ReviewedContentLimits
{
    internal const int TrackedPaths = 20_000;
    internal const long TreeMetadataBytes = 8L * 1024 * 1024;
    internal const int PathBytes = 1_024;
    internal const int TreeDepth = 64;
    internal const int UniqueTreeAndBlobObjects = 4_000;
    internal const int GitObjectRequests = 4_096;
    internal const long HeadBlobBytes = 1024L * 1024;
    internal const long AggregateHeadBlobBytes = 256L * 1024 * 1024;
    internal const long GitObjectResponseBytes = 2L * 1024 * 1024;
    internal const long AggregateResponseBytes = 512L * 1024 * 1024;
    internal static readonly TimeSpan AcquisitionAndMaterializationTimeout =
        TimeSpan.FromSeconds(300);

    internal static ReviewedContentLimitProfile Production { get; } = new(
        GitObjectRequests,
        GitObjectResponseBytes,
        AggregateResponseBytes,
        AcquisitionAndMaterializationTimeout);
}

internal sealed record ReviewedContentLimitProfile(
    int MaximumRequests,
    long MaximumResponseBytes,
    long MaximumAggregateResponseBytes,
    TimeSpan Timeout)
{
    internal bool IsValid =>
        MaximumRequests > 0 &&
        MaximumResponseBytes > 0 &&
        MaximumAggregateResponseBytes >= MaximumResponseBytes &&
        Timeout > TimeSpan.Zero;
}

internal sealed record ReviewedContentBudgetRemaining(
    int Requests,
    long ResponseBytes,
    TimeSpan Time);

internal sealed class ReviewedContentBudget
{
    private readonly object _gate = new();
    private readonly ReviewedContentLimitProfile _limits;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedTimestamp;
    private int _requestCount;
    private long _aggregateResponseBytes;
    private bool _usable = true;

    private ReviewedContentBudget(
        ReviewedContentLimitProfile limits,
        TimeProvider timeProvider)
    {
        if (!limits.IsValid)
        {
            throw new ArgumentException(
                "Reviewed-content limits are invalid.",
                nameof(limits));
        }

        _limits = limits;
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        _startedTimestamp = _timeProvider.GetTimestamp();
    }

    internal static ReviewedContentBudget Create(
        ReviewedContentLimitProfile limits,
        TimeProvider timeProvider) => new(limits, timeProvider);

    internal bool TryReserveRequest(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_usable || DeadlineExceededLocked() ||
                _requestCount >= _limits.MaximumRequests)
            {
                return false;
            }

            _requestCount++;
            return true;
        }
    }

    internal bool TryConsumeResponseBytes(
        ref long responseBytes,
        int count,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        lock (_gate)
        {
            if (!_usable || DeadlineExceededLocked() ||
                responseBytes > _limits.MaximumResponseBytes - count ||
                _aggregateResponseBytes >
                    _limits.MaximumAggregateResponseBytes - count)
            {
                return false;
            }

            responseBytes += count;
            _aggregateResponseBytes += count;
            return true;
        }
    }

    internal bool WouldExceedResponse(
        long responseBytes,
        long contentLength)
    {
        if (responseBytes < 0 || contentLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(contentLength));
        }

        lock (_gate)
        {
            return !_usable ||
                DeadlineExceededLocked() ||
                responseBytes > _limits.MaximumResponseBytes - contentLength ||
                _aggregateResponseBytes >
                    _limits.MaximumAggregateResponseBytes - contentLength;
        }
    }

    internal bool TryGetRemaining(
        out ReviewedContentBudgetRemaining? remaining)
    {
        lock (_gate)
        {
            if (!_usable || DeadlineExceededLocked())
            {
                remaining = null;
                return false;
            }

            remaining = new ReviewedContentBudgetRemaining(
                _limits.MaximumRequests - _requestCount,
                _limits.MaximumAggregateResponseBytes -
                    _aggregateResponseBytes,
                RemainingTimeLocked());
            return true;
        }
    }

    internal void Invalidate()
    {
        lock (_gate)
        {
            _usable = false;
        }
    }

    private bool DeadlineExceededLocked() =>
        _timeProvider.GetElapsedTime(
            _startedTimestamp,
            _timeProvider.GetTimestamp()) >= _limits.Timeout;

    private TimeSpan RemainingTimeLocked()
    {
        var elapsed = _timeProvider.GetElapsedTime(
            _startedTimestamp,
            _timeProvider.GetTimestamp());
        return elapsed >= _limits.Timeout
            ? TimeSpan.Zero
            : _limits.Timeout - elapsed;
    }
}

internal sealed class ReviewedTreeTraversalMeter
{
    private int _leafPaths;
    private long _treeMetadataBytes;
    private int _uniqueObjects;
    private long _logicalHeadBlobBytes;

    internal bool TryAddLogicalEntry(int completePathBytes)
    {
        if (completePathBytes < 0 ||
            _treeMetadataBytes >
                ReviewedContentLimits.TreeMetadataBytes - completePathBytes)
        {
            return false;
        }

        _treeMetadataBytes += completePathBytes;
        return true;
    }

    internal bool TryAddLeafPath()
    {
        if (_leafPaths >= ReviewedContentLimits.TrackedPaths)
        {
            return false;
        }

        _leafPaths++;
        return true;
    }

    internal bool TryAddUniqueObject()
    {
        if (_uniqueObjects >= ReviewedContentLimits.UniqueTreeAndBlobObjects)
        {
            return false;
        }

        _uniqueObjects++;
        return true;
    }

    internal bool TryAddLogicalHeadBlobBytes(long bytes)
    {
        if (bytes < 0 ||
            _logicalHeadBlobBytes >
                ReviewedContentLimits.AggregateHeadBlobBytes - bytes)
        {
            return false;
        }

        _logicalHeadBlobBytes += bytes;
        return true;
    }
}
