using System.Collections.Immutable;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Canonical;

/// <summary>
/// Canonicalizes arbitrary <see cref="JsonElement"/> values into RFC 8785
/// canonical bytes with duplicate-property detection and structural bounds.
/// Rejections raise <see cref="Rfc8785CanonicalizationException"/>; callers
/// map them to prefix-contract diagnostics.
///
/// When a byte cap is supplied, the writer switches to discard/count-only
/// mode once the cap is crossed: emission stops growing but the traversal
/// always completes, so canonical-domain defects anywhere in the document
/// still surface before the cap verdict.
/// </summary>
internal static class JsonElementCanonicalizer
{
    /// <summary>Sentinel standing in for a property name that cannot be decoded as valid UTF-16.</summary>
    internal const string InvalidNameSentinel = "\uD800";

    internal static ImmutableArray<byte> Canonicalize(
        JsonElement element,
        int maxDepth,
        int maxProperties,
        int maxArrayItems,
        long maxBytes,
        out bool capExceeded)
    {
        var writer = new Rfc8785Writer(4096);
        if (maxBytes != long.MaxValue)
        {
            writer.DiscardLimit = maxBytes;
        }

        WriteValue(ref writer, element, depth: 0, maxDepth, maxProperties, maxArrayItems, segments: System.Array.Empty<CanonicalPathSegment>());
        capExceeded = writer.Exceeded;
        return writer.ToImmutableArray();
    }

    /// <summary>
    /// Writes a nested canonical value into an enclosing writer. Used for
    /// values that were already validated/canonicalized as part of an
    /// envelope; bounds are re-applied defensively at the envelope limits.
    /// </summary>
    internal static void WriteCanonicalValue(ref Rfc8785Writer writer, JsonElement element)
    {
        WriteValue(ref writer, element, depth: 1, 64, 256, 1_024, segments: System.Array.Empty<CanonicalPathSegment>());
    }

    private static CanonicalPathSegment[] Append(System.Collections.Generic.IReadOnlyList<CanonicalPathSegment> segments, CanonicalPathSegment next)
    {
        var copy = new CanonicalPathSegment[segments.Count + 1];
        for (var i = 0; i < segments.Count; i++)
        {
            copy[i] = segments[i];
        }

        copy[segments.Count] = next;
        return copy;
    }

    /// <summary>
    /// Enumerates an object's properties from bounded raw token spans. The
    /// token views preserve invalid UTF-16 names without copying the complete
    /// object subtree.
    /// </summary>
    private static System.Collections.Generic.List<LenientJsonObjectEnumerator.Entry> EnumerateProperties(
        JsonElement element,
        int maxProperties)
    {
        var properties = new System.Collections.Generic.List<LenientJsonObjectEnumerator.Entry>();
        foreach (var entry in LenientJsonObjectEnumerator.Enumerate(element, maxProperties + 1))
        {
            properties.Add(entry);
        }

        return properties;
    }

    private static void WriteValue(
        ref Rfc8785Writer writer,
        JsonElement element,
        int depth,
        int maxDepth,
        int maxProperties,
        int maxArrayItems,
        System.Collections.Generic.IReadOnlyList<CanonicalPathSegment> segments)
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
                        segments);
                }

                var properties = new List<LenientJsonObjectEnumerator.Entry>();
                foreach (var entry in EnumerateProperties(element, maxProperties))
                {
                    // Preserve the input-occurrence duplicate check without
                    // decoding any complete property name. The object bound
                    // caps this comparison at 256 prior raw-token views.
                    if (properties.Any(previous => LenientJsonObjectEnumerator.CompareNames(previous, entry) == 0))
                    {
                        var diagnosticName = LenientJsonObjectEnumerator.DiagnosticName(entry);
                        throw new Rfc8785CanonicalizationException(
                            Rfc8785RejectionReason.DuplicateProperty,
                            "Duplicate JSON property name.",
                            Append(segments, CanonicalPathSegment.Property(diagnosticName)));
                    }

                    properties.Add(entry);
                }

                if (properties.Count > maxProperties)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.PropertyCountExceeded,
                        $"Object property count exceeds the limit of {maxProperties}.",
                        segments);
                }

                // RFC 8785 orders keys by decoded UTF-16 code units. The raw
                // comparer yields those units incrementally and retains no
                // token-sized string.
                properties.Sort(static (a, b) => LenientJsonObjectEnumerator.CompareNames(a, b));

                writer.WriteObjectStart();
                for (var i = 0; i < properties.Count; i++)
                {
                    var property = properties[i];
                    var diagnosticName = LenientJsonObjectEnumerator.DiagnosticName(property);
                    if (!LenientJsonObjectEnumerator.NameIsWellFormed(property))
                    {
                        throw new Rfc8785CanonicalizationException(
                            Rfc8785RejectionReason.UnpairedSurrogate,
                            "Unpaired UTF-16 surrogate in JSON property name.",
                            Append(segments, CanonicalPathSegment.Property(diagnosticName)));
                    }

                    // Property commas are handled by the writer's state machine;
                    // an explicit WriteComma here would double up.
                    writer.WriteRawProperty(LenientJsonObjectEnumerator.RawName(property));
                    WriteValue(
                        ref writer,
                        property.Value,
                        depth + 1,
                        maxDepth,
                        maxProperties,
                        maxArrayItems,
                        Append(segments, CanonicalPathSegment.Property(diagnosticName)));
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
                        $"Array nesting exceeds the limit of {maxDepth}.",
                        segments);
                }

                var count = element.GetArrayLength();
                if (count > maxArrayItems)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.ArrayLengthExceeded,
                        $"Array length exceeds the limit of {maxArrayItems}.",
                        segments);
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
                        Append(segments, CanonicalPathSegment.Index(index)));
                    index++;
                }

                writer.WriteArrayEnd();
                return;
            }

            case JsonValueKind.String:
            {
                try
                {
                    writer.WriteRawString(LenientJsonObjectEnumerator.RawStringValue(element));
                }
                catch (Rfc8785CanonicalizationException ex) when (ex.Reason == Rfc8785RejectionReason.UnpairedSurrogate)
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.UnpairedSurrogate,
                        "Unpaired UTF-16 surrogate in JSON string.",
                        segments);
                }
                return;
            }

            case JsonValueKind.Number:
            {
                if (!element.TryGetDouble(out var number) || double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new Rfc8785CanonicalizationException(
                        Rfc8785RejectionReason.NonFiniteNumber,
                        "Number is outside the finite IEEE-754 binary64 domain.",
                        segments);
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
                    segments);
        }
    }
}
