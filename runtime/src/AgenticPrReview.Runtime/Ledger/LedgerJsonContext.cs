using System.Text.Json.Serialization;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Source-generated <see cref="System.Text.Json"/> metadata for the closed
/// ledger model.
///
/// <para>
/// The runtime parser is intentionally reflection-free: it uses
/// <see cref="System.Text.Json.JsonDocument"/> for shape validation and a
/// hand-written canonical writer (<see cref="LedgerCanonicalizer"/>) for
/// bytes. Neither path consumes this context.
/// </para>
///
/// <para>
/// This context registers STJ metadata for every ledger record type so that
/// future callers that need typed STJ deserialization (for example a diagnostic
/// dump or a debugger view) can use it without opting back into reflection.
/// The types intentionally mirror the C# record shape (<see cref="LedgerRecord"/>
/// carries both a nullable context and outcome) rather than the on-wire flat
/// record shape; a full wire-shape mapping is a follow-up when a callsite
/// needs it.
/// </para>
///
/// <para>
/// <see cref="JsonSourceGenerationOptionsAttribute.DefaultIgnoreCondition"/>
/// stays at its default (<c>Never</c>) so nullable-but-required finding
/// fields such as <c>path</c>, <c>startLine</c>, and <c>endLine</c> serialize
/// <c>null</c> explicitly, as required by the schema.
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
