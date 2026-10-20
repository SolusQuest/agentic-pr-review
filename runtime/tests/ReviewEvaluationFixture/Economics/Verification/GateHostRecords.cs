using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

// The production Host wrote these encrypted records. Read them through the
// existing authority and codecs; no plaintext or key enters the report.
internal sealed record GateHostRecords(StateControlHeaderV1 HeadHeader, LineageHeadV1 Head,
    StateGenerationRecordV1 Tail, string[] AcceptanceIdentities, string? ConsumedRecordSha256)
{
    internal static GateHostRecords Read(ResetProbeWorld world, ResetProbeInvocation invocation,
        string fact, string reasoning, params string[] excluded)
    {
        var launch = invocation.Launch;
        var repositoryId = launch.RepositoryId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle(repositoryId);
        var current = launch.Inputs.StateKey!.ExportForPrivateLaunch();
        Require(LocatorStateKeyRing.TryCreate(access, repositoryId, current, null, out var ring, out _));
        using var keys = ring!;
        var rootMetadata = world.Store.Objects.Single(item => item.Reference.Name.Value == LocatorRootFormat.StoreName);
        Require(LocatorRootSentinelCodec.TryDecrypt(access, keys, world.Store.Bytes(rootMetadata), out var sentinel, out _));
        var root = sentinel!;
        try
        {
            Require(LocatorContext.TryCreate(access, keys, root.Root, true, root.RequiredExpiresAtUnixSeconds, world.Time, out var selected));
            using var context = selected!;
            var heads = new List<(StateControlHeaderV1 Header, LineageHeadV1 Head)>();
            var generations = new Dictionary<string, StateGenerationRecordV1>();
            var receipts = new Dictionary<string, AcceptanceReceiptV1>();
            var claims = new List<(StateControlHeaderV1 Header, SolusContextClaimRecord Claim)>();
            foreach (var metadata in world.Store.Objects.Where(item => !ReferenceEquals(item, rootMetadata)))
            {
                Require(StateControlEnvelopeV1Codec.TryDecrypt(context, access, metadata.Reference.Name,
                    world.Store.Bytes(metadata), out var header, out var payload, out _));
                try
                {
                    if (header!.ObjectClass == StateObjectClass.Candidate)
                    {
                        Require(AcceptedStateGenerationRecordCodec.TryDecode(payload, out var generation));
                        generations.Add(header.ObjectIdentity, generation!);
                    }
                    else if (header.ObjectClass == StateObjectClass.Acceptance)
                    {
                        Require(AcceptedStateAcceptanceReceiptCodec.TryDecode(payload, out var receipt));
                        receipts.Add(header.ObjectIdentity, receipt!);
                    }
                    else if (header.ObjectClass == StateObjectClass.ContextClaim)
                    {
                        Require(SolusContextClaimRecordCodec.TryDecode(payload, out var claim) && claim is not null);
                        claims.Add((header, claim!));
                    }
                    else if (header.ObjectClass == StateObjectClass.LineageHead)
                    {
                        Require(LineageHeadCodec.TryDecode(payload, out var head));
                        heads.Add((header, head!));
                    }
                }
                finally { CryptographicOperations.ZeroMemory(payload); }
            }
            Require(heads.Count > 0 && receipts.Count > 0);
            var selectedHead = heads.MaxBy(value => value.Head.Ordinal);
            var receiptTail = receipts.Single(value => !receipts.Values.Any(other => other.PreviousAcceptanceReceiptIdentity == value.Key));
            var tail = generations[receiptTail.Value.OriginalCandidateObjectIdentity];
            var run = invocation.Provider.Request!;
            var plan = run.StablePlan;
            var scope = new RestrictedStateScope(plan.RepositoryId, plan.WorkflowIdentity, plan.ReviewTarget, run.SessionId,
                plan.ProviderId, plan.ModelId, plan.AdapterId, plan.PolicySha256, plan.LimitsSha256, plan.ToolsetSha256, plan.BuildId);
            AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var stateAccess);
            Require(stateAccess is not null);
            var binding = new RestrictedStateBinding(scope, tail.ProducerBaseSha, tail.ProducerHeadSha, tail.Generation,
                tail.PredecessorEnvelopeSha256, tail.PreparedAtUnixSeconds, tail.PreparedExpiresAtUnixSeconds);
            Require(RestrictedStateEnvelope.TryDecrypt(stateAccess!, binding, tail.EncryptedStateEnvelope.AsSpan(),
                new ReadKeys(stateAccess!, access, context), out var plaintext, out _));
            try
            {
                Require(SolusRestrictedStateSnapshotCodec.TryRead(plaintext!,
                    CurrentEvaluationHostTestComposition.Compatibility(), out var record, out _) && record is not null);
                Require(SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext!) == tail.SessionSha256);
                // Structural records establish Host framing/binding only. Current
                // facts and reasoning are observed from actual current tool work;
                // genuine opaque SDK history is qualified by the installed suite.
                if (invocation.Provider.Outcome?.Succeeded == true)
                {
                    var wire = invocation.Provider.Requests.Select(Encoding.UTF8.GetString).ToArray();
                    Require(wire.Length >= 2 && wire.Skip(1).Any(text => text.Contains(fact, StringComparison.Ordinal)) &&
                        wire.Skip(1).Any(text => text.Contains(reasoning, StringComparison.Ordinal)) &&
                        wire.All(text => excluded.All(marker => !text.Contains(marker, StringComparison.Ordinal))));
                    Require(!wire[0].Contains(reasoning, StringComparison.Ordinal));
                }
            }
            finally { CryptographicOperations.ZeroMemory(plaintext!); }
            var currentClaims = claims.Where(value => value.Header.ProducingRunIdentity ==
                launch.RunId.ToString(System.Globalization.CultureInfo.InvariantCulture) &&
                value.Header.ProducingRunAttempt == launch.RunAttempt).ToArray();
            Require(currentClaims.Length <= 1);
            foreach (var currentClaim in currentClaims)
                Require(currentClaim.Claim.BaseScopeDigest == selectedHead.Header.BaseScopeDigest &&
                    currentClaim.Claim.Epoch == selectedHead.Header.Epoch &&
                    currentClaim.Claim.SessionId == selectedHead.Header.SessionId);
            return new(selectedHead.Header, selectedHead.Head, tail, receipts.Keys.Order(StringComparer.Ordinal).ToArray(),
                currentClaims.Length == 0 ? null : currentClaims[0].Claim.RecordSha256);
        }
        finally { CryptographicOperations.ZeroMemory(root.Root); }
    }

    private sealed class ReadKeys(AuthorizedStateAccess state, AuthorizedLocatorAccess access, LocatorContext locator) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(AuthorizedStateAccess authority, out RestrictedStateKey? key) { key = null; return false; }
        public bool TryGetApprovedReadKey(AuthorizedStateAccess authority, string keyId, long expiry, out RestrictedStateKey? key)
        {
            key = null;
            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!ReferenceEquals(authority, state) || !locator.TryCopyApprovedReadKey(access, keyId, material)) return false;
                key = new RestrictedStateKey(keyId, material);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(material); }
        }
    }
}
