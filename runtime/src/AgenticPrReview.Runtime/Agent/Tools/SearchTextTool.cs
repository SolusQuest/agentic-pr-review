using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record SearchTextArguments(
    string Query,
    string? Path,
    byte[] CanonicalBytes);

internal sealed record PreparedSearchTextCall(
    string CallId,
    SearchTextArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.SearchTextName,
        Arguments.CanonicalBytes);

internal static partial class AgentToolArguments
{
    internal static bool TrySearchText(
        string json,
        out SearchTextArguments? arguments) =>
        TrySearchText(json, allowCanonicalNullPath: false, out arguments);

    internal static bool TrySearchTextCanonical(
        string json,
        out SearchTextArguments? arguments) =>
        TrySearchText(json, allowCanonicalNullPath: true, out arguments);

    private static bool TrySearchText(
        string json,
        bool allowCanonicalNullPath,
        out SearchTextArguments? arguments)
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
                AgentToolJsonContext.Default.SearchTextArgumentsDto);
            if (dto?.Query is null)
            {
                return false;
            }

            var queryBytes = Encoding.UTF8.GetByteCount(dto.Query);
            if (queryBytes is < 1 or > AgentLimits.QueryBytes ||
                dto.Query.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
                AgentTextValidation.IsOnlyFixedWhitespace(dto.Query) ||
                (dto.Path is not null && !RepositoryPath.IsValid(dto.Path)))
            {
                return false;
            }

            var absent = WriteSearchText(dto.Query, null, false);
            var present = dto.Path is null && !allowCanonicalNullPath
                ? null
                : WriteSearchText(dto.Query, dto.Path, true);
            var accepted = allowCanonicalNullPath
                ? present is not null &&
                    input.AsSpan().SequenceEqual(present)
                : input.AsSpan().SequenceEqual(absent) ||
                    present is not null &&
                    input.AsSpan().SequenceEqual(present);
            if (!accepted)
            {
                return false;
            }

            var canonical = WriteSearchText(dto.Query, dto.Path, true);
            arguments = new SearchTextArguments(dto.Query, dto.Path, canonical);
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

    private static byte[] WriteSearchText(
        string query,
        string? path,
        bool includePath)
    {
        var writer = new Rfc8785Writer(128);
        writer.WriteObjectStart();
        writer.WriteProperty("query");
        writer.WriteString(query);
        if (includePath)
        {
            writer.WriteProperty("path");
            if (path is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteString(path);
            }
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class SearchTextArgumentsDto
{
    [JsonPropertyName("query")]
    [JsonPropertyOrder(0)]
    public string? Query { get; set; }

    [JsonPropertyName("path")]
    [JsonPropertyOrder(1)]
    public string? Path { get; set; }
}

internal static partial class AgentToolResultAdmission
{
    private static bool TryAdmitSearch(
        PreparedSearchTextCall call,
        ReviewedIdentity expectedIdentity,
        JsonElement root,
        byte[] canonical,
        AgentObservation observation)
    {
        var matches = root.GetProperty("matches")
            .EnumerateArray()
            .Select(match => new SearchMatch(
                match.GetProperty("path").GetString()!,
                match.GetProperty("raw_sha256").GetString()!,
                match.GetProperty("line").GetInt32(),
                match.GetProperty("text").GetString()!))
            .ToImmutableArray();
        var result = new SearchTextResult(
            root.GetProperty("status").GetString()!,
            ReadIdentity(root.GetProperty("reviewed_identity")),
            root.GetProperty("query_sha256").GetString()!,
            NullableString(root.GetProperty("path")),
            root.GetProperty("files_scanned").GetInt32(),
            root.GetProperty("raw_bytes_scanned").GetInt64(),
            root.GetProperty("skipped_invalid_utf8").GetInt32(),
            root.GetProperty("skipped_binary").GetInt32(),
            root.GetProperty("skipped_lone_cr").GetInt32(),
            root.GetProperty("skipped_oversized").GetInt32(),
            matches,
            root.GetProperty("truncated").GetBoolean(),
            NullableString(root.GetProperty("truncation_reason")),
            root.GetProperty("observation_id").GetString()!);
        var returned = matches
            .GroupBy(match => match.Path, StringComparer.Ordinal)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.Select(match => match.Line).ToImmutableHashSet(),
                StringComparer.Ordinal);
        return result.ReviewedIdentity == expectedIdentity &&
            StringComparer.Ordinal.Equals(result.Status, "ok") &&
            StringComparer.Ordinal.Equals(
                result.QuerySha256,
                AgentCanonical.QuerySha256(call.Arguments.Query)) &&
            StringComparer.Ordinal.Equals(result.Path, call.Arguments.Path) &&
            result.FilesScanned >= 0 &&
            result.RawBytesScanned >= 0 &&
            result.SkippedInvalidUtf8 >= 0 &&
            result.SkippedBinary >= 0 &&
            result.SkippedLoneCr >= 0 &&
            result.SkippedOversized >= 0 &&
            matches.All(match =>
                RepositoryPath.IsValid(match.Path) &&
                IsLowerHex(match.RawSha256, 64) &&
                match.Line >= 1 &&
                (call.Arguments.Path is null ||
                    StringComparer.Ordinal.Equals(
                        match.Path,
                        call.Arguments.Path))) &&
            IsLowerHex(result.ObservationId, 64) &&
            canonical.AsSpan().SequenceEqual(SearchTextResultWriter.Write(result)) &&
            StringComparer.Ordinal.Equals(
                result.ObservationId,
                AgentCanonical.HashDomain(
                    AgentCanonical.SearchObservationDomain,
                    SearchTextResultWriter.Write(
                        result with { ObservationId = null },
                        includeObservationId: false))) &&
            ObservationMatches(
                observation,
                expectedIdentity,
                result.ObservationId!,
                returned);
    }
}

internal sealed partial class SnapshotToolExecutor
{
    private async ValueTask<AgentToolExecution> ExecuteSearchAsync(
        PreparedSearchTextCall call,
        CancellationToken cancellationToken)
    {
        if (call.Arguments.Path is not null)
        {
            var pathFailure = ValidatePath(call.Arguments.Path);
            if (pathFailure is not null)
            {
                return AgentToolExecution.Failure(pathFailure);
            }
        }

        var paths = call.Arguments.Path is null
            ? snapshot.OrderedTrackedFiles
            : ImmutableArray.Create(call.Arguments.Path);
        var matches = ImmutableArray.CreateBuilder<SearchMatch>();
        var filesScanned = 0;
        long rawBytesScanned = 0;
        var skippedInvalidUtf8 = 0;
        var skippedBinary = 0;
        var skippedLoneCr = 0;
        var skippedOversized = 0;
        string? truncationReason = null;

        foreach (var path in paths)
        {
            if (filesScanned >= AgentLimits.SearchFiles)
            {
                truncationReason = "files_scanned";
                break;
            }

            filesScanned++;
            var metadata = fileAccess.InspectMetadata(snapshot, path);
            var metadataFailure = AccessFailure(metadata.Status);
            if (metadataFailure is not null)
            {
                return AgentToolExecution.Failure(metadataFailure);
            }

            if (metadata.Length > AgentLimits.SearchFileBytes)
            {
                if (call.Arguments.Path is not null)
                {
                    return AgentToolExecution.Failure(
                        AgentFailureCodes.ToolFileTooLarge);
                }

                skippedOversized++;
                continue;
            }

            if (rawBytesScanned + metadata.Length > AgentLimits.SearchRawBytes)
            {
                truncationReason = "bytes_scanned";
                break;
            }

            var probe = fileAccess.Probe(snapshot, path);
            var probeFailure = AccessFailure(probe.Status);
            if (probeFailure is not null)
            {
                return AgentToolExecution.Failure(probeFailure);
            }

            if (probe.Length != metadata.Length)
            {
                return AgentToolExecution.Failure(
                    AgentFailureCodes.ToolPathUnsafe);
            }

            var read = await fileAccess.ReadAsync(
                snapshot,
                path,
                probe,
                cancellationToken);
            var readFailure = AccessFailure(read.Status);
            if (readFailure is not null)
            {
                return AgentToolExecution.Failure(readFailure);
            }

            var bytes = read.Bytes!;
            rawBytesScanned += bytes.Length;
            var classified = Decode(bytes);
            if (classified.FailureCode is not null)
            {
                if (call.Arguments.Path is not null)
                {
                    return AgentToolExecution.Failure(classified.FailureCode);
                }

                switch (classified.FailureCode)
                {
                    case AgentFailureCodes.ToolFileBinary:
                        skippedBinary++;
                        break;
                    case AgentFailureCodes.ToolFileInvalidUtf8:
                        skippedInvalidUtf8++;
                        break;
                    case AgentFailureCodes.ToolFileLoneCr:
                        skippedLoneCr++;
                        break;
                }

                continue;
            }

            var rawSha256 = AgentCanonical.HashRaw(bytes);
            for (var lineIndex = 0; lineIndex < classified.Lines.Length; lineIndex++)
            {
                if (!classified.Lines[lineIndex].Contains(
                        call.Arguments.Query,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                if (matches.Count >= AgentLimits.SearchMatches)
                {
                    truncationReason = "matches";
                    break;
                }

                var match = new SearchMatch(
                    path,
                    rawSha256,
                    lineIndex + 1,
                    classified.Lines[lineIndex]);
                matches.Add(match);
                var provisional = CreateSearchResult(
                    call.Arguments,
                    filesScanned,
                    rawBytesScanned,
                    skippedInvalidUtf8,
                    skippedBinary,
                    skippedLoneCr,
                    skippedOversized,
                    matches,
                    false,
                    null,
                    new string('0', 64));
                if (SearchTextResultWriter.Write(provisional).Length >
                    AgentLimits.ToolResultBytes)
                {
                    matches.RemoveAt(matches.Count - 1);
                    truncationReason = "result_bytes";
                    break;
                }
            }

            if (truncationReason is not null)
            {
                break;
            }
        }

        SearchTextResult withoutIdentity;
        while (true)
        {
            withoutIdentity = CreateSearchResult(
                call.Arguments,
                filesScanned,
                rawBytesScanned,
                skippedInvalidUtf8,
                skippedBinary,
                skippedLoneCr,
                skippedOversized,
                matches,
                truncationReason is not null,
                truncationReason,
                null);
            var sized = withoutIdentity with
            {
                ObservationId = new string('0', 64),
            };
            if (SearchTextResultWriter.Write(sized).Length <=
                AgentLimits.ToolResultBytes)
            {
                break;
            }

            if (matches.Count == 0)
            {
                return AgentToolExecution.Failure(
                    AgentFailureCodes.ToolResultLimit);
            }

            matches.RemoveAt(matches.Count - 1);
            truncationReason = "result_bytes";
        }

        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.SearchObservationDomain,
            SearchTextResultWriter.Write(withoutIdentity, includeObservationId: false));
        var result = withoutIdentity with { ObservationId = observationId };
        var canonical = SearchTextResultWriter.Write(result);
        if (canonical.Length > AgentLimits.ToolResultBytes)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolResultLimit);
        }

        var returned = matches
            .GroupBy(match => match.Path, StringComparer.Ordinal)
            .ToImmutableDictionary(
                group => group.Key,
                group => group.Select(match => match.Line).ToImmutableHashSet(),
                StringComparer.Ordinal);
        return new AgentToolExecution(
            true,
            null,
            StrictUtf8.GetString(canonical),
            canonical,
            new AgentObservation(observationId, snapshot.Identity, returned));
    }

    private SearchTextResult CreateSearchResult(
        SearchTextArguments arguments,
        int filesScanned,
        long rawBytesScanned,
        int skippedInvalidUtf8,
        int skippedBinary,
        int skippedLoneCr,
        int skippedOversized,
        IEnumerable<SearchMatch> matches,
        bool truncated,
        string? truncationReason,
        string? observationId) =>
        new(
            "ok",
            snapshot.Identity,
            AgentCanonical.QuerySha256(arguments.Query),
            arguments.Path,
            filesScanned,
            rawBytesScanned,
            skippedInvalidUtf8,
            skippedBinary,
            skippedLoneCr,
            skippedOversized,
            matches.ToImmutableArray(),
            truncated,
            truncationReason,
            observationId);
}

