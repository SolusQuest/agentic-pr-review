using System.Reflection;
using System.Text.Json;
using Json.Schema;

namespace AgenticPrReview.Runtime;

public enum SchemaKind { Input, Result, Trace, Ledger }

public sealed class SchemaContracts
{
    private static readonly Lazy<SchemaContracts> Default = new(() => LoadCore(typeof(RuntimeApplication).Assembly));
    private readonly JsonSchema input;
    private readonly JsonSchema result;
    private readonly JsonSchema trace;
    private readonly JsonSchema ledger;

    private SchemaContracts(JsonSchema input, JsonSchema result, JsonSchema trace, JsonSchema ledger)
    {
        this.input = input;
        this.result = result;
        this.trace = trace;
        this.ledger = ledger;
    }

    public static SchemaContracts Load(Assembly assembly)
    {
        if (assembly == typeof(RuntimeApplication).Assembly)
        {
            return Default.Value;
        }

        return LoadCore(assembly);
    }

    private static SchemaContracts LoadCore(Assembly assembly) => new(
        ReadSchema(assembly, "AgenticPrReview.Protocol.review-input.v1.json"),
        ReadSchema(assembly, "AgenticPrReview.Protocol.review-result.v1.json"),
        ReadSchema(assembly, "AgenticPrReview.Protocol.review-trace.v1.json"),
        ReadSchema(assembly, "AgenticPrReview.Protocol.provider-session-ledger.v1.json"));

    public bool IsValid(SchemaKind kind, JsonElement instance) =>
        GetSchema(kind).Evaluate(instance, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    internal JsonSchema GetSchema(SchemaKind kind) =>
        kind switch
        {
            SchemaKind.Input => input,
            SchemaKind.Result => result,
            SchemaKind.Trace => trace,
            SchemaKind.Ledger => ledger,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static JsonSchema ReadSchema(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded schema resource: {resourceName}");
        using var reader = new StreamReader(stream);
        return JsonSchema.FromText(reader.ReadToEnd());
    }
}

public static class SemanticValidation
{
    public static bool HasValidFindingLocations(JsonElement result)
    {
        if (!result.TryGetProperty("findings", out var findings) || findings.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var finding in findings.EnumerateArray())
        {
            var start = finding.GetProperty("startLine");
            var end = finding.GetProperty("endLine");
            var hasStart = start.ValueKind != JsonValueKind.Null;
            var hasEnd = end.ValueKind != JsonValueKind.Null;
            if (hasStart != hasEnd)
            {
                return false;
            }

            if (hasStart)
            {
                if (finding.GetProperty("path").ValueKind != JsonValueKind.String || start.GetInt32() > end.GetInt32())
                {
                    return false;
                }
            }
        }

        return true;
    }
}
