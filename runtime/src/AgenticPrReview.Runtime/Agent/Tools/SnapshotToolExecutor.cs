using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal abstract record PreparedAgentToolCall(
    string CallId,
    string Name,
    byte[] CanonicalArguments);

internal sealed record PreparedListFilesCall(
    string CallId,
    ListFilesArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.ListFilesName,
        Arguments.CanonicalBytes);

internal sealed record PreparedReadFileCall(
    string CallId,
    ReadFileArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.ReadFileName,
        Arguments.CanonicalBytes);

internal sealed record PreparedSearchTextCall(
    string CallId,
    SearchTextArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.SearchTextName,
        Arguments.CanonicalBytes);

internal sealed record PreparedFinishReviewCall(
    string CallId,
    FinishReviewArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.FinishReviewName,
        Arguments.CanonicalBytes);

internal sealed record AgentObservation(
    string ObservationId,
    ReviewedIdentity Identity,
    ImmutableDictionary<string, ImmutableHashSet<int>> ReturnedLines)
{
    internal bool Grounds(AgentEvidence evidence)
    {
        if (!StringComparer.Ordinal.Equals(ObservationId, evidence.ObservationId) ||
            evidence.StartLine < 1 ||
            evidence.EndLine < evidence.StartLine ||
            !ReturnedLines.TryGetValue(evidence.Path, out var lines))
        {
            return false;
        }

        for (var line = evidence.StartLine; line <= evidence.EndLine; line++)
        {
            if (!lines.Contains(line))
            {
                return false;
            }
        }

        return true;
    }
}

internal sealed record AgentToolExecution(
    bool Succeeded,
    string? FailureCode,
    string? ResultJson,
    byte[]? CanonicalResult,
    AgentObservation? Observation)
{
    internal static AgentToolExecution Failure(string code) =>
        new(false, code, null, null, null);
}

