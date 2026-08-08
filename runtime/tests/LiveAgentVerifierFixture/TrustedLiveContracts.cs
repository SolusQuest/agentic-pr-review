using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
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

internal static class TrustedLiveDiagnosticCodes
{
    internal const string PhaseChildFailed = "phase_child_failed";
    internal const string PhaseCanary = "phase_canary";
    internal const string PhaseReceiptInvalid = "phase_receipt_invalid";
    internal const string PhaseReceiptMissing = "phase_receipt_missing";
}

internal static class TrustedLiveFailureKinds
{
    internal const string Application = "application";
    internal const string Child = "child";
    internal const string ReceiptInvalid = "receipt_invalid";
    internal const string ReceiptMissing = "receipt_missing";
    internal const string Canary = "canary";
}

internal static class TrustedLiveChildStages
{
    internal const string FixtureInputRead = "fixture_input_read";
    internal const string FixtureMaterialization = "fixture_materialization";
    internal const string FreshProcessFilesystem = "fresh_process_filesystem";
    internal const string CommandPreparation = "command_preparation";
    internal const string ProfileActivation = "profile_activation";
    internal const string CommandExecution = "command_execution";
    internal const string ProductResultRead = "product_result_read";
    internal const string LineageRead = "lineage_read";
    internal const string QualityProjection = "quality_projection";
    internal const string PhaseReceiptProjection = "phase_receipt_projection";
    internal const string PhaseReceiptWrite = "phase_receipt_write";

    internal static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            FixtureInputRead,
            FixtureMaterialization,
            FreshProcessFilesystem,
            CommandPreparation,
            ProfileActivation,
            CommandExecution,
            ProductResultRead,
            LineageRead,
            QualityProjection,
            PhaseReceiptProjection,
            PhaseReceiptWrite,
        };
}

internal static class TrustedLiveFailureCategories
{
    internal const string Invalid = "invalid";
    internal const string Argument = "argument";
    internal const string Io = "io";
    internal const string Access = "access";
    internal const string Unsupported = "unsupported";
    internal const string Cryptography = "cryptography";
    internal const string Json = "json";
    internal const string Process = "process";
    internal const string Cancelled = "cancelled";
    internal const string Other = "other";

    internal static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            Invalid,
            Argument,
            Io,
            Access,
            Unsupported,
            Cryptography,
            Json,
            Process,
            Cancelled,
            Other,
        };

    internal static string FromException(Exception exception) =>
        exception switch
        {
            ArgumentException or
            FormatException or
            OverflowException => Argument,
            IOException => Io,
            System.Security.SecurityException or
            UnauthorizedAccessException => Access,
            NotSupportedException => Unsupported,
            CryptographicException => Cryptography,
            JsonException => Json,
            Win32Exception => Process,
            OperationCanceledException => Cancelled,
            _ => Other,
        };
}

internal static class TrustedLiveSuccessCodes
{
    internal const string MustFind = "APR_R3_TRUSTED_LIVE_MUST_FIND_OK";
    internal const string MustNotFind = "APR_R3_TRUSTED_LIVE_MUST_NOT_FIND_OK";
    internal const string ContinuationSeed =
        "APR_R3_TRUSTED_LIVE_CONTINUATION_SEED_OK";
    internal const string ContinuationRestore =
        "APR_R3_TRUSTED_LIVE_CONTINUATION_RESTORE_OK";
}

internal sealed record TrustedLiveFailureEvidence(
    string Scenario,
    string Kind,
    string? Stage,
    string? Category,
    string DiagnosticCode,
    int ProcessExitCode,
    int ModelCalls,
    int ToolCalls,
    string? ProductCode,
    string? OutcomeCode,
    string? QualityClassification,
    string? QualityCode);

internal sealed record TrustedLivePrivateFailure(
    string Kind,
    string Stage,
    string Category);

internal static class TrustedLivePrivateFailureCodec
{
    private const int MaximumBytes = 512;

