using System.Text.Json;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Author-time conformance checks for protocol/schemas/provider-session-ledger.v1.json,
/// mirroring src/state-v2/schema-conformance.test.ts and
/// src/provider-metadata/schema-conformance.test.ts: no tuple-form items/additionalItems,
/// an acyclic local $ref graph, no $ref sibling keywords, and every object node closed
/// with explicit additionalProperties: false.
/// </summary>
public sealed class LedgerSchemaConformanceTests
{
    [Fact]
    public void SchemaIsDraft07AndClosedAtEveryObjectNode()
    {
        using var schema = LoadLedgerSchema();
        Assert.Equal("http://json-schema.org/draft-07/schema#", schema.RootElement.GetProperty("$schema").GetString());

        Walk(schema.RootElement, node =>
        {
            if (!DeclaresObjectShape(node))
            {
                return;
            }

            Assert.True(
                node.TryGetProperty("additionalProperties", out var additionalProperties) &&
                additionalProperties.ValueKind == JsonValueKind.False,
                $"object node is not closed with additionalProperties: false: {Preview(node)}");
        });
    }

    [Fact]
    public void SchemaHasNoTupleFormItemsOrAdditionalItems()
    {
        using var schema = LoadLedgerSchema();

        Walk(schema.RootElement, node =>
        {
            if (node.TryGetProperty("items", out var items))
            {
                Assert.True(items.ValueKind != JsonValueKind.Array, $"tuple-form items: {Preview(node)}");
            }

            Assert.False(node.TryGetProperty("additionalItems", out _), $"additionalItems keyword: {Preview(node)}");
        });
    }

    [Fact]
    public void SchemaRefGraphIsAcyclic()
    {
        using var schema = LoadLedgerSchema();

        Assert.Null(FindRefCycle(schema.RootElement));
    }

    [Fact]
    public void RefCycleDetectorRejectsAnInlineAncestorBackEdge()
    {
        using var cyclic = JsonDocument.Parse("""
            {
              "type": "object",
              "properties": {
                "payload": {
                  "type": "object",
                  "properties": {
                    "next": { "$ref": "#/properties/payload" }
                  }
                }
              }
            }
            """);

        Assert.NotNull(FindRefCycle(cyclic.RootElement));
    }

    [Fact]
    public void SchemaRefNodesHaveNoSiblingKeywords()
    {
        using var schema = LoadLedgerSchema();

        Walk(schema.RootElement, node =>
        {
            if (!node.TryGetProperty("$ref", out _))
            {
                return;
            }

            var keywordCount = 0;
            foreach (var _ in node.EnumerateObject())
            {
                keywordCount++;
            }

            Assert.Equal(1, keywordCount);
        });
    }

    private static JsonDocument LoadLedgerSchema()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "protocol", "schemas", "provider-session-ledger.v1.json");
            if (File.Exists(candidate))
            {
                return JsonDocument.Parse(File.ReadAllBytes(candidate));
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException(
            "Could not locate protocol/schemas/provider-session-ledger.v1.json above the test output directory.");
    }

    private static void Walk(JsonElement node, Action<JsonElement> visit)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                Walk(item, visit);
            }

            return;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        visit(node);
        foreach (var property in node.EnumerateObject())
        {
            Walk(property.Value, visit);
        }
    }

    private static bool DeclaresObjectShape(JsonElement node)
    {
        if (node.TryGetProperty("properties", out _))
        {
            return true;
        }

        if (!node.TryGetProperty("type", out var type))
        {
            return false;
        }

        if (type.ValueKind == JsonValueKind.String)
        {
            return type.GetString() == "object";
        }

        if (type.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in type.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() == "object")
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Walks the schema dereferencing local $refs; returns a description of the first
    // defect (cycle, non-local, or unresolvable reference) or null when the $ref graph is
    // acyclic. Reference targets are identified by their pointer into this document,
    // which is canonical here because every $ref is a direct "#/..." pointer.
    private static string? FindRefCycle(JsonElement root)
    {
        return VisitForCycle(root, root, new HashSet<string>(StringComparer.Ordinal));
    }

    private static string? VisitForCycle(JsonElement node, JsonElement root, HashSet<string> activeTargets)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
            {
                var defect = VisitForCycle(item, root, activeTargets);
                if (defect is not null)
                {
                    return defect;
                }
            }

            return null;
        }

        if (node.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (node.TryGetProperty("$ref", out var refElement) && refElement.ValueKind == JsonValueKind.String)
        {
            var reference = refElement.GetString()!;
            if (reference != "#" && !reference.StartsWith("#/", StringComparison.Ordinal))
            {
                return $"non-local $ref '{reference}'";
            }

            if (activeTargets.Contains(reference))
            {
                return $"$ref cycle at '{reference}'";
            }

            if (!TryResolvePointer(root, reference, out var target))
            {
                return $"unresolvable $ref '{reference}'";
            }

            var childActive = new HashSet<string>(activeTargets, StringComparer.Ordinal) { reference };
            return VisitForCycle(target, root, childActive);
        }

        foreach (var property in node.EnumerateObject())
        {
            var defect = VisitForCycle(property.Value, root, activeTargets);
            if (defect is not null)
            {
                return defect;
            }
        }

        return null;
    }

    private static bool TryResolvePointer(JsonElement root, string reference, out JsonElement target)
    {
        target = root;
        var pointer = reference.Substring(1);
        if (pointer == "")
        {
            return true;
        }

        foreach (var rawSegment in pointer.Substring(1).Split('/'))
        {
            var segment = rawSegment.Replace("~1", "/").Replace("~0", "~");
            if (target.ValueKind != JsonValueKind.Object || !target.TryGetProperty(segment, out target))
            {
                return false;
            }
        }

        return true;
    }

    private static string Preview(JsonElement node)
    {
        var raw = node.GetRawText();
        return raw.Length <= 120 ? raw : raw.Substring(0, 120) + "...";
    }
}