internal static class AgentToolResultAdmission
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsFrozenFailureCode(string? code) =>
        code is AgentFailureCodes.ToolPathInvalid or
            AgentFailureCodes.ToolPathNotTracked or
            AgentFailureCodes.ToolCursorInvalid or
            AgentFailureCodes.ToolPathUnsafe or
            AgentFailureCodes.ToolFileTooLarge or
            AgentFailureCodes.ToolFileBinary or
            AgentFailureCodes.ToolFileInvalidUtf8 or
            AgentFailureCodes.ToolFileLoneCr or
            AgentFailureCodes.ToolIoFailed or
            AgentFailureCodes.ToolResultLimit;

    internal static bool TryAdmit(
        PreparedAgentToolCall call,
        ReviewedIdentity expectedIdentity,
        AgentToolExecution execution,
        out string resultJson,
        out AgentObservation observation)
    {
        resultJson = string.Empty;
        observation = null!;
        if (!execution.Succeeded ||
            execution.FailureCode is not null ||
            execution.ResultJson is null ||
            execution.CanonicalResult is null ||
            execution.Observation is null)
        {
            return false;
        }

        try
        {
            resultJson = StrictUtf8.GetString(execution.CanonicalResult);
            if (!StringComparer.Ordinal.Equals(resultJson, execution.ResultJson))
            {
                return false;
            }

            using var document = JsonDocument.Parse(execution.CanonicalResult);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var admitted = call switch
            {
                PreparedListFilesCall list => TryAdmitListFiles(
                    list,
                    expectedIdentity,
                    root,
                    execution.CanonicalResult,
                    execution.Observation),
                PreparedReadFileCall read => TryAdmitRead(
                    read,
                    expectedIdentity,
                    root,
                    execution.CanonicalResult,
                    execution.Observation),
                PreparedSearchTextCall search => TryAdmitSearch(
                    search,
                    expectedIdentity,
                    root,
                    execution.CanonicalResult,
                    execution.Observation),
                _ => false,
            };
            if (!admitted)
            {
                resultJson = string.Empty;
                return false;
            }

            observation = execution.Observation;
            return true;
        }
        catch
        {
            resultJson = string.Empty;
            observation = null!;
            return false;
        }
    }

    private static bool TryAdmitListFiles(
        PreparedListFilesCall call,
        ReviewedIdentity expectedIdentity,
        JsonElement root,
        byte[] canonical,
        AgentObservation observation)
    {
        var paths = root.GetProperty("paths")
            .EnumerateArray()
            .Select(path => path.GetString()!)
            .ToImmutableArray();
        var result = new ListFilesResult(
            root.GetProperty("status").GetString()!,
            ReadIdentity(root.GetProperty("reviewed_identity")),
            NullableString(root.GetProperty("prefix")),
            NullableString(root.GetProperty("after")),
            paths,
            root.GetProperty("truncated").GetBoolean(),
            NullableString(root.GetProperty("next_after")),
            root.GetProperty("observation_id").GetString()!);
        return result.ReviewedIdentity == expectedIdentity &&
            StringComparer.Ordinal.Equals(result.Status, "ok") &&
            StringComparer.Ordinal.Equals(result.Prefix, call.Arguments.Prefix) &&
            StringComparer.Ordinal.Equals(result.After, call.Arguments.After) &&
            result.Paths.Length <= AgentLimits.ListFilesEntries &&
            PathsAreAdmissible(result.Paths, result.Prefix, result.After) &&
            (!result.Truncated && result.NextAfter is null ||
                result.Truncated &&
                result.Paths.Length > 0 &&
                StringComparer.Ordinal.Equals(
                    result.NextAfter,
                    result.Paths[^1])) &&
            IsLowerHex(result.ObservationId, 64) &&
            canonical.Length <= AgentLimits.ToolResultBytes &&
            canonical.AsSpan().SequenceEqual(ListFilesResultWriter.Write(result)) &&
            StringComparer.Ordinal.Equals(
                result.ObservationId,
                AgentCanonical.HashDomain(
                    AgentCanonical.ListFilesObservationDomain,
                    ListFilesResultWriter.Write(
                        result with { ObservationId = null },
                        includeObservationId: false))) &&
            ObservationMatches(
                observation,
                expectedIdentity,
                result.ObservationId,
                EmptyReturnedLines());
    }

    private static bool PathsAreAdmissible(
        ImmutableArray<string> paths,
        string? prefix,
        string? after)
    {
        string? previous = null;
        foreach (var path in paths)
        {
            if (!RepositoryPath.IsValid(path) ||
                (prefix is not null &&
                    !StringComparer.Ordinal.Equals(path, prefix) &&
                    !path.StartsWith(prefix + "/", StringComparison.Ordinal)) ||
                (after is not null &&
                    StringComparer.Ordinal.Compare(path, after) <= 0) ||
                (previous is not null &&
                    StringComparer.Ordinal.Compare(path, previous) <= 0))
            {
                return false;
            }

            previous = path;
        }

        return true;
    }

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

    private static ReviewedIdentity ReadIdentity(JsonElement element) =>
        new(
            element.GetProperty("repository_id").GetString()!,
            element.GetProperty("review_target").GetInt64(),
            element.GetProperty("base_sha").GetString()!,
            element.GetProperty("head_sha").GetString()!);

    private static int? NullableInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    private static string? NullableString(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetString();

    private static ImmutableDictionary<string, ImmutableHashSet<int>>
        EmptyReturnedLines() =>
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal);

    private static bool ObservationMatches(
        AgentObservation observation,
        ReviewedIdentity expectedIdentity,
        string observationId,
        ImmutableDictionary<string, ImmutableHashSet<int>> returned)
    {
        if (observation.Identity != expectedIdentity ||
            !StringComparer.Ordinal.Equals(
                observation.ObservationId,
                observationId) ||
            observation.ReturnedLines.Count != returned.Count)
        {
            return false;
        }

        foreach (var pair in returned)
        {
            if (!observation.ReturnedLines.TryGetValue(pair.Key, out var lines) ||
                !lines.SetEquals(pair.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal interface IAgentToolExecutor
{
    string? Preflight(PreparedAgentToolCall call);

    ValueTask<AgentToolExecution> ExecuteAsync(
        PreparedAgentToolCall call,
        CancellationToken cancellationToken);
}

internal sealed class SnapshotToolExecutor(
    ReviewedSnapshot snapshot,
    IReviewedFileAccess fileAccess) : IAgentToolExecutor
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public string? Preflight(PreparedAgentToolCall call) =>
        call switch
        {
            PreparedListFilesCall list => ValidateListFiles(list.Arguments),
            PreparedReadFileCall read => ValidatePath(read.Arguments.Path),
            PreparedSearchTextCall { Arguments.Path: { } path } =>
                ValidatePath(path),
            PreparedSearchTextCall => null,
            _ => AgentFailureCodes.UnknownTool,
        };

    public ValueTask<AgentToolExecution> ExecuteAsync(
        PreparedAgentToolCall call,
        CancellationToken cancellationToken) =>
        call switch
        {
            PreparedListFilesCall list => ValueTask.FromResult(
                ExecuteListFiles(list, cancellationToken)),
            PreparedReadFileCall read => ExecuteReadAsync(read, cancellationToken),
            PreparedSearchTextCall search => ExecuteSearchAsync(search, cancellationToken),
            _ => ValueTask.FromResult(AgentToolExecution.Failure(
                AgentFailureCodes.UnknownTool)),
        };

    private AgentToolExecution ExecuteListFiles(
        PreparedListFilesCall call,
        CancellationToken cancellationToken)
    {
        var preflightFailure = ValidateListFiles(call.Arguments);
        if (preflightFailure is not null)
        {
            return AgentToolExecution.Failure(preflightFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var selection = CreateListFilesSelection(call.Arguments.Prefix);
        var position = ListFilesStartPosition(selection, call.Arguments.After);
        if (position < 0)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed);
        }

        var empty = new ListFilesResult(
            "ok",
            snapshot.Identity,
            call.Arguments.Prefix,
            call.Arguments.After,
            [],
            false,
            null,
            new string('0', 64));
        if (ListFilesResultWriter.Write(empty).Length > AgentLimits.ToolResultBytes)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolResultLimit);
        }

        var selected = ImmutableArray.CreateBuilder<string>();
        while (position < selection.Count &&
            selected.Count < AgentLimits.ListFilesEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ListFilesPathAt(selection, position);
            selected.Add(path);
            var hasMore = position + 1 < selection.Count;
            var provisional = empty with
            {
                Paths = selected.ToImmutable(),
                Truncated = hasMore,
                NextAfter = hasMore ? path : null,
            };
            if (ListFilesResultWriter.Write(provisional).Length >
                AgentLimits.ToolResultBytes)
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

        var truncated = position < selection.Count;
        var withoutIdentity = empty with
        {
            Paths = selected.ToImmutable(),
            Truncated = truncated,
            NextAfter = truncated ? selected[^1] : null,
            ObservationId = null,
        };
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ListFilesObservationDomain,
            ListFilesResultWriter.Write(
                withoutIdentity,
                includeObservationId: false));
        var result = withoutIdentity with { ObservationId = observationId };
        var canonical = ListFilesResultWriter.Write(result);
        if (canonical.Length > AgentLimits.ToolResultBytes)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolResultLimit);
        }

        return new AgentToolExecution(
            true,
            null,
            StrictUtf8.GetString(canonical),
            canonical,
            new AgentObservation(
                observationId,
                snapshot.Identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                    .WithComparers(StringComparer.Ordinal)));
    }

    private string? ValidateListFiles(ListFilesArguments arguments)
    {
        var selection = CreateListFilesSelection(arguments.Prefix);
        return arguments.After is null ||
            ListFilesStartPosition(selection, arguments.After) >= 0
                ? null
                : AgentFailureCodes.ToolCursorInvalid;
    }

    private ListFilesSelection CreateListFilesSelection(string? prefix)
    {
        var paths = snapshot.OrderedTrackedFiles;
        if (prefix is null)
        {
            return new ListFilesSelection(-1, 0, paths.Length);
        }

        var exact = LowerBound(paths, prefix);
        var exactIndex = exact < paths.Length &&
            StringComparer.Ordinal.Equals(paths[exact], prefix)
                ? exact
                : -1;
        var descendantsStart = LowerBound(paths, prefix + "/");
        var descendantsEnd = LowerBound(paths, prefix + "0");
        return new ListFilesSelection(
            exactIndex,
            descendantsStart,
            descendantsEnd);
    }

    private int ListFilesStartPosition(
        ListFilesSelection selection,
        string? after)
    {
        if (after is null)
        {
            return 0;
        }

        if (selection.ExactIndex >= 0 &&
            StringComparer.Ordinal.Equals(
                snapshot.OrderedTrackedFiles[selection.ExactIndex],
                after))
        {
            return 1;
        }

        var index = LowerBound(
            snapshot.OrderedTrackedFiles,
            after,
            selection.DescendantsStart,
            selection.DescendantsEnd);
        return index < selection.DescendantsEnd &&
            StringComparer.Ordinal.Equals(
                snapshot.OrderedTrackedFiles[index],
                after)
                ? selection.ExactCount + index - selection.DescendantsStart + 1
                : -1;
    }

    private string ListFilesPathAt(ListFilesSelection selection, int position)
    {
        if (selection.ExactIndex >= 0 && position == 0)
        {
            return snapshot.OrderedTrackedFiles[selection.ExactIndex];
        }

        return snapshot.OrderedTrackedFiles[
            selection.DescendantsStart + position - selection.ExactCount];
    }

    private static int LowerBound(
        ImmutableArray<string> paths,
        string value,
        int start = 0,
        int? end = null)
    {
        var low = start;
        var high = end ?? paths.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (StringComparer.Ordinal.Compare(paths[middle], value) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

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

    private string? ValidatePath(string path)
    {
        if (!RepositoryPath.IsValid(path))
        {
            return AgentFailureCodes.ToolPathInvalid;
        }

        return snapshot.Contains(path)
            ? null
            : AgentFailureCodes.ToolPathNotTracked;
    }

    private static string? AccessFailure(ReviewedFileAccessStatus status) =>
        status switch
        {
            ReviewedFileAccessStatus.Success => null,
            ReviewedFileAccessStatus.Unsafe => AgentFailureCodes.ToolPathUnsafe,
            ReviewedFileAccessStatus.IoFailure => AgentFailureCodes.ToolIoFailed,
            _ => AgentFailureCodes.ToolIoFailed,
        };

    private static ClassifiedText Decode(byte[] bytes)
    {
        if (bytes.AsSpan().IndexOf((byte)0) >= 0)
        {
            return new ClassifiedText(AgentFailureCodes.ToolFileBinary, []);
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return new ClassifiedText(AgentFailureCodes.ToolFileInvalidUtf8, []);
        }

        if (bytes.Length >= 3 &&
            bytes[0] == 0xEF &&
            bytes[1] == 0xBB &&
            bytes[2] == 0xBF &&
            text.Length > 0 &&
            text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\r' &&
                (index + 1 >= text.Length || text[index + 1] != '\n'))
            {
                return new ClassifiedText(AgentFailureCodes.ToolFileLoneCr, []);
            }
        }

        if (text.Length == 0)
        {
            return new ClassifiedText(null, []);
        }

        var split = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var length = split.Length;
        if (text.EndsWith('\n'))
        {
            length--;
        }

        return new ClassifiedText(null, split[..length].ToImmutableArray());
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

    private sealed record ClassifiedText(
        string? FailureCode,
        ImmutableArray<string> Lines);

    private readonly record struct ListFilesSelection(
        int ExactIndex,
        int DescendantsStart,
        int DescendantsEnd)
    {
        internal int ExactCount => ExactIndex >= 0 ? 1 : 0;

        internal int Count =>
            ExactCount + DescendantsEnd - DescendantsStart;
    }
}

internal sealed record ListFilesResult(
    string Status,
    ReviewedIdentity ReviewedIdentity,
    string? Prefix,
    string? After,
    ImmutableArray<string> Paths,
    bool Truncated,
    string? NextAfter,
    string? ObservationId);

internal static class ListFilesResultWriter
{
    internal static byte[] Write(
        ListFilesResult result,
        bool includeObservationId = true)
    {
        var writer = new Rfc8785Writer(1_024);
        writer.WriteObjectStart();
        writer.WriteProperty("status");
        writer.WriteString(result.Status);
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, result.ReviewedIdentity);
        writer.WriteProperty("prefix");
        WriteNullableString(ref writer, result.Prefix);
        writer.WriteProperty("after");
        WriteNullableString(ref writer, result.After);
        writer.WriteProperty("paths");
        writer.WriteArrayStart();
        for (var index = 0; index < result.Paths.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            writer.WriteString(result.Paths[index]);
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("truncated");
        writer.WriteBoolean(result.Truncated);
        writer.WriteProperty("next_after");
        WriteNullableString(ref writer, result.NextAfter);
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
