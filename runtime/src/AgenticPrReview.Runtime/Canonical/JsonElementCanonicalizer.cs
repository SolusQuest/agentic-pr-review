using System.Collections.Immutable;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Canonicalizes arbitrary <see cref="JsonElement"/> values into RFC 8785
/// canonical bytes with duplicate-property detection and structural bounds.
/// Rejections raise <see cref="Rfc8785CanonicalizationException"/>; callers
/// map them to prefix-contract diagnostics.
/// </summary>
internal static class JsonElementCanonicalizer
{
    internal static ImmutableArray<byte> Canonicalize(
        JsonElement element,
        int maxDepth,
        int maxProperties,
        int maxArrayItems)
    {
        var writer = new Rfc8785Writer(4096);
        WriteValue(ref writer, element, depth: 1, maxDepth, maxProperties, maxArrayItems, path: string.Empty);
        return writer.ToImmutableArray();
    }

    /// <summary>
    /// Writes a nested canonical value into an enclosing writer. Used for
    /// values that were already validated/canonicalized as part of an
    /// envelope; bounds are re-applied defensively at the envelope limits.
    /// </summary>
    internal static void WriteCanonicalValue(ref Rfc8785Writer writer, JsonElement element)
    {
        WriteValue(ref writer, element, depth: 1, 64, 256, 1_024, path: string.Empty);
    }

    private static void WriteValue(
        ref Rfc8785Writer writer,
        JsonElement element,
        int depth,
        int maxDepth,
        int maxProperties,
        int maxArrayItems,
        string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                if (depth > maxDepth)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.DepthLimitExceeded,
                        $"Object nesting exceeds the depth limit of {maxDepth}.",
                        path);
                }

                var properties = new List<(string Name, JsonElement Value)>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        throw new Rfc8785CanonicalizationException(
                            Rfc8785RejectionReason.DuplicateProperty,
                            "Duplicate JSON property name.",
                            path);
                    }

                    properties.Add((property.Name, property.Value));
                }

                if (properties.Count > maxProperties)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.PropertyCountExceeded,
                        $"Object property count exceeds the limit of {maxProperties}.",
                        path);
                }

                // RFC 8785 orders keys by UTF-16 code units; StringComparer.Ordinal
                // compares .NET strings by UTF-16 code unit.
                properties.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));

                writer.WriteObjectStart();
                for (var i = 0; i < properties.Count; i++)
                {
                    if (i > 0)
                    {
                        writer.WriteComma();
                    }

                    writer.WriteProperty(properties[i].Name);
                    WriteValue(
                        ref writer,
                        properties[i].Value,
                        depth + 1,
                        maxDepth,
                        maxProperties,
                        maxArrayItems,
                        path + "/" + properties[i].Name);
                }

                writer.WriteObjectEnd();
                return;
            }

            case JsonValueKind.Array:
            {
                if (depth > maxDepth)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.DepthLimitExceeded,
                        $"Array nesting exceeds the depth limit of {maxDepth}.",
                        path);
                }

                var count = element.GetArrayLength();
                if (count > maxArrayItems)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.ArrayLengthExceeded,
                        $"Array length exceeds the limit of {maxArrayItems}.",
                        path);
                }

                writer.WriteArrayStart();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index > 0)
                    {
                        writer.WriteComma();
                    }

                    WriteValue(
                        ref writer,
                        item,
                        depth + 1,
                        maxDepth,
                        maxProperties,
                        maxArrayItems,
                        path + "/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    index++;
                }

                writer.WriteArrayEnd();
                return;
            }

            case JsonValueKind.String:
                writer.WriteString(element.GetString()!);
                return;

            case JsonValueKind.Number:
            {
                if (!element.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.NonFiniteNumber,
                        "Number is outside the finite IEEE-754 binary64 domain.",
                        path);
                }

                writer.WriteNumber(number);
                return;
            }

            case JsonValueKind.True:
                writer.WriteBoolean(true);
                return;

            case JsonValueKind.False:
                writer.WriteBoolean(false);
                return;

            case JsonValueKind.Null:
                writer.WriteNull();
                return;

            default:
                throw new Rfc8785CanonicalizationException(
                    Rfc8785RejectionReason.NonFiniteNumber,
                    $"Unsupported JSON value kind {element.ValueKind}.",
                    path);
        }
    }
}
