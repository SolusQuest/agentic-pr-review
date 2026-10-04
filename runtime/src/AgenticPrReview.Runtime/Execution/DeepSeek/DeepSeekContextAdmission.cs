using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

// A conservative prompt bound, not provider usage or an exact token counter.
// Provenance and template/tokenizer assumptions: r7/session-capacity/README.md.
// Input is the already validated, bounded provider projection, including replayed reasoning.
internal static class DeepSeekContextAdmission
{
    internal const int ContextTokens = 1_000_000;
    internal const string Policy = "dsv41-utf8-upper-v1";
    internal const int FixedBytes = 4096;
    internal const int MessageBytes = 128;
    internal const int ToolBytes = 128;
    internal const int ParameterBytes = 128;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryEstimate(ReadOnlySpan<byte> projection, out long inputUpperBound)
    {
        inputUpperBound = 0;
        if (projection.Length is < 1 or > DeepSeekTransportPolicy.RequestBodyMaxBytes) return false;
        try
        {
            var reader = new Utf8JsonReader(projection, new JsonReaderOptions { MaxDepth = 64 });
            using var document = JsonDocument.ParseValue(ref reader);
            if (reader.BytesConsumed != projection.Length) return false;
            var root = document.RootElement;
            long total = FixedBytes;
            foreach (var message in root.GetProperty("messages").EnumerateArray())
            {
                total = checked(total + MessageBytes);
                foreach (var field in new[] { "content", "reasoning_content" })
                    if (message.TryGetProperty(field, out var text) && text.ValueKind != JsonValueKind.Null)
                        total = checked(total + Bytes(text.GetString()!));
                if (!message.TryGetProperty("tool_calls", out var calls)) continue;
                foreach (var call in calls.EnumerateArray())
                {
                    var function = call.GetProperty("function");
                    var arguments = function.GetProperty("arguments").GetString()!;
                    using var parsed = JsonDocument.Parse(arguments, new JsonDocumentOptions { MaxDepth = 64 });
                    if (parsed.RootElement.ValueKind != JsonValueKind.Object || !TryJsonBound(parsed.RootElement, out var bound)) return false;
                    total = checked(total + ToolBytes + Bytes(function.GetProperty("name").GetString()!) + bound +
                        (long)ParameterBytes * parsed.RootElement.EnumerateObject().Count());
                }
            }
            foreach (var tool in root.GetProperty("tools").EnumerateArray())
            {
                var function = tool.GetProperty("function");
                if (!TryJsonBound(function.GetProperty("parameters"), out var bound)) return false;
                // Python-style JSON may escape every ASCII control as six bytes.
                total = checked(total + ToolBytes + bound + 6L * Bytes(function.GetProperty("name").GetString()!) +
                    6L * Bytes(function.GetProperty("description").GetString()!));
            }
            inputUpperBound = total;
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            KeyNotFoundException or ArgumentException or OverflowException)
        {
            return false;
        }
    }

    internal static bool Allows(long inputUpperBound, DeepSeekRequestProfile profile) =>
        Allows(inputUpperBound, DeepSeekRequestWriter.MaxTokensFor(profile));

    internal static bool Allows(long inputUpperBound, int outputAllowance) =>
        outputAllowance is >= 1 and <= 65_536 && inputUpperBound >= 0 &&
        inputUpperBound <= ContextTokens - outputAllowance;

    private static int Bytes(string value) => StrictUtf8.GetByteCount(value);

    private static bool TryJsonBound(JsonElement value, out long bound)
    {
        bound = 0;
        long numbers = 0;
        if (!Visit(value, ref numbers)) return false;
        // Spaces after separators fit within twice the source JSON. Finite
        // binary64 normalization (including exponent expansion) fits in 32 bytes.
        bound = checked(2L * Bytes(value.GetRawText()) + 32 * numbers);
        return true;
    }

    private static bool Visit(JsonElement value, ref long numbers)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number)) return false;
                numbers++;
                return true;
            case JsonValueKind.Object:
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in value.EnumerateObject())
                    if (!names.Add(property.Name) || !Visit(property.Value, ref numbers)) return false;
                return true;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    if (!Visit(item, ref numbers)) return false;
                return true;
            default:
                return value.ValueKind is JsonValueKind.String or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        }
    }
}
