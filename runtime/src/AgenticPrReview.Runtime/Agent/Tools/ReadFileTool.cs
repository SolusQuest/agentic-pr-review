using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record ReadFileArguments(
    string Path,
    int StartLine,
    int LineCount,
    byte[] CanonicalBytes);

internal sealed record PreparedReadFileCall(
    string CallId,
    ReadFileArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.ReadFileName,
        Arguments.CanonicalBytes);

internal static partial class AgentToolArguments
{
    internal static bool TryReadFile(
        string json,
        out ReadFileArguments? arguments) =>
        TryReadFile(json, allowProviderSpelling: false, out arguments);

    internal static bool TryReadFileProvider(
        string json,
        out ReadFileArguments? arguments) =>
        TryReadFile(json, allowProviderSpelling: true, out arguments);

    private static bool TryReadFile(
        string json,
        bool allowProviderSpelling,
        out ReadFileArguments? arguments)
    {
        arguments = null;
        var input = StrictInputBytes(json);
        if (input is null)
        {
            return false;
        }
        var providerComparison = allowProviderSpelling
            ? ProviderComparisonBytes(input)
            : null;
        var deserializationInput = allowProviderSpelling
            ? ProviderDeserializationBytes(input)
            : input;
        if (deserializationInput is null ||
            allowProviderSpelling && providerComparison is null)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize(
                deserializationInput,
                AgentToolJsonContext.Default.ReadFileArgumentsDto);
            if (dto?.Path is null ||
                !RepositoryPath.IsValid(dto.Path) ||
                dto.StartLine is < 1 ||
                dto.LineCount is < 1 or > AgentLimits.ReadFileLines)
            {
                return false;
            }

            var start = dto.StartLine ?? 1;
            var count = dto.LineCount ?? AgentLimits.ReadFileLines;
            if (!MatchesReadInput(
                    input,
                    providerComparison,
                    dto.Path,
                    start,
                    count))
            {
                return false;
            }

            var canonical = WriteReadFile(dto.Path, start, count, true, true);
            arguments = new ReadFileArguments(dto.Path, start, count, canonical);
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

    private static bool MatchesReadInput(
        ReadOnlySpan<byte> input,
        byte[]? providerComparison,
        string path,
        int startLine,
        int lineCount)
    {
        return MatchesInput(input, providerComparison,
                WriteReadFile(path, startLine, lineCount, false, false)) ||
            MatchesInput(input, providerComparison,
                WriteReadFile(path, startLine, lineCount, true, false)) ||
            MatchesInput(input, providerComparison,
                WriteReadFile(path, startLine, lineCount, false, true)) ||
            MatchesInput(input, providerComparison,
                WriteReadFile(path, startLine, lineCount, true, true));
    }

    private static byte[] WriteReadFile(
        string path,
        int startLine,
        int lineCount,
        bool includeStart,
        bool includeCount)
    {
        var writer = new Rfc8785Writer(128);
        writer.WriteObjectStart();
        writer.WriteProperty("path");
        writer.WriteString(path);
        if (includeStart)
        {
            writer.WriteProperty("start_line");
            writer.WriteNumber(startLine);
        }

        if (includeCount)
        {
            writer.WriteProperty("line_count");
            writer.WriteNumber(lineCount);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ReadFileArgumentsDto
{
    [JsonPropertyName("path")]
    [JsonPropertyOrder(0)]
    public string? Path { get; set; }

    [JsonPropertyName("start_line")]
    [JsonPropertyOrder(1)]
    public int? StartLine { get; set; }

    [JsonPropertyName("line_count")]
    [JsonPropertyOrder(2)]
    public int? LineCount { get; set; }
}

internal static partial class AgentToolResultAdmission
{
    private static bool TryAdmitRead(
        PreparedReadFileCall call,
        ReviewedIdentity expectedIdentity,
        JsonElement root,
        byte[] canonical,
        AgentObservation observation)
    {
        var lines = root.GetProperty("lines")
            .EnumerateArray()
            .Select(line => new ReadFileLine(
                line.GetProperty("line").GetInt32(),
                line.GetProperty("text").GetString()!))
            .ToImmutableArray();
        var result = new ReadFileResult(
            root.GetProperty("status").GetString()!,
            ReadIdentity(root.GetProperty("reviewed_identity")),
            root.GetProperty("path").GetString()!,
            root.GetProperty("raw_sha256").GetString()!,
            root.GetProperty("requested_start_line").GetInt32(),
            root.GetProperty("requested_line_count").GetInt32(),
            NullableInt32(root.GetProperty("returned_start_line")),
            NullableInt32(root.GetProperty("returned_end_line")),
            lines,
            root.GetProperty("truncated").GetBoolean(),
            NullableString(root.GetProperty("truncation_reason")),
            root.GetProperty("observation_id").GetString()!);
        var returned = lines.Length == 0
            ? EmptyReturnedLines()
            : EmptyReturnedLines().Add(
                result.Path,
                lines.Select(line => line.Line).ToImmutableHashSet());
        return result.ReviewedIdentity == expectedIdentity &&
            result.Status is "ok" or "start_after_eof" &&
            StringComparer.Ordinal.Equals(result.Path, call.Arguments.Path) &&
            result.RequestedStartLine == call.Arguments.StartLine &&
            result.RequestedLineCount == call.Arguments.LineCount &&
            IsLowerHex(result.RawSha256, 64) &&
            IsLowerHex(result.ObservationId, 64) &&
            canonical.AsSpan().SequenceEqual(ReadFileResultWriter.Write(result)) &&
            StringComparer.Ordinal.Equals(
                result.ObservationId,
                AgentCanonical.HashDomain(
                    AgentCanonical.ReadObservationDomain,
                    ReadFileResultWriter.Write(
                        result with { ObservationId = null },
                        includeObservationId: false))) &&
            ObservationMatches(
                observation,
                expectedIdentity,
                result.ObservationId,
                returned);
    }
}

internal sealed partial class SnapshotToolExecutor
{
    private async ValueTask<AgentToolExecution> ExecuteReadAsync(
        PreparedReadFileCall call,
        CancellationToken cancellationToken)
    {
        var pathFailure = ValidatePath(call.Arguments.Path);
        if (pathFailure is not null)
        {
            return AgentToolExecution.Failure(pathFailure);
        }

        var metadata = fileAccess.InspectMetadata(snapshot, call.Arguments.Path);
        var metadataFailure = AccessFailure(metadata.Status);
        if (metadataFailure is not null)
        {
            return AgentToolExecution.Failure(metadataFailure);
        }

        if (metadata.Length > AgentLimits.ReadFileRawBytes)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolFileTooLarge);
        }

        var probe = fileAccess.Probe(snapshot, call.Arguments.Path);
        var probeFailure = AccessFailure(probe.Status);
        if (probeFailure is not null)
        {
            return AgentToolExecution.Failure(probeFailure);
        }

        if (probe.Length != metadata.Length)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolPathUnsafe);
        }

        var read = await fileAccess.ReadAsync(
            snapshot,
            call.Arguments.Path,
            probe,
            cancellationToken);
        var readFailure = AccessFailure(read.Status);
        if (readFailure is not null)
        {
            return AgentToolExecution.Failure(readFailure);
        }

        var classified = Decode(read.Bytes!);
        if (classified.FailureCode is not null)
        {
            return AgentToolExecution.Failure(classified.FailureCode);
        }

        return BuildReadResult(
            call.Arguments,
            read.Bytes!,
            classified.Lines);
    }

