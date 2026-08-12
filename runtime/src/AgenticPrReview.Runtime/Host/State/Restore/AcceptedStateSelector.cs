using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed record SelectedAcceptedGeneration(
    AuthenticatedStateObject Physical,
    StateGenerationRecordV1 Generation,
    AcceptanceReceiptV1 Receipt,
    AuthenticatedStateObject ReceiptPhysical,
    string LogicalGenerationIdentity,
    string OriginalCandidateObjectIdentity);

internal sealed record AcceptedStateSelection(
    SelectedAcceptedGeneration Current,
    SelectedAcceptedGeneration? ImmediatePredecessor,
    LineageHeadCandidate LineageHead,
    long RequiredCurrentWindowUnixSeconds);

internal sealed record AcceptedStateSelectionResult(
    string Code,
    bool IsBootstrap,
    AcceptedStateSelection? Selection,
    AcceptedStateSelector.AuthorizedInitialLineageAbsence? InitialAbsence,
    AcceptedStateSelector.AcceptedStateExpiryCandidate? Expiry)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, AcceptedStateCodes.Ready) &&
        Selection is not null;

    internal static AcceptedStateSelectionResult Ready(
        AcceptedStateSelection selection) =>
        new(AcceptedStateCodes.Ready, false, selection, null, null);

    internal static AcceptedStateSelectionResult Bootstrap(
        AcceptedStateSelector.AuthorizedInitialLineageAbsence? authority =
            null) =>
        new(AcceptedStateCodes.Bootstrap, true, null, authority, null);

    internal static AcceptedStateSelectionResult Expired(
        AcceptedStateSelection selection,
        AcceptedStateSelector.AcceptedStateExpiryCandidate candidate) =>
        new(AcceptedStateCodes.Expired, false, selection, null, candidate);

    internal static AcceptedStateSelectionResult Fail(string code) =>
        new(code, false, null, null, null);
}

internal sealed class AcceptedStateSelector
{
    private static readonly object CapabilityIssuer = new();
    private readonly TimeProvider timeProvider;

    internal AcceptedStateSelector(TimeProvider? timeProvider = null)
    {
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal AcceptedStateSelectionResult Select(
        LineageReadOnlyObservationContext observation,
        LineageResolveRequest request)
    {
        if (observation is null ||
            request is null ||
            observation.Snapshot is not { } snapshot ||
            !LineageValidation.IsSha256(observation.BaseScopeDigest) ||
            !LineageValidation.IsSha256(observation.CurrentKeyId))
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.AccessDenied);
        }

        if (observation.Selection.IsAbsent)
        {
            if (snapshot.PhysicalCount != 0 ||
                !snapshot.Authenticated.IsEmpty ||
                !snapshot.UnderRetained.IsEmpty ||
                !snapshot.Unknown.IsEmpty)
            {
                return AcceptedStateSelectionResult.Fail(
                    AcceptedStateCodes.Conflict);
            }

            return AcceptedStateSelectionResult.Bootstrap(
                new AuthorizedInitialLineageAbsence(
                    CapabilityIssuer,
                    request.Access,
                    request.BaseScope.RepositoryId,
                    observation.BaseScopeDigest,
                    observation.InventoryDigest,
                    observation.CurrentKeyId,
                    request.ProducingRunIdentity,
                    request.ProducingRunAttempt,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    observation.RequiredPlatformExpiresAtUnixSeconds));
        }

