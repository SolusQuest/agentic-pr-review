using System.Text;

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
