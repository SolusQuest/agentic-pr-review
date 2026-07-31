using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Agent.Session;

internal static class AgentSessionToolObservationAdmission
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryValidateArguments(
        string callId,
        string name,
        string argumentsJson,
        ReadOnlySpan<byte> canonicalArguments)
    {
        if (!TryPrepareCall(
                callId,
                name,
                argumentsJson,
                out var prepared))
        {
            return false;
        }

        return canonicalArguments.SequenceEqual(
            prepared!.CanonicalArguments);
    }

    internal static bool TryAdmit(
        AgentSessionToolCallContent call,
        AgentSessionToolResultRecord stored,
        ReviewedIdentity expectedIdentity,
        byte[] canonicalResult,
        out AgentObservation? observation)
    {
        observation = null;
        if (!TryPrepareCall(
                call.CallId,
                call.Name,
                call.ArgumentsJson,
                out var prepared))
        {
            return false;
        }

        try
        {
            if (!StrictUtf8.GetBytes(call.ArgumentsJson)
                    .AsSpan()
                    .SequenceEqual(prepared!.CanonicalArguments))
            {
                return false;
            }
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        ImmutableDictionary<string, ImmutableHashSet<int>> returnedLines;
        try
        {
            if (StringComparer.Ordinal.Equals(
                    call.Name,
                    AgentToolRegistry.ReadDiffName))
            {
                if (!TryReadDiffReturnedLines(
                        canonicalResult,
                        out returnedLines))
                {
                    return false;
                }
            }
            else
            {
                returnedLines = EmptyReturnedLines();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            FormatException or
            InvalidOperationException or
            JsonException or
            KeyNotFoundException or
            NotSupportedException or
            OverflowException)
        {
            return false;
        }

        string resultJson;
        try
        {
            resultJson = StrictUtf8.GetString(canonicalResult);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        var execution = new AgentToolExecution(
            true,
            FailureCode: null,
            resultJson,
            canonicalResult,
            new AgentObservation(
                stored.ObservationId,
                expectedIdentity,
                returnedLines));
        if (!AgentToolResultAdmission.TryAdmit(
                prepared!,
                expectedIdentity,
                execution,
                out var admittedJson,
                out var admittedObservation) ||
            !StringComparer.Ordinal.Equals(
                admittedJson,
                stored.ResultJson))
        {
            return false;
        }

        observation = admittedObservation;
        return true;
    }

    private static bool TryPrepareCall(
        string callId,
        string name,
        string argumentsJson,
        out PreparedAgentToolCall? prepared)
    {
        prepared = null;
        switch (name)
        {
            case AgentToolRegistry.ListFilesName:
                if (AgentToolArguments.TryListFilesCanonical(
                        argumentsJson,
                        out var list))
                {
                    prepared = new PreparedListFilesCall(callId, list!);
                }

                break;
            case AgentToolRegistry.ListChangedFilesName:
                if (AgentToolArguments.TryListChangedFilesCanonical(
                        argumentsJson,
                        out var changed))
                {
                    prepared = new PreparedListChangedFilesCall(
                        callId,
                        changed!);
                }

                break;
            case AgentToolRegistry.ReadDiffName:
                if (AgentToolArguments.TryReadDiff(
                        argumentsJson,
                        out var diff))
                {
                    prepared = new PreparedReadDiffCall(callId, diff!);
                }

                break;
        }

        return prepared is not null;
    }

    private static bool TryReadDiffReturnedLines(
        byte[] canonicalResult,
        out ImmutableDictionary<string, ImmutableHashSet<int>> returnedLines)
    {
        returnedLines = EmptyReturnedLines();
        using var document = JsonDocument.Parse(canonicalResult);
        var root = document.RootElement;
        var path = root.GetProperty("path").GetString();
        if (path is null)
        {
            return false;
        }

        var lines = ImmutableHashSet.CreateBuilder<int>();
        foreach (var hunk in root.GetProperty("hunks").EnumerateArray())
        {
            foreach (var line in hunk.GetProperty("lines").EnumerateArray())
            {
                var kind = line.GetProperty("kind").GetString();
                if (kind is "context" or "addition")
                {
                    lines.Add(line.GetProperty("new_line").GetInt32());
                }
            }
        }

        if (lines.Count > 0)
        {
            returnedLines = returnedLines.Add(path, lines.ToImmutable());
        }

        return true;
    }

    private static ImmutableDictionary<string, ImmutableHashSet<int>>
        EmptyReturnedLines() =>
        ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
            .WithComparers(StringComparer.Ordinal);
}
