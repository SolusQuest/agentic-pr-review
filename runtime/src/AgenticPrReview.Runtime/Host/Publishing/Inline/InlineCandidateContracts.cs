using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.Inline;

internal static class InlineCandidateReasonCodes
{
    internal const string NoCurrentRightSideLocation =
        "inline_no_current_right_side_location";
    internal const string CandidateCap = "inline_candidate_cap";

    internal static string FromReason(InlineStickyOnlyReason reason) =>
        reason switch
        {
            InlineStickyOnlyReason.NoCurrentRightSideLocation =>
                NoCurrentRightSideLocation,
            InlineStickyOnlyReason.CandidateCap => CandidateCap,
            _ => throw new ArgumentOutOfRangeException(nameof(reason)),
        };
}

internal enum InlineStickyOnlyReason
{
    NoCurrentRightSideLocation = 1,
    CandidateCap,
}

internal sealed record InlineCandidate(
    R4FindingIdentityV1 FindingIdentity,
    string Path,
    int Line,
    string InlineKey);

internal sealed record InlineStickyOnlyFinding(
    R4FindingIdentityV1 FindingIdentity,
    InlineStickyOnlyReason Reason)
{
    internal string ReasonCode => InlineCandidateReasonCodes.FromReason(Reason);
}

internal sealed record InlineCandidateReasonCounts(
    int NoCurrentRightSideLocation,
    int CandidateCap);

internal sealed class InlineCandidateMap
{
    internal InlineCandidateMap(
        ReviewedIdentity reviewedIdentity,
        string policySha256,
        string diffSha256,
        ImmutableArray<InlineCandidate> candidates,
        ImmutableArray<InlineStickyOnlyFinding> stickyOnlyFindings,
        InlineCandidateReasonCounts reasonCounts)
    {
        ReviewedIdentity = reviewedIdentity;
        PolicySha256 = policySha256;
        DiffSha256 = diffSha256;
        Candidates = candidates;
        StickyOnlyFindings = stickyOnlyFindings;
        ReasonCounts = reasonCounts;
    }

    internal ReviewedIdentity ReviewedIdentity { get; }

    internal string PolicySha256 { get; }

    internal string DiffSha256 { get; }

    internal ImmutableArray<InlineCandidate> Candidates { get; }

    internal ImmutableArray<InlineStickyOnlyFinding> StickyOnlyFindings { get; }

    internal InlineCandidateReasonCounts ReasonCounts { get; }
}

internal sealed class InlineDiffCoordinates
{
    private readonly ImmutableDictionary<string, ImmutableArray<int>>
        _linesByPath;

    private InlineDiffCoordinates(
        ReviewedIdentity reviewedIdentity,
        string diffSha256,
        ImmutableDictionary<string, ImmutableArray<int>> linesByPath)
    {
        ReviewedIdentity = reviewedIdentity;
        DiffSha256 = diffSha256;
        _linesByPath = linesByPath;
    }

    internal ReviewedIdentity ReviewedIdentity { get; }

    internal string DiffSha256 { get; }

    internal static bool TryCreate(
        ReviewedSnapshot? snapshot,
        ReviewedSnapshotIdentities? identities,
        out InlineDiffCoordinates? coordinates)
    {
        coordinates = null;
        if (snapshot is null ||
            identities is null ||
            !IdentityMatches(snapshot.Identity, identities) ||
            !IsLowerHexSha256(identities.DiffSha256))
        {
            return false;
        }

        var linesByPath = ImmutableDictionary.CreateBuilder<
            string,
            ImmutableArray<int>>(StringComparer.Ordinal);
        foreach (var pair in snapshot.DiffByChangedPath)
        {
            var source = pair.Value;
            if (source is null ||
                source.ReviewedIdentity != snapshot.Identity ||
                !StringComparer.Ordinal.Equals(pair.Key, source.Path))
            {
                return false;
            }

            var lines = ImmutableArray.CreateBuilder<int>();
            var previousLine = 0;
            foreach (var hunk in source.Hunks)
            {
                foreach (var line in hunk.Lines)
                {
                    if (line.Kind is "context" or "addition")
                    {
                        if (line.NewLine is not int currentLine ||
                            currentLine <= previousLine)
                        {
                            return false;
                        }

                        lines.Add(currentLine);
                        previousLine = currentLine;
                    }
                    else if (line.Kind is not "deletion" and not "no_newline")
                    {
                        return false;
                    }
                }
            }

            if (!linesByPath.TryAdd(source.Path, lines.ToImmutable()))
            {
                return false;
            }
        }

        coordinates = new InlineDiffCoordinates(
            snapshot.Identity,
            identities.DiffSha256,
            linesByPath.ToImmutable());
        return true;
    }

    internal bool TryFindFirst(
        string path,
        int startLine,
        int endLine,
        out int selectedLine)
    {
        selectedLine = 0;
        if (!_linesByPath.TryGetValue(path, out var lines))
        {
            return false;
        }

        var low = 0;
        var high = lines.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (lines[middle] < startLine)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        if (low >= lines.Length || lines[low] > endLine)
        {
            return false;
        }

        selectedLine = lines[low];
        return true;
    }

    private static bool IdentityMatches(
        ReviewedIdentity identity,
        ReviewedSnapshotIdentities identities) =>
        identity.IsValid() &&
        identities.RepositoryId > 0 &&
        long.TryParse(
            identity.RepositoryId,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var repositoryId) &&
        repositoryId == identities.RepositoryId &&
        identity.ReviewTarget == identities.PullRequestNumber &&
        StringComparer.Ordinal.Equals(identity.BaseSha, identities.BaseSha) &&
        StringComparer.Ordinal.Equals(identity.HeadSha, identities.HeadSha);

    private static bool IsLowerHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9') &&
                !(character is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
