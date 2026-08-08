using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record ListChangedFilesArguments(
    string? After,
    byte[] CanonicalBytes);

internal sealed record PreparedListChangedFilesCall(
    string CallId,
    ListChangedFilesArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.ListChangedFilesName,
        Arguments.CanonicalBytes);

internal static partial class AgentToolArguments
{
    internal static bool TryListChangedFiles(
        string json,
        out ListChangedFilesArguments? arguments) =>
        TryListChangedFiles(
            json,
            allowCanonicalNull: false,
            allowProviderSpelling: false,
            out arguments);

    internal static bool TryListChangedFilesCanonical(
        string json,
        out ListChangedFilesArguments? arguments) =>
        TryListChangedFiles(
            json,
            allowCanonicalNull: true,
            allowProviderSpelling: false,
            out arguments);

    internal static bool TryListChangedFilesProvider(
        string json,
        out ListChangedFilesArguments? arguments) =>
        TryListChangedFiles(
            json,
            allowCanonicalNull: false,
            allowProviderSpelling: true,
            out arguments);

    private static bool TryListChangedFiles(
        string json,
        bool allowCanonicalNull,
        bool allowProviderSpelling,
        out ListChangedFilesArguments? arguments)
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
        if (allowProviderSpelling && providerComparison is null)
        {
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize(
                input,
                AgentToolJsonContext.Default.ListChangedFilesArgumentsDto);
            if (dto is null ||
                dto.After is not null && !RepositoryPath.IsValid(dto.After))
            {
                return false;
            }

            var accepted = allowCanonicalNull
                ? MatchesInput(input, providerComparison,
                    WriteListChangedFiles(dto.After, includeAfter: true))
                : MatchesInput(input, providerComparison,
                    WriteListChangedFiles(dto.After, includeAfter: false)) ||
                    dto.After is not null &&
                    MatchesInput(input, providerComparison,
                        WriteListChangedFiles(dto.After, includeAfter: true));
            if (!accepted)
            {
                return false;
            }

            arguments = new ListChangedFilesArguments(
                dto.After,
                WriteListChangedFiles(dto.After, includeAfter: true));
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

    internal static byte[] WriteListChangedFiles(
        string? after,
        bool includeAfter)
    {
        var writer = new Rfc8785Writer(64);
        writer.WriteObjectStart();
        if (includeAfter)
        {
            writer.WriteProperty("after");
            ReviewedChangedFileWriter.WriteNullableString(ref writer, after);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ListChangedFilesArgumentsDto
{
    [JsonPropertyName("after")]
    [JsonPropertyOrder(0)]
    public string? After { get; set; }
}

internal static partial class AgentToolResultAdmission
{
    private static bool TryAdmitListChangedFiles(
        PreparedListChangedFilesCall call,
        ReviewedIdentity expectedIdentity,
        JsonElement root,
        byte[] canonical,
        AgentObservation observation)
    {
        var changes = root.GetProperty("changes")
            .EnumerateArray()
            .Select(ReadChangedFile)
            .ToImmutableArray();
        var result = new ListChangedFilesResult(
            root.GetProperty("status").GetString()!,
            ReadIdentity(root.GetProperty("reviewed_identity")),
            NullableString(root.GetProperty("after")),
            changes,
            root.GetProperty("truncated").GetBoolean(),
            NullableString(root.GetProperty("next_after")),
            root.GetProperty("observation_id").GetString()!);
        return result.ReviewedIdentity == expectedIdentity &&
            StringComparer.Ordinal.Equals(result.Status, "ok") &&
            StringComparer.Ordinal.Equals(result.After, call.Arguments.After) &&
            result.Changes.Length <= AgentLimits.ListChangedFilesEntries &&
            ChangesAreAdmissible(result.Changes, result.After) &&
            (!result.Truncated && result.NextAfter is null ||
                result.Truncated &&
                result.Changes.Length > 0 &&
                StringComparer.Ordinal.Equals(
                    result.NextAfter,
                    result.Changes[^1].Path)) &&
            IsLowerHex(result.ObservationId, 64) &&
            canonical.Length <= AgentLimits.ToolResultBytes &&
            canonical.AsSpan().SequenceEqual(
                ListChangedFilesResultWriter.Write(result)) &&
            StringComparer.Ordinal.Equals(
                result.ObservationId,
                AgentCanonical.HashDomain(
                    AgentCanonical.ListChangedFilesObservationDomain,
                    ListChangedFilesResultWriter.Write(
                        result with { ObservationId = null },
                        includeObservationId: false))) &&
            ObservationMatches(
                observation,
                expectedIdentity,
                result.ObservationId,
                EmptyReturnedLines());
    }

    private static ReviewedChangedFile ReadChangedFile(JsonElement element) =>
        new(
            element.GetProperty("path").GetString()!,
            NullableString(element.GetProperty("previous_path")),
            element.GetProperty("status").GetString()!,
            element.GetProperty("additions").GetInt32(),
            element.GetProperty("deletions").GetInt32(),
            element.GetProperty("changes").GetInt32(),
            element.GetProperty("patch_status").GetString()!,
            NullableString(element.GetProperty("patch_sha256")),
            element.GetProperty("source_truncated").GetBoolean());

    private static bool ChangesAreAdmissible(
        ImmutableArray<ReviewedChangedFile> changes,
        string? after)
    {
        var previous = after;
        foreach (var change in changes)
        {
            if (!ReviewedChangedFileValidation.IsShapeValid(change) ||
                previous is not null &&
                StringComparer.Ordinal.Compare(change.Path, previous) <= 0)
            {
                return false;
            }

            previous = change.Path;
        }

        return true;
    }
}

internal sealed partial class SnapshotToolExecutor
{
    private AgentToolExecution ExecuteListChangedFiles(
        PreparedListChangedFilesCall call,
        CancellationToken cancellationToken) =>
        ExecuteListChangedFiles(call, cancellationToken, AgentLimits.ToolResultBytes);

    internal AgentToolExecution ExecuteListChangedFilesWithLimit(
        PreparedListChangedFilesCall call,
        CancellationToken cancellationToken,
        int resultLimit) =>
        ExecuteListChangedFiles(call, cancellationToken, resultLimit);

    private AgentToolExecution ExecuteListChangedFiles(
        PreparedListChangedFilesCall call,
        CancellationToken cancellationToken,
        int resultLimit)
    {
        var preflightFailure = ValidateListChangedFiles(call.Arguments);
        if (preflightFailure is not null)
        {
            return AgentToolExecution.Failure(preflightFailure);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var position = ListChangedFilesStartPosition(call.Arguments.After);
        if (position < 0)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolIoFailed);
        }

        var empty = new ListChangedFilesResult(
            "ok",
            snapshot.Identity,
            call.Arguments.After,
            [],
            false,
            null,
            new string('0', 64));
        if (ListChangedFilesResultWriter.Write(empty).Length > resultLimit)
        {
            return AgentToolExecution.Failure(AgentFailureCodes.ToolResultLimit);
        }

        var selected = ImmutableArray.CreateBuilder<ReviewedChangedFile>();
        while (position < snapshot.OrderedChangedFiles.Length &&
            selected.Count < AgentLimits.ListChangedFilesEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var change = snapshot.OrderedChangedFiles[position];
            selected.Add(change);
            var hasMore = position + 1 < snapshot.OrderedChangedFiles.Length;
            var provisional = empty with
            {
                Changes = selected.ToImmutable(),
                Truncated = hasMore,
                NextAfter = hasMore ? change.Path : null,
            };
            if (ListChangedFilesResultWriter.Write(provisional).Length >
                resultLimit)
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

        var truncated = position < snapshot.OrderedChangedFiles.Length;
        var withoutObservation = empty with
        {
            Changes = selected.ToImmutable(),
            Truncated = truncated,
            NextAfter = truncated ? selected[^1].Path : null,
            ObservationId = null,
        };
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ListChangedFilesObservationDomain,
            ListChangedFilesResultWriter.Write(
                withoutObservation,
                includeObservationId: false));
        var result = withoutObservation with { ObservationId = observationId };
        var canonical = ListChangedFilesResultWriter.Write(result);
        if (canonical.Length > resultLimit)
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

    private string? ValidateListChangedFiles(ListChangedFilesArguments arguments) =>
        arguments.After is null ||
        ListChangedFilesStartPosition(arguments.After) >= 0
            ? null
            : AgentFailureCodes.ToolCursorInvalid;

    private int ListChangedFilesStartPosition(string? after)
    {
        if (after is null)
        {
            return 0;
        }

        var index = ChangedFileLowerBound(after);
        return index < snapshot.OrderedChangedFiles.Length &&
            StringComparer.Ordinal.Equals(
                snapshot.OrderedChangedFiles[index].Path,
                after)
                ? index + 1
                : -1;
    }

    private int ChangedFileLowerBound(string value)
    {
        var low = 0;
        var high = snapshot.OrderedChangedFiles.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (StringComparer.Ordinal.Compare(
                    snapshot.OrderedChangedFiles[middle].Path,
                    value) < 0)
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
}

internal sealed record ListChangedFilesResult(
    string Status,
    ReviewedIdentity ReviewedIdentity,
    string? After,
    ImmutableArray<ReviewedChangedFile> Changes,
    bool Truncated,
    string? NextAfter,
    string? ObservationId);

internal static class ListChangedFilesResultWriter
{
    internal static byte[] Write(
        ListChangedFilesResult result,
        bool includeObservationId = true)
    {
        var writer = new Rfc8785Writer(1_024);
        writer.WriteObjectStart();
        writer.WriteProperty("status");
        writer.WriteString(result.Status);
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, result.ReviewedIdentity);
        writer.WriteProperty("after");
        ReviewedChangedFileWriter.WriteNullableString(ref writer, result.After);
        writer.WriteProperty("changes");
        writer.WriteArrayStart();
        for (var index = 0; index < result.Changes.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            ReviewedChangedFileWriter.WriteTo(ref writer, result.Changes[index]);
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("truncated");
        writer.WriteBoolean(result.Truncated);
        writer.WriteProperty("next_after");
        ReviewedChangedFileWriter.WriteNullableString(ref writer, result.NextAfter);
        if (includeObservationId)
        {
            writer.WriteProperty("observation_id");
            writer.WriteString(result.ObservationId!);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}
