using System.Text.Json;
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

        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            if (entry.GetProperty("type").GetString() != "fixture")
            {
                continue;
            }

            var kind = entry.GetProperty("contract").GetString() switch
            {
                "input" => SchemaKind.Input,
                "result" => SchemaKind.Result,
                "trace" => SchemaKind.Trace,
                _ => throw new InvalidOperationException("Unknown fixture contract."),
            };
            using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, entry.GetProperty("file").GetString()!)));
            var valid = schemas.IsValid(kind, fixture.RootElement) &&
                (kind != SchemaKind.Result || SemanticValidation.HasValidFindingLocations(fixture.RootElement));

            Assert.Equal(entry.GetProperty("valid").GetBoolean(), valid);
        }
    }
}
