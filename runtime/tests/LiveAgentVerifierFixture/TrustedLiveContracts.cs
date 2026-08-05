using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal static class TrustedLiveCodes
{
    internal const string Passed = "APR_R3_TRUSTED_LIVE_OK";
    internal const string Arguments = "APR_R3_TRUSTED_LIVE_ARGUMENTS_INVALID";
    internal const string Provenance = "APR_R3_TRUSTED_LIVE_PROVENANCE_FAILED";
    internal const string Provider = "APR_R3_TRUSTED_LIVE_UPSTREAM_FAILED";
    internal const string MissingTool = "APR_R3_TRUSTED_LIVE_MISSING_TOOL";
    internal const string Grounding = "APR_R3_TRUSTED_LIVE_GROUNDING_FAILED";
    internal const string MustFind = "APR_R3_TRUSTED_LIVE_MUST_FIND_FAILED";
    internal const string MustNotFind = "APR_R3_TRUSTED_LIVE_MUST_NOT_FIND_FAILED";
    internal const string Continuation = "APR_R3_TRUSTED_LIVE_CONTINUATION_FAILED";
    internal const string Cleanup = "APR_R3_TRUSTED_LIVE_CLEANUP_FAILED";
    internal const string Canary = "APR_R3_TRUSTED_LIVE_CANARY_FAILED";
    internal const string Timeout = "APR_R3_TRUSTED_LIVE_TIMEOUT";
    internal const string Infrastructure =
        "APR_R3_TRUSTED_LIVE_INFRASTRUCTURE_FAILED";
}

internal sealed record TrustedLivePhaseReceipt(
    string Scenario,
    string Status,
    string OutcomeCode,
    string ProductCode,
    long? Generation,
    string Transition,
    int ModelCalls,
    int ToolCalls,
    bool HandoffReady,
    bool AcceptedTupleValidated,
    string InvocationIdentitySha256,
    string? LineageSha256,
    string? AcceptedSessionSha256,
    string? AcceptedEnvelopeSha256,
    string? TerminalSha256,
    string? QualityStatus,
    string? QualityClassification,
    string? QualityCode,
    int FindingCount,
    int QualityToolCallCount,
    string ExecutionArtifactSha256,
    string BuildPairSha256,
    string Kind = "apr-r3-trusted-live-phase-v1");