    internal static byte[] Write(TrustedLivePrivateFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", failure.Kind);
            writer.WriteString("stage", failure.Stage);
            writer.WriteString("category", failure.Category);
            writer.WriteEndObject();
        }
        var bytes = stream.ToArray();
        if (bytes.Length > MaximumBytes || !IsAdmitted(failure))
        {
            throw new InvalidOperationException(
                "The trusted-live private failure is invalid.");
        }
        return bytes;
    }

    internal static TrustedLivePrivateFailure? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is <= 0 or > MaximumBytes)
            {
                return null;
            }
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.EnumerateObject().Select(item => item.Name)
                    .SequenceEqual(
                        ["kind", "stage", "category"],
                        StringComparer.Ordinal))
            {
                return null;
            }
            var failure = new TrustedLivePrivateFailure(
                RequiredString(root, "kind"),
                RequiredString(root, "stage"),
                RequiredString(root, "category"));
            return IsAdmitted(failure) ? failure : null;
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

    private static bool IsAdmitted(TrustedLivePrivateFailure failure) =>
        failure.Kind == TrustedLiveFailureKinds.Child &&
        TrustedLiveChildStages.All.Contains(failure.Stage) &&
        TrustedLiveFailureCategories.All.Contains(failure.Category);

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new JsonException();
}

internal static class TrustedLiveDomain
{
    private static readonly HashSet<string> productCodes =
        new(StringComparer.Ordinal)
        {
            R3LiveAgentCodes.Completed,
            R3LiveAgentCodes.InputInvalid,
            R3LiveAgentCodes.SecretInvalid,
            R3LiveAgentCodes.CompositionFailed,
            R3LiveAgentCodes.HandoffUnavailable,
            R3LiveAgentCodes.HandoffCleanupFailed,
            LiveAgentFreshProcessCodes.UsageInvalid,
            LiveAgentFreshProcessCodes.AuthorizationInvalid,
            LiveAgentFreshProcessCodes.RootInvalid,
            LiveAgentFreshProcessCodes.InputInvalid,
            LiveAgentFreshProcessCodes.LineageInvalid,
            LiveAgentFreshProcessCodes.TransitionRejected,
            LiveAgentFreshProcessCodes.ProcessIdentityReused,
            LiveAgentFreshProcessCodes.TransportProofFailed,
            LiveAgentFreshProcessCodes.OutputFailed,
            AgentFailureCodes.Cancelled,
            AgentFailureCodes.DeadlineExceeded,
            AgentFailureCodes.ChatFailed,
            AgentFailureCodes.ModelLimit,
            AgentFailureCodes.ToolLimit,
            AgentFailureCodes.TokenLimit,
            AgentFailureCodes.RequestTooLarge,
            AgentFailureCodes.ResponseTooLarge,
            AgentFailureCodes.UsageInvalid,
            AgentFailureCodes.ResponseInvalid,
            AgentFailureCodes.MissingTool,
            AgentFailureCodes.UnknownTool,
            AgentFailureCodes.ToolArgumentsInvalid,
            AgentFailureCodes.TerminalSequenceInvalid,
            AgentFailureCodes.TerminalInvalid,
            AgentFailureCodes.ToolPathInvalid,
            AgentFailureCodes.ToolPathNotTracked,
            AgentFailureCodes.ToolCursorInvalid,
            AgentFailureCodes.ToolPathUnsafe,
            AgentFailureCodes.ToolFileTooLarge,
            AgentFailureCodes.ToolFileBinary,
            AgentFailureCodes.ToolFileInvalidUtf8,
            AgentFailureCodes.ToolFileLoneCr,
            AgentFailureCodes.ToolIoFailed,
            AgentFailureCodes.ToolResultLimit,
            AgentSessionCodes.BootstrapAbsent,
            AgentSessionCodes.BootstrapIncompatible,
            AgentSessionCodes.ResetExplicit,
            AgentSessionCodes.ExplicitMissing,
            AgentSessionCodes.ExplicitIncompatible,
            AgentSessionCodes.CurrentMalformed,
            AgentSessionCodes.CurrentOversized,
            AgentSessionCodes.ScopeMismatch,
            AgentSessionCodes.TransitionRejected,
            AgentSessionCodes.RecordInvalid,
            AgentSessionCodes.ClassificationInvalid,
            AgentSessionCodes.AssociationInvalid,
            AgentSessionCodes.ContinuationInvalid,
            AgentSessionCodes.ConstructionLimit,
        };

