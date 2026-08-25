using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.State.Evidence;

internal sealed record TrustedProofEncryptedArtifact(
    string ArtifactId,
    string Role,
    string Scope,
    string OpaqueName,
    byte[] Envelope);

internal sealed record TrustedProofDecodedArtifact(
    string ArtifactId,
    string Role,
    string Scope,
    string ObjectClass,
    string KeyId,
    string ObjectIdentity,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    string PayloadSha256);

internal sealed record TrustedProofCodecOracleResult(
    bool ExactSevenSuccess,
    bool RecoveryOnly,
    ImmutableArray<TrustedProofDecodedArtifact> Records);

internal sealed record TrustedProofLineageFact(
    string ArtifactId,
    string ObjectIdentity,
    string BaseScopeDigest,
    string ReviewedHeadSha,
    LineageTransitionKind Transition,
    ulong Ordinal);

internal sealed record TrustedProofCandidateFact(
    string ArtifactId,
    string ObjectIdentity,
    string BaseScopeDigest,
    long Generation,
    string StateEnvelopeSha256,
    string? PredecessorEnvelopeSha256,
    string? PreviousLogicalGenerationIdentity,
    string ProducerHeadSha);

internal sealed record TrustedProofAcceptanceFact(
    string ArtifactId,
    string ObjectIdentity,
    string BaseScopeDigest,
    string LogicalGenerationIdentity,
    string OriginalCandidateObjectIdentity,
    string? PreviousLogicalGenerationIdentity,
    string? PreviousAcceptanceReceiptIdentity,
    string ReviewedHeadSha,
    int PublicationOperation,
    string ProducingRunIdentity,
    long ProducingRunAttempt);

internal static class TrustedProofEvidenceCodecOracle
{
    private static readonly string[] ExpectedRoles =
    [
        "repository-locator-root",
        "normal-lineage-head",
        "stale-lineage-head",
        "bootstrap-candidate",
        "continuation-candidate",
        "bootstrap-acceptance",
        "continuation-acceptance",
    ];

    internal static bool TryDecode(
        string repositoryId,
        string currentKeyBase64,
        string? previousKeyBase64,
        IReadOnlyList<TrustedProofEncryptedArtifact> artifacts,
        out TrustedProofCodecOracleResult? result)
    {
        result = null;
        if (artifacts.Count is < 7 or > 256 ||
            artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() !=
                artifacts.Count ||
            artifacts[0].Role != "repository-locator-root" ||
            artifacts[0].Scope != "repository" ||
            artifacts[0].OpaqueName != LocatorRootFormat.StoreName)
        {
            return false;
        }

        using var access = AuthorizedLocatorAccess.IssueTrustedProofEvidenceOracle(repositoryId);
        if (!LocatorStateKeyRing.TryCreate(
                access,
                repositoryId,
                currentKeyBase64,
                previousKeyBase64,
                out var keys,
                out _) ||
            keys is null)
        {
            return false;
        }

        using (keys)
        {
            if (!LocatorRootSentinelCodec.TryDecrypt(
                    access,
                    keys,
                    artifacts[0].Envelope,
                    out var sentinel,
                    out _) ||
                sentinel is null)
            {
                return false;
            }

            try
            {
                if (!LocatorContext.TryCreate(
                        access,
                        keys,
                        sentinel.Root,
                        currentSingletonProven: true,
                        sentinel.RequiredExpiresAtUnixSeconds,
                        TimeProvider.System,
                        out var context) ||
                    context is null)
                {
                    return false;
                }

                using (context)
                {
                    var records = ImmutableArray.CreateBuilder<TrustedProofDecodedArtifact>(
                        artifacts.Count);
                    var lineages = new List<TrustedProofLineageFact>();
                    var candidates = new List<TrustedProofCandidateFact>();
                    var acceptances = new List<TrustedProofAcceptanceFact>();
                    records.Add(new TrustedProofDecodedArtifact(
                        artifacts[0].ArtifactId,
                        artifacts[0].Role,
                        artifacts[0].Scope,
                        "locator_root",
                        sentinel.WriterKeyId,
                        CanonicalHash(sentinel.Root),
                        string.Empty,
                        0,
                        CanonicalHash(sentinel.Root)));

                    for (var index = 1; index < artifacts.Count; index++)
                    {
                        var artifact = artifacts[index];
                        var name = new OpaqueStoreName(artifact.OpaqueName);
                        if (!OpaqueStoreValidation.IsValid(name) ||
                            !StateControlEnvelopeV1Codec.TryDecrypt(
                                context,
                                access,
                                name,
                                artifact.Envelope,
                                out var header,
                                out var payload,
                                out _) ||
                            header is null)
                        {
                            return false;
                        }

                        try
                        {
                            if (!CaptureProductFact(
                                    artifact.ArtifactId,
                                    header,
                                    payload,
                                    lineages,
                                    candidates,
                                    acceptances))
                            {
                                return false;
                            }

                            records.Add(new TrustedProofDecodedArtifact(
                                artifact.ArtifactId,
                                artifact.Role,
                                artifact.Scope,
                                StateObjectClasses.ToWireName(header.ObjectClass),
                                header.KeyId,
                                header.ObjectIdentity,
                                header.ProducingRunIdentity,
                                header.ProducingRunAttempt,
                                CanonicalHash(payload)));
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(payload);
                        }
                    }

                    var decoded = records.MoveToImmutable();
                    var topologyValid = TryDeriveProductRoles(
                        decoded,
                        lineages,
                        candidates,
                        acceptances,
                        out var derivedRoles);
                    var classified = decoded.Select(record =>
                    {
                        if (!derivedRoles.TryGetValue(record.ArtifactId, out var derived))
                        {
                            return record;
                        }

                        return record with { Role = derived.Role, Scope = derived.Scope };
                    }).ToImmutableArray();
                    var exact = topologyValid && decoded
                        .Where(record => derivedRoles.ContainsKey(record.ArtifactId))
                        .All(record =>
                        {
                            var derived = derivedRoles[record.ArtifactId];
                            return record.Role == derived.Role && record.Scope == derived.Scope;
                        });
                    result = new TrustedProofCodecOracleResult(
                        exact,
                        RecoveryOnly: !exact,
                        classified);
                    return true;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sentinel.Root);
            }
        }
    }

