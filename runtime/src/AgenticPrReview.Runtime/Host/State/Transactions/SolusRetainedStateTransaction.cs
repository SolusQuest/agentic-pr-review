using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal enum SolusTransactionMode { Fresh, CompletedContextReview, FreshOrCompletedContextReview }

// Selected only by compiled Host composition, separate from S2 consumption.
internal sealed record SolusTransactionSelectionRequest(
    SolusFreshHostContext Targets, SolusRestrictedStateCompatibility Compatibility, Guid ExecutionId,
    SolusTransactionMode Mode = SolusTransactionMode.Fresh);

internal sealed record SolusRetainedStateMaterial(byte[] Envelope, string RecordSha256,
    ValidatedPublicationPayloadV1 Publication, RetainedStateCurrentReviewProjection Projection,
    long PreparedAt, long LogicalExpiry, long RequiredPlatformExpiry,
    long Generation = 0, string? PredecessorEnvelopeSha256 = null);

internal sealed partial class RetainedStateTransactionAuthority
{
    internal bool CanPrepareFreshSolus => IsLive && solusTransaction?.Mode == SolusTransactionMode.Fresh &&
        acceptedSelection is null && !HasTerminalAcceptance;

    internal bool CanPrepareRestoredSolus => IsLive &&
        solusTransaction?.Mode == SolusTransactionMode.CompletedContextReview &&
        acceptedSelection is not null && !HasTerminalAcceptance;

    internal bool TryBuildSolusSuccessor(RetainedStateAuthorityLease lease,
        SolusRestrictedStateRecord record, R4ValidatedPublicationReview review,
        out SolusRetainedStateMaterial? material)
    {
        material = null;
        return Allows(lease) && CanPrepareFreshSolus &&
            TryBuildSolusMaterial(lease, record, review, 0, null, out material);
    }

    internal bool TryBuildRestoredSolusSuccessor(RetainedStateAuthorityLease lease,
        SolusConsumedContext consumed, SolusRestrictedStateRecord record,
        R4ValidatedPublicationReview review, RetainedStateObservation observation,
        out SolusRetainedStateMaterial? material)
    {
        material = null;
        var selected = acceptedSelection;
        var observed = observation.AcceptedState.Selection;
        if (!Allows(lease) || !CanPrepareRestoredSolus || selected is null || observed is null ||
            observation.Snapshot is not { } inventory || !consumed.WasClaimed ||
            !ReferenceEquals(consumed.Targets, solusTransaction!.Targets) ||
            consumed.Compatibility != solusTransaction.Compatibility ||
            observed.LineageHead.Header.ObjectIdentity != selected.LineageHead.Header.ObjectIdentity ||
            observed.Current.ReceiptPhysical.Header.ObjectIdentity != selected.Current.ReceiptPhysical.Header.ObjectIdentity ||
            observed.Current.LogicalGenerationIdentity != selected.Current.LogicalGenerationIdentity ||
            !TryReadTrustedTime(lease, out var now) ||
            !RetainedStateTransactionService.HasNoPendingPublicationWork(inventory, observed, now) ||
            !MatchesSolusSourceClaim(consumed.Claim, selected) ||
            !HasExactSolusClaim(inventory, consumed.Claim) ||
            !MatchesSolusSuccessorCheckpoint(record, consumed.Claim) ||
            now >= consumed.Claim.SourceExpiresAtUnixSeconds ||
            selected.Current.Generation.Generation == long.MaxValue) return false;
        return TryBuildSolusMaterial(lease, record, review,
            selected.Current.Generation.Generation + 1,
            selected.Current.Generation.StateEnvelopeSha256, out material);
    }

