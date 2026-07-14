using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Source-generated <see cref="System.Text.Json"/> metadata for the closed
/// ledger model. The runtime parser uses <see cref="System.Text.Json.JsonDocument"/>
/// so all AOT-relevant paths remain reflection-free; this context is defined
/// so that any future serialization or deserialization API that wishes to use
/// STJ's typed model can consume it without opting back into reflection.
///
/// <para>
/// <see cref="JsonSerializerDefaultsAttribute.DefaultIgnoreCondition"/> is
/// intentionally left at its default (<c>Never</c>) so nullable-but-required
/// finding fields such as <c>path</c>, <c>startLine</c>, and <c>endLine</c>
/// serialize <c>null</c> explicitly, as the ledger schema requires.
/// </para>
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false)]
[JsonSerializable(typeof(LedgerModel))]
[JsonSerializable(typeof(LedgerHeader))]
[JsonSerializable(typeof(LedgerRecord))]
[JsonSerializable(typeof(ReviewContextRecord))]
[JsonSerializable(typeof(ReviewOutcomeRecord))]
[JsonSerializable(typeof(ChangedFileEntry))]
[JsonSerializable(typeof(ChangedFilePatch))]
[JsonSerializable(typeof(LedgerFinding))]
public partial class LedgerJsonContext : JsonSerializerContext
{
}
