using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.Ledger;

internal static class LedgerSafePath
{
    public const int MaxDiagnosticChars = 256;
    public const int MaxDiagnosticBytes = 1024;
    public const string UnicodeCode = LedgerDiagnosticCodes.InvalidUnicode;

    private static readonly Lazy<SchemaResolver> Resolver = new(() => new SchemaResolver());

    public static SchemaPosition RootSchemaPosition => Resolver.Value.Root;

    public static LedgerDiagnostic? ScanForUnicodeViolation(JsonElement root)
    {
        var location = Scan(root, ImmutableArray<string>.Empty, RootSchemaPosition, true);
        if (!location.HasValue)
        {
            return null;
        }

        return new LedgerDiagnostic
        {
            Code = UnicodeCode,
            Message = FormatMessage(location.Value, UnicodeCode)
        };
    }

    public static string FormatMessage(UnsafeLocation location, string code)
    {
        if (location.IsRootScalar)
        {
            return $"{code}:";
        }

        var segments = location.Segments;
        var finalSegment = segments[^1];
        var leadingSegments = segments.RemoveAt(segments.Length - 1);
        var fullPath = BuildPath(segments);

        var codePrefixChars = code.Length;
        var codePrefixBytes = Encoding.UTF8.GetByteCount(code);
        var charBudget = MaxDiagnosticChars - codePrefixChars - 1;
        var byteBudget = MaxDiagnosticBytes - codePrefixBytes - 1;

        if (Fits(fullPath, charBudget, byteBudget))
        {
            return $"{code}:{fullPath}";
        }

        var reservedChars = "/<path-truncated>".Length + 1 + finalSegment.Length;
        var reservedBytes = Encoding.UTF8.GetByteCount("/<path-truncated>") + Encoding.UTF8.GetByteCount("/") + Encoding.UTF8.GetByteCount(finalSegment);
        var allowanceChars = charBudget - reservedChars;
        var allowanceBytes = byteBudget - reservedBytes;

        var prefixBuilder = new StringBuilder();
        var prefixByteCount = 0;
        var prefixCharCount = 0;
        foreach (var segment in leadingSegments)
        {
            var segmentWithSlash = "/" + segment;
            var segmentChars = segmentWithSlash.Length;
            var segmentBytes = Encoding.UTF8.GetByteCount(segmentWithSlash);
            if (prefixCharCount + segmentChars > allowanceChars || prefixByteCount + segmentBytes > allowanceBytes)
            {
                break;
            }

            prefixBuilder.Append(segmentWithSlash);
            prefixCharCount += segmentChars;
            prefixByteCount += segmentBytes;
        }

        return $"{code}:{prefixBuilder}/<path-truncated>/{finalSegment}";
    }

    public static string SanitizeInstancePointer(string pointer)
    {
        if (pointer == "")
        {
            return "";
        }

        var segments = pointer.Split('/');
        var position = RootSchemaPosition;
        var trusted = true;
        var sb = new StringBuilder();
        for (var i = 1; i < segments.Length; i++)
        {
            var rawSegment = segments[i].Replace("~1", "/").Replace("~0", "~");
            string sanitized;
            if (position is ArraySchemaPosition && int.TryParse(rawSegment, out _))
            {
                var resolved = ResolveArrayItem(position);
                sanitized = rawSegment;
                trusted = trusted && resolved.SchemaKnown;
                position = trusted ? resolved.ChildSchemaPosition : SchemaPosition.Unknown;
            }
            else
            {
                var resolved = ResolveProperty(position, rawSegment);
                var keyIsSchemaKnown = trusted && resolved.SchemaKnown;
                sanitized = SanitizeSegment(rawSegment, keyIsSchemaKnown);
                trusted = keyIsSchemaKnown;
                position = trusted ? resolved.ChildSchemaPosition : SchemaPosition.Unknown;
            }

            sb.Append('/');
            sb.Append(sanitized);
        }

        return sb.ToString();
    }

