using System.Collections.Immutable;
using System.Text;

namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal sealed record ProofCanaryValues(
    string Scenario,
    ImmutableDictionary<string, string> Text,
    byte[] StateKey)
{
    internal string Provider => Text["provider"];

    internal static ProofCanaryValues Create(string scenario)
    {
        var values = CanarySet.Classes.ToImmutableDictionary(
            @class => @class,
            @class => CanarySet.Create(@class, scenario),
            StringComparer.Ordinal);
        return new ProofCanaryValues(
            scenario,
            values,
            CanarySet.StateKey(scenario));
    }
}

internal static class CanaryScanner
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool VerifyPositive(
        ProofCanaryValues canaries,
        Uri endpoint,
        IReadOnlyList<ProviderCapture> captures,
        IEnumerable<byte[]> forbiddenChannels)
    {
        if (canaries.Text.Count != CanarySet.Classes.Length ||
            canaries.Text.Values.Distinct(StringComparer.Ordinal).Count() !=
                CanarySet.Classes.Length ||
            canaries.StateKey.Length != 32 ||
            ContainsForbidden(
                canaries,
                StrictUtf8.GetBytes(endpoint.OriginalString)))
        {
            return false;
        }

        foreach (var capture in captures)
        {
            if (capture.HeaderNames.Length != capture.HeaderValues.Length ||
                ContainsForbidden(
                    canaries,
                    StrictUtf8.GetBytes(capture.RequestTarget)) ||
                ContainsForbidden(canaries, capture.Body))
            {
                return false;
            }

            var providerOccurrences = 0;
            for (var index = 0;
                index < capture.HeaderNames.Length;
                index++)
            {
                if (ContainsForbidden(
                        canaries,
                        StrictUtf8.GetBytes(capture.HeaderNames[index])))
                {
                    return false;
                }

                var value = capture.HeaderValues[index];
                if (StringComparer.Ordinal.Equals(
                        capture.HeaderNames[index],
                        "Authorization"))
                {
                    if (!StringComparer.Ordinal.Equals(
                            value,
                            string.Concat("Bearer ", canaries.Provider)) ||
                        ContainsAnyTextCanaryExceptProvider(canaries, value) ||
                        ContainsStateKeyRepresentation(
                            canaries,
                            StrictUtf8.GetBytes(value)))
                    {
                        return false;
                    }

                    providerOccurrences += CountOccurrences(
                        value,
                        canaries.Provider);
                }
                else if (ContainsForbidden(
                    canaries,
                    StrictUtf8.GetBytes(value)))
                {
                    return false;
                }
            }

            if (providerOccurrences != 1)
            {
                return false;
            }
        }

        return forbiddenChannels.All(channel =>
            !ContainsForbidden(canaries, channel));
    }

    internal static bool RejectsRepresentativeLeak(
        string channel)
    {
        var canaries = ProofCanaryValues.Create(
            string.Concat("issue88-leak-", channel));
        var leaked = channel switch
        {
            "model" => StrictUtf8.GetBytes(canaries.Text["github"]),
            "durable" => canaries.StateKey.ToArray(),
            "transport" => StrictUtf8.GetBytes(canaries.Provider),
            "diagnostic" => StrictUtf8.GetBytes(canaries.Text["cloud"]),
            "environment" => StrictUtf8.GetBytes(canaries.Text["signing"]),
            _ => throw new InvalidOperationException(),
        };
        return ContainsForbidden(canaries, leaked);
    }

    private static bool ContainsForbidden(
        ProofCanaryValues canaries,
        byte[] channel)
    {
        foreach (var value in canaries.Text.Values)
        {
            if (channel.AsSpan().IndexOf(StrictUtf8.GetBytes(value)) >= 0)
            {
                return true;
            }
        }

        return ContainsStateKeyRepresentation(canaries, channel);
    }

    private static bool ContainsStateKeyRepresentation(
        ProofCanaryValues canaries,
        byte[] channel)
    {
        if (channel.AsSpan().IndexOf(canaries.StateKey) >= 0)
        {
            return true;
        }

        var lowerHex = Convert.ToHexString(canaries.StateKey)
            .ToLowerInvariant();
        var upperHex = lowerHex.ToUpperInvariant();
        var base64 = Convert.ToBase64String(canaries.StateKey);
        return channel.AsSpan().IndexOf(StrictUtf8.GetBytes(lowerHex)) >= 0 ||
            channel.AsSpan().IndexOf(StrictUtf8.GetBytes(upperHex)) >= 0 ||
            channel.AsSpan().IndexOf(StrictUtf8.GetBytes(base64)) >= 0;
    }

    private static bool ContainsAnyTextCanaryExceptProvider(
        ProofCanaryValues canaries,
        string value) =>
        canaries.Text
            .Where(pair => !StringComparer.Ordinal.Equals(
                pair.Key,
                "provider"))
            .Any(pair => value.Contains(
                pair.Value,
                StringComparison.Ordinal));

    private static int CountOccurrences(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while (offset <= value.Length - needle.Length)
        {
            var found = value.IndexOf(
                needle,
                offset,
                StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            count++;
            offset = found + needle.Length;
        }

        return count;
    }
}
