using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record ReviewedChangedFile(
    string Path,
    string? PreviousPath,
    string Status,
    int Additions,
    int Deletions,
    int Changes,
    string PatchStatus,
    string? PatchSha256,
    bool SourceTruncated);

internal sealed record ReviewedDiffLine(
    string Kind,
    int? OldLine,
    int? NewLine,
    string Text);

internal sealed class ReviewedDiffHunk
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal ReviewedDiffHunk(
        int oldStart,
        int oldCount,
        int newStart,
        int newCount,
        IEnumerable<ReviewedDiffLine> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!ValidRange(oldStart, oldCount) ||
            !ValidRange(newStart, newCount) ||
            oldCount == 0 && newCount == 0)
        {
            throw new ArgumentException("Diff hunk range is invalid.");
        }

        var oldEnd = (long)oldStart + oldCount;
        var newEnd = (long)newStart + newCount;
        var oldCounter = (long)oldStart;
        var newCounter = (long)newStart;
        var additions = 0;
        var deletions = 0;
        var previousWasContent = false;
        var builder = ImmutableArray.CreateBuilder<ReviewedDiffLine>();
        foreach (var line in lines)
        {
            if (builder.Count >= AgentLimits.DiffLinesPerHunk)
            {
                throw new ArgumentException(
                    "Diff hunk line count exceeds the stable limit.",
                    nameof(lines));
            }

            if (line is null ||
                !TryAdmitLine(
                    line,
                    ref oldCounter,
                    ref newCounter,
                    ref additions,
                    ref deletions,
                    ref previousWasContent))
            {
                throw new ArgumentException("Diff line is invalid.", nameof(lines));
            }

            builder.Add(new ReviewedDiffLine(
                line.Kind,
                line.OldLine,
                line.NewLine,
                line.Text));
        }

        if (builder.Count == 0 ||
            oldCounter != oldEnd ||
            newCounter != newEnd)
        {
            throw new ArgumentException(
                "Diff hunk line progression is invalid.",
                nameof(lines));
        }

        OldStart = oldStart;
        OldCount = oldCount;
        NewStart = newStart;
        NewCount = newCount;
        Lines = builder.ToImmutable();
        OldEnd = oldEnd;
        NewEnd = newEnd;
        AdditionLines = additions;
        DeletionLines = deletions;
    }

    internal int OldStart { get; }

    internal int OldCount { get; }

    internal int NewStart { get; }

    internal int NewCount { get; }

    internal ImmutableArray<ReviewedDiffLine> Lines { get; }

    internal long OldEnd { get; }

    internal long NewEnd { get; }

    internal int AdditionLines { get; }

    internal int DeletionLines { get; }

    private static bool ValidRange(int start, int count) =>
        start >= 0 &&
        count is >= 0 and <= 1_000_000 &&
        (start != 0 || count == 0) &&
        (long)start + count <= int.MaxValue;

    private static bool TryAdmitLine(
        ReviewedDiffLine line,
        ref long oldCounter,
        ref long newCounter,
        ref int additions,
        ref int deletions,
        ref bool previousWasContent)
    {
        switch (line.Kind)
        {
            case "context":
                if (!ValidText(line.Text) ||
                    line.OldLine != oldCounter ||
                    line.NewLine != newCounter)
                {
                    return false;
                }

                oldCounter++;
                newCounter++;
                previousWasContent = true;
                return true;
            case "addition":
                if (!ValidText(line.Text) ||
                    line.OldLine is not null ||
                    line.NewLine != newCounter ||
                    line.NewLine < 1)
                {
                    return false;
                }

                newCounter++;
                additions++;
                previousWasContent = true;
                return true;
            case "deletion":
                if (!ValidText(line.Text) ||
                    line.OldLine != oldCounter ||
                    line.NewLine is not null)
                {
                    return false;
                }

                oldCounter++;
                deletions++;
                previousWasContent = true;
                return true;
            case "no_newline":
                if (!previousWasContent ||
                    line.OldLine is not null ||
                    line.NewLine is not null ||
                    !StringComparer.Ordinal.Equals(line.Text, string.Empty))
                {
                    return false;
                }

                previousWasContent = false;
                return true;
            default:
                return false;
        }
    }

    private static bool ValidText(string? text)
    {
        if (text is null ||
            text.Contains('\0') ||
            text.Contains('\r') ||
            text.Contains('\n'))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(text) <= AgentLimits.DiffLineTextBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}

