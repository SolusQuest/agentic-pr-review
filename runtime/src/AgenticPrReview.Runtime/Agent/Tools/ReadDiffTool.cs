using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record ReadDiffArguments(
    string Path,
    int StartHunk,
    int HunkCount,
    byte[] CanonicalBytes);

internal sealed record PreparedReadDiffCall(
    string CallId,
    ReadDiffArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.ReadDiffName,
        Arguments.CanonicalBytes);

internal static partial class AgentToolArguments
{
    internal static bool TryReadDiff(
        string json,
        out ReadDiffArguments? arguments)
    {
        arguments = null;
        var input = StrictInputBytes(json);
        if (input is null)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize(
                input,
                AgentToolJsonContext.Default.ReadDiffArgumentsDto);
            if (dto?.Path is null ||
                !RepositoryPath.IsValid(dto.Path) ||
                dto.StartHunk is < 1 ||
                dto.HunkCount is < 1 or > AgentLimits.ReadDiffHunks)
            {
                return false;
            }

            var start = dto.StartHunk ?? 1;
            var count = dto.HunkCount ?? AgentLimits.ReadDiffHunks;
            if (!MatchesReadDiffInput(input, dto.Path, start, count))
            {
                return false;
            }

            arguments = new ReadDiffArguments(
                dto.Path,
                start,
                count,
                WriteReadDiff(dto.Path, start, count, true, true));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    private static bool MatchesReadDiffInput(
        ReadOnlySpan<byte> input,
        string path,
        int startHunk,
        int hunkCount) =>
        input.SequenceEqual(
            WriteReadDiff(path, startHunk, hunkCount, false, false)) ||
        input.SequenceEqual(
            WriteReadDiff(path, startHunk, hunkCount, true, false)) ||
        input.SequenceEqual(
            WriteReadDiff(path, startHunk, hunkCount, false, true)) ||
        input.SequenceEqual(
            WriteReadDiff(path, startHunk, hunkCount, true, true));

    internal static byte[] WriteReadDiff(
        string path,
        int startHunk,
        int hunkCount,
        bool includeStart,
        bool includeCount)
    {
        var writer = new Rfc8785Writer(128);
        writer.WriteObjectStart();
        writer.WriteProperty("path");
        writer.WriteString(path);
        if (includeStart)
        {
            writer.WriteProperty("start_hunk");
            writer.WriteNumber(startHunk);
        }

        if (includeCount)
        {
            writer.WriteProperty("hunk_count");
            writer.WriteNumber(hunkCount);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ReadDiffArgumentsDto
{
    [JsonPropertyName("path")]
    [JsonPropertyOrder(0)]
    public string? Path { get; set; }

    [JsonPropertyName("start_hunk")]
    [JsonPropertyOrder(1)]
    public int? StartHunk { get; set; }

    [JsonPropertyName("hunk_count")]
    [JsonPropertyOrder(2)]
    public int? HunkCount { get; set; }
}

internal static partial class AgentToolResultAdmission
{
    private static bool TryAdmitReadDiff(
        PreparedReadDiffCall call,
        ReviewedIdentity expectedIdentity,
        JsonElement root,
        byte[] canonical,
        AgentObservation observation)
    {
        var hunks = root.GetProperty("hunks")
            .EnumerateArray()
            .Select(ReadDiffHunk)
            .ToImmutableArray();
        var result = new ReadDiffResult(
            root.GetProperty("status").GetString()!,
            ReadIdentity(root.GetProperty("reviewed_identity")),
            root.GetProperty("path").GetString()!,
            NullableString(root.GetProperty("patch_sha256")),
            root.GetProperty("source_truncated").GetBoolean(),
            root.GetProperty("requested_start_hunk").GetInt32(),
            root.GetProperty("requested_hunk_count").GetInt32(),
            NullableInt32(root.GetProperty("returned_start_hunk")),
            NullableInt32(root.GetProperty("returned_end_hunk")),
            hunks,
            root.GetProperty("truncated").GetBoolean(),
            NullableInt32(root.GetProperty("next_start_hunk")),
            root.GetProperty("observation_id").GetString()!);
        var returned = ReadDiffReturnedLines(result);
        return result.ReviewedIdentity == expectedIdentity &&
            StringComparer.Ordinal.Equals(result.Path, call.Arguments.Path) &&
            result.RequestedStartHunk == call.Arguments.StartHunk &&
            result.RequestedHunkCount == call.Arguments.HunkCount &&
            ReadDiffShapeIsAdmissible(result) &&
            IsLowerHex(result.ObservationId, 64) &&
            canonical.Length <= AgentLimits.ToolResultBytes &&
            canonical.AsSpan().SequenceEqual(ReadDiffResultWriter.Write(result)) &&
            StringComparer.Ordinal.Equals(
                result.ObservationId,
                AgentCanonical.HashDomain(
                    AgentCanonical.ReadDiffObservationDomain,
                    ReadDiffResultWriter.Write(
                        result with { ObservationId = null },
                        includeObservationId: false))) &&
            ObservationMatches(
                observation,
                expectedIdentity,
                result.ObservationId,
                returned);
    }

    private static ReviewedDiffHunk ReadDiffHunk(JsonElement element) =>
        new(
            element.GetProperty("old_start").GetInt32(),
            element.GetProperty("old_count").GetInt32(),
            element.GetProperty("new_start").GetInt32(),
            element.GetProperty("new_count").GetInt32(),
            element.GetProperty("lines")
                .EnumerateArray()
                .Select(line => new ReviewedDiffLine(
                    line.GetProperty("kind").GetString()!,
                    NullableInt32(line.GetProperty("old_line")),
                    NullableInt32(line.GetProperty("new_line")),
                    line.GetProperty("text").GetString()!)));

    private static bool ReadDiffShapeIsAdmissible(ReadDiffResult result)
    {
        if (result.RequestedStartHunk < 1 ||
            result.RequestedHunkCount is < 1 or > AgentLimits.ReadDiffHunks ||
            !RepositoryPath.IsValid(result.Path))
        {
            return false;
        }

        return result.Status switch
        {
            "ok" => IsLowerHex(result.PatchSha256, 64) &&
                result.Hunks.Length is >= 1 &&
                result.Hunks.Length <= result.RequestedHunkCount &&
                result.ReturnedStartHunk == result.RequestedStartHunk &&
                result.ReturnedStartHunk <= AgentLimits.DiffHunksPerFile &&
                (long)result.ReturnedStartHunk + result.Hunks.Length - 1 ==
                    result.ReturnedEndHunk &&
                result.ReturnedEndHunk <= AgentLimits.DiffHunksPerFile &&
                ReadDiffHunksAreOrdered(result.Hunks) &&
                (result.Truncated
                    ? result.ReturnedEndHunk < AgentLimits.DiffHunksPerFile &&
                        result.NextStartHunk == result.ReturnedEndHunk + 1
                    : result.NextStartHunk is null),
            "empty" => IsLowerHex(result.PatchSha256, 64) &&
                ReadDiffEmptyPageIsAdmissible(result),
            "eof" => result.RequestedStartHunk > 1 &&
                IsLowerHex(result.PatchSha256, 64) &&
                ReadDiffEmptyPageIsAdmissible(result),
            "unavailable" or "binary" => result.PatchSha256 is null &&
                !result.SourceTruncated &&
                ReadDiffEmptyPageIsAdmissible(result),
            _ => false,
        };
    }

    private static bool ReadDiffEmptyPageIsAdmissible(ReadDiffResult result) =>
        result.ReturnedStartHunk is null &&
        result.ReturnedEndHunk is null &&
        result.Hunks.IsEmpty &&
        !result.Truncated &&
        result.NextStartHunk is null;

    private static bool ReadDiffHunksAreOrdered(
        ImmutableArray<ReviewedDiffHunk> hunks)
    {
        for (var index = 1; index < hunks.Length; index++)
        {
            if (hunks[index - 1].OldEnd > hunks[index].OldStart ||
                hunks[index - 1].NewEnd > hunks[index].NewStart)
            {
                return false;
            }
        }

        return true;
    }

    private static ImmutableDictionary<string, ImmutableHashSet<int>>
        ReadDiffReturnedLines(ReadDiffResult result)
    {
        var lines = result.Hunks
            .SelectMany(hunk => hunk.Lines)
            .Where(line => line.Kind is "context" or "addition")
            .Select(line => line.NewLine!.Value)
            .ToImmutableHashSet();
        return lines.Count == 0
            ? EmptyReturnedLines()
            : EmptyReturnedLines().Add(result.Path, lines);
    }
}

internal sealed partial class SnapshotToolExecutor
{
    private string? ValidateReadDiff(ReadDiffArguments arguments)
    {
        if (!RepositoryPath.IsValid(arguments.Path))
        {
            return AgentFailureCodes.ToolPathInvalid;
        }

        return snapshot.ContainsChangedPath(arguments.Path)
            ? null
            : AgentFailureCodes.ToolPathNotTracked;
    }

    private AgentToolExecution ExecuteReadDiff(
        PreparedReadDiffCall call,
        CancellationToken cancellationToken) =>
        ExecuteReadDiff(call, cancellationToken, AgentLimits.ToolResultBytes);

    internal AgentToolExecution ExecuteReadDiffWithLimit(
        PreparedReadDiffCall call,
        CancellationToken cancellationToken,
        int resultLimit) =>
        ExecuteReadDiff(call, cancellationToken, resultLimit);

    private AgentToolExecution ExecuteReadDiff(
        PreparedReadDiffCall call,
        CancellationToken cancellationToken,
        int resultLimit)
    {
        var preflightFailure = ValidateReadDiff(call.Arguments);
        if (preflightFailure is not null)
        {
            return AgentToolExecution.Failure(preflightFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!snapshot.TryGetChangedFile(call.Arguments.Path, out var change))
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed);
        }

        if (change.PatchStatus is "unavailable" or "binary")
        {
            if (snapshot.TryGetDiffSource(change.Path, out _))
            {
                return AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed);
            }

            return FinalizeReadDiff(
                EmptyReadDiffResult(
                    change.PatchStatus,
                    call.Arguments,
                    null,
                    false),
                resultLimit);
        }

        if (change.PatchStatus != "available" ||
            !snapshot.TryGetDiffSource(change.Path, out var source) ||
            source.ReviewedIdentity != snapshot.Identity ||
            !StringComparer.Ordinal.Equals(source.Path, change.Path) ||
            !StringComparer.Ordinal.Equals(source.PatchSha256, change.PatchSha256) ||
            source.SourceTruncated != change.SourceTruncated)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed);
        }

        if (source.Hunks.IsEmpty)
        {
            return FinalizeReadDiff(
                EmptyReadDiffResult(
                    "empty",
                    call.Arguments,
                    source.PatchSha256,
                    source.SourceTruncated),
                resultLimit);
        }

        if (call.Arguments.StartHunk > source.Hunks.Length)
        {
            return FinalizeReadDiff(
                EmptyReadDiffResult(
                    "eof",
                    call.Arguments,
                    source.PatchSha256,
                    source.SourceTruncated),
                resultLimit);
        }

        var position = call.Arguments.StartHunk - 1;
        var selected = ImmutableArray.CreateBuilder<ReviewedDiffHunk>();
        while (position < source.Hunks.Length &&
            selected.Count < call.Arguments.HunkCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            selected.Add(source.Hunks[position]);
            var hasMore = position + 1 < source.Hunks.Length;
            var provisional = new ReadDiffResult(
                "ok",
                snapshot.Identity,
                call.Arguments.Path,
                source.PatchSha256,
                source.SourceTruncated,
                call.Arguments.StartHunk,
                call.Arguments.HunkCount,
                call.Arguments.StartHunk,
                call.Arguments.StartHunk + selected.Count - 1,
                selected.ToImmutable(),
                hasMore,
                hasMore
                    ? call.Arguments.StartHunk + selected.Count
                    : null,
                new string('0', 64));
            if (ReadDiffResultWriter.Write(provisional).Length > resultLimit)
            {
                selected.RemoveAt(selected.Count - 1);
                if (selected.Count == 0)
                {
                    return AgentToolExecution.Failure(
                        AgentFailureCodes.ToolResultLimit);
                }

                break;
            }

            position++;
        }

        var truncated = position < source.Hunks.Length;
        var end = call.Arguments.StartHunk + selected.Count - 1;
        return FinalizeReadDiff(
            new ReadDiffResult(
                "ok",
                snapshot.Identity,
                call.Arguments.Path,
                source.PatchSha256,
                source.SourceTruncated,
                call.Arguments.StartHunk,
                call.Arguments.HunkCount,
                call.Arguments.StartHunk,
                end,
                selected.ToImmutable(),
                truncated,
                truncated ? end + 1 : null,
                null),
            resultLimit);
    }