    private bool TryBuildSolusMaterial(RetainedStateAuthorityLease lease,
        SolusRestrictedStateRecord record, R4ValidatedPublicationReview review,
        long generation, string? predecessorEnvelope, out SolusRetainedStateMaterial? material)
    {
        material = null;
        if (!Allows(lease) || solusTransaction is null || record is null || review is null ||
            record.Compatibility != solusTransaction!.Compatibility ||
            record.Checkpoint.ExecutionId != solusTransaction.ExecutionId ||
            !SameIdentity(review.ReviewedIdentity, currentReviewedIdentity) || review.Scope != publication.Scope ||
            !TryReadTrustedTime(lease, out var now) ||
            !RetainedStateRetention.TryCandidate(now, out var logicalExpiry, out var platformExpiry)) return false;
        byte[]? envelope = null;
        try
        {
            var rendered = R4StickyRenderer.Render(review);
            if (!ValidatedPublicationPayloadV1.TryCreate(rendered.Comment,
                    checked((long)publication.Scope.RepositoryId), publication.RepositoryName,
                    checked((long)publication.Scope.PullRequestNumber), policy.PolicyIdentitySha256,
                    publication.PayloadSha256, publication.BuildDiscriminator, AcceptedStateFormat.RenderingVersion,
                    out var payload) || payload is null || payload.ScopeSha256 != publication.ScopeSha256 ||
                payload.ReviewedHeadSha != currentReviewedIdentity.HeadSha ||
                locatorAccess is null || locator is null ||
                !SolusRestrictedStateSnapshotCodec.TrySeal(stateAccess,
                    new(stateAccess.Scope, reviewed.BaseSha, reviewed.HeadSha, generation, predecessorEnvelope, now, logicalExpiry),
                    record, new TransactionRestrictedStateKeyResolver(stateAccess, locatorAccess, locator),
                    out envelope, out var digest, out _) || envelope is null || digest is null) return false;
            material = new(envelope, digest, payload,
                new(currentReviewedIdentity, rendered.OrderedFindings), now, logicalExpiry, platformExpiry,
                generation, predecessorEnvelope);
            envelope = null;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            OverflowException or CryptographicException or R4PublicationException) { return false; }
        finally { if (envelope is not null) CryptographicOperations.ZeroMemory(envelope); }
    }

    private bool TryValidateSelectedSolusAcceptance()
    {
        var selection = acceptedSelection;
        return selection is null ||
            AcceptedStateRestoreService.MatchesPublication(selection.Current, policy, publication) &&
            (selection.ImmediatePredecessor is null ||
                AcceptedStateRestoreService.MatchesPublication(selection.ImmediatePredecessor, policy, publication)) &&
            TryValidateSolusGeneration(selection.Current.Generation, acceptedGeneration: true);
    }