internal sealed record SearchMatch(
    string Path,
    string RawSha256,
    int Line,
    string Text);

internal sealed record SearchTextResult(
    string Status,
    ReviewedIdentity ReviewedIdentity,
    string QuerySha256,
    string? Path,
    int FilesScanned,
    long RawBytesScanned,
    int SkippedInvalidUtf8,
    int SkippedBinary,
    int SkippedLoneCr,
    int SkippedOversized,
    ImmutableArray<SearchMatch> Matches,
    bool Truncated,
    string? TruncationReason,
    string? ObservationId);

internal static class SearchTextResultWriter
{
    internal static byte[] Write(
        SearchTextResult result,
        bool includeObservationId = true)
    {
        var writer = new Rfc8785Writer(1_024);
        writer.WriteObjectStart();
        writer.WriteProperty("status");
        writer.WriteString(result.Status);
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, result.ReviewedIdentity);
        writer.WriteProperty("query_sha256");
        writer.WriteString(result.QuerySha256);
        writer.WriteProperty("path");
        WriteNullableString(ref writer, result.Path);
        writer.WriteProperty("files_scanned");
        writer.WriteNumber(result.FilesScanned);
        writer.WriteProperty("raw_bytes_scanned");
        writer.WriteNumber(result.RawBytesScanned);
        writer.WriteProperty("skipped_invalid_utf8");
        writer.WriteNumber(result.SkippedInvalidUtf8);
        writer.WriteProperty("skipped_binary");
        writer.WriteNumber(result.SkippedBinary);
        writer.WriteProperty("skipped_lone_cr");
        writer.WriteNumber(result.SkippedLoneCr);
        writer.WriteProperty("skipped_oversized");
        writer.WriteNumber(result.SkippedOversized);
        writer.WriteProperty("matches");
        writer.WriteArrayStart();
        for (var index = 0; index < result.Matches.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            var match = result.Matches[index];
            writer.WriteObjectStart();
            writer.WriteProperty("path");
            writer.WriteString(match.Path);
            writer.WriteProperty("raw_sha256");
            writer.WriteString(match.RawSha256);
            writer.WriteProperty("line");
            writer.WriteNumber(match.Line);
            writer.WriteProperty("text");
            writer.WriteString(match.Text);
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