internal sealed class ReviewedDiffSource
{
    internal ReviewedDiffSource(
        ReviewedIdentity reviewedIdentity,
        string path,
        string? previousPath,
        string status,
        bool sourceTruncated,
        IEnumerable<ReviewedDiffHunk> hunks)
    {
        ArgumentNullException.ThrowIfNull(hunks);
        if (!reviewedIdentity.IsValid() ||
            !ReviewedChangedFileValidation.IsLifecycleShapeValid(
                path,
                previousPath,
                status))
        {
            throw new ArgumentException("Diff source root is invalid.");
        }

        var builder = ImmutableArray.CreateBuilder<ReviewedDiffHunk>();
        ReviewedDiffHunk? previous = null;
        var additions = 0;
        var deletions = 0;
        foreach (var hunk in hunks)
        {
            if (builder.Count >= AgentLimits.DiffHunksPerFile)
            {
                throw new ArgumentException(
                    "Diff source hunk count exceeds the stable limit.",
                    nameof(hunks));
            }

            if (hunk is null ||
                previous is not null &&
                (previous.OldEnd > hunk.OldStart ||
                    previous.NewEnd > hunk.NewStart))
            {
                throw new ArgumentException(
                    "Diff source hunk order is invalid.",
                    nameof(hunks));
            }

            additions = checked(additions + hunk.AdditionLines);
            deletions = checked(deletions + hunk.DeletionLines);
            builder.Add(hunk);
            previous = hunk;
        }

        ReviewedIdentity = reviewedIdentity;
        Path = path;
        PreviousPath = previousPath;
        Status = status;
        SourceTruncated = sourceTruncated;
        Hunks = builder.ToImmutable();
        RepresentedAdditions = additions;
        RepresentedDeletions = deletions;
        if (!ReviewedDiffSourceWriter.TryWriteBounded(
                this,
                AgentLimits.DiffSourceBytesPerFile,
                out var canonicalBytes))
        {
            throw new ArgumentException(
                "Diff source bytes exceed the stable limit.",
                nameof(hunks));
        }

        CanonicalBytes = canonicalBytes;
        PatchSha256 = AgentCanonical.HashDomain(
            AgentCanonical.DiffSourceDomain,
            canonicalBytes.AsSpan());
    }

    internal ReviewedIdentity ReviewedIdentity { get; }

    internal string Path { get; }

    internal string? PreviousPath { get; }

    internal string Status { get; }

    internal bool SourceTruncated { get; }

    internal ImmutableArray<ReviewedDiffHunk> Hunks { get; }

    internal int RepresentedAdditions { get; }

    internal int RepresentedDeletions { get; }

    internal ImmutableArray<byte> CanonicalBytes { get; }

    internal string PatchSha256 { get; }
}

internal static class ReviewedChangedFileValidation
{
    internal static bool IsShapeValid(ReviewedChangedFile? change) =>
        change is not null &&
        IsLifecycleShapeValid(
            change.Path,
            change.PreviousPath,
            change.Status) &&
        CountsAreValid(change) &&
        PatchFieldsAreValid(change);

    internal static bool IsLifecycleShapeValid(
        string? path,
        string? previousPath,
        string? status)
    {
        if (!RepositoryPath.IsValid(path!))
        {
            return false;
        }

        return status switch
        {
            "added" or "removed" or "modified" or "changed" =>
                previousPath is null,
            "renamed" or "copied" =>
                RepositoryPath.IsValid(previousPath!) &&
                !StringComparer.Ordinal.Equals(path, previousPath),
            _ => false,
        };
    }

    internal static bool MembershipIsValid(
        ReviewedChangedFile change,
        IImmutableSet<string> trackedFiles) =>
        change.Status == "removed"
            ? !trackedFiles.Contains(change.Path)
            : trackedFiles.Contains(change.Path);

    private static bool CountsAreValid(ReviewedChangedFile change)
    {
        if (change.Additions is < 0 or > 1_000_000 ||
            change.Deletions is < 0 or > 1_000_000)
        {
            return false;
        }

        var total = (long)change.Additions + change.Deletions;
        return total <= 1_000_000 && change.Changes == total;
    }

