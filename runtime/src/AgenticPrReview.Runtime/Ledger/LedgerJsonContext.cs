using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Source-generated <see cref="System.Text.Json"/> metadata for the closed
/// ledger model. The runtime parser is reflection-free (JsonDocument-driven)
/// and does not consume this context; it is registered for future callsites
/// (diagnostic dumps, debugger views) that want typed STJ deserialization
/// without opting back into reflection.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(LedgerModel))]
[JsonSerializable(typeof(LedgerHeader))]
[JsonSerializable(typeof(LedgerRecord))]
[JsonSerializable(typeof(ReviewContextRecord))]
[JsonSerializable(typeof(ReviewOutcomeRecord))]
[JsonSerializable(typeof(LedgerChangedFile))]
[JsonSerializable(typeof(LedgerBoundedPatch))]
[JsonSerializable(typeof(LedgerFinding))]
public partial class LedgerJsonContext : JsonSerializerContext
{
}
