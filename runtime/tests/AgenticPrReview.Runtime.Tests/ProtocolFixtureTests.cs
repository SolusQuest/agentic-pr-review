using System.Text.Json;
using System.Security.Cryptography;
using AgenticPrReview.Runtime;

namespace AgenticPrReview.Runtime.Tests;

public sealed class ProtocolFixtureTests
{
    [Fact]
    public void ManifestFixturesMatchTheEmbeddedSchemasAndSemanticRules()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));
        var schemas = SchemaContracts.Load(typeof(RuntimeApplication).Assembly);
        var registeredFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() == "fixture")
            {
                AssertFixture(entry, root, schemas);
                registeredFiles.Add(entry.GetProperty("file").GetString()!);
                continue;
            }

            if (entry.GetProperty("type").GetString() == "case")
            {
                AssertCase(entry, root, schemas);
                var directory = entry.GetProperty("directory").GetString()!;
                foreach (var contract in entry.GetProperty("contracts").EnumerateObject())
                {
                    registeredFiles.Add($"{directory}/{contract.Value.GetString()}");
                }
                continue;
            }

            throw new InvalidOperationException("Unknown manifest entry type.");
        }

        var actualFiles = Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Where(path => path != "manifest.json")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.Equal(actualFiles.Order(), registeredFiles.Order());
    }

    private static void AssertFixture(JsonElement entry, string root, SchemaContracts schemas)
    {
        var kind = ParseKind(entry.GetProperty("contract").GetString()!);
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, entry.GetProperty("file").GetString()!)));
        var valid = schemas.IsValid(kind, fixture.RootElement) &&
            (kind != SchemaKind.Result || SemanticValidation.HasValidFindingLocations(fixture.RootElement));
        Assert.Equal(entry.GetProperty("valid").GetBoolean(), valid);
    }

    private static void AssertCase(JsonElement entry, string root, SchemaContracts schemas)
    {
        var directory = Path.Combine(root, entry.GetProperty("directory").GetString()!);
        var contracts = entry.GetProperty("contracts");
        var documents = new Dictionary<string, JsonDocument>();
        try
        {
            foreach (var contract in contracts.EnumerateObject())
            {
                var kind = ParseKind(contract.Name);
                var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, contract.Value.GetString()!)));
                documents.Add(contract.Name, document);
                Assert.Equal(entry.GetProperty("valid").GetBoolean(), schemas.IsValid(kind, document.RootElement));
                if (kind == SchemaKind.Result)
                {
                    Assert.True(SemanticValidation.HasValidFindingLocations(document.RootElement));
                }
            }

            foreach (var link in entry.GetProperty("verifyHashChain").EnumerateArray())
            {
                var path = link.GetString()!;
                var parts = path.Split('.');
                var actual = documents[parts[0]].RootElement;
                foreach (var part in parts.Skip(1))
                {
                    actual = actual.GetProperty(part);
                }

                var expectedFile = parts[^1] == "inputSha256" ? "input" : "trace";
                Assert.Equal(Hash(File.ReadAllBytes(Path.Combine(directory, contracts.GetProperty(expectedFile).GetString()!))), actual.GetString());
            }
        }
        finally
        {
            foreach (var document in documents.Values)
            {
                document.Dispose();
            }
        }
    }

    private static SchemaKind ParseKind(string contract) => contract switch
    {
        "input" => SchemaKind.Input,
        "result" => SchemaKind.Result,
        "trace" => SchemaKind.Trace,
        _ => throw new InvalidOperationException("Unknown fixture contract."),
    };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