    private ReadDiffResult EmptyReadDiffResult(
        string status,
        ReadDiffArguments arguments,
        string? patchSha256,
        bool sourceTruncated) =>
        new(
            status,
            snapshot.Identity,
            arguments.Path,
            patchSha256,
            sourceTruncated,
            arguments.StartHunk,
            arguments.HunkCount,
            null,
            null,
            [],
            false,
            null,
            null);

    private AgentToolExecution FinalizeReadDiff(
        ReadDiffResult withoutObservation,
        int resultLimit)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadDiffObservationDomain,
            ReadDiffResultWriter.Write(
                withoutObservation,
                includeObservationId: false));
        var result = withoutObservation with { ObservationId = observationId };
        var canonical = ReadDiffResultWriter.Write(result);
        if (canonical.Length > resultLimit)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolResultLimit);
        }

        var lines = result.Hunks
            .SelectMany(hunk => hunk.Lines)
            .Where(line => line.Kind is "context" or "addition")
            .Select(line => line.NewLine!.Value)
            .ToImmutableHashSet();
        var returned = lines.Count == 0
            ? ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
            : ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(result.Path, lines);
        return new AgentToolExecution(
            true,
            null,
            StrictUtf8.GetString(canonical),
            canonical,
            new AgentObservation(observationId, snapshot.Identity, returned));
    }
}

