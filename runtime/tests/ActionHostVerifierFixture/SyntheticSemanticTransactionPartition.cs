using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class SyntheticSemanticTransactionPartition
{
    private static readonly string[] Scenarios =
    [
        "dispatch-bootstrap",
        "dispatch-continuation",
        "stale-head",
    ];

    internal static JsonObject? Derive(
        string root,
        SyntheticTransactionPartitionBinding binding) =>
        Derive(root, binding, static scenario =>
            StringComparer.Ordinal.Equals(
                scenario,
                "dispatch-bootstrap")
                ? FrameworkCanaries.PreviousStateKey
                : FrameworkCanaries.StateKey,
            requireCurrentProofProfile: true);

    internal static JsonObject? Derive(
        string root,
        SyntheticTransactionPartitionBinding binding,
        string stateKey) =>
        Derive(
            root,
            binding,
            _ => stateKey,
            requireCurrentProofProfile: false);

    private static JsonObject? Derive(
        string root,
        SyntheticTransactionPartitionBinding binding,
        Func<string, string> stateKeyForScenario,
        bool requireCurrentProofProfile)
    {
        var records = new Dictionary<string, SemanticRecord>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in Scenarios)
        {
            if (!ReadScenario(
                    root,
                    scenario,
                    stateKeyForScenario(scenario),
                    records,
                    deleted))
                return null;
        }

        if (records.Count != 35 ||
            !deleted.IsSubsetOf(records.Keys)) return null;

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in records.Values.Where(value => !value.IsCleanup))
        {
            if (!resolved.TryAdd(record.ObjectId, record.BaseRole)) return null;
        }

        foreach (var record in records.Values.Where(value => value.IsCleanup))
        {
            var targets = record.TargetObjectIds
                .Select(id => resolved.TryGetValue(id, out var role) ? role : null)
                .ToArray();
            if (targets.Any(value => value is null) ||
                !TryCleanupRole(record.Scenario, targets, out var role) ||
                !resolved.TryAdd(record.ObjectId, role)) return null;
        }

        var liveRoles = Roles(records, deleted, resolved,
            value => !deleted.Contains(value.ObjectId));
        var internallyReconciledRoles = Roles(records, deleted, resolved,
            value => deleted.Contains(value.ObjectId) && !value.IsCleanup);
        var cleanupSelfDeletedRoles = Roles(records, deleted, resolved,
            value => deleted.Contains(value.ObjectId) && value.IsCleanup);
        if (requireCurrentProofProfile &&
            (!ExactRoles(liveRoles, ExpectedLiveRoles()) ||
                !ExactRoles(internallyReconciledRoles,
                    ExpectedInternallyReconciledRoles()) ||
                !ExactRoles(cleanupSelfDeletedRoles,
                    ExpectedCleanupSelfDeletedRoles()))) return null;

        var allRoles = liveRoles.Concat(internallyReconciledRoles)
            .Concat(cleanupSelfDeletedRoles).ToArray();
        if (allRoles.Length != records.Count ||
            allRoles.Distinct(StringComparer.Ordinal).Count() != records.Count)
            return null;

        var live = Identities(liveRoles);
        var internallyReconciled = Identities(internallyReconciledRoles);
        var cleanupSelfDeleted = Identities(cleanupSelfDeletedRoles);
        var preimage = FrameworkJson.Object(
            ("kind", "apr-r4-e4-synthetic-transaction-partition-v1"),
            ("payload_source_commit", binding.PayloadSourceCommit),
            ("payload_source_tree", binding.PayloadSourceTree),
            ("payload_sha256", binding.PayloadSha256),
            ("verifier_sha256", binding.VerifierSha256),
            ("total_record_count", records.Count),
            ("live_anchor_count", live.Length),
            ("transient_record_count", deleted.Count),
            ("internally_reconciled_count", internallyReconciled.Length),
            ("cleanup_self_deleted_count", cleanupSelfDeleted.Length),
            ("live_anchor_object_identities", FrameworkJson.Array(live)),
            ("internally_reconciled_object_identities",
                FrameworkJson.Array(internallyReconciled)),
            ("cleanup_self_deleted_object_identities",
                FrameworkJson.Array(cleanupSelfDeleted)));
        var digest = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(FrameworkJson.Serialize(preimage))))
            .ToLowerInvariant();
        return FrameworkJson.Object(
            ("kind", "apr-r4-e4-synthetic-transaction-partition-v1"),
            ("total_record_count", records.Count),
            ("live_anchor_count", live.Length),
            ("transient_record_count", deleted.Count),
            ("internally_reconciled_count", internallyReconciled.Length),
            ("cleanup_self_deleted_count", cleanupSelfDeleted.Length),
            ("live_anchor_object_identities", FrameworkJson.Array(live)),
            ("internally_reconciled_object_identities",
                FrameworkJson.Array(internallyReconciled)),
            ("cleanup_self_deleted_object_identities",
                FrameworkJson.Array(cleanupSelfDeleted)),
            ("transaction_route_evidence_sha256", digest));
    }

    private static string[] Roles(
        IReadOnlyDictionary<string, SemanticRecord> records,
        IReadOnlySet<string> deleted,
        IReadOnlyDictionary<string, string> resolved,
        Func<SemanticRecord, bool> predicate) => records.Values
        .Where(predicate)
        .Select(value => resolved[value.ObjectId])
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static string[] Identities(IEnumerable<string> roles) => roles
        .Select(RoleIdentity).Order(StringComparer.Ordinal).ToArray();

    private static bool ReadScenario(
        string root,
        string scenario,
        string stateKey,
        Dictionary<string, SemanticRecord> records,
        HashSet<string> deleted)
    {
        var path = Path.Join(root, scenario, "state-lifecycle-evidence.tsv");
        if (!File.Exists(path)) return false;
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('\t');
            if (fields.Length != 14) return false;
            if (fields[0] == "upload" && fields[4] == "None" &&
                fields[5] == "Committed")
            {
                if (!TryReadUpload(scenario, stateKey, fields, out var record) ||
                    record is null || !records.TryAdd(record.ObjectId, record))
                    return false;
            }
            else if (fields[0] == "delete" && fields[4] == "None" &&
                fields[5] == "Committed")
            {
                if (!records.TryGetValue(fields[2], out var deletedRecord) ||
                    !StringComparer.Ordinal.Equals(
                        fields[1], deletedRecord.Name.Value) ||
                    !StringComparer.Ordinal.Equals(
                        fields[3], deletedRecord.EncryptedDigest) ||
                    !deleted.Add(fields[2])) return false;
            }
        }
        return true;
    }

    private static bool TryReadUpload(
        string scenario,
        string stateKey,
        string[] fields,
        out SemanticRecord? record)
    {
        record = null;
        byte[] encrypted;
        try { encrypted = Convert.FromBase64String(fields[13]); }
        catch (FormatException) { return false; }
        var name = new OpaqueStoreName(fields[1]);
        if (!OpaqueStoreValidation.IsValid(name) ||
            !IsIdentity(fields[2]) || !IsIdentity(fields[3]) ||
            !long.TryParse(fields[6], NumberStyles.None,
                CultureInfo.InvariantCulture, out var objectId) ||
            objectId <= 0 || objectId > 9_007_199_254_740_991 ||
            !IsIdentity(fields[7]) ||
            !IsIdentity(fields[8]) ||
            !StringComparer.Ordinal.Equals(fields[3],
                OpaqueStoreHash.Sha256(encrypted)) ||
            !StringComparer.Ordinal.Equals(fields[3], fields[8]) ||
            !long.TryParse(fields[9], NumberStyles.None,
                CultureInfo.InvariantCulture, out var expiresAt) ||
            !long.TryParse(fields[11], NumberStyles.None,
                CultureInfo.InvariantCulture, out var attempt) ||
            !long.TryParse(fields[12], NumberStyles.None,
                CultureInfo.InvariantCulture, out var size) ||
            size != encrypted.Length || expiresAt <= 0 || attempt <= 0) return false;

        if (LocatorRootSentinelCodec.TryDecryptForSyntheticEvidence(
                FrameworkGitHubHandler.RepositoryId.ToString(
                    CultureInfo.InvariantCulture),
                stateKey,
                encrypted,
                out var sentinel) && sentinel is not null &&
            StringComparer.Ordinal.Equals(name.Value, LocatorRootFormat.StoreName) &&
            StringComparer.Ordinal.Equals(
                fields[2],
                LocatorCryptography.CorrelationId(
                    Convert.FromHexString(fields[3]))))
        {
            CryptographicOperations.ZeroMemory(sentinel.Root);
            record = new(
                fields[6], name, fields[8], scenario,
                $"{scenario}/locator-root/generation-{sentinel.Generation}",
                false, []);
            return true;
        }
        if (!StateControlEnvelopeV1Codec.TryDecryptForSyntheticEvidence(
                name, stateKey, encrypted,
                out var header, out var payload) || header is null ||
            !StringComparer.Ordinal.Equals(
                fields[2], LineageCryptography.CorrelationId(encrypted)))
            return false;
        try
        {
            if (!StringComparer.Ordinal.Equals(header.ProducingRunIdentity,
                    fields[10]) || header.ProducingRunAttempt != attempt ||
                !TryClassifyScoped(scenario, stateKey, header, payload,
                    out var baseRole, out var isCleanup, out var targets))
                return false;
            record = new(
                fields[6], name, fields[8], scenario,
                baseRole, isCleanup, targets);
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static bool TryClassifyScoped(
        string scenario,
        string stateKey,
        StateControlHeaderV1 header,
        ReadOnlySpan<byte> payload,
        out string role,
        out bool isCleanup,
        out ImmutableArray<string> targets)
    {
        role = string.Empty;
        isCleanup = false;
        targets = [];
        switch (header.ObjectClass)
        {
            case StateObjectClass.LineageHead
                when LineageHeadCodec.TryDecode(payload, out _):
                role = $"{scenario}/lineage-head";
                return true;
            case StateObjectClass.Candidate
                when AcceptedStateGenerationRecordCodec.TryDecode(payload, out _):
                role = $"{scenario}/candidate/generation";
                return true;
            case StateObjectClass.Candidate
                when AcceptedStatePhysicalCopyCodec.TryDecode(payload, out _):
                role = $"{scenario}/candidate/physical-copy";
                return true;
            case StateObjectClass.Acceptance
                when AcceptedStateAcceptanceReceiptCodec.TryDecode(payload, out _):
                role = $"{scenario}/acceptance/receipt";
                return true;
            case StateObjectClass.PublicationIntent:
                return TryPublicationIntentRole(scenario, payload, out role);
            case StateObjectClass.Cleanup:
                if (RetainedStateOpaqueWriteAnchorCodec.TryDecode(
                        payload, out var anchor) && anchor is not null &&
                    TryAnchorTargetRole(stateKey, anchor, out var targetRole))
                {
                    role = $"{scenario}/cleanup/opaque-write-anchor/{targetRole}";
                    return true;
                }
                if (RetainedStateCleanupRecordCodec.TryDecode(
                        payload, out var cleanup) && cleanup is not null)
                {
                    role = $"{scenario}/cleanup/record";
                    isCleanup = true;
                    targets = cleanup.Targets
                        .Select(value => value.Reference.ObjectId.Value)
                        .ToImmutableArray();
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private static bool TryPublicationIntentRole(
        string scenario,
        ReadOnlySpan<byte> payload,
        out string role)
    {
        role = string.Empty;
        if (PublicationIntentV1Codec.TryDecode(payload, out _))
        {
            role = $"{scenario}/publication-intent/initial-intent";
            return true;
        }
        if (StickyReadbackRecordV1Codec.TryDecode(payload, out _))
        {
            role = $"{scenario}/publication-intent/sticky-readback";
            return true;
        }
        if (!RecoveryRecordV1Codec.TryDecode(payload, out var recovery,
                out _, out _) || recovery is null ||
            !RetainedStateAcceptanceRecoveryCodec.TryDecode(
                recovery.AcceptanceRecoveryHandoff.AsSpan(), out _,
                out var acceptanceEnvelope, out var predecessorCopy)) return false;
        CryptographicOperations.ZeroMemory(acceptanceEnvelope);
        if (predecessorCopy?.Envelope is { } copyEnvelope)
            CryptographicOperations.ZeroMemory(copyEnvelope);
        role = $"{scenario}/publication-intent/acceptance-recovery";
        return true;
    }

    private static bool TryAnchorTargetRole(
        string stateKey,
        RetainedStateOpaqueWriteAnchor anchor,
        out string role)
    {
        role = string.Empty;
        if (!StateControlEnvelopeV1Codec.TryDecryptForSyntheticEvidence(
                anchor.TargetName, stateKey,
                anchor.TargetEnvelope.AsSpan(), out var targetHeader,
                out var targetPayload) ||
            targetHeader?.ObjectClass != StateObjectClass.PublicationIntent) return false;
        try
        {
            const string prefix = "anchor-target";
            if (!TryPublicationIntentRole(prefix, targetPayload, out var target))
                return false;
            role = target[(prefix.Length + 1)..];
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(targetPayload); }
    }

    private static bool TryCleanupRole(
        string scenario,
        string?[] targets,
        out string role)
    {
        role = string.Empty;
        if (targets.Length == 0)
        {
            role = $"{scenario}/cleanup/s6-internal/empty";
            return true;
        }
        if (targets.Length == 5)
        {
            var expected = new[]
            {
                "dispatch-bootstrap/acceptance/receipt",
                "dispatch-bootstrap/candidate/generation",
                "dispatch-bootstrap/lineage-head",
                "dispatch-continuation/acceptance/receipt",
                "dispatch-continuation/candidate/generation",
            };
            if (!ExactRoles(targets, expected)) return false;
            role = $"{scenario}/cleanup/s6-final";
            return true;
        }
        if (targets.Length != 1 || targets[0] is not { } target) return false;
        if (target.Contains(
                "/cleanup/opaque-write-anchor/",
                StringComparison.Ordinal))
        {
            role = scenario + "/cleanup/p5-anchor/" + target;
            return true;
        }
        if (target.Contains("/publication-intent/", StringComparison.Ordinal))
        {
            role = scenario + "/cleanup/p5-record/" + target;
            return true;
        }
        if (StringComparer.Ordinal.Equals(target,
                scenario + "/candidate/physical-copy"))
        {
            role = scenario + "/cleanup/s6-internal/candidate/physical-copy";
            return true;
        }
        return false;
    }

    private static IEnumerable<string> ExpectedLiveRoles() =>
    [
        "dispatch-bootstrap/acceptance/receipt",
        "dispatch-bootstrap/candidate/generation",
        "dispatch-continuation/acceptance/receipt",
        "dispatch-continuation/candidate/generation",
        "dispatch-continuation/cleanup/opaque-write-anchor/" +
            "publication-intent/acceptance-recovery",
        "dispatch-continuation/lineage-head",
        "dispatch-continuation/locator-root/generation-3",
        "dispatch-continuation/publication-intent/acceptance-recovery",
        "dispatch-continuation/publication-intent/initial-intent",
        "dispatch-continuation/publication-intent/sticky-readback",
        "stale-head/lineage-head",
        "stale-head/locator-root/generation-0",
    ];

    private static IEnumerable<string> ExpectedInternallyReconciledRoles()
    {
        foreach (var kind in new[]
                 { "initial-intent", "sticky-readback", "acceptance-recovery" })
        {
            yield return $"dispatch-bootstrap/publication-intent/{kind}";
            yield return "dispatch-bootstrap/cleanup/opaque-write-anchor/" +
                $"publication-intent/{kind}";
        }
        yield return "dispatch-bootstrap/lineage-head";
        yield return "dispatch-bootstrap/locator-root/generation-0";
        yield return "dispatch-bootstrap/locator-root/generation-1";
        yield return "dispatch-continuation/candidate/physical-copy";
        yield return "dispatch-continuation/cleanup/opaque-write-anchor/" +
            "publication-intent/initial-intent";
        yield return "dispatch-continuation/cleanup/opaque-write-anchor/" +
            "publication-intent/sticky-readback";
        yield return "dispatch-continuation/locator-root/generation-2";
    }

    private static IEnumerable<string> ExpectedCleanupSelfDeletedRoles()
    {
        yield return "dispatch-bootstrap/cleanup/p5-anchor/" +
            "dispatch-bootstrap/cleanup/opaque-write-anchor/" +
            "publication-intent/initial-intent";
        yield return "dispatch-bootstrap/cleanup/p5-anchor/" +
            "dispatch-bootstrap/cleanup/opaque-write-anchor/" +
            "publication-intent/sticky-readback";
        yield return "dispatch-bootstrap/cleanup/s6-internal/empty";
        foreach (var kind in new[]
                 { "initial-intent", "sticky-readback", "acceptance-recovery" })
        {
            yield return "dispatch-continuation/cleanup/p5-record/" +
                $"dispatch-bootstrap/publication-intent/{kind}";
        }
        yield return "dispatch-continuation/cleanup/p5-anchor/" +
            "dispatch-bootstrap/cleanup/opaque-write-anchor/" +
            "publication-intent/acceptance-recovery";
        yield return "dispatch-continuation/cleanup/p5-anchor/" +
            "dispatch-continuation/cleanup/opaque-write-anchor/" +
            "publication-intent/initial-intent";
        yield return "dispatch-continuation/cleanup/p5-anchor/" +
            "dispatch-continuation/cleanup/opaque-write-anchor/" +
            "publication-intent/sticky-readback";
        yield return "dispatch-continuation/cleanup/s6-internal/candidate/physical-copy";
    }

    private static bool ExactRoles(
        IEnumerable<string?> actual,
        IEnumerable<string> expected) => actual
        .Where(value => value is not null).Cast<string>()
        .Order(StringComparer.Ordinal)
        .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string RoleIdentity(string role) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(
            "apr-r4-e4-synthetic-role-v1\0" + role))).ToLowerInvariant();

    private static bool IsIdentity(string value) => value.Length == 64 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private sealed record SemanticRecord(
        string ObjectId,
        OpaqueStoreName Name,
        string EncryptedDigest,
        string Scenario,
        string BaseRole,
        bool IsCleanup,
        ImmutableArray<string> TargetObjectIds);
}