    private bool TryValidateSolusGeneration(StateGenerationRecordV1 generation, bool acceptedGeneration,
        ScopedStateInventorySnapshot? inventory = null)
    {
        if (!IsLive || solusTransaction is null || generation is null || locatorAccess is null || locator is null ||
            generation.PolicyIdentitySha256 != policy.PolicyIdentitySha256 || generation.ConfigSha256 != policy.ConfigSha256 ||
            generation.InstructionsSha256 != policy.InstructionsSha256 || generation.PayloadSha256 != policy.PayloadSha256 ||
            generation.BuildDiscriminator != policy.BuildDiscriminator ||
            !AcceptedStateRecordValidation.FixedDigest(generation.StateEnvelopeSha256,
                RestrictedStateEnvelope.EnvelopeSha256(generation.EncryptedStateEnvelope.AsSpan()))) return false;
        if (solusTransaction.Mode == SolusTransactionMode.Fresh)
        {
            if (generation.Generation != 0 || generation.PredecessorEnvelopeSha256 is not null ||
                generation.PreviousLogicalGenerationIdentity is not null ||
                generation.ProducerBaseSha != reviewed.BaseSha || generation.ProducerHeadSha != reviewed.HeadSha)
                return false;
        }
        else if (solusTransaction.Mode != SolusTransactionMode.CompletedContextReview ||
            acceptedSelection is null || !acceptedGeneration &&
            (generation.ProducerBaseSha != reviewed.BaseSha || generation.ProducerHeadSha != reviewed.HeadSha ||
                acceptedSelection.Current.Generation.Generation == long.MaxValue ||
                generation.Generation != acceptedSelection.Current.Generation.Generation + 1 ||
                generation.PredecessorEnvelopeSha256 != acceptedSelection.Current.Generation.StateEnvelopeSha256 ||
                generation.PreviousLogicalGenerationIdentity != acceptedSelection.Current.LogicalGenerationIdentity))
            return false;
        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (now < generation.PreparedAtUnixSeconds || now >= generation.PreparedExpiresAtUnixSeconds) return false;
        var binding = new RestrictedStateBinding(stateAccess.Scope, generation.ProducerBaseSha,
            generation.ProducerHeadSha, generation.Generation, generation.PredecessorEnvelopeSha256,
            generation.PreparedAtUnixSeconds, generation.PreparedExpiresAtUnixSeconds);
        var resolver = new TransactionRestrictedStateKeyResolver(stateAccess, locatorAccess, locator);
        if (acceptedGeneration)
            return TryReadAcceptedSolusRecord(out _);
        byte[]? plaintext = null;
        try
        {
            // Pending candidates have no accepted-lineage capability. Authenticate
            // against the independently selected current transaction instead.
            if (!(acceptedSelection is null || solusTransaction.Mode == SolusTransactionMode.CompletedContextReview) ||
                !RestrictedStateEnvelope.TryDecrypt(stateAccess, binding,
                    generation.EncryptedStateEnvelope.AsSpan(), resolver, out plaintext, out _) || plaintext is null)
                return false;
            if (!AcceptedStateRecordValidation.FixedDigest(SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext),
                    generation.SessionSha256) ||
                !SolusRestrictedStateSnapshotCodec.TryRead(plaintext, solusTransaction.Compatibility, out var record, out _) ||
                record is null) return false;
            if (solusTransaction.Mode == SolusTransactionMode.Fresh) return true;
            if (inventory is null || acceptedSelection is null) return false;
            var claims = inventory.Authenticated.Where(item => item.Header.ObjectClass == StateObjectClass.ContextClaim)
                .Select(item => SolusContextClaimRecordCodec.TryDecode(item.Payload, out var claim) ? claim : null)
                .Where(claim => claim is not null && MatchesSolusSourceClaim(claim, acceptedSelection) &&
                    MatchesSolusSuccessorCheckpoint(record, claim)).ToArray();
            return claims.Length == 1 && HasExactSolusClaim(inventory, claims[0]!);
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private bool MatchesSolusSourceClaim(SolusContextClaimRecord claim, AcceptedStateSelection selected)
    {
        var head = selected.LineageHead.Header;
        var current = selected.Current;
        return claim.BaseScopeDigest == head.BaseScopeDigest && claim.Epoch == head.Epoch &&
            claim.SessionId == head.SessionId && claim.SelectedHeadIdentity == head.ObjectIdentity &&
            claim.AcceptanceIdentity == current.ReceiptPhysical.Header.ObjectIdentity &&
            claim.LogicalGenerationIdentity == current.LogicalGenerationIdentity &&
            claim.EnvelopeSha256 == current.Generation.StateEnvelopeSha256 &&
            claim.RecordSha256 == current.Generation.SessionSha256 &&
            claim.SourceExpiresAtUnixSeconds == current.Generation.PreparedExpiresAtUnixSeconds &&
            TryReadAcceptedSolusRecord(out var source) && source?.Checkpoint == claim.Source;
    }

    private static bool MatchesSolusSuccessorCheckpoint(SolusRestrictedStateRecord record, SolusContextClaimRecord claim) =>
        record.Checkpoint.ExecutionId == claim.ExecutionId && record.Checkpoint.RoundId == claim.RoundId &&
        record.Checkpoint.LogicalWorkId != claim.Source.LogicalWorkId &&
        record.Checkpoint.CheckpointId != claim.Source.CheckpointId;

    private static bool HasExactSolusClaim(ScopedStateInventorySnapshot inventory, SolusContextClaimRecord expected)
    {
        if (!inventory.Unknown.IsEmpty || inventory.UnderRetained.Any(item => item.Header.ObjectClass == StateObjectClass.ContextClaim))
            return false;
        var matching = 0;
        foreach (var item in inventory.Authenticated.Where(item => item.Header.ObjectClass == StateObjectClass.ContextClaim))
        {
            if (!SolusContextClaimRecordCodec.TryDecode(item.Payload, out var claim) || claim is null) return false;
            if (claim == expected) matching++;
            else if (claim.ConflictsWith(expected)) return false;
        }
        return matching == 1;
    }

    internal bool TryReadAcceptedSolusRecord(out SolusRestrictedStateRecord? record)
    {
        record = null;
        var selected = acceptedSelection?.Current;
        if (!IsLive || solusTransaction is null || selected is null || locatorAccess is null || locator is null) return false;
        var generation = selected.Generation;
        var binding = new RestrictedStateBinding(stateAccess.Scope, generation.ProducerBaseSha,
            generation.ProducerHeadSha, generation.Generation, generation.PredecessorEnvelopeSha256,
            generation.PreparedAtUnixSeconds, generation.PreparedExpiresAtUnixSeconds);
        // This lineage comes only from the official selector's authenticated receipt.
        var lineage = new AcceptedLineage(stateAccess.Scope, generation.Generation, generation.SessionSha256,
            generation.StateEnvelopeSha256, generation.PredecessorEnvelopeSha256,
            generation.PreparedAtUnixSeconds, generation.PreparedExpiresAtUnixSeconds, true);
        return SolusRestrictedStateSnapshotCodec.TryRestore(stateAccess, binding, lineage,
            generation.EncryptedStateEnvelope.AsSpan(), solusTransaction.Compatibility,
            timeProvider.GetUtcNow().ToUnixTimeSeconds(),
            new TransactionRestrictedStateKeyResolver(stateAccess, locatorAccess, locator), out record, out _);
    }

    private bool TryRecoverSolusProjection(StateGenerationRecordV1 generation,
        out RetainedStateCurrentReviewProjection? projection)
    {
        projection = null;
        byte[] canonical = [];
        try
        {
            var previousAcceptance = generation.PreviousLogicalGenerationIdentity is null ? null :
                acceptedSelection?.Current.LogicalGenerationIdentity == generation.PreviousLogicalGenerationIdentity
                    ? acceptedSelection.Current.ReceiptPhysical.Header.ObjectIdentity
                    : acceptedSelection?.ImmediatePredecessor?.LogicalGenerationIdentity == generation.PreviousLogicalGenerationIdentity
                        ? acceptedSelection.ImmediatePredecessor.ReceiptPhysical.Header.ObjectIdentity : null;
            // The same call retains its exact full validated projection. Only a
            // new process needs the deliberately incomplete public count view.
            if (TryGetLineageSnapshot(out var selected) && selected is not null &&
                AcceptedStateGenerationRecordCodec.TryEncode(generation, out canonical) &&
                AcceptedStateIdentity.TryComputeLogicalGeneration(canonical, selected.BaseScopeDigest,
                    selected.Epoch, selected.SessionId,
                    previousAcceptanceReceiptIdentity: previousAcceptance,
                    out var identity) &&
                TryGetBoundCurrentReview(identity, out projection)) return true;
        }
        finally { CryptographicOperations.ZeroMemory(canonical); }
        if (!AcceptedStatePublicationPayloadCodec.TryDecode(generation.PublicationPayloadBytes.AsSpan(), out var payload) ||
            payload is null || payload.ScopeSha256 != publication.ScopeSha256 ||
            payload.ReviewedHeadSha != currentReviewedIdentity.HeadSha ||
            !SolusRecoveredPublicationCount.TryRead(payload, out var count)) return false;
        projection = new(currentReviewedIdentity, default, count);
        return true;
    }
}

// Counts only authenticated deterministic R4 rendering. It creates no finding,
// grounding, inline capability, runtime context or source-consumption claim.
internal static class SolusRecoveredPublicationCount
{
    internal static bool TryRead(ValidatedPublicationPayloadV1 publication, out int count)
    {
        count = 0;
        var comment = Encoding.UTF8.GetString(publication.FinalizedCommentUtf8.AsSpan());
        var inspected = R4StickyMarker.Inspect(comment);
        if (inspected.Kind != R4StickyInspectionKind.ValidR4 || inspected.Body is not { } body ||
            inspected.Identity is not { } identity || identity.ScopeSha256 != publication.ScopeSha256 ||
            identity.BodySha256 != publication.BodySha256 ||
            identity.HeadSha != publication.ReviewedHeadSha) return false;
        var prefix = "## Agentic PR Review\n\nReviewed head: `" + publication.ReviewedHeadSha + "`\n\n### Summary\n\n";
        if (!body.StartsWith(prefix, StringComparison.Ordinal)) return false;
        const string boundary = "\n\n### Findings";
        var section = body.IndexOf(boundary, prefix.Length, StringComparison.Ordinal);
        if (section < 0 || body.IndexOf(boundary, section + boundary.Length, StringComparison.Ordinal) >= 0) return false;
        var findings = body[(section + boundary.Length)..];
        if (findings == "\n\nNo actionable findings.") return true;
        var omitted = 0;
        var notice = "\n\n" + R4StickyRenderer.TruncationNoticePrefix;
        var truncation = findings.IndexOf(notice, StringComparison.Ordinal);
        if (truncation >= 0)
        {
            var suffix = R4StickyRenderer.TruncationNoticeSuffix;
            var number = findings[(truncation + notice.Length)..];
            if (!number.EndsWith(suffix, StringComparison.Ordinal)) return false;
            number = number[..^suffix.Length];
            if (number.Length is < 1 or > 2 || number[0] == '0' || number.Any(c => c is < '0' or > '9') ||
                !int.TryParse(number, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out omitted) || omitted > AgentLimits.Findings) return false;
            findings = findings[..truncation];
        }
        var rendered = 0;
        if (findings.Length != 0)
        {
            const string separator = "\n\n#### ";
            if (!findings.StartsWith(separator, StringComparison.Ordinal)) return false;
            var fingerprints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var block in findings[separator.Length..].Split(separator, StringSplitOptions.None))
            {
                if (!TryFindingBlock(block, fingerprints) || ++rendered > AgentLimits.Findings) return false;
            }
        }
        count = rendered + omitted;
        return count is > 0 and <= AgentLimits.Findings;
    }