    private AgentToolExecution BuildReadResult(
        ReadFileArguments arguments,
        byte[] rawBytes,
        ImmutableArray<string> allLines)
    {
        var rawSha256 = AgentCanonical.HashRaw(rawBytes);
        if (arguments.StartLine > allLines.Length)
        {
            var empty = new ReadFileResult(
                "start_after_eof",
                snapshot.Identity,
                arguments.Path,
                rawSha256,
                arguments.StartLine,
                arguments.LineCount,
                null,
                null,
                [],
                false,
                null,
                null);
            return FinalizeRead(empty);
        }

        var selected = ImmutableArray.CreateBuilder<ReadFileLine>();
        var startIndex = arguments.StartLine - 1;
        var availableWithinCount = Math.Min(
            arguments.LineCount,
            allLines.Length - startIndex);
        var resultByteTruncated = false;
        for (var index = 0; index < availableWithinCount; index++)
        {
            selected.Add(new ReadFileLine(
                arguments.StartLine + index,
                allLines[startIndex + index]));
            var selectedAllRequested = index + 1 == availableWithinCount;
            var hasMoreFileLines =
                startIndex + selected.Count < allLines.Length;
            var provisionalReason = selectedAllRequested
                ? hasMoreFileLines &&
                    selected.Count == arguments.LineCount
                    ? "line_count"
                    : null
                : "result_bytes";
            var provisional = new ReadFileResult(
                "ok",
                snapshot.Identity,
                arguments.Path,
                rawSha256,
                arguments.StartLine,
                arguments.LineCount,
                arguments.StartLine,
                arguments.StartLine + selected.Count - 1,
                selected.ToImmutable(),
                provisionalReason is not null,
                provisionalReason,
                new string('0', 64));
            if (ReadFileResultWriter.Write(provisional).Length >
                AgentLimits.ToolResultBytes)
            {
                selected.RemoveAt(selected.Count - 1);
                resultByteTruncated = true;
                break;
            }
        }

        var lineCountTruncated =
            !resultByteTruncated &&
            selected.Count == arguments.LineCount &&
            startIndex + selected.Count < allLines.Length;
        var reason = resultByteTruncated
            ? "result_bytes"
            : lineCountTruncated
                ? "line_count"
                : null;
        var result = new ReadFileResult(
            "ok",
            snapshot.Identity,
            arguments.Path,
            rawSha256,
            arguments.StartLine,
            arguments.LineCount,
            selected.Count == 0 ? null : arguments.StartLine,
            selected.Count == 0 ? null : arguments.StartLine + selected.Count - 1,
            selected.ToImmutable(),
            reason is not null,
            reason,
            null);
        return FinalizeRead(result);
    }

