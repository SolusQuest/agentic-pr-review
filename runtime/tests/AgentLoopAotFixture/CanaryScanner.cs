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

    // No provider transport is exercised by this APR-domain proof.
    internal static bool VerifyDomainOutput(ProofCanaryValues canaries, IEnumerable<byte[]> channels) =>
        canaries.Text.Count == CanarySet.Classes.Length && canaries.StateKey.Length == 32 &&
        channels.All(channel => !ContainsForbidden(canaries, channel));

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

}
