using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;

namespace AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;

internal enum ReviewedUnavailableReason
{
    None = 0,
    Binary,
    NonText,
    NonRegular,
    Missing,
    LineTooLong,
    PatchContradiction,
}

internal sealed record ReviewedBuiltChange(
    ReviewedChangedFile Change,
    ReviewedDiffSource? Source,
    ReviewedUnavailableReason UnavailableReason);

internal sealed class ReviewedDiffIdentity
{
    internal ReviewedDiffIdentity(
        string sha256,
        ImmutableArray<byte> canonicalPreimage)
    {
        Sha256 = sha256;
        CanonicalPreimage = canonicalPreimage;
    }

    internal string Sha256 { get; }
    internal ImmutableArray<byte> CanonicalPreimage { get; }
}

internal sealed class ReviewedDiffBuildSet
{
    internal ReviewedDiffBuildSet(
        IEnumerable<ReviewedBuiltChange> changes,
        ReviewedChangedFileIdentity changedFileIdentity,
        ReviewedDiffIdentity identity)
    {
        Changes = changes
            .OrderBy(static change => change.Change.Path, StringComparer.Ordinal)
            .ToImmutableArray();
        ChangedFileIdentity = changedFileIdentity;
        Identity = identity;
    }

    internal ImmutableArray<ReviewedBuiltChange> Changes { get; }
    internal ReviewedChangedFileIdentity ChangedFileIdentity { get; }
    internal ReviewedDiffIdentity Identity { get; }
}

