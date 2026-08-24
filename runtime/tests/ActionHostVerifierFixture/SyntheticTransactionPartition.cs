using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed record SyntheticTransactionPartitionBinding(
    string PayloadSourceCommit,
    string PayloadSourceTree,
    string PayloadSha256,
    string VerifierSha256)
{
    internal static SyntheticTransactionPartitionBinding? TryCreate(
        IReadOnlyDictionary<string, string> values)
    {
        static bool Hex(string value, int length) =>
            value.Length == length && value.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');

        return values.TryGetValue("payload-source-commit", out var commit) &&
            values.TryGetValue("payload-source-tree", out var tree) &&
            values.TryGetValue("production-payload-sha256", out var payload) &&
            values.TryGetValue("verifier-sha256", out var verifier) &&
            Hex(commit, 40) && Hex(tree, 40) &&
            Hex(payload, 64) && Hex(verifier, 64)
                ? new(commit, tree, payload, verifier)
                : null;
    }
}

internal static class SyntheticTransactionPartition
{
    private static readonly string[] Scenarios =
    [
        "dispatch-bootstrap",
        "dispatch-continuation",
        "stale-head",
    ];

    internal static JsonObject? Derive(
        string root,
        SyntheticTransactionPartitionBinding binding)
    {
        var identitiesByObjectId = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var uploaded = new HashSet<string>(StringComparer.Ordinal);
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var selfDeleted = new HashSet<string>(StringComparer.Ordinal);

        foreach (var scenario in Scenarios)
        {
            var path = Path.Join(
                root,
                scenario,
                "state-operation-identities.tsv");
            if (!File.Exists(path) ||
                !ReadScenario(
                    path,
                    identitiesByObjectId,
                    uploaded,
                    deleted,
                    selfDeleted))
            {
                return null;
            }
        }

        if (uploaded.Count != 35 || deleted.Count != 28 ||
            selfDeleted.Count != 15 ||
            !selfDeleted.IsSubsetOf(deleted))
        {
            return null;
        }

        var live = uploaded.Except(deleted, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var internallyReconciled = deleted.Except(
                selfDeleted,
                StringComparer.Ordinal)
            .Order(StringComparer.Ordinal).ToArray();
        var cleanupSelfDeleted = selfDeleted.Order(StringComparer.Ordinal)
            .ToArray();
        if (live.Length != 7 || internallyReconciled.Length != 13)
        {
            return null;
        }

        var totalRecordCount = uploaded.Count;
        var liveAnchorCount = live.Length;
        var transientRecordCount = deleted.Count;
        var internallyReconciledCount = internallyReconciled.Length;
        var cleanupSelfDeletedCount = cleanupSelfDeleted.Length;

        var preimage = FrameworkJson.Object(
            ("kind", "apr-r4-e4-synthetic-transaction-partition-v1"),
            ("payload_source_commit", binding.PayloadSourceCommit),
            ("payload_source_tree", binding.PayloadSourceTree),
            ("payload_sha256", binding.PayloadSha256),
            ("verifier_sha256", binding.VerifierSha256),
            ("total_record_count", totalRecordCount),
            ("live_anchor_count", liveAnchorCount),
            ("transient_record_count", transientRecordCount),
            ("internally_reconciled_count", internallyReconciledCount),
            ("cleanup_self_deleted_count", cleanupSelfDeletedCount),
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
            ("total_record_count", totalRecordCount),
            ("live_anchor_count", liveAnchorCount),
            ("transient_record_count", transientRecordCount),
            ("internally_reconciled_count", internallyReconciledCount),
            ("cleanup_self_deleted_count", cleanupSelfDeletedCount),
            ("live_anchor_object_identities", FrameworkJson.Array(live)),
            ("internally_reconciled_object_identities",
                FrameworkJson.Array(internallyReconciled)),
            ("cleanup_self_deleted_object_identities",
                FrameworkJson.Array(cleanupSelfDeleted)),
            ("transaction_route_evidence_sha256", digest));
    }

    private static bool ReadScenario(
        string path,
        Dictionary<string, string> identitiesByObjectId,
        HashSet<string> uploaded,
        HashSet<string> deleted,
        HashSet<string> selfDeleted)
    {
        string? cleanupCandidate = null;
        foreach (var line in File.ReadLines(path))
        {
            var fields = line.Split('\t');
            if (fields.Length != 7)
            {
                return false;
            }

            if (fields[0] == "upload" && fields[4] == "None" &&
                fields[5] == "Committed")
            {
                var result = fields[6].Split('|');
                if (result.Length != 7 ||
                    !IsIdentity(fields[2]) ||
                    !identitiesByObjectId.TryAdd(result[0], fields[2]) ||
                    !uploaded.Add(fields[2]))
                {
                    return false;
                }

                cleanupCandidate = result[0];
            }
            else if (fields[0] == "delete" && fields[4] == "None" &&
                fields[5] == "Committed")
            {
                if (!identitiesByObjectId.TryGetValue(
                        fields[2],
                        out var identity) ||
                    !deleted.Add(identity))
                {
                    return false;
                }

                if (fields[2] == cleanupCandidate)
                {
                    selfDeleted.Add(identity);
                    cleanupCandidate = null;
                }
            }
        }

        return true;
    }

    private static bool IsIdentity(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
}
