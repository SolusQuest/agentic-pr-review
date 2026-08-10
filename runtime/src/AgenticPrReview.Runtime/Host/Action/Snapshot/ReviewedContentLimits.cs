using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;

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

}

internal sealed record ReviewedContentBudgetRemaining(
    int Requests,
    long ResponseBytes,
    TimeSpan Time);

internal sealed class ReviewedContentBudget
{
    private readonly object _gate = new();
    private readonly int _maximumRequests;
    private readonly long _maximumResponseBytes;
    private readonly long _maximumAggregateResponseBytes;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _timeProvider;
    private readonly long _startedTimestamp;
    private int _requestCount;
    private long _aggregateResponseBytes;
    private bool _usable = true;

    private ReviewedContentBudget(
        int maximumRequests,
        long maximumResponseBytes,
        long maximumAggregateResponseBytes,
        TimeSpan timeout,
        TimeProvider timeProvider)
    {
        if (maximumRequests <= 0 ||
            maximumResponseBytes <= 0 ||
            maximumAggregateResponseBytes < maximumResponseBytes ||
            timeout <= TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Reviewed-content limits are invalid.");
        }

        _maximumRequests = maximumRequests;
        _maximumResponseBytes = maximumResponseBytes;
        _maximumAggregateResponseBytes = maximumAggregateResponseBytes;
        _timeout = timeout;
        _timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        _startedTimestamp = _timeProvider.GetTimestamp();
    }

    internal static ReviewedContentBudget Mint(
        object authority,
        TimeProvider timeProvider)
    {
        if (!ReviewedTreeReader.HasMintAuthority(authority))
        {
            throw new InvalidOperationException(
                "Only the reviewed-tree reader may mint the shared budget.");
        }

        return new ReviewedContentBudget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
            timeProvider);
    }

    internal bool TryReserveRequest(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_usable || DeadlineExceededLocked() ||
                _requestCount >= _maximumRequests)
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
                responseBytes > _maximumResponseBytes - count ||
                _aggregateResponseBytes >
                    _maximumAggregateResponseBytes - count)
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
                responseBytes > _maximumResponseBytes - contentLength ||
                _aggregateResponseBytes >
                    _maximumAggregateResponseBytes - contentLength;
        }
    }

    internal bool TryBeginOperation(
        CancellationToken cancellationToken,
        out OperationLease? operation)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_usable || DeadlineExceededLocked())
            {
                operation = null;
                return false;
            }

            operation = new OperationLease(
                cancellationToken,
                RemainingTimeLocked(),
                _timeProvider);
            return true;
        }
    }

    internal bool TryContinue(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return _usable && !DeadlineExceededLocked();
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
                _maximumRequests - _requestCount,
                _maximumAggregateResponseBytes -
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
            _timeProvider.GetTimestamp()) >= _timeout;

    private TimeSpan RemainingTimeLocked()
    {
        var elapsed = _timeProvider.GetElapsedTime(
            _startedTimestamp,
            _timeProvider.GetTimestamp());
        return elapsed >= _timeout
            ? TimeSpan.Zero
            : _timeout - elapsed;
    }

    internal sealed class OperationLease : IDisposable
    {
        private readonly CancellationToken _callerToken;
        private readonly CancellationTokenSource _deadline;
        private readonly CancellationTokenSource _linked;

        internal OperationLease(
            CancellationToken callerToken,
            TimeSpan remaining,
            TimeProvider timeProvider)
        {
            _callerToken = callerToken;
            _deadline = new CancellationTokenSource(remaining, timeProvider);
            _linked = CancellationTokenSource.CreateLinkedTokenSource(
                callerToken,
                _deadline.Token);
        }

        internal CancellationToken Token => _linked.Token;

        internal bool DeadlineExpired =>
            !_callerToken.IsCancellationRequested &&
            _deadline.IsCancellationRequested;

        public void Dispose()
        {
            _linked.Dispose();
            _deadline.Dispose();
        }
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
