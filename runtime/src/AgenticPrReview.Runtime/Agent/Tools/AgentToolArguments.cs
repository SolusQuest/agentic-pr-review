using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static partial class AgentToolArguments
{
    private static byte[]? StrictInputBytes(
        string json,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        try
        {
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            return bytes.Length <= maximumBytes ? bytes : null;
        }
        catch (EncoderFallbackException)
        {
            return null;
        }
    }

    private static byte[]? ProviderComparisonBytes(
        byte[] input,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        try
        {
            using var document = JsonDocument.Parse(
                input,
                new JsonDocumentOptions { MaxDepth = 16 });
            var canonical = JsonElementCanonicalizer.Canonicalize(
                document.RootElement,
                maxDepth: 8,
                maxProperties: 64,
                maxArrayItems: 64,
                maxBytes: maximumBytes,
                out var capExceeded);
            return capExceeded ? null : canonical.ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
        catch (Rfc8785CanonicalizationException)
        {
            return null;
        }
    }

    private static bool MatchesInput(
        ReadOnlySpan<byte> strictInput,
        byte[]? providerComparison,
        byte[] expected,
        int maximumBytes = AgentLimits.ToolArgumentsBytes)
    {
        if (providerComparison is null)
        {
            return strictInput.SequenceEqual(expected);
        }

        var expectedComparison = ProviderComparisonBytes(
            expected,
            maximumBytes);
        return expectedComparison is not null &&
            providerComparison.AsSpan().SequenceEqual(expectedComparison);
    }
}

internal static class AgentTextValidation
{
    internal static bool IsOnlyFixedWhitespace(string value)
    {
        var any = false;
        foreach (var rune in value.EnumerateRunes())
        {
            any = true;
            if (!IsFixedWhitespace(rune.Value))
            {
                return false;
            }
        }

        return any;
    }

    private static bool IsFixedWhitespace(int scalar) =>
        scalar is 0x0009 or 0x000A or 0x000B or 0x000C or 0x000D or
            0x0020 or 0x0085 or
            0x00A0 or 0x1680 or 0x2028 or 0x2029 or 0x202F or 0x205F or
            0x3000 ||
        scalar is >= 0x2000 and <= 0x200A;
}