internal sealed class ReviewedExactDiffBuilder
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly ReviewedContentBudget _budget;

    internal ReviewedExactDiffBuilder(ReviewedContentBudget budget)
    {
        _budget = budget ?? throw new ArgumentNullException(nameof(budget));
    }

    internal async Task<ReviewedSnapshotReadResult<ReviewedDiffBuildSet>>
        BuildAsync(
            ReviewedIdentity reviewedIdentity,
            ReviewedChangedFileSet changedFiles,
            ReviewedTreeSnapshot tree,
            ReviewedBaseObjectResolver baseResolver,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reviewedIdentity);
        ArgumentNullException.ThrowIfNull(changedFiles);
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(baseResolver);
        var headByPath = tree.Records.ToDictionary(
            static record => record.Path,
            StringComparer.Ordinal);
        var built = ImmutableArray.CreateBuilder<ReviewedBuiltChange>(
            changedFiles.Files.Length);
        long aggregateSourceBytes = 0;
        foreach (var file in changedFiles.Files)
        {
            if (!_budget.TryContinue(cancellationToken))
            {
                return Unsupported();
            }

            ReviewedBaseOperand? baseOperand = null;
            if (file.Status != "added")
            {
                var basePath = file.Status is "renamed" or "copied"
                    ? file.PreviousPath!
                    : file.Path;
                var resolved = await baseResolver.ResolveAsync(
                    basePath,
                    cancellationToken);
                if (resolved.Value is null)
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(resolved.Failure);
                }

                baseOperand = resolved.Value;
                if (file.Status == "removed" &&
                    baseOperand.Sha is not null &&
                    !StringComparer.Ordinal.Equals(file.Sha, baseOperand.Sha))
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(ReviewedSnapshotReadFailure.IdentityMismatch);
                }
            }

            headByPath.TryGetValue(file.Path, out var headRecord);
            var unavailable = ClassifyNonRegular(
                file,
                headRecord,
                baseOperand);
            if (unavailable != ReviewedUnavailableReason.None)
            {
                built.Add(Unavailable(file, unavailable));
                continue;
            }

            byte[] baseBytes;
            if (file.Status == "added")
            {
                baseBytes = [];
            }
            else
            {
                var verifiedBase = await baseOperand!.Blob!
                    .ReadVerifiedDetailedAsync(cancellationToken);
                if (verifiedBase.Bytes is null)
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(Map(verifiedBase.Failure));
                }

                baseBytes = verifiedBase.Bytes;
                if (baseBytes.LongLength != baseOperand.Size)
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(ReviewedSnapshotReadFailure.IdentityMismatch);
                }
            }

            byte[] headBytes;
            if (file.Status == "removed")
            {
                headBytes = [];
            }
            else
            {
                using var destination = new MemoryStream(
                    checked((int)headRecord!.Size!.Value));
                var copied = await headRecord.StagedBlob!
                    .CopyVerifiedDetailedAsync(destination, cancellationToken);
                if (copied != ReviewedStagedBlobCopyFailure.None)
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(Map(copied));
                }

                headBytes = destination.ToArray();
            }

            var baseText = Decode(baseBytes, cancellationToken);
            var headText = Decode(headBytes, cancellationToken);
            if (baseText.Status == TextStatus.Unsupported ||
                headText.Status == TextStatus.Unsupported)
            {
                return Unsupported();
            }

            if (baseText.Status == TextStatus.Binary ||
                headText.Status == TextStatus.Binary)
            {
                built.Add(Unavailable(file, ReviewedUnavailableReason.Binary));
                continue;
            }

            if (baseText.Status == TextStatus.Unavailable ||
                headText.Status == TextStatus.Unavailable)
            {
                built.Add(Unavailable(file, ReviewedUnavailableReason.NonText));
                continue;
            }

            if (baseText.Status == TextStatus.LineTooLong ||
                headText.Status == TextStatus.LineTooLong)
            {
                built.Add(Unavailable(file, ReviewedUnavailableReason.LineTooLong));
                continue;
            }

            if (EvaluatePatch(
                    file,
                    baseText.Lines,
                    headText.Lines,
                    cancellationToken) == PatchEvidence.Contradictory)
            {
                built.Add(Unavailable(
                    file,
                    ReviewedUnavailableReason.PatchContradiction));
                continue;
            }

            if (!TryBuildOperations(
                    baseText.Lines,
                    headText.Lines,
                    cancellationToken,
                    out var operations) ||
                !TryBuildHunks(
                    operations,
                    baseText.Lines.Length,
                    headText.Lines.Length,
                    cancellationToken,
                    out var hunks))
            {
                return Unsupported();
            }

            if (!_budget.TryContinue(cancellationToken))
            {
                return Unsupported();
            }

            ReviewedDiffSource source;
            try
            {
                source = new ReviewedDiffSource(
                    reviewedIdentity,
                    file.Path,
                    file.PreviousPath,
                    file.Status,
                    sourceTruncated: false,
                    hunks);
            }
            catch (ArgumentException)
            {
                return Unsupported();
            }

            aggregateSourceBytes = checked(
                aggregateSourceBytes + source.CanonicalBytes.Length);
            if (aggregateSourceBytes > AgentLimits.DiffSnapshotBytes)
            {
                return Unsupported();
            }

            var changes = checked(
                source.RepresentedAdditions + source.RepresentedDeletions);
            built.Add(new(
                new ReviewedChangedFile(
                    file.Path,
                    file.PreviousPath,
                    file.Status,
                    source.RepresentedAdditions,
                    source.RepresentedDeletions,
                    changes,
                    "available",
                    source.PatchSha256,
                    false),
                source,
                ReviewedUnavailableReason.None));
        }

        if (!_budget.TryContinue(cancellationToken))
        {
            return Unsupported();
        }

        var completed = built.ToImmutable();
        return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>.Success(
            new(
                completed,
                ReviewedFinalChangedFileIdentityWriter.Write(
                    changedFiles.Identity,
                    completed),
                ReviewedDiffIdentityWriter.Write(completed)));
    }

    private static ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
        Unsupported() => ReviewedSnapshotReadResult<ReviewedDiffBuildSet>.Failed(
            ReviewedSnapshotReadFailure.UnsupportedSize);

    private static ReviewedSnapshotReadFailure Map(
        ReviewedStagedBlobCopyFailure failure) => failure switch
        {
            ReviewedStagedBlobCopyFailure.IdentityMismatch =>
                ReviewedSnapshotReadFailure.IdentityMismatch,
            ReviewedStagedBlobCopyFailure.UnsupportedSize =>
                ReviewedSnapshotReadFailure.UnsupportedSize,
            _ => ReviewedSnapshotReadFailure.StagingFailure,
        };

    private static ReviewedUnavailableReason ClassifyNonRegular(
        ReviewedPullRequestFileFact file,
        ReviewedTreePathRecord? head,
        ReviewedBaseOperand? historical)
    {
        if (file.Status != "removed" && head?.Kind is not
                ReviewedTreeEntryKind.Regular)
        {
            return ReviewedUnavailableReason.NonRegular;
        }

        if (file.Status != "added" && historical?.Kind is
                ReviewedBaseOperandKind.Symlink or
                ReviewedBaseOperandKind.Submodule)
        {
            return ReviewedUnavailableReason.NonRegular;
        }

        if (file.Status != "added" && historical?.Kind is
                ReviewedBaseOperandKind.Missing)
        {
            return ReviewedUnavailableReason.Missing;
        }

        return ReviewedUnavailableReason.None;
    }

    private static ReviewedBuiltChange Unavailable(
        ReviewedPullRequestFileFact file,
        ReviewedUnavailableReason reason)
    {
        var patchStatus = reason == ReviewedUnavailableReason.Binary
            ? "binary"
            : "unavailable";
        return new(
            new ReviewedChangedFile(
                file.Path,
                file.PreviousPath,
                file.Status,
                file.Additions,
                file.Deletions,
                file.Changes,
                patchStatus,
                null,
                false),
            null,
            reason);
    }

    private DecodedText Decode(
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (!_budget.TryContinue(cancellationToken))
        {
            return new(TextStatus.Unsupported, []);
        }

        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return new(TextStatus.Binary, []);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new(TextStatus.Unavailable, []);
        }

        if (bytes.AsSpan().StartsWith("\uFEFF"u8) &&
            text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        for (var index = 0; index < text.Length; index++)
        {
            if ((index & 1023) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return new(TextStatus.Unsupported, []);
            }

            if (text[index] == '\r' &&
                (index + 1 >= text.Length || text[index + 1] != '\n'))
            {
                return new(TextStatus.Unavailable, []);
            }
        }

        text = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (text.Length == 0)
        {
            return new(TextStatus.Text, []);
        }

        var terminated = text.EndsWith('\n');
        var values = text.Split('\n');
        var count = terminated ? values.Length - 1 : values.Length;
        var lines = ImmutableArray.CreateBuilder<TextLine>(count);
        for (var index = 0; index < count; index++)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return new(TextStatus.Unsupported, []);
            }

            try
            {
                if (StrictUtf8.GetByteCount(values[index]) >
                    AgentLimits.DiffLineTextBytes)
                {
                    return new(TextStatus.LineTooLong, []);
                }
            }
            catch (EncoderFallbackException)
            {
                return new(TextStatus.Unavailable, []);
            }

            lines.Add(new(
                values[index],
                index < count - 1 || terminated));
        }

        return new(TextStatus.Text, lines.ToImmutable());
    }

    private bool TryBuildOperations(
        ImmutableArray<TextLine> oldLines,
        ImmutableArray<TextLine> newLines,
        CancellationToken cancellationToken,
        out ImmutableArray<DiffOperation> operations)
    {
        var builder = ImmutableArray.CreateBuilder<DiffOperation>();
        if (!DiffRange(
                oldLines,
                0,
                oldLines.Length,
                newLines,
                0,
                newLines.Length,
                0,
                builder,
                cancellationToken))
        {
            operations = default;
            return false;
        }

        return TryNumber(
            builder.ToImmutable(),
            cancellationToken,
            out operations);
    }

    private bool DiffRange(
        ImmutableArray<TextLine> oldLines,
        int oldStart,
        int oldEnd,
        ImmutableArray<TextLine> newLines,
        int newStart,
        int newEnd,
        int depth,
        ImmutableArray<DiffOperation>.Builder output,
        CancellationToken cancellationToken)
    {
        if (!_budget.TryContinue(cancellationToken))
        {
            return false;
        }

        while (oldStart < oldEnd && newStart < newEnd &&
            oldLines[oldStart] == newLines[newStart])
        {
            if ((oldStart & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return false;
            }

            output.Add(DiffOperation.Equal(oldLines[oldStart]));
            oldStart++;
            newStart++;
        }

        var suffix = 0;
        while (oldStart < oldEnd - suffix &&
            newStart < newEnd - suffix &&
            oldLines[oldEnd - suffix - 1] == newLines[newEnd - suffix - 1])
        {
            suffix++;
            if ((suffix & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return false;
            }
        }

        oldEnd -= suffix;
        newEnd -= suffix;
        if (oldStart == oldEnd || newStart == newEnd || depth >= 64)
        {
            for (var index = oldStart; index < oldEnd; index++)
            {
                if ((index & 255) == 0 &&
                    !_budget.TryContinue(cancellationToken))
                {
                    return false;
                }

                output.Add(DiffOperation.Delete(oldLines[index]));
            }

            for (var index = newStart; index < newEnd; index++)
            {
                if ((index & 255) == 0 &&
                    !_budget.TryContinue(cancellationToken))
                {
                    return false;
                }

                output.Add(DiffOperation.Add(newLines[index]));
            }
        }
        else
        {
            if (!TryUniqueAnchors(
                    oldLines,
                    oldStart,
                    oldEnd,
                    newLines,
                    newStart,
                    newEnd,
                    cancellationToken,
                    out var anchors))
            {
                return false;
            }

            if (anchors.Length == 0)
            {
                for (var index = oldStart; index < oldEnd; index++)
                {
                    if ((index & 255) == 0 &&
                        !_budget.TryContinue(cancellationToken))
                    {
                        return false;
                    }

                    output.Add(DiffOperation.Delete(oldLines[index]));
                }

                for (var index = newStart; index < newEnd; index++)
                {
                    if ((index & 255) == 0 &&
                        !_budget.TryContinue(cancellationToken))
                    {
                        return false;
                    }

                    output.Add(DiffOperation.Add(newLines[index]));
                }
            }
            else
            {
                var previousOld = oldStart;
                var previousNew = newStart;
                foreach (var anchor in anchors)
                {
                    if (!DiffRange(
                            oldLines,
                            previousOld,
                            anchor.Old,
                            newLines,
                            previousNew,
                            anchor.New,
                            depth + 1,
                            output,
                            cancellationToken))
                    {
                        return false;
                    }

                    output.Add(DiffOperation.Equal(oldLines[anchor.Old]));
                    previousOld = anchor.Old + 1;
                    previousNew = anchor.New + 1;
                }

                if (!DiffRange(
                        oldLines,
                        previousOld,
                        oldEnd,
                        newLines,
                        previousNew,
                        newEnd,
                        depth + 1,
                        output,
                        cancellationToken))
                {
                    return false;
                }
            }
        }

        for (var index = suffix; index > 0; index--)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return false;
            }

            output.Add(DiffOperation.Equal(oldLines[oldEnd + index - 1]));
        }

        return true;
    }

    private bool TryUniqueAnchors(
        ImmutableArray<TextLine> oldLines,
        int oldStart,
        int oldEnd,
        ImmutableArray<TextLine> newLines,
        int newStart,
        int newEnd,
        CancellationToken cancellationToken,
        out ImmutableArray<Anchor> anchors)
    {
        if (!TryCount(
                oldLines,
                oldStart,
                oldEnd,
                cancellationToken,
                out var oldCounts) ||
            !TryCount(
                newLines,
                newStart,
                newEnd,
                cancellationToken,
                out var newCounts))
        {
            anchors = default;
            return false;
        }

        var candidateBuilder = new List<Anchor>();
        var candidateIndex = 0;
        foreach (var pair in oldCounts)
        {
            if ((candidateIndex++ & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                anchors = default;
                return false;
            }

            if (pair.Value.Count == 1 &&
                newCounts.TryGetValue(pair.Key, out var other) &&
                other.Count == 1)
            {
                candidateBuilder.Add(new Anchor(
                    pair.Value.Index,
                    other.Index));
            }
        }

        candidateBuilder.Sort(static (left, right) =>
            left.Old.CompareTo(right.Old));
        if (!_budget.TryContinue(cancellationToken))
        {
            anchors = default;
            return false;
        }

        var candidates = candidateBuilder.ToArray();
        if (candidates.Length == 0)
        {
            anchors = [];
            return true;
        }

        var tails = new int[candidates.Length];
        var tailIndices = new int[candidates.Length];
        var previous = Enumerable.Repeat(-1, candidates.Length).ToArray();
        var length = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                anchors = default;
                return false;
            }

            var value = candidates[index].New;
            var low = 0;
            var high = length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (tails[middle] < value)
                {
                    low = middle + 1;
                }
                else
                {
                    high = middle;
                }
            }

            tails[low] = value;
            previous[index] = low == 0 ? -1 : tailIndices[low - 1];
            tailIndices[low] = index;
            if (low == length)
            {
                length++;
            }
        }

        var result = new Anchor[length];
        var current = tailIndices[length - 1];
        for (var index = length - 1; index >= 0; index--)
        {
            result[index] = candidates[current];
            current = previous[current];
        }

        anchors = ImmutableArray.Create(result);
        return true;
    }

    private bool TryCount(
        ImmutableArray<TextLine> lines,
        int start,
        int end,
        CancellationToken cancellationToken,
        out Dictionary<TextLine, LineOccurrence> result)
    {
        result = new Dictionary<TextLine, LineOccurrence>();
        for (var index = start; index < end; index++)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return false;
            }

            result[lines[index]] = result.TryGetValue(lines[index], out var value)
                ? new(value.Index, value.Count + 1)
                : new(index, 1);
        }

        return true;
    }

    private bool TryNumber(
        ImmutableArray<DiffOperation> operations,
        CancellationToken cancellationToken,
        out ImmutableArray<DiffOperation> numbered)
    {
        var oldLine = 1;
        var newLine = 1;
        var builder = ImmutableArray.CreateBuilder<DiffOperation>(
            operations.Length);
        for (var index = 0; index < operations.Length; index++)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                numbered = default;
                return false;
            }

            var operation = operations[index];
            builder.Add(operation with
            {
                OldPosition = oldLine,
                NewPosition = newLine,
            });
            if (operation.Kind is DiffKind.Equal or DiffKind.Delete)
            {
                oldLine++;
            }

            if (operation.Kind is DiffKind.Equal or DiffKind.Add)
            {
                newLine++;
            }
        }

        numbered = builder.ToImmutable();
        return true;
    }

    private bool TryBuildHunks(
        ImmutableArray<DiffOperation> operations,
        int oldLineCount,
        int newLineCount,
        CancellationToken cancellationToken,
        out ImmutableArray<ReviewedDiffHunk> hunks)
    {
        var builder = ImmutableArray.CreateBuilder<ReviewedDiffHunk>();
        var cursor = 0;
        while (cursor < operations.Length)
        {
            if (!_budget.TryContinue(cancellationToken))
            {
                hunks = default;
                return false;
            }

            var first = FindChange(operations, cursor, cancellationToken);
            if (first == -2)
            {
                hunks = default;
                return false;
            }

            if (first < 0)
            {
                break;
            }

            var start = first;
            var context = 0;
            while (start > 0 &&
                operations[start - 1].Kind == DiffKind.Equal &&
                context < 3)
            {
                start--;
                context++;
            }

            var last = first;
            var scan = first + 1;
            while (scan < operations.Length)
            {
                var next = FindChange(operations, scan, cancellationToken);
                if (next == -2)
                {
                    hunks = default;
                    return false;
                }

                if (next < 0)
                {
                    break;
                }

                var equalGap = 0;
                for (var index = last + 1; index < next; index++)
                {
                    if ((index & 255) == 0 &&
                        !_budget.TryContinue(cancellationToken))
                    {
                        hunks = default;
                        return false;
                    }

                    if (operations[index].Kind == DiffKind.Equal)
                    {
                        equalGap++;
                    }
                }

                if (equalGap > 6)
                {
                    break;
                }

                last = next;
                scan = next + 1;
            }

            var end = last + 1;
            context = 0;
            while (end < operations.Length &&
                operations[end].Kind == DiffKind.Equal &&
                context < 3)
            {
                end++;
                context++;
            }

            var chunkStart = start;
            while (chunkStart < end)
            {
                var chunkEnd = chunkStart;
                var records = 0;
                while (chunkEnd < end)
                {
                    if ((chunkEnd & 255) == 0 &&
                        !_budget.TryContinue(cancellationToken))
                    {
                        hunks = default;
                        return false;
                    }

                    var required = operations[chunkEnd].Line.Terminated ? 1 : 2;
                    if (records != 0 &&
                        records > AgentLimits.DiffLinesPerHunk - required)
                    {
                        break;
                    }

                    records += required;
                    chunkEnd++;
                }

                if (!TryCreateHunk(
                        operations,
                        chunkStart,
                        chunkEnd,
                        oldLineCount,
                        newLineCount,
                        cancellationToken,
                        out var hunk))
                {
                    hunks = default;
                    return false;
                }

                builder.Add(hunk!);
                if (builder.Count > AgentLimits.DiffHunksPerFile)
                {
                    hunks = default;
                    return false;
                }

                chunkStart = chunkEnd;
            }

            cursor = end;
        }

        hunks = builder.ToImmutable();
        return true;
    }

    private int FindChange(
        ImmutableArray<DiffOperation> operations,
        int start,
        CancellationToken cancellationToken)
    {
        for (var index = start; index < operations.Length; index++)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return -2;
            }

            if (operations[index].Kind != DiffKind.Equal)
            {
                return index;
            }
        }

        return -1;
    }

    private bool TryCreateHunk(
        ImmutableArray<DiffOperation> operations,
        int start,
        int end,
        int oldLineCount,
        int newLineCount,
        CancellationToken cancellationToken,
        out ReviewedDiffHunk? hunk)
    {
        hunk = null;
        var lines = ImmutableArray.CreateBuilder<ReviewedDiffLine>();
        var oldCount = 0;
        var newCount = 0;
        for (var index = start; index < end; index++)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return false;
            }

            var operation = operations[index];
            switch (operation.Kind)
            {
                case DiffKind.Equal:
                    lines.Add(new(
                        "context",
                        operation.OldPosition,
                        operation.NewPosition,
                        operation.Line.Text));
                    oldCount++;
                    newCount++;
                    break;
                case DiffKind.Delete:
                    lines.Add(new(
                        "deletion",
                        operation.OldPosition,
                        null,
                        operation.Line.Text));
                    oldCount++;
                    break;
                case DiffKind.Add:
                    lines.Add(new(
                        "addition",
                        null,
                        operation.NewPosition,
                        operation.Line.Text));
                    newCount++;
                    break;
            }

            if (!operation.Line.Terminated)
            {
                lines.Add(new("no_newline", null, null, string.Empty));
            }
        }

        if (lines.Count > AgentLimits.DiffLinesPerHunk)
        {
            return false;
        }

        var first = operations[start];
        var oldStart = oldLineCount == 0 ? 0 : first.OldPosition;
        var newStart = newLineCount == 0 ? 0 : first.NewPosition;
        try
        {
            hunk = new ReviewedDiffHunk(
                oldStart,
                oldCount,
                newStart,
                newCount,
                lines);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private PatchEvidence EvaluatePatch(
        ReviewedPullRequestFileFact file,
        ImmutableArray<TextLine> oldLines,
        ImmutableArray<TextLine> newLines,
        CancellationToken cancellationToken)
    {
        if (file.Patch is null || file.PatchIncomplete)
        {
            return PatchEvidence.NotAvailableOrIncomplete;
        }

        var patch = file.Patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (patch.Contains('\r'))
        {
            return PatchEvidence.NotAvailableOrIncomplete;
        }

        var lines = patch.Split('\n');
        var sawHunk = false;
        var consistent = true;
        var previousOldEnd = 0;
        var previousNewEnd = 0;
        var index = 0;
        while (index < lines.Length)
        {
            if ((index & 255) == 0 &&
                !_budget.TryContinue(cancellationToken))
            {
                return PatchEvidence.NotAvailableOrIncomplete;
            }

            if (index == lines.Length - 1 && lines[index].Length == 0)
            {
                index++;
                continue;
            }

            if (!TryParseHunkHeader(
                    lines[index],
                    out var oldStart,
                    out var oldCount,
                    out var newStart,
                    out var newCount) ||
                oldStart < previousOldEnd ||
                newStart < previousNewEnd)
            {
                return PatchEvidence.NotAvailableOrIncomplete;
            }

            sawHunk = true;
            index++;
            var oldConsumed = 0;
            var newConsumed = 0;
            var lastWasContent = false;
            while (index < lines.Length &&
                !lines[index].StartsWith("@@", StringComparison.Ordinal))
            {
                if ((index & 255) == 0 &&
                    !_budget.TryContinue(cancellationToken))
                {
                    return PatchEvidence.NotAvailableOrIncomplete;
                }

                var line = lines[index];
                if (index == lines.Length - 1 && line.Length == 0)
                {
                    index++;
                    break;
                }

                if (StringComparer.Ordinal.Equals(
                        line,
                        "\\ No newline at end of file"))
                {
                    if (!lastWasContent)
                    {
                        return PatchEvidence.NotAvailableOrIncomplete;
                    }

                    lastWasContent = false;
                    index++;
                    continue;
                }

                if (line.Length == 0)
                {
                    return PatchEvidence.NotAvailableOrIncomplete;
                }

                var text = line[1..];
                switch (line[0])
                {
                    case ' ':
                        oldConsumed++;
                        newConsumed++;
                        consistent &= MatchesPatchLine(
                            oldLines,
                            oldStart + oldConsumed - 1,
                            text);
                        consistent &= MatchesPatchLine(
                            newLines,
                            newStart + newConsumed - 1,
                            text);
                        break;
                    case '-':
                        oldConsumed++;
                        consistent &= MatchesPatchLine(
                            oldLines,
                            oldStart + oldConsumed - 1,
                            text);
                        break;
                    case '+':
                        newConsumed++;
                        consistent &= MatchesPatchLine(
                            newLines,
                            newStart + newConsumed - 1,
                            text);
                        break;
                    default:
                        return PatchEvidence.NotAvailableOrIncomplete;
                }

                if (oldConsumed > oldCount || newConsumed > newCount)
                {
                    return PatchEvidence.NotAvailableOrIncomplete;
                }

                lastWasContent = true;
                index++;
            }

            if (oldConsumed != oldCount || newConsumed != newCount)
            {
                return PatchEvidence.NotAvailableOrIncomplete;
            }

            previousOldEnd = checked(oldStart + oldCount);
            previousNewEnd = checked(newStart + newCount);
        }

        if (!sawHunk)
        {
            return PatchEvidence.NotAvailableOrIncomplete;
        }

        return consistent
            ? PatchEvidence.Consistent
            : PatchEvidence.Contradictory;
    }

    private static bool TryParseHunkHeader(
        string line,
        out int oldStart,
        out int oldCount,
        out int newStart,
        out int newCount)
    {
        oldStart = 0;
        oldCount = 0;
        newStart = 0;
        newCount = 0;
        if (!line.StartsWith("@@ -", StringComparison.Ordinal))
        {
            return false;
        }

        var closing = line.IndexOf("@@", 3, StringComparison.Ordinal);
        if (closing < 0)
        {
            return false;
        }

        var header = line.AsSpan(3, closing - 3);
        var separator = header.IndexOf(" +".AsSpan());
        if (separator <= 0)
        {
            return false;
        }

        var oldRange = header[1..separator];
        var newRange = header[(separator + 2)..].Trim();
        var trailing = newRange.IndexOf(' ');
        if (trailing >= 0)
        {
            newRange = newRange[..trailing];
        }

        return TryRange(oldRange, out oldStart, out oldCount) &&
            TryRange(newRange, out newStart, out newCount);
    }

    private static bool TryRange(
        ReadOnlySpan<char> value,
        out int start,
        out int count)
    {
        start = 0;
        count = 1;
        value = value.Trim();
        var comma = value.IndexOf(',');
        var startValue = comma >= 0 ? value[..comma] : value;
        if (!int.TryParse(
                startValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out start) ||
            start < 0)
        {
            return false;
        }

        if (comma < 0)
        {
            return start < int.MaxValue;
        }

        return int.TryParse(
                value[(comma + 1)..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out count) &&
            count >= 0 &&
            start <= int.MaxValue - count;
    }

    private static bool MatchesPatchLine(
        ImmutableArray<TextLine> lines,
        int oneBased,
        string text)
    {
        if (oneBased <= 0 || oneBased > lines.Length)
        {
            return false;
        }

        if (oneBased == 1 && text.StartsWith('\uFEFF'))
        {
            text = text[1..];
        }

        return StringComparer.Ordinal.Equals(
            lines[oneBased - 1].Text,
            text);
    }

    private enum TextStatus
    {
        Text = 1,
        Binary,
        Unavailable,
        LineTooLong,
        Unsupported,
    }

    private enum PatchEvidence
    {
        NotAvailableOrIncomplete = 0,
        Consistent,
        Contradictory,
    }

    private enum DiffKind
    {
        Equal = 1,
        Delete,
        Add,
    }

    private readonly record struct TextLine(string Text, bool Terminated);
    private readonly record struct DecodedText(
        TextStatus Status,
        ImmutableArray<TextLine> Lines);
    private readonly record struct LineOccurrence(int Index, int Count);
    private readonly record struct Anchor(int Old, int New);
    private sealed record DiffOperation(
        DiffKind Kind,
        TextLine Line,
        int OldPosition,
        int NewPosition)
    {
        internal static DiffOperation Equal(TextLine line) =>
            new(DiffKind.Equal, line, 0, 0);
        internal static DiffOperation Delete(TextLine line) =>
            new(DiffKind.Delete, line, 0, 0);
        internal static DiffOperation Add(TextLine line) =>
            new(DiffKind.Add, line, 0, 0);
    }
}

internal static class ReviewedFinalChangedFileIdentityWriter
{
    private static readonly byte[] Domain =
        "agentic-pr-review.final-changed-files.v1"u8.ToArray();

    internal static ReviewedChangedFileIdentity Write(
        ReviewedChangedFileIdentity endpointIdentity,
        ImmutableArray<ReviewedBuiltChange> changes)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, Domain);
        WriteFrame(stream, endpointIdentity.CanonicalPreimage.AsSpan());
        WriteInt32(stream, changes.Length);
        foreach (var change in changes.OrderBy(
                     static change => change.Change.Path,
                     StringComparer.Ordinal))
        {
            WriteFrame(stream, ReviewedChangedFileWriter.Write(change.Change));
            stream.WriteByte((byte)change.UnavailableReason);
        }

        var preimage = stream.ToArray();
        return new ReviewedChangedFileIdentity(
            Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant(),
            ImmutableArray.CreateRange(preimage));
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}

internal static class ReviewedDiffIdentityWriter
{
    private static readonly byte[] Domain =
        "agentic-pr-review.reviewed-diff.v1"u8.ToArray();

    internal static ReviewedDiffIdentity Write(
        ImmutableArray<ReviewedBuiltChange> changes)
    {
        using var stream = new MemoryStream();
        WriteFrame(stream, Domain);
        WriteInt32(stream, changes.Length);
        foreach (var change in changes.OrderBy(
                     static change => change.Change.Path,
                     StringComparer.Ordinal))
        {
            WriteFrame(stream, ReviewedChangedFileWriter.Write(change.Change));
            stream.WriteByte((byte)change.UnavailableReason);
            if (change.Source is null)
            {
                WriteInt32(stream, -1);
            }
            else
            {
                WriteFrame(stream, change.Source.CanonicalBytes.AsSpan());
            }
        }

        var preimage = stream.ToArray();
        return new(
            Convert.ToHexString(SHA256.HashData(preimage)).ToLowerInvariant(),
            ImmutableArray.CreateRange(preimage));
    }

    private static void WriteFrame(Stream stream, ReadOnlySpan<byte> value)
    {
        WriteInt32(stream, value.Length);
        stream.Write(value);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