internal static class TrustedLiveReceiptCodec
{
    internal static byte[] Write(TrustedLivePhaseReceipt receipt)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", receipt.Kind);
            writer.WriteString("scenario", receipt.Scenario);
            writer.WriteString("status", receipt.Status);
            writer.WriteString("outcome_code", receipt.OutcomeCode);
            writer.WriteString("product_code", receipt.ProductCode);
            WriteNullableNumber(writer, "generation", receipt.Generation);
            writer.WriteString("transition", receipt.Transition);
            writer.WriteNumber("model_calls", receipt.ModelCalls);
            writer.WriteNumber("tool_calls", receipt.ToolCalls);
            writer.WriteBoolean("handoff_ready", receipt.HandoffReady);
            writer.WriteBoolean(
                "accepted_tuple_validated",
                receipt.AcceptedTupleValidated);
            writer.WriteString(
                "invocation_identity_sha256",
                receipt.InvocationIdentitySha256);
            WriteNullableString(writer, "lineage_sha256", receipt.LineageSha256);
            WriteNullableString(
                writer,
                "accepted_session_sha256",
                receipt.AcceptedSessionSha256);
            WriteNullableString(
                writer,
                "accepted_envelope_sha256",
                receipt.AcceptedEnvelopeSha256);
            WriteNullableString(
                writer,
                "terminal_sha256",
                receipt.TerminalSha256);
            WriteNullableString(writer, "quality_status", receipt.QualityStatus);
            WriteNullableString(
                writer,
                "quality_classification",
                receipt.QualityClassification);
            WriteNullableString(writer, "quality_code", receipt.QualityCode);
            writer.WriteNumber("finding_count", receipt.FindingCount);
            writer.WriteNumber(
                "quality_tool_call_count",
                receipt.QualityToolCallCount);
            writer.WriteString(
                "execution_artifact_sha256",
                receipt.ExecutionArtifactSha256);
            writer.WriteString("build_pair_sha256", receipt.BuildPairSha256);
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    internal static TrustedLivePhaseReceipt? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > 64 * 1024)
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            var expected = new[]
            {
                "kind",
                "scenario",
                "status",
                "outcome_code",
                "product_code",
                "generation",
                "transition",
                "model_calls",
                "tool_calls",
                "handoff_ready",
                "accepted_tuple_validated",
                "invocation_identity_sha256",
                "lineage_sha256",
                "accepted_session_sha256",
                "accepted_envelope_sha256",
                "terminal_sha256",
                "quality_status",
                "quality_classification",
                "quality_code",
                "finding_count",
                "quality_tool_call_count",
                "execution_artifact_sha256",
                "build_pair_sha256",
            };
            if (root.ValueKind != JsonValueKind.Object ||
                !root.EnumerateObject().Select(item => item.Name)
                    .SequenceEqual(expected, StringComparer.Ordinal))
            {
                return null;
            }
            var receipt = new TrustedLivePhaseReceipt(
                RequiredString(root, "scenario"),
                RequiredString(root, "status"),
                RequiredString(root, "outcome_code"),
                RequiredString(root, "product_code"),
                NullableInt64(root, "generation"),
                RequiredString(root, "transition"),
                RequiredInt32(root, "model_calls"),
                RequiredInt32(root, "tool_calls"),
                RequiredBoolean(root, "handoff_ready"),
                RequiredBoolean(root, "accepted_tuple_validated"),
                RequiredString(root, "invocation_identity_sha256"),
                NullableString(root, "lineage_sha256"),
                NullableString(root, "accepted_session_sha256"),
                NullableString(root, "accepted_envelope_sha256"),
                NullableString(root, "terminal_sha256"),
                NullableString(root, "quality_status"),
                NullableString(root, "quality_classification"),
                NullableString(root, "quality_code"),
                RequiredInt32(root, "finding_count"),
                RequiredInt32(root, "quality_tool_call_count"),
                RequiredString(root, "execution_artifact_sha256"),
                RequiredString(root, "build_pair_sha256"),
                RequiredString(root, "kind"));
            return PhaseIsBounded(receipt) ? receipt : null;
        }
        catch (Exception exception) when (exception is
            ArgumentException or
            IOException or
            InvalidOperationException or
            JsonException or
            NotSupportedException or
            UnauthorizedAccessException or
            System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static string WriteCompletion(
        string status,
        string code,
        string sha,
        string workflowRef,
        string workflowSha,
        string? fixtureSha256,
        VerifierBuildPair? buildPair,
        IReadOnlyList<TrustedLivePhaseReceipt> phases)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", "apr-r3-trusted-live-completion");
            writer.WriteString("status", status);
            writer.WriteString("code", code);
            writer.WriteString("tested_sha", sha);
            writer.WriteString("workflow_ref", workflowRef);
            writer.WriteString("workflow_sha", workflowSha);
            writer.WriteString("provider", "deepseek");
            writer.WriteString("model", DeepSeekAdapterContext.Model);
            writer.WriteString("adapter", DeepSeekAdapterContext.Adapter);
            writer.WriteString("fixture", "apr-r3-quality-corpus");
            WriteNullableString(
                writer,
                "fixture_sha256",
                fixtureSha256);
            WriteNullableString(
                writer,
                "execution_kind",
                buildPair?.ExecutionKind);
            WriteNullableString(
                writer,
                "execution_artifact_sha256",
                buildPair?.ExecutionArtifactSha256);
            WriteNullableString(
                writer,
                "architecture_assembly_sha256",
                buildPair?.ArchitectureAssemblySha256);
            WriteNullableString(
                writer,
                "build_pair_sha256",
                buildPair?.BuildPairSha256);
            writer.WriteNumber("phase_count", phases.Count);
            writer.WriteNumber("model_calls", phases.Sum(item => item.ModelCalls));
            writer.WriteNumber("tool_calls", phases.Sum(item => item.ToolCalls));
            writer.WriteStartArray("phases");
            foreach (var phase in phases)
            {
                writer.WriteStartObject();
                writer.WriteString("scenario", phase.Scenario);
                writer.WriteString("outcome_code", phase.OutcomeCode);
                WriteNullableNumber(writer, "generation", phase.Generation);
                writer.WriteString("transition", phase.Transition);
                writer.WriteNumber("model_calls", phase.ModelCalls);
                writer.WriteNumber("tool_calls", phase.ToolCalls);
                WriteNullableString(
                    writer,
                    "terminal_sha256",
                    phase.TerminalSha256);
                WriteNullableString(
                    writer,
                    "lineage_sha256",
                    phase.LineageSha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool PhaseIsBounded(TrustedLivePhaseReceipt receipt) =>
        receipt.Kind == "apr-r3-trusted-live-phase-v1" &&
        receipt.Status is "passed" or "failed" &&
        receipt.ModelCalls is >= 0 and <= 8 &&
        receipt.ToolCalls is >= 0 and <= 16 &&
        receipt.FindingCount is >= 0 and <= 32 &&
        receipt.QualityToolCallCount is >= 0 and <= 16 &&
        LiveAgentFreshProcessDomain.IsSha256(
            receipt.ExecutionArtifactSha256) &&
        LiveAgentFreshProcessDomain.IsSha256(receipt.BuildPairSha256);

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new JsonException();

    private static string? NullableString(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null
            ? null
            : value.GetString() ?? throw new JsonException();
    }

    private static int RequiredInt32(JsonElement root, string name) =>
        root.GetProperty(name).GetInt32();

    private static long? NullableInt64(JsonElement root, string name)
    {
        var value = root.GetProperty(name);
        return value.ValueKind == JsonValueKind.Null ? null : value.GetInt64();
    }

    private static bool RequiredBoolean(JsonElement root, string name) =>
        root.GetProperty(name).GetBoolean();

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static void WriteNullableNumber(
        Utf8JsonWriter writer,
        string name,
        long? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteNumber(name, value.Value);
        }
    }
}

internal sealed record TrustedLiveProcessResult(
    int ExitCode,
    bool TimedOut,
    bool Cancelled,
    bool SensitiveBytesObserved,
    bool OutputLimitExceeded,
    string StandardOutput,
    string StandardError);
