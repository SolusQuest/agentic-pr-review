using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.SolusProductionCutoverFixture;

// Reads the genuine historical producer's accepted objects. The rebind is an
// adversarial reader input in memory; it never changes stored state or authority.
internal static class HistoricalRefusalProof
{
    internal static async Task VerifyAsync(string root, byte[] originalLineage)
    {
        var document = LiveAgentFreshProcessCodec.ReadLineage(originalLineage);
        Require(document is not null && LiveAgentFreshProcessDomain.TryMapScope(document.Scope, out _),
            "historical_lineage");
        LiveAgentFreshProcessDomain.TryMapScope(document!.Scope, out var scope);
        Require(AuthorizedStateAccess.Authorize(new(scope!, scope!, true, true, false), out var access).Action ==
            StateAction.Authorized, "historical_access");
        Require(R3LiveAgentStateKeyResolver.TryCreate(Program.Key, out var resolver), "historical_key");
        using var ownedKeys = resolver!;
        var keys = new ReadOnlyKeys(resolver!);
        var snapshot = await new RestrictedStateOpaqueSnapshotStore(
            new LocalRestrictedStateStore(Path.Join(root, "state")), keys).ReadAsync(access!, CancellationToken.None);
        Require(snapshot.Succeeded && snapshot.Snapshot is { Accepted.Length: 1, Staging: null },
            "historical_accepted_object");
        var accepted = snapshot.Snapshot!.Accepted[0];
        var lineage = new AcceptedLineage(scope!, document.Generation, document.SessionSha256,
            document.EnvelopeSha256, document.ExpectedPredecessorEnvelopeSha256,
            document.AcceptedAtUnixSeconds, document.ExpiresAtUnixSeconds, document.TransitionAuthorized);
        Require(accepted.Binding.Scope == scope && accepted.Binding.Generation == lineage.Generation &&
            accepted.Binding.ProducerBaseSha == document.ProducerBaseSha &&
            accepted.Binding.ProducerHeadSha == document.ProducerHeadSha &&
            accepted.SessionSha256 == lineage.SessionSha256 && accepted.EnvelopeSha256 == lineage.EnvelopeSha256 &&
            accepted.Binding.PredecessorEnvelopeSha256 == lineage.ExpectedPredecessorEnvelopeSha256 &&
            accepted.Binding.AcceptedAtUnixSeconds == lineage.AcceptedAtUnixSeconds &&
            accepted.Binding.ExpiresAtUnixSeconds == lineage.ExpiresAtUnixSeconds,
            "historical_exact_binding");
        var envelopeBefore = accepted.Envelope.ToArray();
        Require(RestrictedStateEnvelope.TryDecrypt(access!, accepted.Binding, accepted.Envelope, keys,
            out var plaintext, out _), "historical_authenticated_bytes");
        try
        {
            Require(plaintext!.AsSpan().StartsWith("APRSES01"u8), "historical_producer_framing");
            // Supported reader configuration only: these two hashes are synthetic.
            // No SDK import or current runtime configuration equivalence is claimed.
            var compatibility = new SolusRestrictedStateCompatibility(
                SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
                SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
                SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
                SolusRestrictedStateFormat.PackageClosureSha256, new string('a', 64), new string('b', 64));
            Require(SolusRestrictedStateAdmission.IsSupported(compatibility), "historical_supported_reader");
            var rebound = lineage with { SessionSha256 = SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext!) };
            Require(rebound.SessionSha256 != lineage.SessionSha256, "historical_distinct_domains");
            foreach (var adversarialRebind in new[] { false, true })
            {
                var reads = keys.ReadCalls;
                Require(!SolusRestrictedStateSnapshotCodec.TryRestore(access, accepted.Binding,
                    adversarialRebind ? rebound : lineage, accepted.Envelope, compatibility,
                    accepted.Binding.AcceptedAtUnixSeconds, keys, out var record, out var failure) &&
                    record is null && failure == (adversarialRebind ? SolusRestrictedStateFailure.Incompatible :
                        SolusRestrictedStateFailure.LineageMismatch) && keys.ReadCalls == reads + 1,
                    adversarialRebind ? "historical_rebind_incompatible" : "historical_original_lineage_mismatch");
            }
            Require(keys.WriteCalls == 0 && accepted.Envelope.AsSpan().SequenceEqual(envelopeBefore) &&
                File.ReadAllBytes(Path.Join(root, "host", "accepted-lineage.json")).AsSpan().SequenceEqual(originalLineage),
                "historical_read_only");
        }
        finally
        {
            if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Require(bool condition, string code)
    { if (!condition) throw new CutoverProofFailure(code); }

    private sealed class ReadOnlyKeys(IRestrictedStateKeyResolver inner) : IRestrictedStateKeyResolver
    {
        internal int ReadCalls { get; private set; }
        internal int WriteCalls { get; private set; }
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess access, out RestrictedStateKey? key)
        { WriteCalls++; key = null; return false; }
        public bool TryGetApprovedReadKey(AuthorizedStateAccess access, string id, long expiry, out RestrictedStateKey? key)
        { ReadCalls++; return inner.TryGetApprovedReadKey(access, id, expiry, out key); }
    }
}