        var selectedHead = observation.Selection.Selection?.Head;
        if (selectedHead is null)
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.Conflict);
        }

        var candidateName = snapshot.Names[StateObjectClass.Candidate];
        var acceptanceName = snapshot.Names[StateObjectClass.Acceptance];
        if (snapshot.Unknown.Any(item =>
            item.Metadata.Reference.Name == candidateName ||
            item.Metadata.Reference.Name == acceptanceName))
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.OutcomeUnknown);
        }

        var relevant = snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item => item.Header.ObjectClass is
                StateObjectClass.Candidate or StateObjectClass.Acceptance)
            .ToImmutableArray();
        var active = relevant
            .Where(item =>
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    selectedHead.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    selectedHead.Header.SessionId))
            .ToImmutableArray();
        var historicalEvidence = selectedHead.Head.Superseded
            .Concat(selectedHead.Head.CompletedCleanup)
            .ToImmutableArray();
        foreach (var item in relevant.Except(active))
        {
            var epochMatches = StringComparer.Ordinal.Equals(
                item.Header.Epoch,
                selectedHead.Header.Epoch);
            var sessionMatches = StringComparer.Ordinal.Equals(
                item.Header.SessionId,
                selectedHead.Header.SessionId);
            if (epochMatches != sessionMatches)
            {
                return AcceptedStateSelectionResult.Fail(
                    AcceptedStateCodes.ScopeMismatch);
            }

            if (!historicalEvidence.Any(evidence =>
                LineageHeadCodec.Matches(evidence, item.Metadata)))
            {
                return AcceptedStateSelectionResult.Fail(
                    AcceptedStateCodes.Conflict);
            }
        }

        var candidates = new List<ParsedCandidate>();
        var receipts = new List<ParsedReceipt>();
        foreach (var item in active)
        {
            if (item.Header.ObjectClass == StateObjectClass.Candidate)
            {
                if (!TryParseCandidate(
                        item,
                        observation.BaseScopeDigest,
                        out var parsed) ||
                    parsed is null)
                {
                    return AcceptedStateSelectionResult.Fail(
                        AcceptedStateCodes.IncompatibleCurrent);
                }

                candidates.Add(parsed);
            }
            else if (item.Header.ObjectClass == StateObjectClass.Acceptance)
            {
                if (!AcceptedStateAcceptanceReceiptCodec.TryDecode(
                        item.Payload,
                        out var receipt) ||
                    receipt is null ||
                    !ReceiptMatchesOuter(item, receipt))
                {
                    return AcceptedStateSelectionResult.Fail(
                        AcceptedStateCodes.IncompatibleCurrent);
                }

                receipts.Add(new ParsedReceipt(item, receipt));
            }
        }

        if (receipts.Count == 0)
        {
            return candidates.Count == 0
                ? AcceptedStateSelectionResult.Bootstrap()
                : AcceptedStateSelectionResult.Fail(
                    AcceptedStateCodes.IncompatibleCurrent);
        }

        if (!TrySelectBoundedReceiptTail(
                receipts,
                out var terminal,
                out var immediateReceipt) ||
            terminal is null)
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.Conflict);
        }

        var requiredWindow = terminal.Receipt.LogicalExpiresAtUnixSeconds;
        if (terminal.Physical.Metadata.ExpiresAtUnixSeconds < requiredWindow ||
            (immediateReceipt is not null &&
                immediateReceipt.Physical.Metadata.ExpiresAtUnixSeconds <
                    requiredWindow))
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.Overflow);
        }

        if (!TrySelectGeneration(
                terminal,
                candidates,
                requiredWindow,
                out var current) ||
            current is null)
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.Conflict);
        }

        SelectedAcceptedGeneration? predecessor = null;
        if (immediateReceipt is not null)
        {
            if (!TrySelectGeneration(
                    immediateReceipt,
                    candidates,
                    requiredWindow,
                    out predecessor) ||
                predecessor is null ||
                !StringComparer.Ordinal.Equals(
                    terminal.Receipt.PreviousLogicalGenerationIdentity,
                    predecessor.LogicalGenerationIdentity) ||
                current.Generation.Generation !=
                    predecessor.Generation.Generation + 1 ||
                !AcceptedStateRecordValidation.FixedDigest(
                    current.Generation.PredecessorEnvelopeSha256!,
                    predecessor.Generation.StateEnvelopeSha256) ||
                !StringComparer.Ordinal.Equals(
                    current.Generation.PreviousLogicalGenerationIdentity,
                    predecessor.LogicalGenerationIdentity))
            {
                return AcceptedStateSelectionResult.Fail(
                    AcceptedStateCodes.Conflict);
            }
        }
        else if (terminal.Receipt.PreviousLogicalGenerationIdentity is not null ||
            current.Generation.Generation != 0)
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.Conflict);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (!LineageValidation.IsTime(now))
        {
            return AcceptedStateSelectionResult.Fail(
                AcceptedStateCodes.OutcomeUnknown);
        }

        if (requiredWindow <= now)
        {
            return AcceptedStateSelectionResult.Expired(
                new AcceptedStateSelection(
                    current,
                    predecessor,
                    selectedHead,
                    requiredWindow),
                new AcceptedStateExpiryCandidate(
                    CapabilityIssuer,
                    request.Access,
                    request.BaseScope.RepositoryId,
                    observation.BaseScopeDigest,
                    observation.InventoryDigest,
                    observation.CurrentKeyId,
                    selectedHead.Header.ObjectIdentity,
                    terminal.Physical.Header.ObjectIdentity,
                    current.OriginalCandidateObjectIdentity,
                    current.LogicalGenerationIdentity,
                    terminal.Receipt.PreviousAcceptanceReceiptIdentity,
                    immediateReceipt?.Physical.Header.ObjectIdentity,
                    requiredWindow,
                    request.ProducingRunIdentity,
                    request.ProducingRunAttempt));
        }

        return AcceptedStateSelectionResult.Ready(
            new AcceptedStateSelection(
                current,
                predecessor,
                selectedHead,
                requiredWindow));
    }

    private static bool TryParseCandidate(
        AuthenticatedStateObject physical,
        string baseScopeDigest,
        out ParsedCandidate? candidate)
    {
        candidate = null;
        StateGenerationRecordV1? generation;
        AcceptedStatePhysicalCopyV1? copy = null;
        byte[] canonical;
        var isOriginal = AcceptedStateGenerationRecordCodec.TryDecode(
            physical.Payload,
            out generation);
        if (isOriginal && generation is not null)
        {
            canonical = physical.Payload.ToArray();
        }
        else if (AcceptedStatePhysicalCopyCodec.TryDecode(
                physical.Payload,
                out copy) &&
            copy is not null &&
            AcceptedStateGenerationRecordCodec.TryDecode(
                copy.CanonicalGenerationBytes.AsSpan(),
                out generation) &&
            generation is not null)
        {
            canonical = copy.CanonicalGenerationBytes.ToArray();
        }
        else
        {
            return false;
        }

        try
        {
            if (!AcceptedStateIdentity.TryComputeLogicalGeneration(
                    canonical,
                    baseScopeDigest,
                    physical.Header.Epoch,
                    physical.Header.SessionId,
                    physical.Header.PredecessorIdentity,
                    out var logicalIdentity) ||
                (copy is not null &&
                    (!StringComparer.Ordinal.Equals(
                        copy.LogicalGenerationIdentity,
                        logicalIdentity) ||
                    StringComparer.Ordinal.Equals(
                        copy.SourceArtifactId,
                        physical.Metadata.Reference.ObjectId.Value) ||
                    StringComparer.Ordinal.Equals(
                        physical.Header.ObjectIdentity,
                        copy.OriginalCandidateObjectIdentity))))
            {
                return false;
            }

            candidate = new ParsedCandidate(
                physical,
                generation,
                canonical.ToImmutableArray(),
                logicalIdentity,
                isOriginal,
                copy);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private static bool ReceiptMatchesOuter(
        AuthenticatedStateObject physical,
        AcceptanceReceiptV1 receipt) =>
        StringComparer.Ordinal.Equals(
            physical.Header.PredecessorIdentity,
            receipt.PreviousAcceptanceReceiptIdentity) &&
        StringComparer.Ordinal.Equals(
            physical.Header.ProducingRunIdentity,
            receipt.ProducingRunIdentity) &&
        physical.Header.ProducingRunAttempt == receipt.ProducingRunAttempt &&
        StringComparer.Ordinal.Equals(
            physical.Metadata.ProducingRun.Identity,
            receipt.ProducingRunIdentity) &&
        physical.Metadata.ProducingRun.Attempt == receipt.ProducingRunAttempt &&
        physical.Header.LogicalExpiresAtUnixSeconds ==
            receipt.LogicalExpiresAtUnixSeconds &&
        physical.Header.CreatedAtUnixSeconds == receipt.AcceptedAtUnixSeconds;

    private static bool TrySelectBoundedReceiptTail(
        IReadOnlyList<ParsedReceipt> receipts,
        out ParsedReceipt? terminal,
        out ParsedReceipt? immediate)
    {
        terminal = null;
        immediate = null;
        var groups = receipts.GroupBy(
                item => item.Physical.Header.ObjectIdentity,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal);
        if (groups.Values.Any(group => group.Length != 1))
        {
            return false;
        }

        var byIdentity = groups.ToDictionary(
            pair => pair.Key,
            pair => pair.Value[0],
            StringComparer.Ordinal);
        var referenced = new HashSet<string>(
            receipts.Select(item =>
                    item.Receipt.PreviousAcceptanceReceiptIdentity)
                .Where(value => value is not null)!,
            StringComparer.Ordinal);
        var terminals = receipts.Where(item =>
                !referenced.Contains(item.Physical.Header.ObjectIdentity))
            .ToArray();
        if (terminals.Length != 1)
        {
            return false;
        }

        terminal = terminals[0];
        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            terminal.Physical.Header.ObjectIdentity,
        };
        var predecessorIdentity =
            terminal.Receipt.PreviousAcceptanceReceiptIdentity;
        var successor = terminal;
        if (predecessorIdentity is not null)
        {
            if (!byIdentity.TryGetValue(predecessorIdentity, out immediate))
            {
                return false;
            }

            if (!ReceiptContinues(successor, immediate))
            {
                return false;
            }

            visited.Add(predecessorIdentity);
            successor = immediate;
            predecessorIdentity =
                immediate.Receipt.PreviousAcceptanceReceiptIdentity;
        }

        while (predecessorIdentity is not null &&
            byIdentity.TryGetValue(predecessorIdentity, out var older))
        {
            if (!visited.Add(predecessorIdentity))
            {
                return false;
            }

            if (!ReceiptContinues(successor, older))
            {
                return false;
            }

            successor = older;
            predecessorIdentity = older.Receipt.PreviousAcceptanceReceiptIdentity;
        }

        return visited.Count == receipts.Count;
    }

    private static bool ReceiptContinues(
        ParsedReceipt successor,
        ParsedReceipt predecessor) =>
        StringComparer.Ordinal.Equals(
            successor.Receipt.PreviousAcceptanceReceiptIdentity,
            predecessor.Physical.Header.ObjectIdentity) &&
        StringComparer.Ordinal.Equals(
            successor.Receipt.PreviousLogicalGenerationIdentity,
            predecessor.Receipt.LogicalGenerationIdentity);

    private static bool TrySelectGeneration(
        ParsedReceipt accepted,
        IReadOnlyList<ParsedCandidate> candidates,
        long requiredWindow,
        out SelectedAcceptedGeneration? selected)
    {
        selected = null;
        var logical = candidates.Where(candidate =>
                StringComparer.Ordinal.Equals(
                    candidate.LogicalIdentity,
                    accepted.Receipt.LogicalGenerationIdentity) &&
                StringComparer.Ordinal.Equals(
                    candidate.Physical.Header.PredecessorIdentity,
                    accepted.Receipt.PreviousAcceptanceReceiptIdentity))
            .ToArray();
        var originals = logical.Where(candidate => candidate.IsOriginal)
            .ToArray();
        if (originals.Length > 1 || logical.Length == 0)
        {
            return false;
        }

        foreach (var candidate in logical)
        {
            if (candidate.Copy is not null &&
                (!StringComparer.Ordinal.Equals(
                    candidate.Copy.OriginalCandidateObjectIdentity,
                    accepted.Receipt.OriginalCandidateObjectIdentity) ||
                originals.Length == 1 &&
                    !SourceMatchesOriginal(
                        candidate.Copy,
                        originals[0].Physical)))
            {
                return false;
            }
        }

        var copies = logical
            .Where(candidate => candidate.Copy is not null)
            .Select(candidate => candidate.Copy!)
            .ToArray();
        if (originals.Length == 0 &&
            copies.Skip(1).Any(copy =>
                !SourceMatchesCopy(copies[0], copy)))
        {
            return false;
        }

        if (originals.Length == 1 &&
            !StringComparer.Ordinal.Equals(
                originals[0].Physical.Header.ObjectIdentity,
                accepted.Receipt.OriginalCandidateObjectIdentity))
        {
            return false;
        }

        if (logical.Any(candidate =>
            !candidate.CanonicalGeneration.AsSpan().SequenceEqual(
                logical[0].CanonicalGeneration.AsSpan()) ||
            !StringComparer.Ordinal.Equals(
                candidate.Generation.PublicationPayloadSha256,
                accepted.Receipt.PublicationPayloadSha256) ||
            !StringComparer.Ordinal.Equals(
                candidate.Generation.ProducerHeadSha,
                accepted.Receipt.ReviewedHeadSha) ||
            !StringComparer.Ordinal.Equals(
                candidate.Generation.PreviousLogicalGenerationIdentity,
                accepted.Receipt.PreviousLogicalGenerationIdentity)))
        {
            return false;
        }

        var eligible = logical.Where(candidate =>
                candidate.Physical.Metadata.ExpiresAtUnixSeconds >=
                    requiredWindow &&
                AcceptedStateRecordValidation.IsCanonicalPositiveDecimal(
                    candidate.Physical.Metadata.Reference.ObjectId.Value))
            .OrderBy(candidate => ulong.Parse(
                candidate.Physical.Metadata.Reference.ObjectId.Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture))
            .ToArray();
        if (eligible.Length == 0)
        {
            return false;
        }

        var winner = eligible[0];
        selected = new SelectedAcceptedGeneration(
            winner.Physical,
            winner.Generation,
            accepted.Receipt,
            accepted.Physical,
            winner.LogicalIdentity,
            accepted.Receipt.OriginalCandidateObjectIdentity);
        return true;
    }

    private static bool SourceMatchesOriginal(
        AcceptedStatePhysicalCopyV1 copy,
        AuthenticatedStateObject original) =>
        StringComparer.Ordinal.Equals(
            copy.SourceArtifactId,
            original.Metadata.Reference.ObjectId.Value) &&
        StringComparer.Ordinal.Equals(
            copy.SourceArchiveSha256,
            original.Metadata.ArchiveDigest.Sha256) &&
        StringComparer.Ordinal.Equals(
            copy.SourceEncryptedEnvelopeSha256,
            original.Metadata.EncryptedObjectDigest.Sha256);

    private static bool SourceMatchesCopy(
        AcceptedStatePhysicalCopyV1 expected,
        AcceptedStatePhysicalCopyV1 actual) =>
        StringComparer.Ordinal.Equals(
            expected.OriginalCandidateObjectIdentity,
            actual.OriginalCandidateObjectIdentity) &&
        StringComparer.Ordinal.Equals(
            expected.SourceArtifactId,
            actual.SourceArtifactId) &&
        AcceptedStateRecordValidation.FixedDigest(
            expected.SourceArchiveSha256,
            actual.SourceArchiveSha256) &&
        AcceptedStateRecordValidation.FixedDigest(
            expected.SourceEncryptedEnvelopeSha256,
            actual.SourceEncryptedEnvelopeSha256);

    private sealed record ParsedCandidate(
        AuthenticatedStateObject Physical,
        StateGenerationRecordV1 Generation,
        ImmutableArray<byte> CanonicalGeneration,
        string LogicalIdentity,
        bool IsOriginal,
        AcceptedStatePhysicalCopyV1? Copy);

    private sealed record ParsedReceipt(
        AuthenticatedStateObject Physical,
        AcceptanceReceiptV1 Receipt);

    internal sealed class AuthorizedInitialLineageAbsence
    {
        private readonly AuthorizedLocatorAccess authority;

        internal AuthorizedInitialLineageAbsence(
            object issuer,
            AuthorizedLocatorAccess authority,
            string repositoryId,
            string baseScopeDigest,
            string inventoryDigest,
            string currentKeyId,
            string producingRunIdentity,
            long producingRunAttempt,
            long requiredLogicalExpiresAtUnixSeconds,
            long requiredPlatformExpiresAtUnixSeconds)
        {
            if (!ReferenceEquals(issuer, CapabilityIssuer))
            {
                throw new InvalidOperationException(
                    "Only accepted-state selection may mint absence authority.");
            }

            this.authority = authority;
            RepositoryId = repositoryId;
            BaseScopeDigest = baseScopeDigest;
            InventoryDigest = inventoryDigest;
            CurrentKeyId = currentKeyId;
            ProducingRunIdentity = producingRunIdentity;
            ProducingRunAttempt = producingRunAttempt;
            RequiredLogicalExpiresAtUnixSeconds =
                requiredLogicalExpiresAtUnixSeconds;
            RequiredPlatformExpiresAtUnixSeconds =
                requiredPlatformExpiresAtUnixSeconds;
        }

        internal string RepositoryId { get; }
        internal string BaseScopeDigest { get; }
        internal string InventoryDigest { get; }
        internal string CurrentKeyId { get; }
        internal string ProducingRunIdentity { get; }
        internal long ProducingRunAttempt { get; }
        internal long RequiredLogicalExpiresAtUnixSeconds { get; }
        internal long RequiredPlatformExpiresAtUnixSeconds { get; }

        internal bool Allows(
            AuthorizedLocatorAccess access,
            LineageResolveRequest request,
            LineageReadOnlyObservationContext observation) =>
            ReferenceEquals(authority, access) &&
            authority.Allows(access, RepositoryId) &&
            StringComparer.Ordinal.Equals(
                RepositoryId,
                request.BaseScope.RepositoryId) &&
            StringComparer.Ordinal.Equals(
                BaseScopeDigest,
                observation.BaseScopeDigest) &&
            StringComparer.Ordinal.Equals(
                InventoryDigest,
                observation.InventoryDigest) &&
            StringComparer.Ordinal.Equals(
                CurrentKeyId,
                observation.CurrentKeyId) &&
            StringComparer.Ordinal.Equals(
                ProducingRunIdentity,
                request.ProducingRunIdentity) &&
            ProducingRunAttempt == request.ProducingRunAttempt &&
            RequiredLogicalExpiresAtUnixSeconds ==
                request.RequiredLogicalExpiresAtUnixSeconds &&
            RequiredPlatformExpiresAtUnixSeconds ==
                observation.RequiredPlatformExpiresAtUnixSeconds &&
            observation.Selection.IsAbsent &&
            observation.Snapshot is
            {
                PhysicalCount: 0,
                Authenticated.IsEmpty: true,
                UnderRetained.IsEmpty: true,
                Unknown.IsEmpty: true,
            };

        public override string ToString() =>
            nameof(AuthorizedInitialLineageAbsence);
    }

    internal sealed class AcceptedStateExpiryCandidate
    {
        private readonly AuthorizedLocatorAccess authority;

        internal AcceptedStateExpiryCandidate(
            object issuer,
            AuthorizedLocatorAccess authority,
            string repositoryId,
            string baseScopeDigest,
            string inventoryDigest,
            string currentKeyId,
            string selectedHeadIdentity,
            string terminalReceiptIdentity,
            string originalCandidateIdentity,
            string logicalGenerationIdentity,
            string? immediatePredecessorReference,
            string? immediatePredecessorIdentity,
            long logicalExpiresAtUnixSeconds,
            string producingRunIdentity,
            long producingRunAttempt)
        {
            if (!ReferenceEquals(issuer, CapabilityIssuer))
            {
                throw new InvalidOperationException(
                    "Only accepted-state selection may identify expiry.");
            }

            this.authority = authority;
            RepositoryId = repositoryId;
            BaseScopeDigest = baseScopeDigest;
            InventoryDigest = inventoryDigest;
            CurrentKeyId = currentKeyId;
            SelectedHeadIdentity = selectedHeadIdentity;
            TerminalReceiptIdentity = terminalReceiptIdentity;
            OriginalCandidateIdentity = originalCandidateIdentity;
            LogicalGenerationIdentity = logicalGenerationIdentity;
            ImmediatePredecessorReference = immediatePredecessorReference;
            ImmediatePredecessorIdentity = immediatePredecessorIdentity;
            LogicalExpiresAtUnixSeconds = logicalExpiresAtUnixSeconds;
            ProducingRunIdentity = producingRunIdentity;
            ProducingRunAttempt = producingRunAttempt;
        }

        internal string RepositoryId { get; }
        internal string BaseScopeDigest { get; }
        internal string InventoryDigest { get; }
        internal string CurrentKeyId { get; }
        internal string SelectedHeadIdentity { get; }
        internal string TerminalReceiptIdentity { get; }
        internal string OriginalCandidateIdentity { get; }
        internal string LogicalGenerationIdentity { get; }
        internal string? ImmediatePredecessorReference { get; }
        internal string? ImmediatePredecessorIdentity { get; }
        internal long LogicalExpiresAtUnixSeconds { get; }
        internal string ProducingRunIdentity { get; }
        internal long ProducingRunAttempt { get; }

        internal bool TryAuthorize(
            AcceptedStateContext? validated,
            out AuthorizedAcceptedStateExpiry? expiry)
        {
            expiry = null;
            if (validated is null ||
                !validated.AllowsExpiry(
                    LogicalGenerationIdentity,
                    SelectedHeadIdentity))
            {
                return false;
            }

            expiry = new AuthorizedAcceptedStateExpiry(
                CapabilityIssuer,
                this,
                validated);
            return true;
        }

        internal bool Allows(
            AuthorizedLocatorAccess access,
            LineageResolveRequest request,
            LineageReadOnlyObservationContext observation) =>
            ReferenceEquals(authority, access) &&
            authority.Allows(access, RepositoryId) &&
            StringComparer.Ordinal.Equals(
                RepositoryId,
                request.BaseScope.RepositoryId) &&
            StringComparer.Ordinal.Equals(
                BaseScopeDigest,
                observation.BaseScopeDigest) &&
            StringComparer.Ordinal.Equals(
                InventoryDigest,
                observation.InventoryDigest) &&
            StringComparer.Ordinal.Equals(
                CurrentKeyId,
                observation.CurrentKeyId) &&
            StringComparer.Ordinal.Equals(
                ProducingRunIdentity,
                request.ProducingRunIdentity) &&
            ProducingRunAttempt == request.ProducingRunAttempt &&
            observation.Selection.Selection is { } selected &&
            StringComparer.Ordinal.Equals(
                selected.Head.Header.ObjectIdentity,
                SelectedHeadIdentity);

        public override string ToString() =>
            nameof(AcceptedStateExpiryCandidate);
    }

    internal sealed class AuthorizedAcceptedStateExpiry
    {
        private readonly AcceptedStateExpiryCandidate candidate;
        private readonly AcceptedStateContext validated;

        internal AuthorizedAcceptedStateExpiry(
            object issuer,
            AcceptedStateExpiryCandidate candidate,
            AcceptedStateContext validated)
        {
            if (!ReferenceEquals(issuer, CapabilityIssuer))
            {
                throw new InvalidOperationException(
                    "Only admitted accepted state may authorize expiry.");
            }

            this.candidate = candidate;
            this.validated = validated;
        }

        internal string TerminalReceiptIdentity =>
            candidate.TerminalReceiptIdentity;
        internal string OriginalCandidateIdentity =>
            candidate.OriginalCandidateIdentity;
        internal string LogicalGenerationIdentity =>
            candidate.LogicalGenerationIdentity;
        internal string? ImmediatePredecessorReference =>
            candidate.ImmediatePredecessorReference;
        internal string? ImmediatePredecessorIdentity =>
            candidate.ImmediatePredecessorIdentity;
        internal long LogicalExpiresAtUnixSeconds =>
            candidate.LogicalExpiresAtUnixSeconds;

        internal bool Allows(
            AuthorizedLocatorAccess access,
            LineageResolveRequest request,
            LineageReadOnlyObservationContext observation) =>
            validated.AllowsExpiry(
                candidate.LogicalGenerationIdentity,
                candidate.SelectedHeadIdentity) &&
            candidate.Allows(access, request, observation);

        public override string ToString() =>
            nameof(AuthorizedAcceptedStateExpiry);
    }
}
