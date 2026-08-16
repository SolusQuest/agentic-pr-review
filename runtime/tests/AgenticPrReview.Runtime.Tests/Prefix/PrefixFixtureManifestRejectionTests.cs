using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixFixtureManifestRejectionTests
{
    public static TheoryData<string> RejectionCases => new()
    {
        "duplicate-id",
        "duplicate-file",
        "missing-listed-file",
        "unlisted-file",
        "unsafe-path",
        "id-mismatch",
        "missing-reference",
        "wrong-reference-kind",
        "unknown-vector-field",
        "missing-kind-field",
        "malformed-manifest-container",
        "malformed-manifest-entry",
        "unknown-invalidation-mode",
        "missing-historical-typescript-code",
        "invalid-expected-union",
        "wrong-recursive-value-type",
        "malformed-materialization-input",
        "envelope-mutation-digest-coupling",
        "dotted-property-path-distinction",
        "non-object-vector",
    };

    [Theory]
    [MemberData(nameof(RejectionCases))]
    public void SyntheticManifestViolationsFailClosed(string caseId)
    {
        var root = CopyCorpus();
        try
        {
            Mutate(root, caseId);
            Assert.ThrowsAny<Exception>(() => PrefixFixtureLoader.LoadManifest(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
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
        if (caseId == "malformed-manifest-container")
        {
            File.WriteAllText(manifestPath, "[]");
            return;
        }

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
            case "malformed-manifest-entry":
                vectors[0] = "not-an-entry";
                Write(manifestPath, manifest);
                return;
        }

        var entry = caseId switch
        {
            "missing-reference" or "wrong-reference-kind" =>
                FindEntry(vectors, kind: "append-vector"),
            "unknown-invalidation-mode" or "envelope-mutation-digest-coupling" or
                "dotted-property-path-distinction" =>
                FindEntry(vectors, kind: "invalidation-vector", idContains: "template-content"),
            "missing-historical-typescript-code" =>
                FindEntry(vectors, kind: "invalid-vector", fileContains: "identity-empty"),
            "invalid-expected-union" =>
                FindEntry(vectors, kind: "invalid-vector", fileContains: "identity-empty"),
            "malformed-materialization-input" =>
                FindEntry(vectors, kind: "materialization-vector"),
            "missing-kind-field" or "wrong-recursive-value-type" =>
                FindEntry(vectors, kind: "framing-vector"),
            _ => first,
        };
        var vectorPath = Path.Join(root, entry["file"]!.GetValue<string>());
        var vector = ReadObject(vectorPath);

        switch (caseId)
        {
            case "id-mismatch":
                vector["id"] = "mismatched-id";
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
            case "invalid-expected-union":
                vector["expected"]!.AsObject()["csharpCode"] = "unexpected";
                break;
            case "wrong-recursive-value-type":
                vector["input"] = "not-an-object";
                break;
            case "malformed-materialization-input":
                vector["input"] = null;
                break;
            case "envelope-mutation-digest-coupling":
                vector["mutation"] = "providerId";
                break;
            case "dotted-property-path-distinction":
            {
                var successorId = vector["successorVectorId"]!.GetValue<string>();
                var successor = FindEntry(vectors, id: successorId);
                var successorPath = Path.Join(root, successor["file"]!.GetValue<string>());
                var successorVector = ReadObject(successorPath);
                successorVector["input"]!.AsObject()["expectedIdentities.providerId"] = "not-nested";
                Write(successorPath, successorVector);
                return;
            }
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