internal sealed record ReadDiffResult(
    string Status,
    ReviewedIdentity ReviewedIdentity,
    string Path,
    string? PatchSha256,
    bool SourceTruncated,
    int RequestedStartHunk,
    int RequestedHunkCount,
    int? ReturnedStartHunk,
    int? ReturnedEndHunk,
    ImmutableArray<ReviewedDiffHunk> Hunks,
    bool Truncated,
    int? NextStartHunk,
    string? ObservationId);

internal static class ReadDiffResultWriter
{
    internal static byte[] Write(
        ReadDiffResult result,
        bool includeObservationId = true)
    {
        var writer = new Rfc8785Writer(4_096);
        writer.WriteObjectStart();
        writer.WriteProperty("status");
        writer.WriteString(result.Status);
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, result.ReviewedIdentity);
        writer.WriteProperty("path");
        writer.WriteString(result.Path);
        writer.WriteProperty("patch_sha256");
        WriteNullableString(ref writer, result.PatchSha256);
        writer.WriteProperty("source_truncated");
        writer.WriteBoolean(result.SourceTruncated);
        writer.WriteProperty("requested_start_hunk");
        writer.WriteNumber(result.RequestedStartHunk);
        writer.WriteProperty("requested_hunk_count");
        writer.WriteNumber(result.RequestedHunkCount);
        writer.WriteProperty("returned_start_hunk");
        WriteNullableInt32(ref writer, result.ReturnedStartHunk);
        writer.WriteProperty("returned_end_hunk");
        WriteNullableInt32(ref writer, result.ReturnedEndHunk);
        writer.WriteProperty("hunks");
        writer.WriteArrayStart();
        for (var index = 0; index < result.Hunks.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            ReviewedDiffSourceWriter.WriteHunk(ref writer, result.Hunks[index]);
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("truncated");
        writer.WriteBoolean(result.Truncated);
        writer.WriteProperty("next_start_hunk");
        WriteNullableInt32(ref writer, result.NextStartHunk);
        if (includeObservationId)
        {
            writer.WriteProperty("observation_id");
            writer.WriteString(result.ObservationId!);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    private static void WriteNullableString(
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
