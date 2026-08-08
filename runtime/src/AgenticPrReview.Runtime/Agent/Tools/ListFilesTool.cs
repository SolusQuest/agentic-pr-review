using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal sealed record ListFilesArguments(
    string? Prefix,
    string? After,
    byte[] CanonicalBytes);

internal sealed record PreparedListFilesCall(
    string CallId,
    ListFilesArguments Arguments)
    : PreparedAgentToolCall(
        CallId,
        AgentToolRegistry.ListFilesName,
        Arguments.CanonicalBytes);

internal static partial class AgentToolArguments
{
    internal static bool TryListFiles(
        string json,
        out ListFilesArguments? arguments) =>
        TryListFiles(
            json,
            allowCanonicalNulls: false,
            allowProviderSpelling: false,
            out arguments);

    internal static bool TryListFilesCanonical(
        string json,
        out ListFilesArguments? arguments) =>
        TryListFiles(
            json,
            allowCanonicalNulls: true,
            allowProviderSpelling: false,
            out arguments);

    internal static bool TryListFilesProvider(
        string json,
        out ListFilesArguments? arguments) =>
        TryListFiles(
            json,
            allowCanonicalNulls: false,
            allowProviderSpelling: true,
            out arguments);

    private static bool TryListFiles(
        string json,
        bool allowCanonicalNulls,
        bool allowProviderSpelling,
        out ListFilesArguments? arguments)
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
                AgentToolJsonContext.Default.ListFilesArgumentsDto);
            if (dto is null ||
                (dto.Prefix is not null && !RepositoryPath.IsValid(dto.Prefix)) ||
                (dto.After is not null && !RepositoryPath.IsValid(dto.After)))
            {
                return false;
            }

            var accepted = allowCanonicalNulls
                ? MatchesInput(input, providerComparison,
                    WriteListFiles(dto.Prefix, dto.After, true, true))
                : MatchesInput(input, providerComparison,
                    WriteListFiles(dto.Prefix, dto.After, false, false)) ||
                    dto.Prefix is not null &&
                    MatchesInput(input, providerComparison,
                        WriteListFiles(dto.Prefix, dto.After, true, false)) ||
                    dto.After is not null &&
                    MatchesInput(input, providerComparison,
                        WriteListFiles(dto.Prefix, dto.After, false, true)) ||
                    dto.Prefix is not null &&
                    dto.After is not null &&
                    MatchesInput(input, providerComparison,
                        WriteListFiles(dto.Prefix, dto.After, true, true));
            if (!accepted)
            {
                return false;
            }

            var canonical = WriteListFiles(dto.Prefix, dto.After, true, true);
            arguments = new ListFilesArguments(
                dto.Prefix,
                dto.After,
                canonical);
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

    internal static byte[] WriteListFiles(
        string? prefix,
        string? after,
        bool includePrefix,
        bool includeAfter)
    {
        var writer = new Rfc8785Writer(128);
        writer.WriteObjectStart();
        if (includePrefix)
        {
            writer.WriteProperty("prefix");
            if (prefix is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteString(prefix);
            }
        }

        if (includeAfter)
        {
            writer.WriteProperty("after");
            if (after is null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteString(after);
            }
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ListFilesArgumentsDto
{
    [JsonPropertyName("prefix")]
    [JsonPropertyOrder(0)]
    public string? Prefix { get; set; }

    [JsonPropertyName("after")]
    [JsonPropertyOrder(1)]
    public string? After { get; set; }
}

internal static partial class AgentToolResultAdmission
{
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
}

internal sealed partial class SnapshotToolExecutor
{
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
