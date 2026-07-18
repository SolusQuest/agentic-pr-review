using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;
using AgenticPrReview.Runtime.Prefix;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Prefix;

/// <summary>
/// Loader + closed-schema validation for the prefix-contract fixture corpus
/// (issue #50, D12). The loader enforces the manifest contract itself:
/// unknown fields, duplicate ids/files, unsafe paths, missing/unlisted files,
/// hex shapes, id/kind equality, and reference integrity.
/// </summary>
internal static class PrefixFixtureLoader
{
    internal static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "prefix-contract", "v1");

    internal sealed record ManifestEntry(string Id, string Kind, string File);

    internal static ImmutableArray<ManifestEntry> LoadManifest()
    {
        var manifestPath = Path.Combine(FixtureRoot, "manifest.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = doc.RootElement;
        AssertAllowedKeys(root, "schemaVersion", "generatedBy", "creationCrossCheck", "vectors");
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        AssertExactObject(root.GetProperty("generatedBy"), "generatedBy", ("tool", JsonValueKind.String), ("version", JsonValueKind.Number));
        AssertExactObject(root.GetProperty("creationCrossCheck"), "creationCrossCheck", ("tool", JsonValueKind.String), ("version", JsonValueKind.String), ("checkedAt", JsonValueKind.String));
        Assert.Matches(
            "^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}Z$",
            root.GetProperty("creationCrossCheck").GetProperty("checkedAt").GetString()!);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("vectors").ValueKind);

        var entries = ImmutableArray.CreateBuilder<ManifestEntry>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.GetProperty("vectors").EnumerateArray())
        {
            AssertAllowedKeys(entry, "id", "kind", "file");
            var id = entry.GetProperty("id").GetString()!;
            var kind = entry.GetProperty("kind").GetString()!;
            var file = entry.GetProperty("file").GetString()!;
            Assert.True(ids.Add(id), $"duplicate id {id}");
            Assert.True(files.Add(file), $"duplicate file {file}");
            AssertSafeRelativePath(file);
            entries.Add(new ManifestEntry(id, kind, file));
        }

        // Full-directory coverage: every file under the root (except manifest.json)
        // is referenced exactly once.
        var onDisk = Directory
            .GetFiles(FixtureRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(FixtureRoot, path).Replace('\\', '/'))
            .Where(rel => rel != "manifest.json")
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(onDisk, files);

        // id/kind equality between manifest entry and vector file content.
        foreach (var entry in entries)
        {
            var vector = LoadVector(entry.File);
            Assert.Equal(entry.Id, vector.GetProperty("id").GetString());
            Assert.Equal(entry.Kind, vector.GetProperty("kind").GetString());
            AssertVectorShape(entry, vector);
        }

        // Reference integrity.
        var materializationIds = entries.Where(e => e.Kind == "materialization-vector").Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        var entriesById = entries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        foreach (var entry in entries.Where(e => e.Kind is "append-vector" or "invalidation-vector"))
        {
            var vector = LoadVector(entry.File);
            foreach (var refProperty in new[] { "baseVectorId", "successorVectorId" })
            {
                if (vector.TryGetProperty(refProperty, out var refElement))
                {
                    var referenced = refElement.GetString()!;
                    Assert.True(materializationIds.Contains(referenced), $"{entry.Id}: {refProperty} -> {referenced} must resolve to a materialization-vector");
                    Assert.NotEqual(entry.Id, referenced);
                }
            }

            if (entry.Kind == "invalidation-vector"
                && vector.GetProperty("mode").GetString() == "materializer")
            {
                var baseEntry = entriesById[vector.GetProperty("baseVectorId").GetString()!];
                var successorEntry = entriesById[vector.GetProperty("successorVectorId").GetString()!];
                var baseInput = LoadVector(baseEntry.File).GetProperty("input");
                var successorInput = LoadVector(successorEntry.File).GetProperty("input");
                var diffs = JsonDiffPaths(baseInput, successorInput);
                AssertMutationDiffs(entry.Id, vector.GetProperty("mutation").GetString()!, diffs);
            }
        }

        return entries.ToImmutable();
    }

    internal static IEnumerable<ManifestEntry> OfKind(string kind) =>
        LoadManifest().Where(entry => entry.Kind == kind);

    internal static JsonElement LoadVector(string relative)
    {
        var text = File.ReadAllText(Path.Combine(FixtureRoot, relative));
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static void AssertVectorShape(ManifestEntry entry, JsonElement vector)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "id", "kind" };
        void Require(params string[] keys)
        {
            foreach (var key in keys)
            {
                allowed.Add(key);
                Assert.True(vector.TryGetProperty(key, out _), $"{entry.Id}: missing field {key}");
            }
        }

        switch (entry.Kind)
        {
            case "framing-vector":
                Require("input", "expected");
                AssertFramingVector(entry.Id, vector);
                break;
            case "digest-vector":
                Require("tag", "envelope", "expected");
                Assert.Equal(JsonValueKind.String, vector.GetProperty("tag").ValueKind);
                Assert.Equal(JsonValueKind.Object, vector.GetProperty("envelope").ValueKind);
                AssertExactObject(vector.GetProperty("expected"), $"{entry.Id}.expected", ("preimageHex", JsonValueKind.String), ("digestHex", JsonValueKind.String));
                AssertHex(vector.GetProperty("expected").GetProperty("preimageHex").GetString()!);
                AssertHex(vector.GetProperty("expected").GetProperty("digestHex").GetString()!, 64);
                break;
            case "interaction-vector":
                Require("predecessor", "consumedInputSha256", "currentHeadSha", "interactionOrdinal", "expected");
                AssertInteractionVector(entry.Id, vector);
                break;
            case "materialization-vector":
                Require("input", "expected");
                AssertMaterializationExpected(entry.Id, vector.GetProperty("expected"));
                break;
            case "append-vector":
                Require("baseVectorId", "successorVectorId", "expected");
                Assert.Equal(JsonValueKind.String, vector.GetProperty("baseVectorId").ValueKind);
                Assert.Equal(JsonValueKind.String, vector.GetProperty("successorVectorId").ValueKind);
                AssertExactObject(
                    vector.GetProperty("expected"),
                    $"{entry.Id}.expected",
                    ("logicalStrictPrefix", JsonValueKind.True, JsonValueKind.False),
                    ("providerStrictPrefix", JsonValueKind.True, JsonValueKind.False),
                    ("promotedContextLogicalBytesEqual", JsonValueKind.True, JsonValueKind.False),
                    ("promotedContextProviderBytesEqual", JsonValueKind.True, JsonValueKind.False));
                break;
            case "invalidation-vector":
                Require("mode", "mutation", "expected");
                Assert.Equal(JsonValueKind.String, vector.GetProperty("mode").ValueKind);
                Assert.Equal(JsonValueKind.String, vector.GetProperty("mutation").ValueKind);
                var mode = vector.GetProperty("mode").GetString();
                if (mode == "materializer")
                {
                    Require("baseVectorId", "successorVectorId");
                    Assert.Equal(JsonValueKind.String, vector.GetProperty("baseVectorId").ValueKind);
                    Assert.Equal(JsonValueKind.String, vector.GetProperty("successorVectorId").ValueKind);
                    AssertChangedExpected(entry.Id, vector.GetProperty("expected"));
                }
                else if (mode == "hash-framing")
                {
                    Require("baseInput", "mutatedInput");
                    AssertHashFramingInput(entry.Id, "baseInput", vector.GetProperty("baseInput"));
                    AssertHashFramingInput(entry.Id, "mutatedInput", vector.GetProperty("mutatedInput"));
                    AssertHashFramingExpected(entry.Id, vector.GetProperty("expected"));
                }
                else
                {
                    Assert.Fail($"{entry.Id}: unknown mode {mode}");
                }

                break;
            case "invalid-vector":
            {
                Require("target", "input", "expected");
                allowed.Add("scope");
                var expected = vector.GetProperty("expected");
                AssertAllowedKeys(expected, "csharpCode", "typescriptCode", "causeCode", "path");
                foreach (var property in expected.EnumerateObject())
                {
                    Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
                }
                var target = vector.GetProperty("target").GetString()!;
                var hasCsharp = expected.TryGetProperty("csharpCode", out _);
                var hasTs = expected.TryGetProperty("typescriptCode", out _);
                var csharpOnlyScope =
                    vector.TryGetProperty("scope", out var scopeElement)
                    && scopeElement.GetString() == "csharp-only";
                switch (target)
                {
                    case "identity":
                    case "model-snapshot":
                        Assert.True(hasTs && !hasCsharp, $"{entry.Id}: TS-only targets must carry only typescriptCode");
                        break;
                    case "materialize":
                    case "length-guard":
                    case "stream-guard":
                        Assert.True(hasCsharp && !hasTs, $"{entry.Id}: C#-only targets must carry only csharpCode");
                        break;
                    case "canonical-json":
                        Assert.True(hasCsharp, $"{entry.Id}: canonical-json must carry csharpCode");
                        Assert.True(hasTs || csharpOnlyScope, $"{entry.Id}: canonical-json must carry typescriptCode unless csharp-only");
                        break;
                    case "template-id":
                    case "policy-id":
                    case "tools-id":
                    case "config-id":
                    case "adapter-id":
                    case "interaction-id":
                        Assert.True(hasCsharp && (hasTs || csharpOnlyScope), $"{entry.Id}: shared targets must carry both codes unless csharp-only");
                        break;
                    default:
                        Assert.Fail($"{entry.Id}: unknown target {target}");
                        break;
                }

                if (vector.TryGetProperty("scope", out _))
                {
                    Assert.True(
                        csharpOnlyScope
                        && entry.Id is "invalid-envelope-duplicate-root"
                            or "invalid-tools-duplicate-wrapper-property"
                            or "invalid-canonical-duplicate-open-json",
                        $"{entry.Id}: scope is only allowed on the enumerated raw duplicate vectors");
                }

                break;
            }
            default:
                Assert.Fail($"{entry.Id}: unknown kind {entry.Kind}");
                break;
        }

        foreach (var property in vector.EnumerateObject())
        {
            Assert.True(allowed.Contains(property.Name), $"{entry.Id}: unknown vector field {property.Name}");
        }
    }

    private static void AssertFramingVector(string id, JsonElement vector)
    {
        var input = vector.GetProperty("input");
        Assert.Equal(JsonValueKind.Object, input.ValueKind);
        var names = input.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.SequenceEqual(new[] { "tag" }, StringComparer.Ordinal))
        {
            AssertExactObject(input, $"{id}.input", ("tag", JsonValueKind.String));
            AssertExactObject(vector.GetProperty("expected"), $"{id}.expected", ("preimageHex", JsonValueKind.String));
            AssertHex(vector.GetProperty("expected").GetProperty("preimageHex").GetString()!);
        }
        else if (names.SequenceEqual(new[] { "value" }, StringComparer.Ordinal))
        {
            AssertExactObject(input, $"{id}.input", ("value", JsonValueKind.String));
            AssertExactObject(vector.GetProperty("expected"), $"{id}.expected", ("framedHex", JsonValueKind.String));
            AssertHex(vector.GetProperty("expected").GetProperty("framedHex").GetString()!);
        }
        else if (names.SequenceEqual(new[] { "payloadHex" }, StringComparer.Ordinal))
        {
            AssertExactObject(input, $"{id}.input", ("payloadHex", JsonValueKind.String));
            AssertHex(input.GetProperty("payloadHex").GetString()!);
            AssertExactObject(vector.GetProperty("expected"), $"{id}.expected", ("framedHex", JsonValueKind.String));
            AssertHex(vector.GetProperty("expected").GetProperty("framedHex").GetString()!);
        }
        else if (names.ToHashSet(StringComparer.Ordinal).SetEquals(new[] { "ledgerSchemaVersion", "prefixContractVersion" }))
        {
            AssertExactObject(input, $"{id}.input", ("ledgerSchemaVersion", JsonValueKind.Number), ("prefixContractVersion", JsonValueKind.Number));
            AssertNonnegativeInteger(input.GetProperty("ledgerSchemaVersion"), $"{id}.input.ledgerSchemaVersion");
            AssertNonnegativeInteger(input.GetProperty("prefixContractVersion"), $"{id}.input.prefixContractVersion");
            AssertExactObject(vector.GetProperty("expected"), $"{id}.expected", ("logicalPrefixSha256", JsonValueKind.String));
            AssertHex(vector.GetProperty("expected").GetProperty("logicalPrefixSha256").GetString()!, 64);
        }
        else
        {
            Assert.Fail($"{id}: unknown framing input union");
        }
    }

    private static void AssertInteractionVector(string id, JsonElement vector)
    {
        AssertHex(vector.GetProperty("consumedInputSha256").GetString()!, 64);
        var head = vector.GetProperty("currentHeadSha").GetString()!;
        Assert.True(head.Length is 40 or 64, $"{id}: currentHeadSha must be 40 or 64 lowercase hex characters");
        AssertHex(head, head.Length);
        AssertNonnegativeInteger(vector.GetProperty("interactionOrdinal"), $"{id}.interactionOrdinal");

        var predecessor = vector.GetProperty("predecessor");
        if (predecessor.TryGetProperty("bootstrap", out var bootstrap))
        {
            AssertExactObject(predecessor, $"{id}.predecessor", ("bootstrap", JsonValueKind.True));
            Assert.True(bootstrap.GetBoolean());
        }
        else
        {
            AssertExactObject(predecessor, $"{id}.predecessor", ("ledgerSha256", JsonValueKind.String));
            AssertHex(predecessor.GetProperty("ledgerSha256").GetString()!, 64);
        }

        var expected = vector.GetProperty("expected");
        AssertExactObject(expected, $"{id}.expected", ("preimageHex", JsonValueKind.String), ("interactionId", JsonValueKind.String));
        AssertHex(expected.GetProperty("preimageHex").GetString()!);
        AssertHex(expected.GetProperty("interactionId").GetString()!, 64);
    }

    private static void AssertMaterializationExpected(string id, JsonElement expected)
    {
        AssertExactObject(
            expected,
            $"{id}.expected",
            ("logicalStreamHex", JsonValueKind.String),
            ("providerStreamHex", JsonValueKind.String),
            ("logicalPrefixSha256", JsonValueKind.String),
            ("prefixSha256", JsonValueKind.String),
            ("digests", JsonValueKind.Object),
            ("stableBoundary", JsonValueKind.Object),
            ("dynamicSuffix", JsonValueKind.Object));
        AssertHex(expected.GetProperty("logicalStreamHex").GetString()!);
        AssertHex(expected.GetProperty("providerStreamHex").GetString()!);
        AssertHex(expected.GetProperty("logicalPrefixSha256").GetString()!, 64);
        AssertHex(expected.GetProperty("prefixSha256").GetString()!, 64);

        var digests = expected.GetProperty("digests");
        AssertExactObject(
            digests,
            $"{id}.expected.digests",
            ("templateId", JsonValueKind.String),
            ("policyId", JsonValueKind.String),
            ("toolDefinitionId", JsonValueKind.String),
            ("cacheConfigId", JsonValueKind.String),
            ("adapterId", JsonValueKind.String));
        foreach (var property in digests.EnumerateObject())
        {
            AssertHex(property.Value.GetString()!, 64);
        }

        var stableBoundary = expected.GetProperty("stableBoundary");
        AssertExactObject(
            stableBoundary,
            $"{id}.expected.stableBoundary",
            ("segmentCount", JsonValueKind.Number),
            ("logicalStreamBytes", JsonValueKind.Number),
            ("providerStreamBytes", JsonValueKind.Number));
        foreach (var property in stableBoundary.EnumerateObject())
        {
            AssertNonnegativeInteger(property.Value, $"{id}.expected.stableBoundary.{property.Name}");
        }

        var suffix = expected.GetProperty("dynamicSuffix");
        AssertExactObject(suffix, $"{id}.expected.dynamicSuffix", ("logicalHex", JsonValueKind.String), ("providerHex", JsonValueKind.String));
        AssertHex(suffix.GetProperty("logicalHex").GetString()!);
        AssertHex(suffix.GetProperty("providerHex").GetString()!);
    }

    private static void AssertChangedExpected(string id, JsonElement expected) =>
        AssertExactObject(
            expected,
            $"{id}.expected",
            ("logicalStreamChanged", JsonValueKind.True, JsonValueKind.False),
            ("providerStreamChanged", JsonValueKind.True, JsonValueKind.False),
            ("logicalHashChanged", JsonValueKind.True, JsonValueKind.False),
            ("prefixHashChanged", JsonValueKind.True, JsonValueKind.False));

    private static void AssertHashFramingInput(string id, string label, JsonElement input)
    {
        AssertExactObject(
            input,
            $"{id}.{label}",
            ("ledgerSchemaVersion", JsonValueKind.Number),
            ("prefixContractVersion", JsonValueKind.Number),
            ("logicalStreamHex", JsonValueKind.String),
            ("providerStreamHex", JsonValueKind.String));
        AssertNonnegativeInteger(input.GetProperty("ledgerSchemaVersion"), $"{id}.{label}.ledgerSchemaVersion");
        AssertNonnegativeInteger(input.GetProperty("prefixContractVersion"), $"{id}.{label}.prefixContractVersion");
        AssertHex(input.GetProperty("logicalStreamHex").GetString()!);
        AssertHex(input.GetProperty("providerStreamHex").GetString()!);
    }

    private static void AssertHashFramingExpected(string id, JsonElement expected)
    {
        AssertExactObject(
            expected,
            $"{id}.expected",
            ("baseLogicalPrefixSha256", JsonValueKind.String, JsonValueKind.String),
            ("mutatedLogicalPrefixSha256", JsonValueKind.String, JsonValueKind.String),
            ("basePrefixSha256", JsonValueKind.String, JsonValueKind.String),
            ("mutatedPrefixSha256", JsonValueKind.String, JsonValueKind.String),
            ("logicalStreamChanged", JsonValueKind.True, JsonValueKind.False),
            ("providerStreamChanged", JsonValueKind.True, JsonValueKind.False),
            ("logicalHashChanged", JsonValueKind.True, JsonValueKind.False),
            ("prefixHashChanged", JsonValueKind.True, JsonValueKind.False));
        foreach (var property in expected.EnumerateObject().Where(property => property.Name.EndsWith("Sha256", StringComparison.Ordinal)))
        {
            AssertHex(property.Value.GetString()!, 64);
        }
    }

    private static void AssertExactObject(
        JsonElement element,
        string label,
        params (string Name, JsonValueKind Kind)[] fields)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        AssertAllowedKeys(element, fields.Select(field => field.Name).ToArray());
        foreach (var field in fields)
        {
            Assert.True(element.TryGetProperty(field.Name, out var value), $"{label}: missing {field.Name}");
            Assert.Equal(field.Kind, value.ValueKind);
        }
    }

    private static void AssertExactObject(
        JsonElement element,
        string label,
        params (string Name, JsonValueKind First, JsonValueKind Second)[] fields)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        AssertAllowedKeys(element, fields.Select(field => field.Name).ToArray());
        foreach (var field in fields)
        {
            Assert.True(element.TryGetProperty(field.Name, out var value), $"{label}: missing {field.Name}");
            Assert.True(value.ValueKind == field.First || value.ValueKind == field.Second, $"{label}.{field.Name}: unexpected JSON kind {value.ValueKind}");
        }
    }

    private static void AssertNonnegativeInteger(JsonElement element, string label)
    {
        Assert.Equal(JsonValueKind.Number, element.ValueKind);
        Assert.True(element.TryGetInt64(out var value) && value >= 0, $"{label}: expected a nonnegative integer");
    }

    private static List<string> JsonDiffPaths(JsonElement left, JsonElement right, string prefix = "")
    {
        if (left.ValueKind != right.ValueKind)
        {
            return new List<string> { prefix.Length == 0 ? "$" : prefix };
        }

        if (left.ValueKind == JsonValueKind.Object)
        {
            var leftProperties = left.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var rightProperties = right.EnumerateObject().ToDictionary(property => property.Name, property => property.Value, StringComparer.Ordinal);
            var diffs = new List<string>();
            foreach (var key in leftProperties.Keys.Concat(rightProperties.Keys).Distinct(StringComparer.Ordinal))
            {
                var childPath = prefix.Length == 0 ? key : $"{prefix}.{key}";
                if (!leftProperties.TryGetValue(key, out var leftValue)
                    || !rightProperties.TryGetValue(key, out var rightValue))
                {
                    diffs.Add(childPath);
                }
                else
                {
                    diffs.AddRange(JsonDiffPaths(leftValue, rightValue, childPath));
                }
            }

            return diffs;
        }

        if (left.ValueKind == JsonValueKind.Array)
        {
            var leftItems = left.EnumerateArray().ToArray();
            var rightItems = right.EnumerateArray().ToArray();
            if (leftItems.Length != rightItems.Length)
            {
                return new List<string> { prefix.Length == 0 ? "$" : prefix };
            }

            var diffs = new List<string>();
            for (var index = 0; index < leftItems.Length; index++)
            {
                diffs.AddRange(JsonDiffPaths(leftItems[index], rightItems[index], $"{prefix}[{index}]"));
            }

            return diffs;
        }

        var equal = left.ValueKind switch
        {
            JsonValueKind.String => left.GetString() == right.GetString(),
            JsonValueKind.Number => left.GetDouble().Equals(right.GetDouble()),
            JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText(),
        };
        return equal
            ? new List<string>()
            : new List<string> { prefix.Length == 0 ? "$" : prefix };
    }

    private static void AssertMutationDiffs(string id, string mutation, IReadOnlyCollection<string> diffs)
    {
        static bool EnvelopeMutation(
            IReadOnlyCollection<string> paths,
            string envelopeName,
            string digestField)
        {
            var envelopePrefix = $"envelopes.{envelopeName}.";
            var schemaVersion = $"{envelopePrefix}schemaVersion";
            var digestPath = $"expectedIdentities.{digestField}";
            return paths.Any(path => path.StartsWith(envelopePrefix, StringComparison.Ordinal) && path != schemaVersion)
                && paths.Contains(digestPath, StringComparer.Ordinal)
                && paths.All(path =>
                    (path.StartsWith(envelopePrefix, StringComparison.Ordinal) && path != schemaVersion)
                    || path == digestPath);
        }

        var valid = mutation switch
        {
            "providerId" => diffs.SequenceEqual(new[] { "expectedIdentities.providerId" }, StringComparer.Ordinal),
            "modelId" => diffs.SequenceEqual(new[] { "expectedIdentities.modelId" }, StringComparer.Ordinal),
            "adapter envelope content/version" => EnvelopeMutation(diffs, "adapter", "adapterId"),
            "cache-config envelope content/version" => EnvelopeMutation(diffs, "cacheConfig", "cacheConfigId"),
            "template envelope content/version" => EnvelopeMutation(diffs, "template", "templateId"),
            "policy envelope content/version" => EnvelopeMutation(diffs, "policy", "policyId"),
            "tools envelope content/version/order" => EnvelopeMutation(diffs, "tools", "toolDefinitionId"),
            "any envelope schemaVersion" => ValidateSchemaVersionMutation(diffs),
            "run/provenance metadata" => diffs.SequenceEqual(new[] { "interaction.interactionId" }, StringComparer.Ordinal),
            _ => false,
        };
        Assert.True(valid, $"{id}: mutation {mutation} does not match its exact input diff predicate ({string.Join(',', diffs)})");
    }

    private static bool ValidateSchemaVersionMutation(IReadOnlyCollection<string> diffs)
    {
        if (diffs.Count != 2)
        {
            return false;
        }

        var digestFields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["template"] = "templateId",
            ["policy"] = "policyId",
            ["tools"] = "toolDefinitionId",
            ["cacheConfig"] = "cacheConfigId",
            ["adapter"] = "adapterId",
        };
        foreach (var pair in digestFields)
        {
            if (diffs.Contains($"envelopes.{pair.Key}.schemaVersion", StringComparer.Ordinal)
                && diffs.Contains($"expectedIdentities.{pair.Value}", StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static void AssertSafeRelativePath(string file)
    {
        Assert.False(string.IsNullOrEmpty(file));
        Assert.DoesNotContain('\\', file);
        Assert.DoesNotContain("..", file);
        Assert.False(Path.IsPathRooted(file));
        Assert.DoesNotContain(':', file);
    }

    internal static void AssertAllowedKeys(JsonElement element, params string[] allowed)
    {
        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            Assert.True(allowedSet.Contains(property.Name), $"unknown field {property.Name}");
        }
    }

    internal static void AssertHex(string value, int? exactLength = null)
    {
        Assert.NotNull(value);
        Assert.Equal(0, value.Length % 2);
        if (exactLength is not null)
        {
            Assert.Equal(exactLength.Value, value.Length);
        }

        foreach (var c in value)
        {
            Assert.True((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), $"non lowercase hex char {c}");
        }
    }

    // ------------------------------------------------------------------
    // Materialization input reconstruction

    internal static PrefixMaterializationInput BuildMaterializeInput(JsonElement input)
    {
        var historyElement = input.GetProperty("history");
        var historyKind = historyElement.GetProperty("kind").GetString()!;
        MaterializationHistory history = historyKind switch
        {
            "bootstrap" => MaterializationHistory.BootstrapHistory.Instance,
            "continuation" => new MaterializationHistory.ContinuationHistory(ParseLedger(historyElement.GetProperty("ledgerHex").GetString()!)),
            "reset" => new MaterializationHistory.ResetHistory(ParseLedger(historyElement.GetProperty("ledgerHex").GetString()!)),
            _ => throw new InvalidOperationException(historyKind),
        };

        var context = input.GetProperty("currentContext");
        var identitiesElement = input.GetProperty("expectedIdentities");
        var identities = new ExpectedIdentities(
            identitiesElement.GetProperty("repository").GetString()!,
            identitiesElement.GetProperty("headRepository").GetString()!,
            identitiesElement.GetProperty("pullRequest").GetInt32(),
            identitiesElement.GetProperty("workflowIdentity").GetString()!,
            identitiesElement.GetProperty("trustedExecutionDomain").GetString()!,
            identitiesElement.GetProperty("providerId").GetString()!,
            identitiesElement.GetProperty("modelId").GetString()!,
            identitiesElement.GetProperty("adapterId").GetString()!,
            identitiesElement.GetProperty("templateId").GetString()!,
            identitiesElement.GetProperty("policyId").GetString()!,
            identitiesElement.GetProperty("toolDefinitionId").GetString()!,
            identitiesElement.GetProperty("cacheConfigId").GetString()!);

        var interaction = input.GetProperty("interaction");
        var envelopes = input.GetProperty("envelopes");

        return new PrefixMaterializationInput(
            history,
            new ValidatedContextSource
            {
                SubjectDigest = context.GetProperty("subjectDigest").GetString()!,
                ReviewedHeadSha = context.GetProperty("reviewedHeadSha").GetString()!,
                ReviewedBaseSha = context.GetProperty("reviewedBaseSha").GetString()!,
                ChangedFiles = BuildChangedFiles(context.GetProperty("changedFiles")),
            },
            new InteractionIdentity(
                interaction.GetProperty("interactionId").GetString()!,
                interaction.GetProperty("interactionOrdinal").GetInt64()),
            identities,
            input.GetProperty("sessionEpoch").GetString()!,
            new RawCacheContractEnvelopes(
                envelopes.GetProperty("template"),
                envelopes.GetProperty("policy"),
                envelopes.GetProperty("tools"),
                envelopes.GetProperty("cacheConfig"),
                envelopes.GetProperty("adapter")));
    }

    internal static ValidatedLedger ParseLedger(string ledgerHex)
    {
        var bytes = Convert.FromHexString(ledgerHex);
        var outcome = LedgerParser.ParseAndValidate(bytes);
        Assert.True(outcome.Ledger is not null, string.Join(',', outcome.Diagnostics.Select(d => d.Code)));
        return outcome.Ledger!;
    }

    private static ImmutableArray<LedgerChangedFile> BuildChangedFiles(JsonElement files)
    {
        var builder = ImmutableArray.CreateBuilder<LedgerChangedFile>();
        foreach (var file in files.EnumerateArray())
        {
            LedgerBoundedPatch? patch = null;
            if (file.TryGetProperty("patch", out var patchElement) && patchElement.ValueKind == JsonValueKind.Object)
            {
                patch = new LedgerBoundedPatch
                {
                    Sha256 = patchElement.GetProperty("sha256").GetString()!,
                    Truncated = patchElement.GetProperty("truncated").GetBoolean(),
                    MaxChars = patchElement.GetProperty("maxChars").GetInt64(),
                };
            }

            builder.Add(new LedgerChangedFile
            {
                Path = file.GetProperty("path").GetString()!,
                PreviousPath = file.TryGetProperty("previousPath", out var previous) && previous.ValueKind == JsonValueKind.String
                    ? previous.GetString()
                    : null,
                Status = file.GetProperty("status").GetString()!,
                Additions = file.GetProperty("additions").GetInt64(),
                Deletions = file.GetProperty("deletions").GetInt64(),
                Changes = file.GetProperty("changes").GetInt64(),
                Patch = patch,
            });
        }

        return builder.ToImmutable();
    }
}
