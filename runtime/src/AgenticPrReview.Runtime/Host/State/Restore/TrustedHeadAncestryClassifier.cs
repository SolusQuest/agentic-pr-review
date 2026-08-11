using System.Collections.Immutable;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed class TrustedHeadAncestryClassifier
{
    internal const int MaximumCommitRequests = 1_024;
    internal const int MaximumCommitResponseBytes = 64 * 1024;
    internal const int MaximumParentsPerCommit = 64;
    internal const long MaximumAggregateResponseBytes = 32L * 1024 * 1024;
    internal static readonly TimeSpan MaximumDuration = TimeSpan.FromSeconds(60);

    private readonly IActionHostGitObjectTransport transport;
    private readonly TimeProvider timeProvider;

    internal TrustedHeadAncestryClassifier(
        IActionHostGitObjectTransport transport,
        TimeProvider? timeProvider = null)
    {
        this.transport = transport ??
            throw new ArgumentNullException(nameof(transport));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<AgentSessionHeadTransition> ClassifyAsync(
        string repositoryName,
        string producerBaseSha,
        string producerHeadSha,
        string currentBaseSha,
        string currentHeadSha,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryName) ||
            !ActionHostGitObjectMapper.IsSha(producerBaseSha) ||
            !ActionHostGitObjectMapper.IsSha(producerHeadSha) ||
            !ActionHostGitObjectMapper.IsSha(currentBaseSha) ||
            !ActionHostGitObjectMapper.IsSha(currentHeadSha))
        {
            return AgentSessionHeadTransition.Unknown;
        }

        var sameHead = StringComparer.Ordinal.Equals(
            producerHeadSha,
            currentHeadSha);
        var sameBase = StringComparer.Ordinal.Equals(
            producerBaseSha,
            currentBaseSha);
        if (sameHead && sameBase)
        {
            return AgentSessionHeadTransition.SameHead;
        }

        if (sameHead)
        {
            return AgentSessionHeadTransition.Diverged;
        }

        var meter = new AncestryMeter(timeProvider.GetUtcNow());
        var cache = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.Ordinal);
        var head = await ProveAncestorAsync(
                repositoryName,
                producerHeadSha,
                currentHeadSha,
                meter,
                cache,
                cancellationToken)
            .ConfigureAwait(false);
        if (head == AncestryProof.Unknown)
        {
            return AgentSessionHeadTransition.Unknown;
        }

        if (head == AncestryProof.NotAncestor)
        {
            return AgentSessionHeadTransition.Diverged;
        }

        if (sameBase)
        {
            return AgentSessionHeadTransition.VerifiedAhead;
        }

        var baseProof = await ProveAncestorAsync(
                repositoryName,
                producerBaseSha,
                currentBaseSha,
                meter,
                cache,
                cancellationToken)
            .ConfigureAwait(false);
        return baseProof switch
        {
            AncestryProof.Ancestor => AgentSessionHeadTransition.VerifiedAhead,
            AncestryProof.NotAncestor => AgentSessionHeadTransition.Diverged,
            _ => AgentSessionHeadTransition.Unknown,
        };
    }

    private async Task<AncestryProof> ProveAncestorAsync(
        string repositoryName,
        string targetAncestorSha,
        string descendantSha,
        AncestryMeter meter,
        Dictionary<string, IReadOnlyList<string>> cache,
        CancellationToken cancellationToken)
    {
        if (StringComparer.Ordinal.Equals(targetAncestorSha, descendantSha))
        {
            return AncestryProof.Ancestor;
        }

        var initialPath = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            descendantSha);
        var queue = new Queue<AncestryNode>();
        queue.Enqueue(new AncestryNode(descendantSha, initialPath));
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            while (queue.Count > 0)
            {
                if (cancellationToken.IsCancellationRequested ||
                    timeProvider.GetUtcNow() - meter.StartedAt > MaximumDuration)
                {
                    return AncestryProof.Unknown;
                }

                var current = queue.Dequeue();
                if (!expanded.Add(current.Sha))
                {
                    continue;
                }

                if (!cache.TryGetValue(current.Sha, out var parents))
                {
                    if (!meter.TryStartRequest(current.Sha))
                    {
                        return AncestryProof.Unknown;
                    }

                    var result = await transport.GetCommitObjectAsync(
                            repositoryName,
                            current.Sha,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (result.Failure != ActionHostGitObjectFailure.None ||
                        result.Value is null ||
                        result.CapturedResponseBytes >
                            MaximumCommitResponseBytes ||
                        !meter.TryAddBytes(result.CapturedResponseBytes) ||
                        result.Value.ParentShas is null ||
                        !StringComparer.Ordinal.Equals(
                            result.Value.Sha,
                            current.Sha))
                    {
                        return AncestryProof.Unknown;
                    }

                    parents = result.Value.ParentShas;
                    if (parents.Count > MaximumParentsPerCommit)
                    {
                        return AncestryProof.Unknown;
                    }

                    cache.Add(current.Sha, parents);
                }

                var uniqueParents = new HashSet<string>(StringComparer.Ordinal);
                foreach (var parent in parents)
                {
                    if (!ActionHostGitObjectMapper.IsSha(parent) ||
                        !uniqueParents.Add(parent) ||
                        current.Path.Contains(parent))
                    {
                        return AncestryProof.Unknown;
                    }

                    if (StringComparer.Ordinal.Equals(
                            parent,
                            targetAncestorSha))
                    {
                        return AncestryProof.Ancestor;
                    }

                    queue.Enqueue(new AncestryNode(
                        parent,
                        current.Path.Add(parent)));
                }
            }
        }
        catch (OperationCanceledException)
        {
            return AncestryProof.Unknown;
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidOperationException or
                ArgumentException or
                ObjectDisposedException or
                TimeoutException)
        {
            return AncestryProof.Unknown;
        }

        return AncestryProof.NotAncestor;
    }

    private enum AncestryProof
    {
        Ancestor,
        NotAncestor,
        Unknown,
    }

    private sealed record AncestryNode(
        string Sha,
        ImmutableHashSet<string> Path);

    private sealed class AncestryMeter(DateTimeOffset startedAt)
    {
        private readonly HashSet<string> requested = new(StringComparer.Ordinal);
        private long aggregateResponseBytes;

        internal DateTimeOffset StartedAt { get; } = startedAt;

        internal bool TryStartRequest(string sha) =>
            !requested.Contains(sha) &&
            requested.Count < MaximumCommitRequests &&
            requested.Add(sha);

        internal bool TryAddBytes(int value)
        {
            if (value < 0 ||
                aggregateResponseBytes > MaximumAggregateResponseBytes - value)
            {
                return false;
            }

            aggregateResponseBytes += value;
            return true;
        }
    }
}