    private AgentToolExecution FinalizeRead(ReadFileResult withoutIdentity)
    {
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(withoutIdentity, includeObservationId: false));
        var result = withoutIdentity with { ObservationId = observationId };
        var canonical = ReadFileResultWriter.Write(result);
        if (canonical.Length > AgentLimits.ToolResultBytes)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolResultLimit);
        }

        var lines = result.Lines.Length == 0
            ? ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
            : ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(
                    result.Path,
                    result.Lines.Select(line => line.Line).ToImmutableHashSet());

        return new AgentToolExecution(
            true,
            null,
            StrictUtf8.GetString(canonical),
            canonical,
            new AgentObservation(observationId, snapshot.Identity, lines));
    }
}

internal sealed record ReadFileLine(int Line, string Text);

internal sealed record ReadFileResult(
    string Status,
    ReviewedIdentity ReviewedIdentity,
    string Path,
    string RawSha256,
    int RequestedStartLine,
    int RequestedLineCount,
    int? ReturnedStartLine,
    int? ReturnedEndLine,
    ImmutableArray<ReadFileLine> Lines,
    bool Truncated,
    string? TruncationReason,
    string? ObservationId);

internal static class ReadFileResultWriter
{
    internal static byte[] Write(
        ReadFileResult result,
        bool includeObservationId = true)
    {
        var writer = new Rfc8785Writer(1_024);
        writer.WriteObjectStart();
        writer.WriteProperty("status");
        writer.WriteString(result.Status);
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, result.ReviewedIdentity);
        writer.WriteProperty("path");
        writer.WriteString(result.Path);
        writer.WriteProperty("raw_sha256");
        writer.WriteString(result.RawSha256);
        writer.WriteProperty("requested_start_line");
        writer.WriteNumber(result.RequestedStartLine);
        writer.WriteProperty("requested_line_count");
        writer.WriteNumber(result.RequestedLineCount);
        writer.WriteProperty("returned_start_line");
        WriteNullableNumber(ref writer, result.ReturnedStartLine);
        writer.WriteProperty("returned_end_line");
        WriteNullableNumber(ref writer, result.ReturnedEndLine);
        writer.WriteProperty("lines");
        writer.WriteArrayStart();
        for (var index = 0; index < result.Lines.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            var line = result.Lines[index];
            writer.WriteObjectStart();
            writer.WriteProperty("line");
            writer.WriteNumber(line.Line);
            writer.WriteProperty("text");
            writer.WriteString(line.Text);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("truncated");
        writer.WriteBoolean(result.Truncated);
        writer.WriteProperty("truncation_reason");
        WriteNullableString(ref writer, result.TruncationReason);
        if (includeObservationId)
        {
            writer.WriteProperty("observation_id");
            writer.WriteString(result.ObservationId!);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    private static void WriteNullableNumber(
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
}