    private static bool CaptureProductFact(
        string artifactId,
        StateControlHeaderV1 header,
        ReadOnlySpan<byte> payload,
        List<TrustedProofLineageFact> lineages,
        List<TrustedProofCandidateFact> candidates,
        List<TrustedProofAcceptanceFact> acceptances)
    {
        switch (header.ObjectClass)
        {
            case StateObjectClass.LineageHead:
                if (!LineageHeadCodec.TryDecode(payload, out var head) || head is null)
                {
                    return false;
                }
                lineages.Add(new TrustedProofLineageFact(
                    artifactId,
                    header.ObjectIdentity,
                    header.BaseScopeDigest,
                    head.Reviewed.HeadSha,
                    head.Transition,
                    head.Ordinal));
                return true;
            case StateObjectClass.Candidate:
                if (!AcceptedStateGenerationRecordCodec.TryDecode(payload, out var generation) ||
                    generation is null)
                {
                    return false;
                }
                candidates.Add(new TrustedProofCandidateFact(
                    artifactId,
                    header.ObjectIdentity,
                    header.BaseScopeDigest,
                    generation.Generation,
                    generation.StateEnvelopeSha256,
                    generation.PredecessorEnvelopeSha256,
                    generation.PreviousLogicalGenerationIdentity,
                    generation.ProducerHeadSha));
                return true;
            case StateObjectClass.Acceptance:
                if (!AcceptedStateAcceptanceReceiptCodec.TryDecode(payload, out var receipt) ||
                    receipt is null)
                {
                    return false;
                }
                acceptances.Add(new TrustedProofAcceptanceFact(
                    artifactId,
                    header.ObjectIdentity,
                    header.BaseScopeDigest,
                    receipt.LogicalGenerationIdentity,
                    receipt.OriginalCandidateObjectIdentity,
                    receipt.PreviousLogicalGenerationIdentity,
                    receipt.PreviousAcceptanceReceiptIdentity,
                    receipt.ReviewedHeadSha,
                    (int)receipt.PublicationOperation,
                    receipt.ProducingRunIdentity,
                    receipt.ProducingRunAttempt));
                return StringComparer.Ordinal.Equals(
                        receipt.ProducingRunIdentity,
                        header.ProducingRunIdentity) &&
                    receipt.ProducingRunAttempt == header.ProducingRunAttempt;
            case StateObjectClass.Cleanup:
                return RetainedStateCleanupRecordCodec.TryDecode(payload, out _) ||
                    RetainedStateOpaqueWriteAnchorCodec.TryDecode(payload, out _);
            case StateObjectClass.PublicationIntent:
            case StateObjectClass.PublicationFailure:
            case StateObjectClass.Abandonment:
            case StateObjectClass.Reset:
            case StateObjectClass.ExpiryTransition:
                return payload.Length > 0;
            default:
                return false;
        }
    }

