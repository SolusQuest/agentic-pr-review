using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

// Ephemeral, same-source gate evidence. These commitments detect substitution;
// they do not authenticate a producer or establish live traffic.
internal sealed record GateCase(string Id, string Selection, string BindingSha256, JsonElement Evidence)
{
    internal static GateCase Create(string id, string selection, byte[] evidence)
    {
        using var parsed = JsonDocument.Parse(evidence);
        var value = parsed.RootElement.Clone();
        return new(id, selection, Binding(id, selection, value), value);
    }

    internal static string Binding(string id, string selection, JsonElement evidence) =>
        AgentCanonical.HashDomain("apr.r6.gate.case", Encoding.UTF8.GetBytes(id + "\n" + selection + "\n" + evidence.GetRawText()));
}

internal sealed record GateSelection(string SourceCommit, string SourceTree, bool SourceClean,
    string Mode, string BuildSha256, string ReplaySha256, string GrowthSha256);
internal sealed record GateReport(GateSelection Selection, ImmutableArray<GateCase> Cases);
internal sealed record GateScalar(string Code, ImmutableArray<decimal?> Values, ImmutableArray<string> Facts);
internal sealed record GatePrefix(ImmutableArray<PrefixObservation> Observations,
    ImmutableArray<PrefixComparison> Comparisons, ImmutableArray<int> Counts);
internal sealed record GateVerdict(string Code, GateSelection Selection, int Cases, string SemanticSha256);
internal sealed record GateHostRow(int Phase, string Action, string Disposition, string? AgentCode, int? ModelCalls,
    int InitialMessages, int AcceptedCount, long Generation, string EpochSha256, string SessionIdSha256,
    string StoredSessionSha256, string? RestoredSessionSha256, ImmutableArray<string> AcceptanceIdentities,
    bool StoredMarkersMatch, bool WireExclusion, int Publications);
internal sealed record GateHostReport(string Mode, string SourceCommit, string SourceTree, bool SourceClean,
    string CorpusSha256, ImmutableArray<GateHostRow> Rows);
internal sealed record GateWorker(int Index, int ProcessId, string Startup);
internal sealed record GateEconomics(EconomicsReport Report, ImmutableArray<GateWorker> Workers,
    ImmutableArray<EconomicsCredentialProof> CredentialProofs, int SecretReads, bool NegativeRootObserved, bool RootAbsent);
internal sealed record GateComparisonConflict(ComparisonInput Left, ComparisonInput Right);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, UseStringEnumConverter = true, MaxDepth = 48)]
[JsonSerializable(typeof(GateReport))]
[JsonSerializable(typeof(GateCase))]
[JsonSerializable(typeof(GateSelection))]
[JsonSerializable(typeof(GateScalar))]
[JsonSerializable(typeof(GatePrefix))]
[JsonSerializable(typeof(GateVerdict))]
[JsonSerializable(typeof(GateHostReport))]
[JsonSerializable(typeof(GateEconomics))]
[JsonSerializable(typeof(GateComparisonConflict))]
internal sealed partial class GateJson : JsonSerializerContext;

internal static class GateContracts
{
    internal const int MaximumBytes = 64 * 1024 * 1024;
    internal const int MaximumCases = 96;
    internal const string Canary = "APR279_PRIVATE_GATE_CANARY";

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "The empty assembly location is the same native launch discriminator used by ReplayProcess.")]
    internal static string Mode => string.IsNullOrEmpty(typeof(Program).Assembly.Location) ? "aot" : "framework";

    internal static GateSelection Select(string fixtures) => new(EvaluationSource.Commit, EvaluationSource.Tree,
        EvaluationSource.Clean, Mode, EconomicsBuild.Current(),
        (ReplayAdmission.Load(Path.Combine(fixtures, "replay")).Fixture ?? throw new IOException()).CorpusSha256,
        (ReplayAdmission.Load(Path.Combine(fixtures, "growth")).Fixture ?? throw new IOException()).CorpusSha256);

    internal static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("r6_gate_invariant");
    }

    internal static GateCase Scalar(string id, string selection, string code, decimal?[] values, params string[] facts) =>
        GateCase.Create(id, selection, JsonSerializer.SerializeToUtf8Bytes(new GateScalar(code, [.. values], [.. facts]),
            GateJson.Default.GateScalar));

    internal static byte[] Bytes(JsonElement value) => Encoding.UTF8.GetBytes(value.GetRawText());
    internal static string Hash(char value, int length = 64) => new(value, length);

    // Inspect decoded strings too: escaping a canary must not hide it.
    internal static bool Safe(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
            return value.EnumerateObject().All(property => SafeText(property.Name) && Safe(property.Value));
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().All(Safe);
        return value.ValueKind != JsonValueKind.String || SafeText(value.GetString()!);
    }

    private static bool SafeText(string value) => !new[]
    {
        Canary, "APR242_TERMINAL_CANARY", "APR242_CANDIDATE_CANARY", "APR242_NOTES_CANARY",
        "APR242_POLICY_CANARY", "APR242_SOURCE_CANARY", "private-prefix-canary-275",
        "synthetic-old-only-tool-fact", "synthetic-old-only-continuation", "synthetic-fresh-tool-fact",
        "synthetic-fresh-continuation", "reasoning_content", "authorization", "private_request",
    }.Concat(EconomicsCredentialProbe.Canaries.Values).Any(forbidden => value.Contains(forbidden, StringComparison.OrdinalIgnoreCase));

    internal static GateReport? Read(ReadOnlySpan<byte> bytes) =>
        PricingJson.ReadValue(bytes, GateJson.Default.GateReport, MaximumBytes, 48);
}
