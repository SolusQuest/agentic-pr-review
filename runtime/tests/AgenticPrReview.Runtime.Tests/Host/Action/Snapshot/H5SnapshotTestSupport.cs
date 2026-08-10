using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

internal sealed record H5HeadEntry(
    string Path,
    string Mode,
    ReviewedTreeEntryKind Kind,
    byte[]? Bytes,
    string? ObjectSha = null);

internal static class H5SnapshotTestSupport
{
    internal static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        AuthorizedInvocation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    internal static ActionHostGitHubToken Token()
    {
        Assert.True(ActionHostGitHubToken.TryCreate(
            "h5-token-canary",
            out var token));
        return token!;
    }

    internal static string TemporaryDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "apr-h5-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    internal static async Task<ReviewedTreeSnapshot> TreeAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        string parent,
        params H5HeadEntry[] entries) =>
        await TreeWithBudgetAsync(
            invocation,
            parent,
            ReviewedSnapshotTestAccess.ProductionBudget(),
            entries);

    internal static async Task<ReviewedTreeSnapshot> TreeWithBudgetAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        string parent,
        ReviewedContentBudget budget,
        params H5HeadEntry[] entries)
    {
        var authority = MintAuthority();
        var staging = ReviewedBlobStagingLease.TryCreate(
                authority,
                parent,
                budget) ??
            throw new InvalidOperationException("Could not create H5 staging.");
        var blobs = new Dictionary<string, ReviewedStagedBlob>(
            StringComparer.Ordinal);
        var records = new List<ReviewedTreePathRecord>();
        foreach (var entry in entries)
        {
            var sha = entry.ObjectSha ?? BlobSha(entry.Bytes ?? []);
            ReviewedStagedBlob? blob = null;
            long? size = null;
            if (entry.Kind == ReviewedTreeEntryKind.Regular)
            {
                var bytes = entry.Bytes ?? [];
                size = bytes.LongLength;
                if (!blobs.TryGetValue(sha, out blob))
                {
                    await using var writer = staging.TryCreateWriter(
                        sha,
                        bytes.LongLength);
                    Assert.NotNull(writer);
                    Assert.True(await writer!.WriteAsync(
                        bytes,
                        CancellationToken.None));
                    blob = await writer.CompleteAsync(CancellationToken.None);
                    Assert.NotNull(blob);
                    blobs.Add(sha, blob!);
                }
            }

            records.Add(ReviewedTreePathRecord.Mint(
                authority,
                entry.Path,
                entry.Mode,
                entry.Kind,
                sha,
                size,
                blob));
        }

        return ReviewedTreeSnapshot.Mint(
            authority,
            invocation.PullRequest.RepositoryId,
            invocation.PullRequest.Number,
            invocation.PullRequest.HeadSha,
            new string('9', 40),
            records,
            budget,
            staging);
    }

    internal static ActionHostGitHubPullRequestFact PullRequest(
        ActionHostAuthorizer.FrozenPullRequest frozen,
        string? baseSha = null,
        string? headSha = null,
        string state = "open",
        bool draft = false,
        DateTimeOffset? mergedAt = null,
        long? headRepositoryId = null) => new(
            12345,
            frozen.Number,
            state,
            draft,
            mergedAt,
            baseSha ?? frozen.BaseSha,
            new(
                frozen.BaseRepositoryId,
                frozen.BaseRepositoryName),
            headSha ?? frozen.HeadSha,
            new(
                headRepositoryId ?? frozen.HeadRepositoryId,
                frozen.HeadRepositoryName));

    internal static string BlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static object MintAuthority() =>
        typeof(ReviewedTreeReader).GetField(
            "MintAuthority",
            BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null) ??
        throw new InvalidOperationException("H4 mint authority was not found.");
}

internal sealed class H5ManualTimeProvider : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _timestamp;

    internal void Advance(TimeSpan time) =>
        _timestamp = checked(_timestamp + time.Ticks);
}

internal sealed class H5SteppedTimeProvider(TimeSpan step) : TimeProvider
{
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    internal bool AdvanceOnRead { get; set; }

    public override long GetTimestamp()
    {
        if (AdvanceOnRead)
        {
            _timestamp = checked(_timestamp + step.Ticks);
        }

        return _timestamp;
    }
}