    public static ResolveResult ResolveProperty(SchemaPosition position, string key)
    {
        return position switch
        {
            ObjectSchemaPosition obj when obj.Node.Properties.TryGetValue(key, out var childNode) =>
                new ResolveResult(true, Resolver.Value.Normalize(childNode, obj.ActiveNodes)),
            ObjectSchemaPosition => new ResolveResult(false, SchemaPosition.Unknown),
            ArraySchemaPosition => new ResolveResult(false, SchemaPosition.Unknown),
            CompositeSchemaPosition comp => ResolveCompositeProperty(comp, key),
            _ => new ResolveResult(false, SchemaPosition.Unknown)
        };
    }

    public static ResolveResult ResolveArrayItem(SchemaPosition position)
    {
        return position switch
        {
            ArraySchemaPosition arr when arr.Node.Items is not null =>
                new ResolveResult(true, Resolver.Value.Normalize(arr.Node.Items, arr.ActiveNodes)),
            ArraySchemaPosition => new ResolveResult(false, SchemaPosition.Unknown),
            CompositeSchemaPosition comp => ResolveCompositeArrayItem(comp),
            _ => new ResolveResult(false, SchemaPosition.Unknown)
        };
    }

    private static UnsafeLocation? Scan(JsonElement element, ImmutableArray<string> segments, SchemaPosition position, bool trustedChain)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var rawText = element.GetRawText();
            if (IsInvalidRawJsonString(rawText))
            {
                return new UnsafeLocation
                {
                    Segments = segments,
                    IsPropertyNameViolation = false,
                    IsRootScalar = segments.IsEmpty
                };
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var resolved = ResolveArrayItem(position);
            var itemTrusted = trustedChain && resolved.SchemaKnown;
            var itemPosition = itemTrusted ? resolved.ChildSchemaPosition : SchemaPosition.Unknown;
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                var result = Scan(item, segments.Add(index.ToStringInvariant()), itemPosition, itemTrusted);
                if (result.HasValue)
                {
                    return result.Value;
                }

                index++;
            }

            return null;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject()
                .OrderBy(p => p.Name, StringComparer.Ordinal)
                .ToImmutableArray();

            foreach (var property in properties)
            {
                var key = property.Name;
                if (ContainsUnpairedSurrogate(key))
                {
                    return new UnsafeLocation
                    {
                        Segments = segments.Add("<invalid-utf16>"),
                        IsPropertyNameViolation = true,
                        IsRootScalar = false
                    };
                }

                if (key.Contains('\0'))
                {
                    return new UnsafeLocation
                    {
                        Segments = segments.Add("<invalid-nul>"),
                        IsPropertyNameViolation = true,
                        IsRootScalar = false
                    };
                }

                var resolved = ResolveProperty(position, key);
                var keyIsSchemaKnown = trustedChain && resolved.SchemaKnown;
                var segment = SanitizeSegment(key, keyIsSchemaKnown);
                var childTrusted = keyIsSchemaKnown;
                var childPosition = childTrusted ? resolved.ChildSchemaPosition : SchemaPosition.Unknown;
                var result = Scan(property.Value, segments.Add(segment), childPosition, childTrusted);
                if (result.HasValue)
                {
                    return result.Value;
                }
            }

