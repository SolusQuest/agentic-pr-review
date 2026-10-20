using System.Collections.Immutable;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

// Historical trace verification only; native SESSION campaigns are retired.
internal static class GateEconomicsCases
{

    internal static bool VerifyCredentialTrace(EconomicsChildInput input, EconomicsReceipt receipt, byte[] key)
    {
        try
        {
            var proof = receipt.CredentialProof;
            if (proof is not { SyntheticCalls: 2, EnvironmentChecked: true, CompletedSessionChecked: true, StoredObjects: >= 1 and <= 16 } ||
                proof.ObservationDomain != "apr-historical-typed-credential-isolation-v1" || proof.RestoredSessionChecked != receipt.Restored ||
                receipt.Calls.Length != 2 || input.Slot.Index != receipt.Index || input.Operation != receipt.Operation ||
                input.PlanSha256 != receipt.PlanSha256 || input.WorkloadSha256 != receipt.WorkloadSha256 ||
                (input.Predecessor is not null) != receipt.Restored) return false;
            var bytes = EconomicsCredentialProbe.ReadTrace(input.Root, receipt.Index);
            EconomicsCredentialProbe.AssertProtected(bytes, key);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != proof!.TraceSha256) return false;
            var lines = new UTF8Encoding(false, true).GetString(bytes).Split('\n');
            if (lines.Length != 2 + receipt.Calls.Length * 2 || lines[^1] != "") return false;
            using var header = JsonDocument.Parse(lines[0], new JsonDocumentOptions { MaxDepth = 4 });
            var h = header.RootElement;
            if (!Properties(h, ["kind", "observation_domain", "operation", "plan_sha256", "workload_sha256", "source_commit",
                    "source_tree", "build_sha256", "index", "phase", "startup", "restored", "session_sha256"]) ||
                h.GetProperty("kind").GetString() != "apr-r6-historical-typed-credential-observations-v1" ||
                h.GetProperty("observation_domain").GetString() != "apr-historical-typed-credential-isolation-v1" ||
                h.GetProperty("operation").GetString() != input.Operation || h.GetProperty("plan_sha256").GetString() != input.PlanSha256 ||
                h.GetProperty("workload_sha256").GetString() != input.WorkloadSha256 ||
                h.GetProperty("source_commit").GetString() != input.Plan.Source.Commit ||
                h.GetProperty("source_tree").GetString() != input.Plan.Source.Tree ||
                h.GetProperty("build_sha256").GetString() != input.Plan.BuildSha256 ||
                h.GetProperty("index").GetInt32() != input.Slot.Index || h.GetProperty("phase").GetInt32() != input.Slot.Phase ||
                h.GetProperty("startup").GetString() != receipt.Startup || h.GetProperty("restored").GetBoolean() != receipt.Restored ||
                h.GetProperty("session_sha256").GetString() != Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Session)))) return false;
            for (var i = 0; i < receipt.Calls.Length; i++)
            {
                var call = receipt.Calls[i];
                using var entered = JsonDocument.Parse(lines[1 + i * 2], new JsonDocumentOptions { MaxDepth = 4 });
                using var ended = JsonDocument.Parse(lines[2 + i * 2], new JsonDocumentOptions { MaxDepth = 4 });
                var start = entered.RootElement; var end = ended.RootElement;
                if (!Properties(start, ["ordinal", "request_sha256", "outcome"]) || !Properties(end, ["ordinal", "request_sha256", "outcome"]) ||
                    call.Ordinal != i + 1 || !call.Dispatched || call.TransportOutcome != "success" || call.ChatOutcome != "returned" ||
                    start.GetProperty("ordinal").GetInt32() != call.Ordinal || end.GetProperty("ordinal").GetInt32() != call.Ordinal ||
                    start.GetProperty("outcome").GetString() != "entered" || end.GetProperty("outcome").GetString() != "succeeded" ||
                    !AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation.EvaluationLimits.Hash(start.GetProperty("request_sha256").GetString()) ||
                    start.GetProperty("request_sha256").GetString() != end.GetProperty("request_sha256").GetString()) return false;
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or
            FormatException or OverflowException or DecoderFallbackException or AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission.ReplayRejected)
        { return false; }
    }

    private static bool Properties(JsonElement value, string[] names) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().Select(property => property.Name).SequenceEqual(names, StringComparer.Ordinal);

}
