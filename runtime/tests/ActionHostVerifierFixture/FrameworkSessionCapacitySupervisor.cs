using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static partial class FrameworkSupervisor
{
    internal static async Task<int> RunCapacityAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux()) return 1;
        var values = ParseArguments(args);
        return await RunR7CapacityAsync(Required(values, "root"), Required(values, "repo"),
            Required(values, "payload"), Required(values, "bundle"), Required(values, "node")) ? 0 : 1;
    }

    private static async Task<bool> RunR7CapacityAsync(string root, string repository, string payload, string bundle, string node)
    {
        Directory.CreateDirectory(root);
        await using var platform = SyntheticOfficialPlatform.Start(root);
        var pids = new HashSet<int>();
        R7Measurement? last = null;
        var copies = 0;
        var messages = 0;
        var maximumOperations = 0;
        for (var generation = 0; generation < FrameworkSessionCapacity.Generations; generation++)
        {
            var name = FrameworkSessionCapacity.Prefix + generation;
            var result = await RunCaseAsync(new(name, name, "reviewed", ExpectedProviderRequests: 6, ExpectedStickyMutations: 1),
                root, repository, payload, bundle, node, platform);
            if (!result.Passed || !pids.Add(result.HostPid)) return false;
            maximumOperations = Math.Max(maximumOperations, File.ReadLines(Path.Join(root, name, "state-operations.tsv")).Count());
            last = ReadR7State(platform, Path.Join(root, name), generation);
            if (last.Generation != generation) return false;
            copies += last.PhysicalCopies;
            using var projection = JsonDocument.Parse(File.ReadAllBytes(Path.Join(root, name, "r7-projection.json")));
            messages = projection.RootElement.GetProperty("messages").GetInt32();
            await File.WriteAllTextAsync(Path.Join(root, "capacity-progress.json"), FrameworkJson.Serialize(FrameworkJson.Object(
                ("generation", last.Generation), ("plaintext_bytes", last.PlaintextBytes), ("records", last.CombinedRecords),
                ("messages", messages), ("physical_copy_observations", copies), ("maximum_store_operations", maximumOperations))));
        }
        if (last is null || last.PlaintextBytes <= 1_048_576 || last.CombinedRecords <= 256 || messages <= 64 || copies == 0 || maximumOperations <= 2048 || maximumOperations > 4096) return false;
        var failureName = FrameworkSessionCapacity.Prefix + FrameworkSessionCapacity.Generations;
        var failed = await RunCaseAsync(new(failureName, failureName, "agent_result_invalid", ExpectedProviderRequests: 0, ExpectedStickyMutations: 0),
            root, repository, payload, bundle, node, platform);
        if (!failed.Passed || !pids.Add(failed.HostPid) ||
            File.ReadAllText(Path.Join(root, failureName, "r7-agent-code")) != "agent_context_limit") return false;
        var after = ReadR7State(platform, Path.Join(root, failureName), FrameworkSessionCapacity.Generations);
        if (after.Generation != last.Generation || after.SessionSha256 != last.SessionSha256) return false;
        var receipt = FrameworkJson.Object(("schema", "r7-artifact-session-capacity-v1"),
            ("accepted_generations", FrameworkSessionCapacity.Generations), ("fresh_host_processes", pids.Count),
            ("messages", messages), ("combined_records", last.CombinedRecords), ("plaintext_bytes", last.PlaintextBytes),
            ("physical_copy_observations", copies), ("maximum_store_operations", maximumOperations), ("refused_physical_sends", failed.ProviderRequests),
            ("predecessor_preserved", true), ("session_sha256", last.SessionSha256));
        await File.WriteAllTextAsync(Path.Join(root, "capacity-receipt.json"), FrameworkJson.Serialize(receipt) + "\n");
        Console.WriteLine("APR_R7_ARTIFACT_SESSION_CAPACITY_OK");
        return true;
    }

    private sealed record R7Measurement(long Generation, int PlaintextBytes, int CombinedRecords, int PhysicalCopies, string SessionSha256);

    private static R7Measurement ReadR7State(SyntheticOfficialPlatform platform, string scenario, int ordinal)
    {
        using var scopeDocument = JsonDocument.Parse(File.ReadAllBytes(Path.Join(scenario, "r7-scope.json")));
        var s = scopeDocument.RootElement;
        string Text(string key) => s.GetProperty(key).GetString()!;
        var scope = new RestrictedStateScope(Text("repository"), Text("workflow"), s.GetProperty("review").GetInt64(),
            Text("session"), Text("provider"), Text("model"), Text("adapter"), Text("policy"), Text("limits"), Text("tools"), Text("build"));
        using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle("42");
        RequireR7(LocatorStateKeyRing.TryCreate(access, "42", FrameworkCanaries.StateKey, FrameworkCanaries.PreviousStateKey,
            out var selectedKeys, out _));
        using var keys = selectedKeys!;
        // Logical collection names are captured at the actual store boundary; platform names
        // independently bind both the collection hash and encrypted object digest.
        var names = Directory.EnumerateFiles(Directory.GetParent(scenario)!.FullName,
                "state-operation-identities.tsv", SearchOption.AllDirectories)
            .SelectMany(File.ReadLines).Select(line => line.Split('\t')[1])
            .Distinct(StringComparer.Ordinal).ToArray();
        var objects = platform.ReadR7EncryptedObjects().Select(item =>
        {
            var digest = Convert.ToHexStringLower(SHA256.HashData(item.Bytes));
            var logical = names.Single(name => item.Name == "apr-object-" +
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("apr-artifact-family\0" + name))) + "-" + digest);
            return (Name: logical, item.Bytes, item.Current);
        }).ToArray();
        try
        {
            var roots = objects.Where(item => item.Name == LocatorRootFormat.StoreName && item.Current).ToArray();
            if (roots.Length != 1) throw new InvalidOperationException("r7_root_count:" + roots.Length + "/" + objects.Length);
            var root = roots[0];
            RequireR7(LocatorRootSentinelCodec.TryDecrypt(access, keys, root.Bytes, out var sentinel, out _));
            try
            {
                RequireR7(LocatorContext.TryCreate(access, keys, sentinel!.Root, true, sentinel.RequiredExpiresAtUnixSeconds,
                    new R7Clock(ordinal), out var selected));
                using var locator = selected!;
                var generations = new Dictionary<string, StateGenerationRecordV1>();
                var receipts = new Dictionary<string, AcceptanceReceiptV1>();
                var copies = 0;
                foreach (var item in objects.Where(item => item.Name != LocatorRootFormat.StoreName))
                {
                    RequireR7(StateControlEnvelopeV1Codec.TryDecrypt(locator, access, new OpaqueStoreName(item.Name), item.Bytes,
                        out var header, out var record, out _));
                    try
                    {
                        if (header!.ObjectClass == StateObjectClass.Candidate)
                        {
                            if (AcceptedStateGenerationRecordCodec.TryDecode(record, out var generation))
                            {
                                if (item.Current) generations[header.ObjectIdentity] = generation!;
                            }
                            else
                            {
                                RequireR7(AcceptedStatePhysicalCopyCodec.TryDecode(record, out var copy));
                                RequireR7(AcceptedStateGenerationRecordCodec.TryDecode(copy!.CanonicalGenerationBytes.AsSpan(), out generation));
                                if (item.Current) generations[copy.OriginalCandidateObjectIdentity] = generation!;
                                copies++;
                            }
                        }
                        else if (header.ObjectClass == StateObjectClass.Acceptance)
                        {
                            RequireR7(AcceptedStateAcceptanceReceiptCodec.TryDecode(record, out var receipt));
                            if (item.Current) receipts[header.ObjectIdentity] = receipt!;
                        }
                    }
                    finally { CryptographicOperations.ZeroMemory(record); }
                }
                var tails = receipts.Where(item => !receipts.Values.Any(other => other.PreviousAcceptanceReceiptIdentity == item.Key)).ToArray();
                if (tails.Length != 1) throw new InvalidOperationException("r7_tail_count:" + tails.Length + "/" + receipts.Count + "/" + generations.Count);
                var tailReceipt = tails[0].Value;
                var tail = generations[tailReceipt.OriginalCandidateObjectIdentity];
                RequireR7(AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var stateAccess).Action == StateAction.Authorized);
                var binding = new RestrictedStateBinding(scope, tail.ProducerBaseSha, tail.ProducerHeadSha, tail.Generation,
                    tail.PredecessorEnvelopeSha256, tail.PreparedAtUnixSeconds, tail.PreparedExpiresAtUnixSeconds);
                RequireR7(RestrictedStateEnvelope.TryDecrypt(stateAccess!, binding, tail.EncryptedStateEnvelope.AsSpan(),
                    new R7ReadKeys(stateAccess!, access, locator), out var plaintext, out _));
                try
                {
                    RequireR7(AgentSessionCodec.TryParse(plaintext!, out var session, out _) && session!.SessionSha256 == tail.SessionSha256);
                    var records = session!.Document.CompletedRuns.Sum(r => r.Records.Length + r.Continuation.Items.Length);
                    return new(tail.Generation, plaintext!.Length, records, copies, tail.SessionSha256);
                }
                finally { CryptographicOperations.ZeroMemory(plaintext!); }
            }
            finally { CryptographicOperations.ZeroMemory(sentinel!.Root); }
        }
        finally { foreach (var item in objects) CryptographicOperations.ZeroMemory(item.Bytes); }
    }

    private static void RequireR7(bool condition)
    { if (!condition) throw new InvalidOperationException("r7_artifact_state_oracle"); }

    private sealed class R7Clock(int ordinal) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => DateTimeOffset.FromUnixTimeSeconds(1_800_000_000 + ordinal * 60L); }

    private sealed class R7ReadKeys(AuthorizedStateAccess state, AuthorizedLocatorAccess access, LocatorContext locator) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess authority, out RestrictedStateKey? key) { key = null; return false; }
        public bool TryGetApprovedReadKey(AuthorizedStateAccess authority, string keyId, long expiry, out RestrictedStateKey? key)
        {
            key = null;
            Span<byte> material = stackalloc byte[32];
            try
            {
                if (!ReferenceEquals(state, authority) || !locator.TryCopyApprovedReadKey(access, keyId, material)) return false;
                key = new(keyId, material);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }
    }
}