    private static readonly HashSet<string> applicationDiagnostics =
        new(StringComparer.Ordinal)
        {
            R3LiveAgentDiagnosticCodes.PreparationFailed,
            R3LiveAgentDiagnosticCodes.StateRestoreFailed,
            R3LiveAgentDiagnosticCodes.SnapshotFailed,
            R3LiveAgentDiagnosticCodes.TransportFailed,
            R3LiveAgentDiagnosticCodes.AgentRunFailed,
            R3LiveAgentDiagnosticCodes.StateCommitFailed,
            R3LiveAgentDiagnosticCodes.ResultFailed,
        };

    private static readonly HashSet<string> nonFailureProductCodes =
        new(StringComparer.Ordinal)
        {
            R3LiveAgentCodes.Completed,
            RestrictedStateCodes.Authorized,
            RestrictedStateCodes.Enumerated,
            RestrictedStateCodes.Prepared,
            RestrictedStateCodes.Absent,
            RestrictedStateCodes.Restored,
            RestrictedStateCodes.Accepted,
            RestrictedStateCodes.Idempotent,
            RestrictedStateCodes.Reset,
            RestrictedStateCodes.HandoffReady,
            AgentSessionCodes.BootstrapAbsent,
            AgentSessionCodes.ResetExplicit,
        };

    private static readonly Dictionary<string, string>
        productFailureClassifications = new(StringComparer.Ordinal);