    private static bool TryDeriveProductRoles(
        ImmutableArray<TrustedProofDecodedArtifact> decoded,
        IReadOnlyList<TrustedProofLineageFact> lineages,
        IReadOnlyList<TrustedProofCandidateFact> candidates,
        IReadOnlyList<TrustedProofAcceptanceFact> acceptances,
        out Dictionary<string, (string Role, string Scope)> roles)
    {
        roles = new Dictionary<string, (string Role, string Scope)>(StringComparer.Ordinal);
        var roots = decoded.Where(record => record.ObjectClass == "locator_root").ToArray();
        var bootstrapCandidates = candidates.Where(value =>
            value.Generation == 0 &&
            value.PredecessorEnvelopeSha256 is null &&
            value.PreviousLogicalGenerationIdentity is null).ToArray();
        var continuationCandidates = candidates.Where(value => value.Generation == 1).ToArray();
        var bootstrapAcceptances = acceptances.Where(value =>
            value.PreviousLogicalGenerationIdentity is null &&
            value.PreviousAcceptanceReceiptIdentity is null &&
            value.PublicationOperation == 3).ToArray();
        var continuationAcceptances = acceptances.Where(value =>
            value.PreviousLogicalGenerationIdentity is not null &&
            value.PreviousAcceptanceReceiptIdentity is not null &&
            value.PublicationOperation == 3).ToArray();
        if (roots.Length != 1 || lineages.Count != 2 || candidates.Count != 2 ||
            acceptances.Count != 2 || bootstrapCandidates.Length != 1 ||
            continuationCandidates.Length != 1 || bootstrapAcceptances.Length != 1 ||
            continuationAcceptances.Length != 1)
        {
            return false;
        }

        var bootstrapCandidate = bootstrapCandidates[0];
        var continuationCandidate = continuationCandidates[0];
        var bootstrapAcceptance = bootstrapAcceptances[0];
        var continuationAcceptance = continuationAcceptances[0];
        if (
            bootstrapCandidate.BaseScopeDigest != continuationCandidate.BaseScopeDigest ||
            bootstrapCandidate.BaseScopeDigest != bootstrapAcceptance.BaseScopeDigest ||
            bootstrapCandidate.BaseScopeDigest != continuationAcceptance.BaseScopeDigest ||
            bootstrapCandidate.ProducerHeadSha != continuationCandidate.ProducerHeadSha ||
            bootstrapCandidate.ProducerHeadSha != bootstrapAcceptance.ReviewedHeadSha ||
            bootstrapCandidate.ProducerHeadSha != continuationAcceptance.ReviewedHeadSha ||
            continuationCandidate.PredecessorEnvelopeSha256 != bootstrapCandidate.StateEnvelopeSha256 ||
            continuationCandidate.PreviousLogicalGenerationIdentity != bootstrapAcceptance.LogicalGenerationIdentity ||
            bootstrapAcceptance.OriginalCandidateObjectIdentity != bootstrapCandidate.ObjectIdentity ||
            continuationAcceptance.OriginalCandidateObjectIdentity != continuationCandidate.ObjectIdentity ||
            continuationAcceptance.PreviousLogicalGenerationIdentity != bootstrapAcceptance.LogicalGenerationIdentity ||
            continuationAcceptance.PreviousAcceptanceReceiptIdentity != bootstrapAcceptance.ObjectIdentity)
        {
            return false;
        }

        var normalHeads = lineages.Where(value =>
            value.BaseScopeDigest == bootstrapCandidate.BaseScopeDigest &&
            value.ReviewedHeadSha == bootstrapCandidate.ProducerHeadSha).ToArray();
        var staleHeads = lineages.Where(value =>
            value.BaseScopeDigest != bootstrapCandidate.BaseScopeDigest).ToArray();
        if (normalHeads.Length != 1 || staleHeads.Length != 1 ||
            lineages.Any(value => value.Transition != LineageTransitionKind.Initial || value.Ordinal != 0))
        {
            return false;
        }

        var normalHead = normalHeads[0];
        var staleHead = staleHeads[0];

        roles.Add(roots[0].ArtifactId, (ExpectedRoles[0], "repository"));
        roles.Add(normalHead.ArtifactId, (ExpectedRoles[1], "normal"));
        roles.Add(staleHead.ArtifactId, (ExpectedRoles[2], "stale"));
        roles.Add(bootstrapCandidate.ArtifactId, (ExpectedRoles[3], "normal"));
        roles.Add(continuationCandidate.ArtifactId, (ExpectedRoles[4], "normal"));
        roles.Add(bootstrapAcceptance.ArtifactId, (ExpectedRoles[5], "normal"));
        roles.Add(continuationAcceptance.ArtifactId, (ExpectedRoles[6], "normal"));
        return true;
    }

    private static string CanonicalHash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));
}
