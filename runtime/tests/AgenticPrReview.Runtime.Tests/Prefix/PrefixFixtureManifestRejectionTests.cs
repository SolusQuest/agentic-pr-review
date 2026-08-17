using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixFixtureManifestRejectionTests
{
    public static TheoryData<string, string> RejectionCases => new()
    {
        { "duplicate-id", "duplicate id" },
        { "duplicate-file", "duplicate file" },
        { "missing-listed-file", "manifest file set must exactly match" },
        { "unlisted-file", "manifest file set must exactly match" },
        { "unsafe-path", "unsafe fixture path" },
        { "id-mismatch", "vector id mismatch" },
        { "kind-mismatch", "vector kind mismatch" },
        { "self-reference", "must not self-reference" },
        { "missing-reference", "missing-vector must resolve" },
        { "wrong-reference-kind", "must resolve to a materialization-vector" },
        { "unknown-vector-field", "unknown vector field unexpected" },
        { "missing-kind-field", "missing field input" },
        { "malformed-manifest-container", "generatedBy: expected object" },
        { "malformed-manifest-vectors", "manifest vectors must be an array" },
        { "malformed-manifest-entry", "manifest entry id kind and file must be strings" },
        { "unknown-invalidation-mode", "unknown mode unknown" },
        { "missing-historical-typescript-code", "canonical-json must carry typescriptCode" },
        { "identity-missing-historical-typescript-code", "historical TypeScript diagnostic provenance" },
        { "invalid-expected-union", "historical TypeScript diagnostic provenance" },
        { "framing-tag-wrong-type", "input.tag: unexpected JSON kind Number" },
        { "append-expected-wrong-type", "expected.logicalStrictPrefix: unexpected JSON kind String" },
        { "materialization-nested-input", "interactionOrdinal: expected a nonnegative integer" },
        { "materialization-invalid-expected", "expected.logicalStreamHex: expected an even number" },
        { "materialization-boundary", "stableBoundary.segmentCount: expected a nonnegative integer" },
        { "invalid-diagnostic-field", "expected.path must be a string" },
        { "non-object-vector", "vector must be an object" },
    };

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public void SyntheticManifestViolationsReachTheirIntendedBranch(
        string caseId,
        string expectedMessage)
    {
        var root = CopyCorpus();
        try
        {
            Assert.NotEmpty(PrefixFixtureLoader.LoadManifest(root));
            Mutate(root, caseId);
            var exception = Assert.ThrowsAny<Exception>(() =>
                PrefixFixtureLoader.LoadManifest(root));
            Assert.Contains(expectedMessage, exception.Message,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void EnvelopeMutationRequiresContentAndMatchingDigestTogether()
    {
        using var baseline = JsonDocument.Parse(
            """{"envelopes":{"template":{"definition":1}},"expectedIdentities":{"templateId":"a"}}""");
        using var contentOnly = JsonDocument.Parse(
            """{"envelopes":{"template":{"definition":2}},"expectedIdentities":{"templateId":"a"}}""");
        using var digestOnly = JsonDocument.Parse(
            """{"envelopes":{"template":{"definition":1}},"expectedIdentities":{"templateId":"b"}}""");
        using var contentAndDigest = JsonDocument.Parse(
            """{"envelopes":{"template":{"definition":2}},"expectedIdentities":{"templateId":"b"}}""");

        var contentException = Assert.ThrowsAny<Exception>(() =>
            PrefixFixtureLoader.AssertMutationDiffs("inv-content",
                "template envelope content/version", baseline.RootElement,
                contentOnly.RootElement));
        Assert.Contains("does not match its exact input diff predicate",
            contentException.Message, StringComparison.Ordinal);
        var digestException = Assert.ThrowsAny<Exception>(() =>
            PrefixFixtureLoader.AssertMutationDiffs("inv-digest",
                "template envelope content/version", baseline.RootElement,
                digestOnly.RootElement));
        Assert.Contains("does not match its exact input diff predicate",
            digestException.Message, StringComparison.Ordinal);
        PrefixFixtureLoader.AssertMutationDiffs("inv-complete",
            "template envelope content/version", baseline.RootElement,
            contentAndDigest.RootElement);
    }

    [Fact]
    public void DottedPropertyNamesAreNotNestedMutationPaths()
    {
        using var baseline = JsonDocument.Parse(
            """{"envelopes":{"template":{"definition":1}},"expectedIdentities":{"templateId":"a"}}""");
        using var forged = JsonDocument.Parse(
            """{"envelopes":{"template":{"definition":1}},"expectedIdentities":{"templateId":"a"},"envelopes.template.definition":2,"expectedIdentities.templateId":"b"}""");

        var exception = Assert.ThrowsAny<Exception>(() =>
            PrefixFixtureLoader.AssertMutationDiffs("inv-dotted",
                "template envelope content/version", baseline.RootElement,
                forged.RootElement));
        Assert.Contains("envelopes.template.definition",
            exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("envelopes/template/definition",
            exception.Message, StringComparison.Ordinal);
    }

    private static string CopyCorpus()
    {
        var root = Path.Join(Path.GetTempPath(), $"apr-prefix-corpus-{Guid.NewGuid():N}");
        foreach (var source in Directory.GetFiles(
                     PrefixFixtureLoader.FixtureRoot, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(PrefixFixtureLoader.FixtureRoot, source);
            var target = Path.Join(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target);
        }

        return root;
    }

    private static void Mutate(string root, string caseId)
    {
        var manifestPath = Path.Join(root, "manifest.json");
        var manifest = ReadObject(manifestPath);
        var vectors = manifest["vectors"]!.AsArray();
        var first = vectors[0]!.AsObject();
        var second = vectors[1]!.AsObject();

        switch (caseId)
        {
            case "duplicate-id":
                second["id"] = first["id"]!.GetValue<string>();
                Write(manifestPath, manifest);
                return;
            case "duplicate-file":
                second["file"] = first["file"]!.GetValue<string>();
                Write(manifestPath, manifest);
                return;
            case "missing-listed-file":
                File.Delete(Path.Join(root, first["file"]!.GetValue<string>()));
                return;
            case "unlisted-file":
                File.WriteAllText(Path.Join(root, "unlisted.json"), "{}");
                return;
            case "unsafe-path":
                first["file"] = "../escape.json";
                Write(manifestPath, manifest);
                return;
            case "malformed-manifest-container":
                manifest["generatedBy"] = null;
                Write(manifestPath, manifest);
                return;
            case "malformed-manifest-vectors":
                manifest["vectors"] = null;
                Write(manifestPath, manifest);
                return;
            case "malformed-manifest-entry":
                first["file"] = 42;
                Write(manifestPath, manifest);
                return;
        }

        var entry = caseId switch
        {
            "self-reference" or "missing-reference" or
                "wrong-reference-kind" or "append-expected-wrong-type" =>
                FindEntry(vectors, kind: "append-vector"),
            "unknown-invalidation-mode" =>
                FindEntry(vectors, kind: "invalidation-vector", idContains: "template-content"),
            "missing-historical-typescript-code" =>
                FindEntry(vectors, kind: "invalid-vector", fileContains: "canonical-non-finite"),
            "kind-mismatch" or "identity-missing-historical-typescript-code" or
                "invalid-expected-union" or "invalid-diagnostic-field" =>
                FindEntry(vectors, kind: "invalid-vector", fileContains: "identity-empty"),
            "materialization-nested-input" or "materialization-invalid-expected" or
                "materialization-boundary" =>
                FindEntry(vectors, kind: "materialization-vector", fileContains: "bootstrap.json"),
            "missing-kind-field" => FindEntry(vectors, kind: "framing-vector"),
            "framing-tag-wrong-type" =>
                FindEntry(vectors, kind: "framing-vector", fileContains: "tag-template.json"),
            _ => first,
        };
        var vectorPath = Path.Join(root, entry["file"]!.GetValue<string>());
        var vector = ReadObject(vectorPath);

        switch (caseId)
        {
            case "id-mismatch":
                vector["id"] = "mismatched-id";
                break;
            case "kind-mismatch":
                vector["kind"] = "digest-vector";
                break;
            case "self-reference":
                vector["successorVectorId"] = entry["id"]!.GetValue<string>();
                break;
            case "missing-reference":
                vector["baseVectorId"] = "missing-vector";
                break;
            case "wrong-reference-kind":
                vector["baseVectorId"] = FindEntry(vectors, kind: "framing-vector")["id"]!
                    .GetValue<string>();
                break;
            case "unknown-vector-field":
                vector["unexpected"] = true;
                break;
            case "missing-kind-field":
                vector.Remove("input");
                break;
            case "unknown-invalidation-mode":
                vector["mode"] = "unknown";
                break;
            case "missing-historical-typescript-code":
                vector["expected"]!.AsObject().Remove("typescriptCode");
                break;
            case "identity-missing-historical-typescript-code":
                vector["expected"]!.AsObject().Remove("typescriptCode");
                break;
            case "invalid-expected-union":
                vector["expected"]!.AsObject()["csharpCode"] = "unexpected";
                break;
            case "framing-tag-wrong-type":
                vector["input"]!.AsObject()["tag"] = 1;
                break;
            case "append-expected-wrong-type":
                vector["expected"]!.AsObject()["logicalStrictPrefix"] = "true";
                break;
            case "materialization-nested-input":
                vector["input"]!.AsObject()["interaction"]!.AsObject()["interactionOrdinal"] = -1;
                break;
            case "materialization-invalid-expected":
                vector["expected"]!.AsObject()["logicalStreamHex"] = "0";
                break;
            case "materialization-boundary":
                vector["expected"]!.AsObject()["stableBoundary"]!.AsObject()["segmentCount"] = 1.5;
                break;
            case "invalid-diagnostic-field":
                vector["expected"]!.AsObject()["path"] = 42;
                break;
            case "non-object-vector":
                File.WriteAllText(vectorPath, "null");
                return;
            default:
                throw new InvalidOperationException($"Unknown rejection case {caseId}.");
        }

        Write(vectorPath, vector);
    }

    private static JsonObject FindEntry(
        JsonArray entries,
        string? kind = null,
        string? id = null,
        string? idContains = null,
        string? fileContains = null) =>
        entries.Select(node => node!.AsObject()).First(entry =>
            (kind is null || entry["kind"]!.GetValue<string>() == kind) &&
            (id is null || entry["id"]!.GetValue<string>() == id) &&
            (idContains is null || entry["id"]!.GetValue<string>().Contains(idContains,
                StringComparison.Ordinal)) &&
            (fileContains is null || entry["file"]!.GetValue<string>().Contains(fileContains,
                StringComparison.Ordinal)));

    private static JsonObject ReadObject(string path) =>
        JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void Write(string path, JsonNode node) =>
        File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
        }));
}
