using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static partial class AgentToolResultAdmission
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
                PreparedListChangedFilesCall changed => TryAdmitListChangedFiles(
                    changed,
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
