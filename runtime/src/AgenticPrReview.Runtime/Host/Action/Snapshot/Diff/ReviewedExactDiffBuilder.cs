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
                var verifiedBaseBytes = await baseOperand!.Blob!.ReadVerifiedAsync(
                    cancellationToken);
                if (verifiedBaseBytes is null)
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(ReviewedSnapshotReadFailure.IdentityMismatch);
                }

                baseBytes = verifiedBaseBytes;
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
                if (!await headRecord.StagedBlob!.CopyVerifiedToAsync(
                        destination,
                        cancellationToken))
                {
                    return ReviewedSnapshotReadResult<ReviewedDiffBuildSet>
                        .Failed(ReviewedSnapshotReadFailure.IdentityMismatch);
                }

                headBytes = destination.ToArray();
            }

            var baseText = Decode(baseBytes);
            var headText = Decode(headBytes);
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

            if (!PatchCorroborates(
                    file,
                    baseText.Lines,
                    headText.Lines))
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
                    out var hunks))
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

    private static DecodedText Decode(byte[] bytes)
    {
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

        operations = Number(builder.ToImmutable());
        return true;
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
        }

        oldEnd -= suffix;
        newEnd -= suffix;
        if (oldStart == oldEnd || newStart == newEnd || depth >= 64)
        {
            for (var index = oldStart; index < oldEnd; index++)
            {
                output.Add(DiffOperation.Delete(oldLines[index]));
            }

            for (var index = newStart; index < newEnd; index++)
            {
                output.Add(DiffOperation.Add(newLines[index]));
            }
        }
        else
        {
            var anchors = UniqueAnchors(
                oldLines,
                oldStart,
                oldEnd,
                newLines,
                newStart,
                newEnd);
            if (anchors.Length == 0)
            {
                for (var index = oldStart; index < oldEnd; index++)
                {
                    output.Add(DiffOperation.Delete(oldLines[index]));
                }

                for (var index = newStart; index < newEnd; index++)
                {
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
            output.Add(DiffOperation.Equal(oldLines[oldEnd + index - 1]));
        }

        return true;
    }

    private static ImmutableArray<Anchor> UniqueAnchors(
        ImmutableArray<TextLine> oldLines,
        int oldStart,
        int oldEnd,
        ImmutableArray<TextLine> newLines,
        int newStart,
        int newEnd)
    {
        var oldCounts = Count(oldLines, oldStart, oldEnd);
        var newCounts = Count(newLines, newStart, newEnd);
        var candidates = oldCounts
            .Where(pair => pair.Value.Count == 1 &&
                newCounts.TryGetValue(pair.Key, out var other) &&
                other.Count == 1)
            .Select(pair => new Anchor(
                pair.Value.Index,
                newCounts[pair.Key].Index))
            .OrderBy(static anchor => anchor.Old)
            .ToArray();
        if (candidates.Length == 0)
        {
            return [];
        }

        var tails = new int[candidates.Length];
        var tailIndices = new int[candidates.Length];
        var previous = Enumerable.Repeat(-1, candidates.Length).ToArray();
        var length = 0;
        for (var index = 0; index < candidates.Length; index++)
        {
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

        return ImmutableArray.Create(result);
    }

    private static Dictionary<TextLine, LineOccurrence> Count(
        ImmutableArray<TextLine> lines,
        int start,
        int end)
    {
        var result = new Dictionary<TextLine, LineOccurrence>();
        for (var index = start; index < end; index++)
        {
            result[lines[index]] = result.TryGetValue(lines[index], out var value)
                ? new(value.Index, value.Count + 1)
                : new(index, 1);
        }

        return result;
    }

    private static ImmutableArray<DiffOperation> Number(
        ImmutableArray<DiffOperation> operations)
    {
        var oldLine = 1;
        var newLine = 1;
        var builder = ImmutableArray.CreateBuilder<DiffOperation>(
            operations.Length);
        foreach (var operation in operations)
        {
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

        return builder.ToImmutable();
    }

    private static bool TryBuildHunks(
        ImmutableArray<DiffOperation> operations,
        int oldLineCount,
        int newLineCount,
        out ImmutableArray<ReviewedDiffHunk> hunks)
    {
        var builder = ImmutableArray.CreateBuilder<ReviewedDiffHunk>();
        var cursor = 0;
        while (cursor < operations.Length)
        {
            var first = FindChange(operations, cursor);
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
                var next = FindChange(operations, scan);
                if (next < 0)
                {
                    break;
                }

                var equalGap = 0;
                for (var index = last + 1; index < next; index++)
                {
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

    private static int FindChange(
        ImmutableArray<DiffOperation> operations,
        int start)
    {
        for (var index = start; index < operations.Length; index++)
        {
            if (operations[index].Kind != DiffKind.Equal)
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryCreateHunk(
        ImmutableArray<DiffOperation> operations,
        int start,
        int end,
        int oldLineCount,
        int newLineCount,
        out ReviewedDiffHunk? hunk)
    {
        hunk = null;
        var lines = ImmutableArray.CreateBuilder<ReviewedDiffLine>();
        var oldCount = 0;
        var newCount = 0;
        for (var index = start; index < end; index++)
        {
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

    private static bool PatchCorroborates(
        ReviewedPullRequestFileFact file,
        ImmutableArray<TextLine> oldLines,
        ImmutableArray<TextLine> newLines)
    {
        if (file.Patch is null || file.PatchIncomplete)
        {
            return true;
        }

        var patch = file.Patch.Replace("\r\n", "\n", StringComparison.Ordinal);
        if (patch.Contains('\r'))
        {
            return true;
        }

        var lines = patch.Split('\n');
        var sawHeader = false;
        var oldLine = 0;
        var newLine = 0;
        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                if (!TryParseHunkHeader(line, out oldLine, out newLine))
                {
                    return true;
                }

                sawHeader = true;
                continue;
            }

            if (!sawHeader || line.Length == 0 || line[0] == '\\')
            {
                continue;
            }

            var text = line[1..];
            switch (line[0])
            {
                case ' ':
                    if (!Matches(oldLines, oldLine++, text) ||
                        !Matches(newLines, newLine++, text))
                    {
                        return false;
                    }
                    break;
                case '-':
                    if (!Matches(oldLines, oldLine++, text))
                    {
                        return false;
                    }
                    break;
                case '+':
                    if (!Matches(newLines, newLine++, text))
                    {
                        return false;
                    }
                    break;
                default:
                    return true;
            }
        }

        return true;
    }

    private static bool TryParseHunkHeader(
        string line,
        out int oldLine,
        out int newLine)
    {
        oldLine = 0;
        newLine = 0;
        var minus = line.IndexOf('-', StringComparison.Ordinal);
        var plus = line.IndexOf('+', StringComparison.Ordinal);
        var closing = line.IndexOf("@@", 2, StringComparison.Ordinal);
        if (minus < 0 || plus <= minus || closing <= plus)
        {
            return false;
        }

        return TryRangeStart(line.AsSpan(minus + 1, plus - minus - 1), out oldLine) &&
            TryRangeStart(line.AsSpan(plus + 1, closing - plus - 1), out newLine);
    }

    private static bool TryRangeStart(ReadOnlySpan<char> value, out int start)
    {
        value = value.Trim();
        var comma = value.IndexOf(',');
        if (comma >= 0)
        {
            value = value[..comma];
        }

        return int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out start) && start >= 0;
    }

    private static bool Matches(
        ImmutableArray<TextLine> lines,
        int oneBased,
        string text) =>
        oneBased > 0 && oneBased <= lines.Length &&
        StringComparer.Ordinal.Equals(lines[oneBased - 1].Text, text);

    private enum TextStatus
    {
        Text = 1,
        Binary,
        Unavailable,
        LineTooLong,
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