            return null;
        }

        return null;
    }

    private static ResolveResult ResolveCompositeProperty(CompositeSchemaPosition composite, string key)
    {
        var matches = ImmutableArray.CreateBuilder<SchemaPosition>();
        foreach (var child in composite.Children)
        {
            var resolved = ResolveProperty(child, key);
            if (resolved.SchemaKnown)
            {
                matches.Add(resolved.ChildSchemaPosition);
            }
        }

        if (matches.Count == 0)
        {
            return new ResolveResult(false, SchemaPosition.Unknown);
        }

        return new ResolveResult(true, Deduplicate(matches.ToImmutable()));
    }

    private static ResolveResult ResolveCompositeArrayItem(CompositeSchemaPosition composite)
    {
        var matches = ImmutableArray.CreateBuilder<SchemaPosition>();
        foreach (var child in composite.Children)
        {
            var resolved = ResolveArrayItem(child);
            if (resolved.SchemaKnown)
            {
                matches.Add(resolved.ChildSchemaPosition);
            }
        }

        if (matches.Count == 0)
        {
            return new ResolveResult(false, SchemaPosition.Unknown);
        }

        return new ResolveResult(true, Deduplicate(matches.ToImmutable()));
    }

    private static SchemaPosition Deduplicate(ImmutableArray<SchemaPosition> positions)
    {
        if (positions.IsEmpty)
        {
            return SchemaPosition.Unknown;
        }

        if (positions.Length == 1)
        {
            return positions[0];
        }

        var unique = ImmutableHashSet.CreateBuilder<SchemaPosition>();
        var builder = ImmutableArray.CreateBuilder<SchemaPosition>();
        foreach (var position in positions)
        {
            if (unique.Add(position))
            {
                builder.Add(position);
            }
        }

        if (builder.Count == 1)
        {
            return builder[0];
        }

        return new CompositeSchemaPosition { Children = builder.ToImmutable() };
    }

    private static string SanitizeSegment(string key, bool schemaKnown)
    {
        if (key.Length == 0)
        {
            return "<empty-name>";
        }

        if (schemaKnown)
        {
            return EscapeJsonPointerSegment(key);
        }

        if (ContainsUnpairedSurrogate(key))
        {
            return "<invalid-utf16>";
        }

        if (key.Contains('\0'))
        {
            return "<invalid-nul>";
        }

        if (ContainsControlCharacter(key))
        {
            return "<invalid-control>";
        }

        return "<untrusted-property>";
    }

    internal static string EscapeJsonPointerSegment(string segment)
    {
        return segment.Replace("~", "~0").Replace("/", "~1");
    }

    private static string BuildPath(ImmutableArray<string> segments)
    {
        if (segments.IsEmpty)
        {
            return "";
        }

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append('/');
            sb.Append(segment);
        }

        return sb.ToString();
    }

    private static bool Fits(string path, int charBudget, int byteBudget)
    {
        if (path.Length > charBudget)
        {
            return false;
        }

        return Encoding.UTF8.GetByteCount(path) <= byteBudget;
    }

    private static bool IsInvalidRawJsonString(string rawText)
    {
        // rawText includes surrounding quotes.
        var i = 1;
        while (i < rawText.Length - 1)
        {
            var c = rawText[i];
            if (c == '\\')
            {
                if (i + 1 >= rawText.Length - 1)
                {
                    return true;
                }

                var next = rawText[i + 1];
                if (next == 'u')
                {
                    if (i + 6 >= rawText.Length)
                    {
                        return true;
                    }

                    var hex = rawText.Substring(i + 2, 4);
                    if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var codepoint))
                    {
                        return true;
                    }

                    if (codepoint == 0)
                    {
                        return true;
                    }

                    if (codepoint >= 0xD800 && codepoint <= 0xDBFF)
                    {
                        // High surrogate; expect a following low-surrogate escape.
                        if (i + 12 < rawText.Length &&
                            rawText[i + 6] == '\\' &&
                            rawText[i + 7] == 'u' &&
                            int.TryParse(rawText.Substring(i + 8, 4), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var low) &&
                            low >= 0xDC00 && low <= 0xDFFF)
                        {
                            i += 12;
                            continue;
                        }

                        return true;
                    }

                    if (codepoint >= 0xDC00 && codepoint <= 0xDFFF)
                    {
                        return true;
                    }

                    i += 6;
                    continue;
                }

                // Other two-character escape; skip both.
                i += 2;
                continue;
            }

            if (c == '\0' || char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
            {
                return true;
            }

            i++;
        }

        return false;
    }

    private static bool ContainsUnpairedSurrogate(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (c < 0x20 || c == 0x7f)
            {
                return true;
            }
        }

        return false;
    }

    private static string ToStringInvariant(this int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

internal readonly record struct UnsafeLocation
{
    public required ImmutableArray<string> Segments { get; init; }
    public required bool IsPropertyNameViolation { get; init; }
    public required bool IsRootScalar { get; init; }
}

internal readonly record struct ResolveResult(bool SchemaKnown, SchemaPosition ChildSchemaPosition);

internal abstract class SchemaPosition
{
    public static SchemaPosition Unknown { get; } = new UnknownSchemaPosition();
}

internal sealed class UnknownSchemaPosition : SchemaPosition
{
    public override string ToString() => "Unknown";
}

internal sealed class ObjectSchemaPosition : SchemaPosition
{
    public required SchemaNode Node { get; init; }
    public required ImmutableHashSet<string> ActiveNodes { get; init; }

    public override bool Equals(object? obj) =>
        obj is ObjectSchemaPosition other && Node.Id == other.Node.Id && ActiveNodes.SetEquals(other.ActiveNodes);

    public override int GetHashCode()
    {
        var hash = Node.Id.GetHashCode();
        foreach (var id in ActiveNodes)
        {
            hash ^= id.GetHashCode();
        }

        return hash;
    }
}

internal sealed class ArraySchemaPosition : SchemaPosition
{
    public required SchemaNode Node { get; init; }
    public required ImmutableHashSet<string> ActiveNodes { get; init; }

    public override bool Equals(object? obj) =>
        obj is ArraySchemaPosition other && Node.Id == other.Node.Id && ActiveNodes.SetEquals(other.ActiveNodes);

    public override int GetHashCode()
    {
        var hash = Node.Id.GetHashCode();
        foreach (var id in ActiveNodes)
        {
            hash ^= id.GetHashCode();
        }

        return hash;
    }
}

internal sealed class CompositeSchemaPosition : SchemaPosition
{
    public required ImmutableArray<SchemaPosition> Children { get; init; }

    public override bool Equals(object? obj)
    {
        if (obj is not CompositeSchemaPosition other || Children.Length != other.Children.Length)
        {
            return false;
        }

        for (var i = 0; i < Children.Length; i++)
        {
            if (!Equals(Children[i], other.Children[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = 0;
        foreach (var child in Children)
        {
            hash = (hash * 397) ^ (child?.GetHashCode() ?? 0);
        }

        return hash;
    }
}

internal sealed class SchemaNode
{
    public string Id { get; set; } = null!;
    public ImmutableDictionary<string, SchemaNode> Properties { get; set; } = ImmutableDictionary<string, SchemaNode>.Empty;
    public SchemaNode? Items { get; set; }
    public ImmutableArray<SchemaNode> OneOf { get; set; } = ImmutableArray<SchemaNode>.Empty;
    public ImmutableArray<SchemaNode> AnyOf { get; set; } = ImmutableArray<SchemaNode>.Empty;
    public ImmutableArray<SchemaNode> AllOf { get; set; } = ImmutableArray<SchemaNode>.Empty;
    public string? Ref { get; set; }
    public bool HasRefSiblings { get; set; }
}

internal sealed class SchemaResolver
{
    private readonly JsonDocument _schemaDocument;
    private readonly Dictionary<string, SchemaNode> _nodes = new();

    public SchemaResolver()
    {
        var assembly = typeof(LedgerSafePath).Assembly;
        using var stream = assembly.GetManifestResourceStream("AgenticPrReview.Protocol.provider-session-ledger.v1.json")
            ?? throw new InvalidOperationException("Missing embedded ledger schema resource.");
        _schemaDocument = JsonDocument.Parse(stream);
        Root = Normalize(BuildNode("#", _schemaDocument.RootElement), ImmutableHashSet<string>.Empty);
    }

    public SchemaPosition Root { get; }

    public SchemaPosition Normalize(SchemaNode node, ImmutableHashSet<string> activeNodes)
    {
        if (activeNodes.Contains(node.Id))
        {
            return SchemaPosition.Unknown;
        }

        var childActive = activeNodes.Add(node.Id);

        if (node.Ref is not null)
        {
            if (node.HasRefSiblings)
            {
                return SchemaPosition.Unknown;
            }

            var target = ResolveRef(node.Ref);
            if (target is null)
            {
                return SchemaPosition.Unknown;
            }

            return Normalize(target, childActive);
        }

        var positions = ImmutableArray.CreateBuilder<SchemaPosition>();
        if (!node.Properties.IsEmpty)
        {
            positions.Add(new ObjectSchemaPosition { Node = node, ActiveNodes = childActive });
        }

        if (node.Items is not null)
        {
            positions.Add(new ArraySchemaPosition { Node = node, ActiveNodes = childActive });
        }

        foreach (var branch in node.OneOf)
        {
            positions.Add(Normalize(branch, childActive));
        }

        foreach (var branch in node.AnyOf)
        {
            positions.Add(Normalize(branch, childActive));
        }

        foreach (var branch in node.AllOf)
        {
            positions.Add(Normalize(branch, childActive));
        }

        if (positions.Count == 0)
        {
            return SchemaPosition.Unknown;
        }

        if (positions.Count == 1)
        {
            return positions[0];
        }

        return DedupComposite(positions.ToImmutable());
    }

    private static SchemaPosition DedupComposite(ImmutableArray<SchemaPosition> positions)
    {
        var unique = ImmutableHashSet.CreateBuilder<SchemaPosition>();
        var builder = ImmutableArray.CreateBuilder<SchemaPosition>();
        foreach (var position in positions)
        {
            if (position is UnknownSchemaPosition)
            {
                continue;
            }

            if (unique.Add(position))
            {
                builder.Add(position);
            }
        }

        if (builder.Count == 0)
        {
            return SchemaPosition.Unknown;
        }

        if (builder.Count == 1)
        {
            return builder[0];
        }

        return new CompositeSchemaPosition { Children = builder.ToImmutable() };
    }

    private SchemaNode BuildNode(string id, JsonElement element)
    {
        if (_nodes.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var node = new SchemaNode { Id = id };
        _nodes[id] = node;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return node;
        }

        var hasRef = element.TryGetProperty("$ref", out _);
        var hasSiblings = false;
        foreach (var property in element.EnumerateObject())
        {
            if (hasRef && property.Name != "$ref")
            {
                hasSiblings = true;
                break;
            }
        }

        node.HasRefSiblings = hasRef && hasSiblings;

        if (element.TryGetProperty("$ref", out var refElement))
        {
            node.Ref = refElement.GetString();
        }

        if (element.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, SchemaNode>();
            foreach (var property in propertiesElement.EnumerateObject())
            {
                var childId = $"{id}/properties/{EscapePointerSegment(property.Name)}";
                builder[property.Name] = BuildNode(childId, property.Value);
            }

            node.Properties = builder.ToImmutable();
        }

        if (element.TryGetProperty("items", out var itemsElement) &&
            itemsElement.ValueKind == JsonValueKind.Object)
        {
            var itemsId = $"{id}/items";
            node.Items = BuildNode(itemsId, itemsElement);
        }

        node.OneOf = BuildCompositionList(id, "oneOf", element);
        node.AnyOf = BuildCompositionList(id, "anyOf", element);
        node.AllOf = BuildCompositionList(id, "allOf", element);

        return node;
    }

    private ImmutableArray<SchemaNode> BuildCompositionList(string parentId, string keyword, JsonElement element)
    {
        if (!element.TryGetProperty(keyword, out var arrayElement) ||
            arrayElement.ValueKind != JsonValueKind.Array)
        {
            return ImmutableArray<SchemaNode>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<SchemaNode>();
        var index = 0;
        foreach (var item in arrayElement.EnumerateArray())
        {
            var childId = $"{parentId}/{keyword}/{index}";
            builder.Add(BuildNode(childId, item));
            index++;
        }

        return builder.ToImmutable();
    }

    private SchemaNode? ResolveRef(string reference)
    {
        if (!reference.StartsWith("#", StringComparison.Ordinal))
        {
            return null;
        }

        var pointer = reference.Substring(1);
        if (pointer == "")
        {
            return _nodes.GetValueOrDefault("#") ?? BuildNode("#", _schemaDocument.RootElement);
        }

        var segments = pointer.Split('/', StringSplitOptions.None);
        var current = _schemaDocument.RootElement;
        var currentId = "#";
        for (var i = 1; i < segments.Length; i++)
        {
            var segment = segments[i].Replace("~1", "/").Replace("~0", "~");
            if (current.ValueKind == JsonValueKind.Object)
            {
                if (!current.TryGetProperty(segment, out var next))
                {
                    return null;
                }

                current = next;
                currentId = $"{currentId}/{EscapePointerSegment(segment)}";
            }
            else
            {
                return null;
            }
        }

        return _nodes.GetValueOrDefault(currentId) ?? BuildNode(currentId, current);
    }

    private static string EscapePointerSegment(string segment)
    {
        return segment.Replace("~", "~0").Replace("/", "~1");
    }
}
