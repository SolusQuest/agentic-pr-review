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
    string OpaqueName,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    byte[] Envelope);

internal sealed record TrustedProofOperationRun(
    string OperationId,
    string Scope,
    string RunIdentity,
    long RunAttempt);

internal sealed record TrustedProofDecodedArtifact(
    string ArtifactId,
    string Role,
    string Scope,
    string BaseScopeDigest,
    string ObjectClass,
    string KeyId,
    string ObjectIdentity,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    string OperationId,
    string PayloadSha256);

internal sealed record TrustedProofCodecOracleResult(
    bool ExactSevenSuccess,
    bool RecoveryOnly,
    ImmutableArray<TrustedProofDecodedArtifact> Records,
    ImmutableArray<TrustedProofCodecHandoff> MaintainerHandoff);

internal sealed record TrustedProofCodecHandoff(
    string ArtifactId,
    string Reason);

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
        IReadOnlyList<TrustedProofOperationRun> operationRuns,
        out TrustedProofCodecOracleResult? result)
    {
        result = null;
        if (artifacts.Count is < 7 or > 256 ||
            artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() !=
                artifacts.Count ||
            artifacts[0].OpaqueName != LocatorRootFormat.StoreName ||
            !TryIndexOperationRuns(operationRuns, out var operationByRun))
        {
            return false;
        }
        foreach (var artifact in artifacts)
        {
            if (!operationByRun.TryGetValue(artifact.ProducingRunIdentity, out var authority) ||
                authority.RunAttempt != artifact.ProducingRunAttempt)
            {
                return false;
            }
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
                         "unclassified",
                         "unclassified",
                         string.Empty,
                         "locator_root",
                        sentinel.WriterKeyId,
                        CanonicalHash(sentinel.Root),
                         artifacts[0].ProducingRunIdentity,
                         artifacts[0].ProducingRunAttempt,
                         operationByRun[artifacts[0].ProducingRunIdentity].OperationId,
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
                            if (!StringComparer.Ordinal.Equals(
                                    header.ProducingRunIdentity,
                                    artifact.ProducingRunIdentity) ||
                                header.ProducingRunAttempt != artifact.ProducingRunAttempt ||
                                !CaptureProductFact(
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
                                 "unclassified",
                                 "unclassified",
                                 header.BaseScopeDigest,
                                 StateObjectClasses.ToWireName(header.ObjectClass),
                                header.KeyId,
                                header.ObjectIdentity,
                                 header.ProducingRunIdentity,
                                 header.ProducingRunAttempt,
                                 operationByRun[header.ProducingRunIdentity].OperationId,
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
                         out var derivedRoles,
                         out var normalBaseScopeDigest,
                         out var staleBaseScopeDigest);
                    var ownershipValid = topologyValid;
                    var classified = decoded.Select(record =>
                    {
                        var scope = record.ObjectClass == "locator_root"
                            ? "repository"
                            : StringComparer.Ordinal.Equals(record.BaseScopeDigest, normalBaseScopeDigest)
                                ? "normal"
                                : StringComparer.Ordinal.Equals(record.BaseScopeDigest, staleBaseScopeDigest)
                                    ? "stale"
                                    : "unclassified";
                        var authority = operationByRun[record.ProducingRunIdentity];
                        var authorityScope = scope == "repository" ? "normal" : scope;
                        if (scope == "unclassified" ||
                            !StringComparer.Ordinal.Equals(authority.Scope, authorityScope))
                        {
                            ownershipValid = false;
                        }
                        var role = derivedRoles.TryGetValue(record.ArtifactId, out var derived)
                            ? derived.Role
                            : "internal-record";
                        return record with { Role = role, Scope = scope };
                    }).ToImmutableArray();
                    var ordinaryTopology = decoded.Count(record => record.ObjectClass == "locator_root") == 1 &&
                        decoded.Count(record => record.ObjectClass == "lineage_head") == 2 &&
                        decoded.Count(record => record.ObjectClass == "candidate") == 2 &&
                        decoded.Count(record => record.ObjectClass == "acceptance") == 2 &&
                        decoded.Count(record => record.ObjectClass == "publication_intent") == 4 &&
                        decoded.Count(record => record.ObjectClass == "cleanup") == 4 &&
                        decoded.Length == 15;
                    if (!ownershipValid)
                    {
                        return false;
                    }
                    var exact = topologyValid && ordinaryTopology && derivedRoles.Count == ExpectedRoles.Length;
                    result = new TrustedProofCodecOracleResult(
                        exact,
                        RecoveryOnly: !exact,
                        classified,
                        []);
                    return true;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sentinel.Root);
            }
        }
    }

    internal static bool TryDecodeRecovery(
        string repositoryId,
        string currentKeyBase64,
        string? previousKeyBase64,
        IReadOnlyList<TrustedProofEncryptedArtifact> artifacts,
        IReadOnlyList<TrustedProofOperationRun> operationRuns,
        out TrustedProofCodecOracleResult? result)
    {
        result = null;
        if (artifacts.Count > 256 ||
            artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() !=
                artifacts.Count ||
            !TryIndexRecoveryOperationRuns(operationRuns, out var operationByRun))
        {
            return false;
        }
        if (artifacts.Count == 0)
        {
            result = RecoveryResult([], []);
            return true;
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
            var locatorIndexes = artifacts
                .Select((artifact, index) => (artifact, index))
                .Where(item => item.artifact.OpaqueName == LocatorRootFormat.StoreName)
                .Select(item => item.index)
                .ToArray();
            if (locatorIndexes.Length != 1)
            {
                result = RecoveryResult(
                    [],
                    artifacts.Select(item => new TrustedProofCodecHandoff(
                        item.ArtifactId,
                        "locator-context-unavailable")));
                return true;
            }

            var locatorIndex = locatorIndexes[0];
            var locator = artifacts[locatorIndex];
            if (!LocatorRootSentinelCodec.TryDecrypt(
                    access,
                    keys,
                    locator.Envelope,
                    out var sentinel,
                    out _) ||
                sentinel is null)
            {
                result = RecoveryResult(
                    [],
                    artifacts.Select(item => new TrustedProofCodecHandoff(
                        item.ArtifactId,
                        "codec-authentication-failed")));
                return true;
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
                    result = RecoveryResult(
                        [],
                        artifacts.Select(item => new TrustedProofCodecHandoff(
                            item.ArtifactId,
                            "locator-context-unavailable")));
                    return true;
                }

                using (context)
                {
                    var records = ImmutableArray.CreateBuilder<TrustedProofDecodedArtifact>();
                    var handoff = ImmutableArray.CreateBuilder<TrustedProofCodecHandoff>();
                    if (TryResolveRecoveryAuthority(locator, operationByRun, out var locatorAuthority))
                    {
                        records.Add(new TrustedProofDecodedArtifact(
                            locator.ArtifactId,
                            "internal-record",
                            "repository",
                            string.Empty,
                            "locator_root",
                            sentinel.WriterKeyId,
                            CanonicalHash(sentinel.Root),
                            locator.ProducingRunIdentity,
                            locator.ProducingRunAttempt,
                            locatorAuthority!.OperationId,
                            CanonicalHash(sentinel.Root)));
                    }
                    else
                    {
                        handoff.Add(new TrustedProofCodecHandoff(
                            locator.ArtifactId,
                            "operation-ownership-unverified"));
                    }

                    for (var index = 0; index < artifacts.Count; index++)
                    {
                        if (index == locatorIndex)
                        {
                            continue;
                        }
                        var artifact = artifacts[index];
                        if (!TryResolveRecoveryAuthority(artifact, operationByRun, out var authority))
                        {
                            handoff.Add(new TrustedProofCodecHandoff(
                                artifact.ArtifactId,
                                "operation-ownership-unverified"));
                            continue;
                        }

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
                            handoff.Add(new TrustedProofCodecHandoff(
                                artifact.ArtifactId,
                                "codec-authentication-failed"));
                            continue;
                        }

                        try
                        {
                            if (!StringComparer.Ordinal.Equals(
                                    header.ProducingRunIdentity,
                                    artifact.ProducingRunIdentity) ||
                                header.ProducingRunAttempt != artifact.ProducingRunAttempt)
                            {
                                handoff.Add(new TrustedProofCodecHandoff(
                                    artifact.ArtifactId,
                                    "operation-ownership-unverified"));
                                continue;
                            }

                            if (!CaptureProductFact(
                                    artifact.ArtifactId,
                                    header,
                                    payload,
                                    [],
                                    [],
                                    []))
                            {
                                handoff.Add(new TrustedProofCodecHandoff(
                                    artifact.ArtifactId,
                                    "codec-payload-invalid"));
                                continue;
                            }

                            records.Add(new TrustedProofDecodedArtifact(
                                artifact.ArtifactId,
                                "internal-record",
                                authority!.Scope,
                                header.BaseScopeDigest,
                                StateObjectClasses.ToWireName(header.ObjectClass),
                                header.KeyId,
                                header.ObjectIdentity,
                                header.ProducingRunIdentity,
                                header.ProducingRunAttempt,
                                authority.OperationId,
                                CanonicalHash(payload)));
                        }
                        finally
                        {
                            CryptographicOperations.ZeroMemory(payload);
                        }
                    }

                    result = RecoveryResult(records, handoff);
                    return true;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sentinel.Root);
            }
        }
    }

    private static TrustedProofCodecOracleResult RecoveryResult(
        IEnumerable<TrustedProofDecodedArtifact> records,
        IEnumerable<TrustedProofCodecHandoff> handoff) =>
        new(
            ExactSevenSuccess: false,
            RecoveryOnly: true,
            records.ToImmutableArray(),
            handoff.ToImmutableArray());

    private static bool TryResolveRecoveryAuthority(
        TrustedProofEncryptedArtifact artifact,
        IReadOnlyDictionary<string, TrustedProofOperationRun> operationByRun,
        out TrustedProofOperationRun? authority)
    {
        if (operationByRun.TryGetValue(artifact.ProducingRunIdentity, out var observed) &&
            observed.RunAttempt == artifact.ProducingRunAttempt)
        {
            authority = observed;
            return true;
        }
        authority = null;
        return false;
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
         out Dictionary<string, (string Role, string Scope)> roles,
         out string normalBaseScopeDigest,
         out string staleBaseScopeDigest)
    {
        roles = new Dictionary<string, (string Role, string Scope)>(StringComparer.Ordinal);
        normalBaseScopeDigest = string.Empty;
        staleBaseScopeDigest = string.Empty;
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
        normalBaseScopeDigest = normalHead.BaseScopeDigest;
        staleBaseScopeDigest = staleHead.BaseScopeDigest;

        roles.Add(roots[0].ArtifactId, (ExpectedRoles[0], "repository"));
        roles.Add(normalHead.ArtifactId, (ExpectedRoles[1], "normal"));
        roles.Add(staleHead.ArtifactId, (ExpectedRoles[2], "stale"));
        roles.Add(bootstrapCandidate.ArtifactId, (ExpectedRoles[3], "normal"));
        roles.Add(continuationCandidate.ArtifactId, (ExpectedRoles[4], "normal"));
        roles.Add(bootstrapAcceptance.ArtifactId, (ExpectedRoles[5], "normal"));
        roles.Add(continuationAcceptance.ArtifactId, (ExpectedRoles[6], "normal"));
        return true;
    }

    private static bool TryIndexOperationRuns(
        IReadOnlyList<TrustedProofOperationRun> operationRuns,
        out Dictionary<string, TrustedProofOperationRun> byRun)
    {
        var indexed = new Dictionary<string, TrustedProofOperationRun>(StringComparer.Ordinal);
        if (operationRuns.Count != 4 ||
            operationRuns.Select(item => item.OperationId).Distinct(StringComparer.Ordinal).Count() != 2 ||
            operationRuns.Any(item =>
                !Sha256(item.OperationId) ||
                !new[] { "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                !PositiveDecimal(item.RunIdentity) ||
                item.RunAttempt != 1 ||
                !indexed.TryAdd(item.RunIdentity, item)) ||
            operationRuns.GroupBy(item => item.OperationId, StringComparer.Ordinal)
                .Any(group => group.Count() != 2 ||
                    group.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Count() != 1) ||
            operationRuns.Count(item => item.Scope == "normal") != 2 ||
            operationRuns.Count(item => item.Scope == "stale") != 2)
        {
            byRun = new Dictionary<string, TrustedProofOperationRun>(StringComparer.Ordinal);
            return false;
        }
        byRun = indexed;
        return true;
    }

    private static bool TryIndexRecoveryOperationRuns(
        IReadOnlyList<TrustedProofOperationRun> operationRuns,
        out Dictionary<string, TrustedProofOperationRun> byRun)
    {
        var indexed = new Dictionary<string, TrustedProofOperationRun>(StringComparer.Ordinal);
        if (operationRuns.Count > 64 ||
            operationRuns.Any(item =>
                !Sha256(item.OperationId) ||
                !new[] { "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                !PositiveDecimal(item.RunIdentity) ||
                item.RunAttempt <= 0 ||
                !indexed.TryAdd(item.RunIdentity, item)))
        {
            byRun = new Dictionary<string, TrustedProofOperationRun>(StringComparer.Ordinal);
            return false;
        }
        byRun = indexed;
        return true;
    }

    private static bool PositiveDecimal(string value) =>
        value.Length is > 0 and <= 20 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        ulong.TryParse(value, out var parsed) && parsed > 0;

    private static bool Sha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string CanonicalHash(ReadOnlySpan<byte> value) =>
        Convert.ToHexStringLower(SHA256.HashData(value));
}
