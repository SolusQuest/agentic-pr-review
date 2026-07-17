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
        AssertAllowedKeys(root.GetProperty("generatedBy"), "tool", "version");
        AssertAllowedKeys(root.GetProperty("creationCrossCheck"), "tool", "version", "checkedAt");

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
                break;
            case "digest-vector":
                Require("tag", "envelope", "expected");
                break;
            case "interaction-vector":
                Require("predecessor", "consumedInputSha256", "currentHeadSha", "interactionOrdinal", "expected");
                break;
            case "materialization-vector":
                Require("input", "expected");
                foreach (var key in new[] { "logicalStreamHex", "providerStreamHex", "logicalPrefixSha256", "prefixSha256", "digests", "stableBoundary", "dynamicSuffix" })
                {
                    Assert.True(vector.GetProperty("expected").TryGetProperty(key, out _), $"{entry.Id}: missing expected.{key}");
                }

                break;
            case "append-vector":
                Require("baseVectorId", "successorVectorId", "expected");
                break;
            case "invalidation-vector":
                Require("mode", "mutation", "expected");
                var mode = vector.GetProperty("mode").GetString();
                if (mode == "materializer")
                {
                    Require("baseVectorId", "successorVectorId");
                }
                else if (mode == "hash-framing")
                {
                    Require("baseInput", "mutatedInput");
                }
                else
                {
                    Assert.Fail($"{entry.Id}: unknown mode {mode}");
                }

                break;
            case "invalid-vector":
                Require("target", "input", "expected");
                allowed.Add("scope");
                var expected = vector.GetProperty("expected");
                Assert.True(
                    expected.TryGetProperty("csharpCode", out _) || expected.TryGetProperty("typescriptCode", out _),
                    $"{entry.Id}: expected must carry at least one language code");
                break;
            default:
                Assert.Fail($"{entry.Id}: unknown kind {entry.Kind}");
                break;
        }

        foreach (var property in vector.EnumerateObject())
        {
            Assert.True(allowed.Contains(property.Name), $"{entry.Id}: unknown vector field {property.Name}");
        }
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