    private static bool PatchFieldsAreValid(ReviewedChangedFile change) =>
        change.PatchStatus switch
        {
            "available" => IsLowerHex(change.PatchSha256, 64),
            "unavailable" or "binary" =>
                change.PatchSha256 is null && !change.SourceTruncated,
            _ => false,
        };

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal static class ReviewedChangedFileWriter
{
    internal static byte[] Write(ReviewedChangedFile change)
    {
        var writer = new Rfc8785Writer(256);
        WriteTo(ref writer, change);
        return writer.ToImmutableArray().ToArray();
    }

    internal static void WriteTo(
        ref Rfc8785Writer writer,
        ReviewedChangedFile change)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("path");
        writer.WriteString(change.Path);
        writer.WriteProperty("previous_path");
        WriteNullableString(ref writer, change.PreviousPath);
        writer.WriteProperty("status");
        writer.WriteString(change.Status);
        writer.WriteProperty("additions");
        writer.WriteNumber(change.Additions);
        writer.WriteProperty("deletions");
        writer.WriteNumber(change.Deletions);
        writer.WriteProperty("changes");
        writer.WriteNumber(change.Changes);
        writer.WriteProperty("patch_status");
        writer.WriteString(change.PatchStatus);
        writer.WriteProperty("patch_sha256");
        WriteNullableString(ref writer, change.PatchSha256);
        writer.WriteProperty("source_truncated");
        writer.WriteBoolean(change.SourceTruncated);
        writer.WriteObjectEnd();
    }

    internal static void WriteNullableString(
        ref Rfc8785Writer writer,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(value);
        }
    }
}

internal static class ReviewedDiffSourceWriter
{
    internal static byte[] Write(ReviewedDiffSource source) =>
        source.CanonicalBytes.ToArray();

    internal static bool TryWriteBounded(
        ReviewedDiffSource source,
        int limit,
        out ImmutableArray<byte> canonicalBytes)
    {
        var writer = new Rfc8785Writer(4_096)
        {
            DiscardLimit = limit,
        };
        WriteTo(ref writer, source);
        if (writer.Exceeded)
        {
            canonicalBytes = default;
            return false;
        }

        canonicalBytes = writer.ToImmutableArray();
        return true;
    }

    private static void WriteTo(
        ref Rfc8785Writer writer,
        ReviewedDiffSource source)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, source.ReviewedIdentity);
        writer.WriteProperty("path");
        writer.WriteString(source.Path);
        writer.WriteProperty("previous_path");
        if (source.PreviousPath is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(source.PreviousPath);
        }

        writer.WriteProperty("status");
        writer.WriteString(source.Status);
        writer.WriteProperty("source_truncated");
        writer.WriteBoolean(source.SourceTruncated);
        writer.WriteProperty("hunks");
        writer.WriteArrayStart();
        for (var hunkIndex = 0; hunkIndex < source.Hunks.Length; hunkIndex++)
        {
            if (hunkIndex > 0)
            {
                writer.WriteComma();
            }

            WriteHunk(ref writer, source.Hunks[hunkIndex]);
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    internal static void WriteHunk(
        ref Rfc8785Writer writer,
        ReviewedDiffHunk hunk)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("old_start");
        writer.WriteNumber(hunk.OldStart);
        writer.WriteProperty("old_count");
        writer.WriteNumber(hunk.OldCount);
        writer.WriteProperty("new_start");
        writer.WriteNumber(hunk.NewStart);
        writer.WriteProperty("new_count");
        writer.WriteNumber(hunk.NewCount);
        writer.WriteProperty("lines");
        writer.WriteArrayStart();
        for (var lineIndex = 0; lineIndex < hunk.Lines.Length; lineIndex++)
        {
            if (lineIndex > 0)
            {
                writer.WriteComma();
            }

            WriteLine(ref writer, hunk.Lines[lineIndex]);
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteLine(
        ref Rfc8785Writer writer,
        ReviewedDiffLine line)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("kind");
        writer.WriteString(line.Kind);
        writer.WriteProperty("old_line");
        WriteNullableInt32(ref writer, line.OldLine);
        writer.WriteProperty("new_line");
        WriteNullableInt32(ref writer, line.NewLine);
        writer.WriteProperty("text");
        writer.WriteString(line.Text);
        writer.WriteObjectEnd();
    }

    private static void WriteNullableInt32(
        ref Rfc8785Writer writer,
        int? value)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteNumber(value.Value);
        }
    }
}