    private static bool TryFindingBlock(string block, HashSet<string> fingerprints)
    {
        if (!new[] { "CRITICAL", "HIGH", "MEDIUM", "LOW" }.Any(severity =>
                block.StartsWith(severity + ": ", StringComparison.Ordinal) && block.Length > severity.Length + 2)) return false;
        const string marker = "\n\n- Fingerprint: `";
        const string evidence = "`\n- Evidence:";
        var index = block.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0 || block.IndexOf(marker, index + marker.Length, StringComparison.Ordinal) >= 0 ||
            block[..index].IndexOf("\n\n", StringComparison.Ordinal) < 0) return false;
        var tail = block[(index + marker.Length)..];
        if (tail.Length < 64 + evidence.Length || !tail.AsSpan(64).StartsWith(evidence, StringComparison.Ordinal)) return false;
        var fingerprint = tail[..64];
        if (!RestrictedStateValidation.IsLowerHex(fingerprint, 64) || !fingerprints.Add(fingerprint)) return false;
        var rows = tail[(64 + evidence.Length)..].Split("\n  - ", StringSplitOptions.None);
        if (rows.Length is < 2 or > AgentLimits.EvidencePerFinding + 1 || rows[0] != "") return false;
        foreach (var row in rows.Skip(1))
        {
            var match = System.Text.RegularExpressions.Regex.Match(row,
                @"^[^\n]+ \(lines ([1-9][0-9]{0,9})-([1-9][0-9]{0,9}); observation `[a-f0-9]{64}`\)$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var first) ||
                !int.TryParse(match.Groups[2].Value, out var last) || last < first) return false;
        }
        return true;
    }
}