    static TrustedLiveDomain()
    {
        productCodes.UnionWith(RestrictedStateCodes.All);
        AddFailureClassification(
            TrustedLiveCodes.Cleanup,
            R3LiveAgentCodes.HandoffCleanupFailed,
            RestrictedStateCodes.CleanupFailed);
        AddFailureClassification(
            TrustedLiveCodes.Continuation,
            LiveAgentFreshProcessCodes.LineageInvalid,
            LiveAgentFreshProcessCodes.TransitionRejected,
            LiveAgentFreshProcessCodes.ProcessIdentityReused,
            RestrictedStateCodes.ExplicitMissing,
            RestrictedStateCodes.CurrentMissing,
            RestrictedStateCodes.Expired,
            RestrictedStateCodes.ReplayRejected,
            AgentSessionCodes.ContinuationInvalid);
        AddFailureClassification(
            TrustedLiveCodes.Grounding,
            AgentFailureCodes.TerminalSequenceInvalid,
            AgentFailureCodes.TerminalInvalid,
            AgentSessionCodes.ScopeMismatch,
            AgentSessionCodes.RecordInvalid,
            AgentSessionCodes.ClassificationInvalid,
            AgentSessionCodes.AssociationInvalid);
        AddFailureClassification(
            TrustedLiveCodes.Provider,
            R3LiveAgentCodes.SecretInvalid,
            AgentFailureCodes.ChatFailed);
        AddFailureClassification(
            TrustedLiveCodes.MissingTool,
            AgentFailureCodes.MissingTool,
            AgentFailureCodes.UnknownTool,
            AgentFailureCodes.ToolArgumentsInvalid);
        AddFailureClassification(
            TrustedLiveCodes.Infrastructure,
            R3LiveAgentCodes.InputInvalid,
            R3LiveAgentCodes.CompositionFailed,
            R3LiveAgentCodes.HandoffUnavailable,
            LiveAgentFreshProcessCodes.UsageInvalid,
            LiveAgentFreshProcessCodes.AuthorizationInvalid,
            LiveAgentFreshProcessCodes.RootInvalid,
            LiveAgentFreshProcessCodes.InputInvalid,
            LiveAgentFreshProcessCodes.TransportProofFailed,
            LiveAgentFreshProcessCodes.OutputFailed,
            AgentFailureCodes.Cancelled,
            AgentFailureCodes.DeadlineExceeded,
            AgentFailureCodes.ModelLimit,
            AgentFailureCodes.ToolLimit,
            AgentFailureCodes.TokenLimit,
            AgentFailureCodes.RequestTooLarge,
            AgentFailureCodes.ResponseTooLarge,
            AgentFailureCodes.UsageInvalid,
            AgentFailureCodes.ResponseInvalid,
            AgentFailureCodes.ToolPathInvalid,
            AgentFailureCodes.ToolPathNotTracked,
            AgentFailureCodes.ToolCursorInvalid,
            AgentFailureCodes.ToolPathUnsafe,
            AgentFailureCodes.ToolFileTooLarge,
            AgentFailureCodes.ToolFileBinary,
            AgentFailureCodes.ToolFileInvalidUtf8,
            AgentFailureCodes.ToolFileLoneCr,
            AgentFailureCodes.ToolIoFailed,
            AgentFailureCodes.ToolResultLimit,
            AgentSessionCodes.BootstrapIncompatible,
            AgentSessionCodes.ExplicitMissing,
            AgentSessionCodes.ExplicitIncompatible,
            AgentSessionCodes.CurrentMalformed,
            AgentSessionCodes.CurrentOversized,
            AgentSessionCodes.ConstructionLimit,
            RestrictedStateCodes.AccessDenied,
            RestrictedStateCodes.EnumerationInvalid,
            RestrictedStateCodes.Cancelled,
            RestrictedStateCodes.EnvelopeInvalid,
            RestrictedStateCodes.KeyUnavailable,
            RestrictedStateCodes.AuthenticationFailed,
            RestrictedStateCodes.Conflict,
            RestrictedStateCodes.IoFailed);

        var expectedFailures = productCodes
            .Where(code => !nonFailureProductCodes.Contains(code))
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedFailures.SetEquals(productFailureClassifications.Keys))
        {
            throw new InvalidOperationException(
                "The trusted-live product failure classification is incomplete.");
        }
    }

    internal static IReadOnlySet<string> ProductCodes => productCodes;

    internal static IReadOnlySet<string> ApplicationDiagnostics =>
        applicationDiagnostics;

    internal static IReadOnlyDictionary<string, string>
        ProductFailureClassifications => productFailureClassifications;

    internal static bool ReceiptIsAdmitted(TrustedLivePhaseReceipt receipt)
    {
        if (receipt.Kind != "apr-r3-trusted-live-phase-v1" ||
            !Enum.TryParse<VerifierScenario>(
                receipt.Scenario,
                ignoreCase: false,
                out var scenario) ||
            receipt.Scenario != scenario.ToString() ||
            scenario is not (VerifierScenario.MustFind or
                VerifierScenario.MustNotFind or
                VerifierScenario.ContinuationSeed or
                VerifierScenario.ContinuationRestore) ||
            receipt.Status is not ("passed" or "failed") ||
            receipt.Transition is not ("same_head" or "verified_ahead") ||
            !productCodes.Contains(receipt.ProductCode) ||
            !QualityIsAdmitted(receipt))
        {
            return false;
        }

        if (receipt.Status == "passed")
        {
            return receipt.ProductCode == R3LiveAgentCodes.Completed &&
                receipt.OutcomeCode == SuccessCode(scenario) &&
                (scenario == VerifierScenario.ContinuationSeed
                    ? receipt.QualityStatus is null &&
                        receipt.QualityClassification is null &&
                        receipt.QualityCode is null
                    : receipt.QualityStatus == "passed" &&
                        receipt.QualityClassification == "quality" &&
                        receipt.QualityCode == R3QualityCodes.Passed);
        }

        if (applicationDiagnostics.Contains(receipt.OutcomeCode))
        {
            return receipt.ProductCode == R3LiveAgentCodes.CompositionFailed;
        }

        if (receipt.QualityStatus is "failed" or "not_evaluated" &&
            receipt.QualityCode is not null and not R3QualityCodes.Passed &&
            receipt.OutcomeCode == receipt.QualityCode)
        {
            return receipt.ProductCode is R3LiveAgentCodes.Completed or
                LiveAgentFreshProcessCodes.TransportProofFailed;
        }

        if (ProductFailureCodeIsAdmitted(receipt.ProductCode))
        {
            return receipt.OutcomeCode == receipt.ProductCode;
        }

        return false;
    }

    internal static string? ApplicationStage(string diagnosticCode) =>
        applicationDiagnostics.Contains(diagnosticCode)
            ? diagnosticCode
            : null;

    internal static string FailureOutcomeCode(
        string? applicationDiagnostic,
        string? productCode,
        R3QualityOutcome? quality)
    {
        if (applicationDiagnostic is not null &&
            applicationDiagnostics.Contains(applicationDiagnostic))
        {
            return applicationDiagnostic;
        }
        if (StringComparer.Ordinal.Equals(
                productCode,
                LiveAgentFreshProcessCodes.TransportProofFailed) &&
            quality is
            {
                Status: "failed" or "not_evaluated",
                Code: not R3QualityCodes.Passed,
            })
        {
            return quality.Code;
        }
        if (productCode is not null &&
            ProductFailureCodeIsAdmitted(productCode))
        {
            return productCode;
        }
        if (quality is
            {
                Status: "failed" or "not_evaluated",
                Code: not R3QualityCodes.Passed,
            })
        {
            return quality.Code;
        }

        return TrustedLiveCodes.Infrastructure;
    }

    internal static bool TryClassifyProductFailure(
        string code,
        out string classification) =>
        productFailureClassifications.TryGetValue(code, out classification!);

    private static bool QualityIsAdmitted(TrustedLivePhaseReceipt receipt)
    {
        if (receipt.QualityStatus is null &&
            receipt.QualityClassification is null &&
            receipt.QualityCode is null)
        {
            return true;
        }
        if (receipt.QualityStatus is null ||
            receipt.QualityClassification is null ||
            receipt.QualityCode is null)
        {
            return false;
        }

        var sourceCode = receipt.QualityClassification == "quality" ||
            receipt.QualityCode is
                R3QualityCodes.FixtureInvalid or
                R3QualityCodes.SubjectInvalid or
                R3QualityCodes.InitialContextLeak or
                R3QualityCodes.FreshInputInvalid or
                R3QualityCodes.ObservationIsolationInvalid
            ? null
            : receipt.ProductCode;
        return R3QualityOutcome.TryCreate(
            "trusted_live",
            new string('a', 64),
            receipt.QualityStatus,
            receipt.QualityClassification,
            receipt.QualityCode,
            sourceCode,
            receipt.FindingCount,
            receipt.QualityToolCallCount,
            receipt.TerminalSha256,
            out _);
    }

    private static string SuccessCode(VerifierScenario scenario) =>
        scenario switch
        {
            VerifierScenario.MustFind => TrustedLiveSuccessCodes.MustFind,
            VerifierScenario.MustNotFind => TrustedLiveSuccessCodes.MustNotFind,
            VerifierScenario.ContinuationSeed =>
                TrustedLiveSuccessCodes.ContinuationSeed,
            VerifierScenario.ContinuationRestore =>
                TrustedLiveSuccessCodes.ContinuationRestore,
            _ => string.Empty,
        };

    internal static bool ProductFailureCodeIsAdmitted(string code) =>
        productFailureClassifications.ContainsKey(code);

    private static void AddFailureClassification(
        string classification,
        params string[] codes)
    {
        foreach (var code in codes)
        {
            if (!productFailureClassifications.TryAdd(code, classification))
            {
                throw new InvalidOperationException(
                    "The trusted-live product failure classification overlaps.");
            }
        }
    }
}

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
        IReadOnlyList<TrustedLivePhaseReceipt> phases,
        TrustedLiveFailureEvidence? failure = null)
    {
        var canaryDominant = code == TrustedLiveCodes.Canary ||
            failure?.Kind == TrustedLiveFailureKinds.Canary;
        var admittedPhases = canaryDominant
            ? Array.Empty<TrustedLivePhaseReceipt>()
            : phases;
        var admittedFailure = canaryDominant ? null : failure;
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
            writer.WriteNumber("phase_count", admittedPhases.Count);
            writer.WriteNumber(
                "attempted_phase_count",
                admittedPhases.Count + (admittedFailure is null ? 0 : 1));
            writer.WriteNumber(
                "model_calls",
                admittedPhases.Sum(item => item.ModelCalls));
            writer.WriteNumber(
                "tool_calls",
                admittedPhases.Sum(item => item.ToolCalls));
            writer.WriteStartArray("phases");
            foreach (var phase in admittedPhases)
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
            if (admittedFailure is null)
            {
                writer.WriteNull("failure");
            }
            else
            {
                writer.WriteStartObject("failure");
                writer.WriteString("scenario", admittedFailure.Scenario);
                writer.WriteString("kind", admittedFailure.Kind);
                WriteNullableString(
                    writer,
                    "stage",
                    admittedFailure.Stage);
                WriteNullableString(
                    writer,
                    "category",
                    admittedFailure.Category);
                writer.WriteString(
                    "diagnostic_code",
                    admittedFailure.DiagnosticCode);
                writer.WriteNumber(
                    "process_exit_code",
                    admittedFailure.ProcessExitCode);
                writer.WriteNumber(
                    "model_calls",
                    admittedFailure.ModelCalls);
                writer.WriteNumber(
                    "tool_calls",
                    admittedFailure.ToolCalls);
                WriteNullableString(
                    writer,
                    "product_code",
                    admittedFailure.ProductCode);
                WriteNullableString(
                    writer,
                    "outcome_code",
                    admittedFailure.OutcomeCode);
                WriteNullableString(
                    writer,
                    "quality_classification",
                    admittedFailure.QualityClassification);
                WriteNullableString(
                    writer,
                    "quality_code",
                    admittedFailure.QualityCode);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static bool PhaseIsBounded(TrustedLivePhaseReceipt receipt) =>
        TrustedLiveDomain.ReceiptIsAdmitted(receipt) &&
        receipt.ModelCalls is >= 0 and <= 8 &&
        receipt.ToolCalls is >= 0 and <= 16 &&
        receipt.FindingCount is >= 0 and <= 32 &&
        receipt.QualityToolCallCount is >= 0 and <= 16 &&
        (receipt.InvocationIdentitySha256.Length == 0 ||
            LiveAgentFreshProcessDomain.IsSha256(
                receipt.InvocationIdentitySha256)) &&
        IsOptionalSha256(receipt.LineageSha256) &&
        IsOptionalSha256(receipt.AcceptedSessionSha256) &&
        IsOptionalSha256(receipt.AcceptedEnvelopeSha256) &&
        IsOptionalSha256(receipt.TerminalSha256) &&
        LiveAgentFreshProcessDomain.IsSha256(
            receipt.ExecutionArtifactSha256) &&
        LiveAgentFreshProcessDomain.IsSha256(receipt.BuildPairSha256);

    private static bool IsOptionalSha256(string? value) =>
        value is null || LiveAgentFreshProcessDomain.IsSha256(value);

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
