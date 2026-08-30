using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record ProducerOutcomeObservation(
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds);

/// <summary>
/// The durable identity of one enrollment producer mutation.  A transport failure after a
/// mutation is sent is never permission to send it again: recovery may only discover one run
/// with this exact, pre-recorded identity.
/// </summary>
public sealed record ProducerMutationIntent(
    string Role,
    string OperationId,
    string ExpectedEvent,
    string ExpectedFixtureHeadSha,
    string ExpectedFixtureHeadRef,
    string ExpectedFixtureRepository,
    string ExpectedPullRequestNumber,
    string RunAttempt,
    long CreatedNotBeforeUnixMilliseconds,
    string ExpectedDisplayTitle);

public sealed record ProducerTargetAuthority(
    string AuthorityId,
    string OperationId,
    string Role,
    string Scope,
    string Producer,
    string ExpectedEvent,
    string TargetDescriptorSha256,
    string ExpectedHeadSha = "",
    string ExpectedHeadBranch = "",
    string ExpectedPullRequestNumber = "",
    string ExpectedFixtureHeadSha = "",
    string ExpectedFixtureHeadRef = "",
    string ExpectedFixtureRepository = "",
    string ExpectedFixtureBaseSha = "",
    string TargetKind = "trigger",
    string RequiredReadbackPhase = "",
    string RequiredSourcePrefix = "",
    string[]? RequiredPreconditionPhases = null,
    string[]? RequiredPreconditionSourcePrefixes = null);

public sealed record ProducerAuthorityDocument(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string MaterializerSourceSha256,
    string MaterializerBuildSha256,
    string Repository,
    string[] OperationIds,
    ProducerTargetAuthority[] Targets,
    long CreatedUnixMilliseconds,
    bool Finalized);

public sealed record ProducerOutcomeEntry(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string OperationId,
    string AuthorityId,
    int Sequence,
    string? PredecessorSha256,
    int Attempt,
    string Outcome,
    ProducerOutcomeObservation Observation,
    string[] ReconciliationSourceIds,
    string? CommittedRunId,
    bool Finalized,
    ProducerMutationIntent? MutationIntent = null);

public sealed record ProducerJournalExpectedRole(
    string Role,
    string OperationId,
    string Scope,
    string RunId,
    string RunAttempt,
    string[] ProducerSourceIds);

public sealed record ProducerJournalObservedRun(
    string OperationId,
    string Scope,
    string RunId,
    string RunAttempt,
    string Ownership);

public sealed record ProducerDiscoverySource(
    string SourceId,
    string Route,
    int Page,
    int Status,
    string BodyPath,
    string BodySha256,
    string BodySize,
    string BodyPhysicalIdentitySha256,
    string SafeHeadersSha256,
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds,
    string? NextRoute);

public sealed record ProducerJournalCheckpointDocument(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string AuthoritySha256,
    int EntryCount,
    string EntryHeadSha256,
    bool Finalized);

public sealed record ProducerJournalCheckpoint(
    ProducerJournalCheckpointDocument Document,
    string Sha256);

/// <summary>Authenticated run-list facts extracted from an enrollment phase fragment.</summary>
public sealed record ProducerEnrollmentDiscoveryRun(
    string RunId,
    string RunAttempt,
    string Event,
    string Status,
    string? Conclusion,
    string WorkflowHeadSha,
    string WorkflowHeadBranch,
    string[] PullRequestNumbers,
    string Repository,
    string WorkflowPath,
    long CreatedUnixMilliseconds,
    string DisplayTitle = "");

public sealed record ProducerOutcomeJournalSealDocument(
    string Kind,
    string DestinationIdentitySha256,
    string ExecutionAuthorizationSha256,
    string AuthoritySha256,
    string AuthorityPhysicalIdentitySha256,
    string[] OperationIds,
    string[] EntrySha256s,
    ProducerDiscoverySource[] DiscoverySources,
    ProducerJournalExpectedRole[] DerivedRoles,
    ProducerJournalObservedRun[] ObservedRuns,
    string Disposition,
    bool Finalized);

public sealed record ProducerOutcomeJournalSeal(
    ProducerOutcomeJournalSealDocument Document,
    string Sha256,
    string PhysicalIdentitySha256);

public sealed class ProducerOutcomeJournal
{
    public const string AuthorityKind = "apr-r4-e3-producer-authority-v1";
    public const string EntryKind = "apr-r4-e3-producer-outcome-v1";
    public const string SealKind = "apr-r4-e3-producer-outcome-journal-seal-v1";
    public const string CheckpointKind = "apr-r4-e3-producer-outcome-journal-checkpoint-v1";
    public const string AuthorityName = "authority.json";
    private static readonly HashSet<string> WorkflowRunStatuses = new(StringComparer.Ordinal)
    {
        "completed",
        "in_progress",
        "pending",
        "queued",
        "requested",
        "waiting",
    };
    private static readonly HashSet<string> WorkflowRunConclusions = new(StringComparer.Ordinal)
    {
        "action_required",
        "cancelled",
        "failure",
        "neutral",
        "skipped",
        "stale",
        "startup_failure",
        "success",
        "timed_out",
    };
    public const string SealName = "seal.json";
    private static readonly string[] Outcomes =
    [
        "before-dispatch",
        "committed",
        "not-committed",
        "outcome-unknown",
        "reconciled-committed",
        "reconciled-not-committed",
    ];
    private readonly RestrictedEvidenceRoot root;
    private readonly string directoryName;
    private readonly ProducerAuthorityDocument authority;
    private readonly string authoritySha256;
    private readonly string authorityPhysicalIdentitySha256;
    private readonly List<ProducerOutcomeEntry> entries;
    private readonly List<string> digests;

    private ProducerOutcomeJournal(
        RestrictedEvidenceRoot root,
        string directoryName,
        ProducerAuthorityDocument authority,
        string authoritySha256,
        string authorityPhysicalIdentitySha256,
        List<ProducerOutcomeEntry> entries,
        List<string> digests)
    {
        this.root = root;
        this.directoryName = directoryName;
        this.authority = authority;
        this.authoritySha256 = authoritySha256;
        this.authorityPhysicalIdentitySha256 = authorityPhysicalIdentitySha256;
        this.entries = entries;
        this.digests = digests;
    }

    public static ProducerOutcomeJournal CreateNew(
        RestrictedEvidenceRoot root,
        string directoryName,
        string executionAuthorizationSha256,
        string materializerSourceSha256,
        string materializerBuildSha256,
        string repository,
        string[] operationIds,
        ProducerTargetAuthority[] targets,
        long createdUnixMilliseconds)
    {
        ValidateCoordinates(directoryName, operationIds);
        var authority = new ProducerAuthorityDocument(
            AuthorityKind,
            root.DestinationIdentitySha256,
            executionAuthorizationSha256,
            materializerSourceSha256,
            materializerBuildSha256,
            repository,
            [.. operationIds],
            [.. targets],
            createdUnixMilliseconds,
            Finalized: true);
        ValidateAuthority(authority, root.DestinationIdentitySha256);
        root.CreateExclusiveDirectory(directoryName);
        var bytes = CanonicalEvidence.Encode(authority, EvidenceJson.Options);
        try
        {
            var identity = root.WritePinnedFileCreateNew($"{directoryName}/{AuthorityName}", bytes);
            return new ProducerOutcomeJournal(
                root,
                directoryName,
                authority,
                CanonicalEvidence.Sha256(bytes),
                identity,
                [],
                []);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static ProducerOutcomeJournal Open(
        RestrictedEvidenceRoot root,
        string directoryName,
        string executionAuthorizationSha256)
    {
        if (!RestrictedEvidenceRoot.IsSinglePathSegment(directoryName) ||
            !Sha256(executionAuthorizationSha256))
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        var absolute = RestrictedEvidenceRoot.ResolveChildPath(root.Path, directoryName);
        var directory = new DirectoryInfo(absolute);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        using var authorityLease = root.AcquirePinnedFile(
            $"{directoryName}/{AuthorityName}",
            EvidenceLimits.MaximumDocumentBytes);
        var authority = ParseCanonical<ProducerAuthorityDocument>(authorityLease.Bytes);
        ValidateAuthority(authority, root.DestinationIdentitySha256);
        if (authority.ExecutionAuthorizationSha256 != executionAuthorizationSha256)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        var names = Directory.EnumerateFiles(absolute)
            .Select(System.IO.Path.GetFileName)
            .Where(name => name!.StartsWith("entry-", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!names.SequenceEqual(
                Enumerable.Range(1, names.Length).Select(index => $"entry-{index:D4}.json"),
                StringComparer.Ordinal))
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        var entries = new List<ProducerOutcomeEntry>();
        var digests = new List<string>();
        foreach (var name in names)
        {
            using var lease = root.AcquirePinnedFile(
                $"{directoryName}/{name}",
                EvidenceLimits.MaximumDocumentBytes);
            entries.Add(ParseCanonical<ProducerOutcomeEntry>(lease.Bytes));
            digests.Add(CanonicalEvidence.Sha256(lease.Bytes));
        }
        ValidateEntries(entries, digests, authority);
        return new ProducerOutcomeJournal(
            root,
            directoryName,
            authority,
            CanonicalEvidence.Sha256(authorityLease.Bytes),
            authorityLease.Identity,
            entries,
            digests);
    }

    public IReadOnlyList<ProducerOutcomeEntry> Entries => entries;
    public ProducerAuthorityDocument Authority => authority;

    /// <summary>
    /// Binds an enrollment-selected run to the producer action which made it possible.
    /// This deliberately consumes only already authenticated journal and fragment facts; it
    /// never treats a caller supplied run id as authority.
    /// </summary>
    public void ValidateEnrollmentRoleRunBinding(
        string role,
        string operationId,
        string selectedRunId,
        string expectedEvent,
        long discoveryRequestStartedUnixMilliseconds,
        ProducerJournalCheckpointDocument discoveryCheckpoint,
        string discoveryCheckpointSha256,
        IReadOnlyList<ProducerEnrollmentDiscoveryRun> discoveryRuns)
    {
        var target = authority.Targets.SingleOrDefault(item => item.TargetKind == "trigger" &&
            item.Role == role && item.OperationId == operationId && item.ExpectedEvent == expectedEvent);
        if (target is null || !PositiveDecimal(selectedRunId) || !ValidEnrollmentTarget(target, authority.Repository))
        {
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        }
        ValidateCheckpoint(discoveryCheckpoint, discoveryCheckpointSha256);
        var terminal = entries.LastOrDefault(item => item.AuthorityId == target.AuthorityId);
        if (terminal is null || terminal.Outcome is not ("committed" or "reconciled-committed") ||
            discoveryCheckpoint.EntryCount < terminal.Sequence ||
            discoveryRequestStartedUnixMilliseconds < terminal.Observation.ResponseReceivedUnixMilliseconds)
        {
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        }
        var before = entries.LastOrDefault(item => item.AuthorityId == target.AuthorityId &&
            item.Outcome == "before-dispatch");
        if (before is null)
        {
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        }
        if (terminal.Attempt != before.Attempt)
        {
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        }
        var intent = terminal.MutationIntent ??
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        var candidates = discoveryRuns.Where(run =>
            MatchesEnrollmentMutationIntent(run, target, intent) &&
            run.Status == "completed" && run.Conclusion == "success" &&
            InMutationDiscoveryWindow(run.CreatedUnixMilliseconds, intent,
                discoveryRequestStartedUnixMilliseconds))
            .ToArray();
        if (candidates.Length != 1 || candidates[0].RunId != selectedRunId ||
            (terminal.CommittedRunId is not null && terminal.CommittedRunId != selectedRunId))
        {
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        }
    }

    public void ValidateEnrollmentFragmentCheckpoint(
        string role,
        string operationId,
        ProducerJournalCheckpointDocument checkpoint,
        string checkpointSha256)
    {
        var target = authority.Targets.SingleOrDefault(item => item.TargetKind == "trigger" &&
            item.Role == role && item.OperationId == operationId);
        var terminal = target is null ? null : entries.LastOrDefault(item => item.AuthorityId == target.AuthorityId);
        ValidateCheckpoint(checkpoint, checkpointSha256);
        if (terminal is null || terminal.Outcome is not ("committed" or "reconciled-committed") ||
            checkpoint.EntryCount < terminal.Sequence)
        {
            throw new InvalidDataException("producer_enrollment_binding_invalid");
        }
    }

    public ProducerJournalCheckpoint CurrentCheckpoint() => CheckpointAt(digests.Count);

    /// <summary>
    /// Returns the immutable prefix checkpoint ending at a particular journal entry.  Consumers
    /// use this when a later, independent producer action must not invalidate a receipt for an
    /// already authenticated role.
    /// </summary>
    public ProducerJournalCheckpoint CheckpointAt(int entryCount)
    {
        if (entryCount < 0 || entryCount > digests.Count)
        {
            throw new InvalidDataException("producer_journal_checkpoint_invalid");
        }
        var document = new ProducerJournalCheckpointDocument(
            CheckpointKind,
            root.DestinationIdentitySha256,
            authority.ExecutionAuthorizationSha256,
            authoritySha256,
            entryCount,
            entryCount == 0 ? authoritySha256 : digests[entryCount - 1],
            Finalized: true);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            return new ProducerJournalCheckpoint(document, CanonicalEvidence.Sha256(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void ValidateCheckpoint(ProducerJournalCheckpointDocument document, string sha256)
    {
        if (document.Kind != CheckpointKind || !document.Finalized ||
            document.DestinationIdentitySha256 != root.DestinationIdentitySha256 ||
            document.ExecutionAuthorizationSha256 != authority.ExecutionAuthorizationSha256 ||
            document.AuthoritySha256 != authoritySha256 ||
            document.EntryCount < 0 || document.EntryCount > digests.Count ||
            document.EntryHeadSha256 !=
                (document.EntryCount == 0 ? authoritySha256 : digests[document.EntryCount - 1]))
        {
            throw new InvalidDataException("producer_journal_checkpoint_invalid");
        }
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            if (CanonicalEvidence.Sha256(bytes) != sha256)
            {
                throw new InvalidDataException("producer_journal_checkpoint_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void AppendCreateNew(
        string authorityId,
        int attempt,
        string outcome,
        long requestStartedUnixMilliseconds,
        long responseReceivedUnixMilliseconds,
        string[] reconciliationSourceIds,
        string? committedRunId = null,
        ProducerMutationIntent? mutationIntent = null)
    {
        if (EvidenceFileHandle.PathEntryExists(
                RestrictedEvidenceRoot.ResolveChildPath(
                    RestrictedEvidenceRoot.ResolveChildPath(root.Path, directoryName),
                    SealName)))
        {
            throw new InvalidDataException("producer_journal_sealed");
        }
        var target = authority.Targets.SingleOrDefault(item => item.AuthorityId == authorityId) ??
            throw new InvalidDataException("producer_journal_invalid");
        // The journal, not a retrying caller, owns the durable intent.  Explicit callers must
        // agree with this value; the small compatibility path below lets existing in-process
        // journal tests exercise the same persisted invariant without hand-building it.
        var prior = entries.LastOrDefault(item => item.AuthorityId == authorityId);
        var effectiveIntent = mutationIntent;
        if (target.TargetKind == "trigger" && ValidEnrollmentTarget(target, authority.Repository))
        {
            effectiveIntent ??= outcome == "before-dispatch"
                ? CreateEnrollmentMutationIntent(target, requestStartedUnixMilliseconds)
                : prior?.MutationIntent;
        }
        var entry = new ProducerOutcomeEntry(
            EntryKind,
            root.DestinationIdentitySha256,
            authority.ExecutionAuthorizationSha256,
            target.OperationId,
            authorityId,
            entries.Count + 1,
            digests.Count == 0 ? null : digests[^1],
            attempt,
            outcome,
            new ProducerOutcomeObservation(
                requestStartedUnixMilliseconds,
                responseReceivedUnixMilliseconds),
            reconciliationSourceIds,
            committedRunId,
            Finalized: true,
            effectiveIntent);
        var proposedEntries = entries.Append(entry).ToArray();
        var bytes = CanonicalEvidence.Encode(entry, EvidenceJson.Options);
        var digest = CanonicalEvidence.Sha256(bytes);
        try
        {
            ValidateEntries(
                proposedEntries,
                digests.Append(digest).ToArray(),
                authority);
            root.WritePinnedFileCreateNew($"{directoryName}/entry-{entry.Sequence:D4}.json", bytes);
            entries.Add(entry);
            digests.Add(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ProducerOutcomeJournalSeal SealCreateNew(ProducerDiscoverySource[] discoverySources)
    {
        var document = BuildSealDocument(discoverySources);
        var bytes = CanonicalEvidence.Encode(document, EvidenceJson.Options);
        try
        {
            var identity = root.WritePinnedFileCreateNew($"{directoryName}/{SealName}", bytes);
            return new ProducerOutcomeJournalSeal(document, CanonicalEvidence.Sha256(bytes), identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public ProducerOutcomeJournalSeal ReadSeal()
    {
        using var lease = root.AcquirePinnedFile(
            $"{directoryName}/{SealName}",
            EvidenceLimits.MaximumDocumentBytes);
        var document = ParseCanonical<ProducerOutcomeJournalSealDocument>(lease.Bytes);
        var expected = BuildSealDocument(document.DiscoverySources);
        var expectedBytes = CanonicalEvidence.Encode(expected, EvidenceJson.Options);
        try
        {
            if (!lease.Bytes.AsSpan().SequenceEqual(expectedBytes))
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
        }
        return new ProducerOutcomeJournalSeal(
            document,
            CanonicalEvidence.Sha256(lease.Bytes),
            lease.Identity);
    }

    public static void ValidateCapture(
        RestrictedEvidenceRoot root,
        CaptureManifestDocument capture)
    {
        var journal = Open(
            root,
            capture.ProducerJournalDirectory,
            capture.ExecutionAuthorizationSha256);
        var seal = journal.ReadSeal();
        if (seal.Sha256 != capture.ProducerJournalSealSha256 ||
            seal.PhysicalIdentitySha256 != capture.ProducerJournalSealFileIdentity ||
            seal.Document.Disposition != capture.Disposition ||
            !seal.Document.OperationIds.SequenceEqual(capture.OperationIds, StringComparer.Ordinal) ||
            seal.Document.DerivedRoles.Length != capture.ExpectedRoles.Length ||
            seal.Document.ObservedRuns.Length != capture.ObservedRuns.Length)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        for (var index = 0; index < capture.ExpectedRoles.Length; index++)
        {
            var expected = seal.Document.DerivedRoles[index];
            var actual = capture.ExpectedRoles[index];
            if (expected.Role != actual.Role || expected.OperationId != actual.OperationId ||
                expected.Scope != actual.Scope || expected.RunId != actual.RunId ||
                expected.RunAttempt != actual.RunAttempt ||
                !expected.ProducerSourceIds.SequenceEqual(actual.ProducerSourceIds, StringComparer.Ordinal))
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
        }
        for (var index = 0; index < capture.ObservedRuns.Length; index++)
        {
            var expected = seal.Document.ObservedRuns[index];
            var actual = capture.ObservedRuns[index];
            if (expected.OperationId != actual.OperationId || expected.Scope != actual.Scope ||
                expected.RunId != actual.RunId || expected.RunAttempt != actual.RunAttempt ||
                expected.Ownership != actual.Ownership)
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
        }
    }

    private ProducerOutcomeJournalSealDocument BuildSealDocument(
        IReadOnlyList<ProducerDiscoverySource> discoverySources)
    {
        ValidateEntries(entries, digests, authority);
        var discovered = ReadDiscoveryRuns(discoverySources);
        var terminal = authority.Targets.Select(target =>
            entries.LastOrDefault(entry => entry.AuthorityId == target.AuthorityId)).ToArray();
        var committed = terminal
            .Where(entry => entry is not null &&
                (entry.Outcome is "committed" or "reconciled-committed"))
            .Select(entry => entry!)
            .ToDictionary(entry => entry.AuthorityId, StringComparer.Ordinal);
        var order = new[]
        {
            "normal-bootstrap",
            "normal-continuation",
            "stale-protected",
            "stale-follow-on",
        };
        var triggerTargets = authority.Targets
            .Where(target => target.TargetKind == "trigger")
            .ToArray();
        var windows = triggerTargets.Select((target, index) =>
        {
            var before = entries.LastOrDefault(entry =>
                entry.AuthorityId == target.AuthorityId && entry.Outcome == "before-dispatch");
            var nextBefore = triggerTargets.Skip(index + 1)
                .Select(next => entries.FirstOrDefault(entry =>
                    entry.AuthorityId == next.AuthorityId && entry.Outcome == "before-dispatch"))
                .FirstOrDefault(entry => entry is not null);
            return new
            {
                Target = target,
                Start = before?.Observation.RequestStartedUnixMilliseconds,
                End = nextBefore?.Observation.RequestStartedUnixMilliseconds,
            };
        }).ToArray();
        var mapped = new List<(ProducerTargetAuthority Target, DiscoveredRun Run)>();
        foreach (var run in discovered)
        {
            var candidates = windows.Where(candidate =>
                candidate.Start is not null &&
                run.CreatedUnixMilliseconds + 999 >= candidate.Start.Value &&
                (candidate.Target.ExpectedEvent != "workflow_dispatch" ||
                    committed.GetValueOrDefault(candidate.Target.AuthorityId)?.CommittedRunId is not null) &&
                (committed.GetValueOrDefault(candidate.Target.AuthorityId)?.CommittedRunId is null ||
                    committed[candidate.Target.AuthorityId].CommittedRunId == run.RunId) &&
                (ValidEnrollmentTarget(candidate.Target, authority.Repository) ||
                    candidate.End is null || run.CreatedUnixMilliseconds < candidate.End.Value) &&
                RunMatchesTarget(run, candidate.Target)).ToArray();
            // Enrollment roles are separated by their immutable fixture head/ref, rather than a
            // later role's mutation start.  In particular, a bootstrap workflow may appear only
            // after continuation has already been dispatched; a next-before cut-off would
            // silently lose that valid run.  Multiple exact candidates remain fail-closed.
            if (candidates.Length == 1)
            {
                mapped.Add((candidates[0].Target, run));
            }
        }
        var candidateCounts = mapped
            .GroupBy(item => item.Target.AuthorityId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var runs = mapped
            .Select(item => new ProducerJournalObservedRun(
                item.Target.OperationId,
                item.Target.Scope,
                item.Run.RunId,
                item.Run.RunAttempt,
                committed.ContainsKey(item.Target.AuthorityId) &&
                    candidateCounts[item.Target.AuthorityId] == 1
                    ? "operation-owned"
                    : "ownership-ambiguous"))
            .ToArray();
        var producerSourceIds = discoverySources.Select(item => item.SourceId).ToArray();
        var roles = windows.Select(window =>
            {
                var candidates = mapped.Where(item =>
                    item.Target.AuthorityId == window.Target.AuthorityId &&
                    item.Run.Event == window.Target.ExpectedEvent).ToArray();
                return committed.ContainsKey(window.Target.AuthorityId) && candidates.Length == 1 &&
                    candidates[0].Run.RunAttempt == "1" &&
                    candidates[0].Run.Status == "completed" &&
                    candidates[0].Run.Conclusion == "success"
                    ? new ProducerJournalExpectedRole(
                        window.Target.Role,
                        window.Target.OperationId,
                        window.Target.Scope,
                        candidates[0].Run.RunId,
                        candidates[0].Run.RunAttempt,
                        producerSourceIds)
                    : null;
            })
            .Where(item => item is not null)
            .Select(item => item!)
            .OrderBy(item => Array.IndexOf(order, item.Role))
            .ToArray();
        var success = roles.Length == order.Length &&
            roles.Select(item => item.Role).SequenceEqual(order, StringComparer.Ordinal) &&
            roles.All(item => item.RunAttempt == "1") &&
            roles.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() == roles.Length &&
            runs.Length == roles.Length &&
            terminal.All(entry => entry is not null &&
                entry.Outcome is "committed" or "reconciled-committed") &&
            mapped.All(item => item.Run.Event == item.Target.ExpectedEvent);
        return new ProducerOutcomeJournalSealDocument(
            SealKind,
            root.DestinationIdentitySha256,
            authority.ExecutionAuthorizationSha256,
            authoritySha256,
            authorityPhysicalIdentitySha256,
            authority.OperationIds,
            [.. digests],
            [.. discoverySources],
            roles,
            runs,
            success ? "success-candidate" : "recovery-only",
            Finalized: true);
    }

    private DiscoveredRun[] ReadDiscoveryRuns(
        IReadOnlyList<ProducerDiscoverySource> discoverySources)
    {
        if (discoverySources.Count == 0 || discoverySources.Count > EvidenceLimits.MaximumPages ||
            discoverySources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() !=
                discoverySources.Count ||
            discoverySources.Select(item => item.BodyPath).Distinct(StringComparer.Ordinal).Count() !=
                discoverySources.Count)
        {
            throw new InvalidDataException("producer_discovery_invalid");
        }
        var runs = new List<DiscoveredRun>();
        var discoveryEndpoint =
            $"/repos/{authority.Repository}/actions/workflows/r4-trusted-proof.yml/runs";
        for (var index = 0; index < discoverySources.Count; index++)
        {
            var source = discoverySources[index];
            var expectedNext = index + 1 < discoverySources.Count
                ? discoverySources[index + 1].Route
                : null;
            if (source.SourceId != $"producer-discovery-final:page:{index + 1}" ||
                source.Page != index + 1 || source.Status != 200 ||
                !source.BodyPath.StartsWith($"{directoryName}/discovery-final-page-", StringComparison.Ordinal) ||
                source.BodyPath != $"{directoryName}/discovery-final-page-{index + 1:D4}.json" ||
                !Sha256(source.BodySha256) || !Sha256(source.BodyPhysicalIdentitySha256) ||
                !Sha256(source.SafeHeadersSha256) ||
                !long.TryParse(source.BodySize, out var bodySize) || bodySize < 1 ||
                source.RequestStartedUnixMilliseconds < authority.CreatedUnixMilliseconds ||
                source.ResponseReceivedUnixMilliseconds < source.RequestStartedUnixMilliseconds ||
                source.NextRoute != expectedNext ||
                (index == 0
                    ? source.Route != $"{discoveryEndpoint}?per_page=100"
                    : source.Route != discoverySources[index - 1].NextRoute))
            {
                throw new InvalidDataException("producer_discovery_invalid");
            }
            using var lease = root.AcquirePinnedFile(source.BodyPath, EvidenceLimits.MaximumDocumentBytes);
            if (lease.Bytes.Length != bodySize || CanonicalEvidence.Sha256(lease.Bytes) != source.BodySha256 ||
                lease.Identity != source.BodyPhysicalIdentitySha256)
            {
                throw new InvalidDataException("producer_discovery_invalid");
            }
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(lease.Bytes);
                var rootElement = document.RootElement;
                var workflowRuns = rootElement.GetProperty("workflow_runs");
                if (workflowRuns.ValueKind != System.Text.Json.JsonValueKind.Array)
                {
                    throw new InvalidDataException("producer_discovery_invalid");
                }
                foreach (var run in workflowRuns.EnumerateArray())
                {
                    var runId = run.GetProperty("id").GetRawText();
                    var runAttempt = run.GetProperty("run_attempt").GetRawText();
                    var runEvent = run.GetProperty("event").GetString() ?? string.Empty;
                    var status = run.GetProperty("status").GetString() ?? string.Empty;
                    var conclusion = run.GetProperty("conclusion").ValueKind ==
                        System.Text.Json.JsonValueKind.Null
                            ? null
                            : run.GetProperty("conclusion").GetString();
                    var headSha = run.TryGetProperty("head_sha", out var headShaElement)
                        ? headShaElement.GetString() ?? string.Empty
                        : string.Empty;
                    var headBranch = run.TryGetProperty("head_branch", out var headBranchElement)
                        ? headBranchElement.GetString() ?? string.Empty
                        : string.Empty;
                    var pullRequestNumbers = run.TryGetProperty("pull_requests", out var pulls) &&
                        pulls.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? pulls.EnumerateArray().Select(item =>
                            item.GetProperty("number").GetRawText()).ToArray()
                        : [];
                    var repository = run.GetProperty("head_repository").GetProperty("full_name")
                        .GetString() ?? string.Empty;
                    var workflowPath = run.GetProperty("path").GetString() ?? string.Empty;
                    var displayTitle = run.TryGetProperty("display_title", out var displayTitleElement)
                        ? displayTitleElement.GetString() ?? string.Empty
                        : string.Empty;
                    var createdText = run.GetProperty("created_at").GetString() ?? string.Empty;
                    if (!PositiveDecimal(runId) || !PositiveDecimal(runAttempt) ||
                        runEvent is not ("workflow_run" or "workflow_dispatch") ||
                        !WorkflowRunStatuses.Contains(status) ||
                        (status == "completed"
                            ? conclusion is null || !WorkflowRunConclusions.Contains(conclusion)
                            : conclusion is not null) ||
                        repository != authority.Repository ||
                        workflowPath != ".github/workflows/r4-trusted-proof.yml" ||
                        !DateTimeOffset.TryParse(
                            createdText,
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.AssumeUniversal |
                                System.Globalization.DateTimeStyles.AdjustToUniversal,
                            out var created))
                    {
                        throw new InvalidDataException("producer_discovery_invalid");
                    }
                    runs.Add(new DiscoveredRun(
                        runId,
                        runAttempt,
                        runEvent,
                        headSha,
                        headBranch,
                        pullRequestNumbers,
                        repository,
                        workflowPath,
                        status,
                        conclusion,
                        created.ToUnixTimeMilliseconds(),
                        displayTitle));
                }
            }
            catch (Exception exception) when (exception is System.Text.Json.JsonException or
                KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new InvalidDataException("producer_discovery_invalid", exception);
            }
        }
        if (runs.Count > EvidenceLimits.MaximumRecords ||
            runs.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() != runs.Count)
        {
            throw new InvalidDataException("producer_discovery_invalid");
        }
        return runs
            .OrderBy(item => item.CreatedUnixMilliseconds)
            .ThenBy(item => item.RunId.Length)
            .ThenBy(item => item.RunId, StringComparer.Ordinal)
            .ToArray();
    }

    private static void ValidateAuthority(
        ProducerAuthorityDocument value,
        string destinationIdentitySha256)
    {
        if (value.Kind != AuthorityKind || !value.Finalized ||
            value.DestinationIdentitySha256 != destinationIdentitySha256 ||
            !Sha256(value.ExecutionAuthorizationSha256) ||
            !Sha256(value.MaterializerSourceSha256) || !Sha256(value.MaterializerBuildSha256) ||
            value.Repository.Split('/').Length != 2 ||
            value.Repository.Split('/').Any(string.IsNullOrWhiteSpace) ||
            value.OperationIds.Length != 2 ||
            value.OperationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
            value.OperationIds.Any(item => !Sha256(item)) || value.Targets.Length == 0 ||
            value.Targets.Select(item => item.AuthorityId).Distinct(StringComparer.Ordinal).Count() !=
                value.Targets.Length ||
            value.Targets.Any(item => !Sha256(item.AuthorityId) ||
                !Sha256(item.TargetDescriptorSha256) ||
                !value.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                string.IsNullOrWhiteSpace(item.Role) || string.IsNullOrWhiteSpace(item.Scope) ||
                string.IsNullOrWhiteSpace(item.Producer) ||
                item.TargetKind is not ("trigger" or "environment-secret" or
                    "authorization-variable" or "deployment-approval" or
                    "proof-control-comment") ||
                (item.TargetKind == "trigger"
                    ? item.ExpectedEvent is not ("workflow_run" or "workflow_dispatch") ||
                        item.RequiredReadbackPhase.Length != 0 ||
                        item.RequiredSourcePrefix.Length != 0
                    : item.ExpectedEvent.Length != 0 ||
                        string.IsNullOrWhiteSpace(item.RequiredReadbackPhase) ||
                        string.IsNullOrWhiteSpace(item.RequiredSourcePrefix)) ||
                (item.ExpectedFixtureHeadSha.Length != 0 && !Hex40(item.ExpectedFixtureHeadSha)) ||
                ((item.ExpectedFixtureHeadRef.Length != 0 || item.ExpectedFixtureRepository.Length != 0 ||
                    item.ExpectedFixtureBaseSha.Length != 0) && !ValidEnrollmentTarget(item, value.Repository)) ||
                ((item.RequiredPreconditionPhases is null) !=
                    (item.RequiredPreconditionSourcePrefixes is null)) ||
                item.RequiredPreconditionPhases is { } phases &&
                    (item.RequiredPreconditionSourcePrefixes is null ||
                        phases.Length == 0 ||
                        phases.Length != item.RequiredPreconditionSourcePrefixes.Length ||
                        phases.Any(string.IsNullOrWhiteSpace) ||
                        item.RequiredPreconditionSourcePrefixes.Any(string.IsNullOrWhiteSpace))) ||
            value.CreatedUnixMilliseconds < 0)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
    }

    private static void ValidateEntries(
        IReadOnlyList<ProducerOutcomeEntry> entries,
        IReadOnlyList<string> digests,
        ProducerAuthorityDocument authority)
    {
        if (entries.Count != digests.Count)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        if (entries.Where(item => item.CommittedRunId is not null)
                .Select(item => item.CommittedRunId)
                .Distinct(StringComparer.Ordinal).Count() !=
            entries.Count(item => item.CommittedRunId is not null))
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        var byTarget = new Dictionary<string, ProducerOutcomeEntry>(StringComparer.Ordinal);
        var fourRoleTargets = FourRoleTriggerTargets(authority);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var target = authority.Targets.SingleOrDefault(item => item.AuthorityId == entry.AuthorityId);
            var prior = byTarget.GetValueOrDefault(entry.AuthorityId);
            if (target is null || entry.Kind != EntryKind || !entry.Finalized ||
                entry.DestinationIdentitySha256 != authority.DestinationIdentitySha256 ||
                entry.ExecutionAuthorizationSha256 != authority.ExecutionAuthorizationSha256 ||
                entry.OperationId != target.OperationId ||
                entry.Sequence != index + 1 ||
                entry.PredecessorSha256 != (index == 0 ? null : digests[index - 1]) ||
                entry.Attempt < 1 || !Outcomes.Contains(entry.Outcome, StringComparer.Ordinal) ||
                entry.Observation.RequestStartedUnixMilliseconds < 0 ||
                entry.Observation.ResponseReceivedUnixMilliseconds <
                    entry.Observation.RequestStartedUnixMilliseconds ||
                entry.ReconciliationSourceIds.Distinct(StringComparer.Ordinal).Count() !=
                    entry.ReconciliationSourceIds.Length ||
                entry.ReconciliationSourceIds.Any(item => !Sha256(item)) ||
                (target.ExpectedEvent == "workflow_dispatch" && entry.Outcome == "committed"
                    ? !PositiveDecimal(entry.CommittedRunId ?? string.Empty)
                    : entry.CommittedRunId is not null &&
                        !(target.TargetKind == "trigger" &&
                            ValidEnrollmentTarget(target, authority.Repository) &&
                            entry.Outcome == "reconciled-committed" &&
                            PositiveDecimal(entry.CommittedRunId))) ||
                !ValidTransition(prior, entry))
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
            if (target.TargetKind == "trigger" && ValidEnrollmentTarget(target, authority.Repository))
            {
                // Each production enrollment transition retains the same pre-mutation intent.
                // This makes recovery auditable and prevents a later caller from widening the
                // candidate set or retrying an uncertain live write.
                if (entry.MutationIntent is null ||
                    !ValidMutationIntent(entry.MutationIntent, target) ||
                    (entry.Outcome == "before-dispatch"
                        ? entry.MutationIntent.CreatedNotBeforeUnixMilliseconds !=
                            entry.Observation.RequestStartedUnixMilliseconds
                        : prior?.MutationIntent is null ||
                            entry.MutationIntent != prior.MutationIntent) ||
                    (entry.Outcome == "committed" && target.ExpectedEvent == "workflow_dispatch" &&
                        !PositiveDecimal(entry.CommittedRunId ?? string.Empty)) ||
                    (entry.Outcome == "committed" && target.ExpectedEvent == "workflow_run" &&
                        entry.CommittedRunId is not null) ||
                    (entry.Outcome == "reconciled-committed" &&
                        !PositiveDecimal(entry.CommittedRunId ?? string.Empty)) ||
                    (entry.Outcome is not ("committed" or "reconciled-committed") &&
                        entry.CommittedRunId is not null))
                {
                    throw new InvalidDataException("producer_journal_invalid");
                }
            }
            else if (entry.MutationIntent is not null)
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
            if (fourRoleTargets is not null && target.TargetKind == "trigger")
            {
                var targetIndex = Array.FindIndex(fourRoleTargets, item =>
                    item.AuthorityId == target.AuthorityId);
                var bootstrap = byTarget.GetValueOrDefault(fourRoleTargets[0].AuthorityId);
                var continuation = byTarget.GetValueOrDefault(fourRoleTargets[1].AuthorityId);
                var staleProtected = byTarget.GetValueOrDefault(fourRoleTargets[2].AuthorityId);
                Func<ProducerOutcomeEntry?, bool> committed = static entry => entry is not null &&
                    entry.Outcome is "committed" or "reconciled-committed";
                // Continuation waits for bootstrap's authenticated producer commit, not the
                // workflow-run terminal receipt; the workflow runs themselves still provide the
                // shared-concurrency serialization proof. Stale work cannot begin until
                // both normal producers are committed; the ref advance then follows the
                // independently authenticated stale-protected receipt in the materializer.
                if (targetIndex < 0 ||
                    (targetIndex == 1 && !committed(bootstrap)) ||
                    (targetIndex == 2 && (!committed(bootstrap) || !committed(continuation))) ||
                    (targetIndex == 3 && !committed(staleProtected)))
                {
                    throw new InvalidDataException("producer_journal_order_invalid");
                }
            }
            byTarget[entry.AuthorityId] = entry;
        }
    }

    private static ProducerTargetAuthority[]? FourRoleTriggerTargets(ProducerAuthorityDocument authority)
    {
        var targets = authority.Targets.Where(item => item.TargetKind == "trigger").ToArray();
        var roles = new[]
        {
            "normal-bootstrap", "normal-continuation", "stale-protected", "stale-follow-on",
        };
        return targets.Length == roles.Length && targets.Select(item => item.Role)
                .SequenceEqual(roles, StringComparer.Ordinal)
            ? targets
            : null;
    }

    /// <summary>
    /// Validates the fixed production enrollment target topology.  Generic journal consumers may
    /// model a single producer for unit tests; the live authorization reader calls this boundary
    /// before it creates the journal authority.
    /// </summary>
    public static void ValidateEnrollmentTargets(
        string repository,
        IReadOnlyList<string> operationIds,
        IReadOnlyList<ProducerTargetAuthority> targets)
    {
        var roles = new[]
        {
            "normal-bootstrap", "normal-continuation", "stale-protected", "stale-follow-on",
        };
        var triggers = targets.Where(item => item.TargetKind == "trigger").ToArray();
        var enrollment = triggers.Where(item => roles.Contains(item.Role, StringComparer.Ordinal)).ToArray();
        if (enrollment.Length == 0 || triggers.Length != roles.Length ||
            !triggers.Select(item => item.Role).SequenceEqual(roles, StringComparer.Ordinal) ||
            !triggers.All(item => ValidEnrollmentTarget(item, repository)) ||
            triggers[0].OperationId != operationIds[0] ||
            triggers[1].OperationId != operationIds[0] ||
            triggers[2].OperationId != operationIds[1] ||
            triggers[3].OperationId != operationIds[1] ||
            triggers[0].Scope != "normal" || triggers[1].Scope != "normal" ||
            triggers[2].Scope != "stale" || triggers[3].Scope != "stale" ||
            triggers[0].ExpectedEvent != "workflow_run" ||
            triggers[1].ExpectedEvent != "workflow_dispatch" ||
            triggers[2].ExpectedEvent != "workflow_run" ||
            triggers[3].ExpectedEvent != "workflow_run")
        {
            throw new InvalidDataException("producer_enrollment_authority_invalid");
        }
    }

    /// <summary>
    /// Captures the immutable match key before a live enrollment mutation starts.  This is kept
    /// with every terminal transition so an interrupted mutation can be reconciled without a
    /// second dispatch or rerun.
    /// </summary>
    public static ProducerMutationIntent CreateEnrollmentMutationIntent(
        ProducerTargetAuthority target,
        long createdNotBeforeUnixMilliseconds)
    {
        if (!ValidEnrollmentTarget(target, target.ExpectedFixtureRepository) ||
            createdNotBeforeUnixMilliseconds < 0)
        {
            throw new InvalidDataException("producer_mutation_intent_invalid");
        }
        return new ProducerMutationIntent(
            target.Role,
            target.OperationId,
            target.ExpectedEvent,
            target.ExpectedFixtureHeadSha,
            target.ExpectedFixtureHeadRef,
            target.ExpectedFixtureRepository,
            target.ExpectedPullRequestNumber,
            "1",
            createdNotBeforeUnixMilliseconds,
            EnrollmentDisplayTitle(target));
    }

    public static bool MatchesEnrollmentMutationIntent(
        ProducerEnrollmentDiscoveryRun run,
        ProducerTargetAuthority target,
        ProducerMutationIntent intent) =>
        intent.Role == target.Role &&
        intent.OperationId == target.OperationId &&
        intent.ExpectedEvent == target.ExpectedEvent &&
        intent.ExpectedFixtureHeadSha == target.ExpectedFixtureHeadSha &&
        intent.ExpectedFixtureHeadRef == target.ExpectedFixtureHeadRef &&
        intent.ExpectedFixtureRepository == target.ExpectedFixtureRepository &&
        intent.ExpectedPullRequestNumber == target.ExpectedPullRequestNumber &&
        intent.RunAttempt == "1" &&
        intent.ExpectedDisplayTitle == EnrollmentDisplayTitle(target) &&
        run.Event == intent.ExpectedEvent && run.RunAttempt == intent.RunAttempt &&
        run.Repository == intent.ExpectedFixtureRepository &&
        run.WorkflowPath == ".github/workflows/r4-trusted-proof.yml" &&
        // The workflow itself always runs from main. The fixture is deliberately a separate
        // binding: it is carried in the exact run title rather than confused with GitHub's
        // workflow transport head fields.
        run.WorkflowHeadSha == target.ExpectedHeadSha &&
        run.WorkflowHeadBranch == target.ExpectedHeadBranch &&
        run.DisplayTitle == intent.ExpectedDisplayTitle &&
        (intent.ExpectedEvent == "workflow_dispatch"
            ? run.PullRequestNumbers.Length == 0
            : run.PullRequestNumbers.Contains(intent.ExpectedPullRequestNumber, StringComparer.Ordinal));

    public static bool InMutationDiscoveryWindow(
        long runCreatedUnixMilliseconds,
        ProducerMutationIntent intent,
        long discoveryRequestStartedUnixMilliseconds) =>
        runCreatedUnixMilliseconds + 999 >= intent.CreatedNotBeforeUnixMilliseconds &&
        runCreatedUnixMilliseconds <= discoveryRequestStartedUnixMilliseconds;

    public static string? SelectEnrollmentRecoveryRun(
        IEnumerable<ProducerEnrollmentDiscoveryRun> candidates,
        ProducerTargetAuthority target,
        ProducerMutationIntent intent,
        long discoveryRequestStartedUnixMilliseconds)
    {
        var matches = candidates.Where(run =>
                PositiveDecimal(run.RunId) &&
                MatchesEnrollmentMutationIntent(run, target, intent) &&
                InMutationDiscoveryWindow(run.CreatedUnixMilliseconds, intent,
                    discoveryRequestStartedUnixMilliseconds))
            .Select(run => run.RunId)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ValidTransition(ProducerOutcomeEntry? prior, ProducerOutcomeEntry current)
    {
        if (prior is null)
        {
            return current.Attempt == 1 && current.Outcome == "before-dispatch" &&
                current.ReconciliationSourceIds.Length == 0;
        }
        if (prior.Outcome == "before-dispatch")
        {
            return current.Attempt == prior.Attempt &&
                current.Outcome is "committed" or "not-committed" or "outcome-unknown" &&
                (current.Outcome == "outcome-unknown"
                    ? current.ReconciliationSourceIds.Length == 0
                    : current.ReconciliationSourceIds.Length > 0);
        }
        if (prior.Outcome == "outcome-unknown")
        {
            return current.Attempt == prior.Attempt &&
                current.Outcome is "reconciled-committed" or "reconciled-not-committed" &&
                current.ReconciliationSourceIds.Length > 0;
        }
        if (prior.Outcome is "not-committed" or "reconciled-not-committed")
        {
            return current.Attempt == prior.Attempt + 1 && current.Outcome == "before-dispatch" &&
                current.ReconciliationSourceIds.Length == 0;
        }
        return false;
    }

    private static void ValidateCoordinates(string directoryName, IReadOnlyList<string> operationIds)
    {
        if (!RestrictedEvidenceRoot.IsSinglePathSegment(directoryName) || operationIds.Count != 2 ||
            operationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
            operationIds.Any(value => !Sha256(value)))
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
    }

    private static T ParseCanonical<T>(byte[] bytes)
    {
        try
        {
            var value = System.Text.Json.JsonSerializer.Deserialize<T>(bytes, EvidenceJson.Options) ??
                throw new InvalidDataException("producer_journal_invalid");
            var canonical = CanonicalEvidence.Encode(value, EvidenceJson.Options);
            try
            {
                if (!bytes.AsSpan().SequenceEqual(canonical))
                {
                    throw new InvalidDataException("producer_journal_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
            return value;
        }
        catch (System.Text.Json.JsonException)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
    }

    private static bool Sha256(string value) =>
        value.Length == 64 &&
            value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool PositiveDecimal(string value) =>
        value.Length > 0 && value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') && value != "0";

    private static bool Hex40(string value) => value.Length == 40 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidEnrollmentTarget(ProducerTargetAuthority target, string repository) =>
        Hex40(target.ExpectedHeadSha) && target.ExpectedHeadBranch == "main" &&
        PositiveDecimal(target.ExpectedPullRequestNumber) && Hex40(target.ExpectedFixtureHeadSha) &&
        target.ExpectedFixtureHeadRef.StartsWith("r4-trusted-proof/", StringComparison.Ordinal) &&
        target.ExpectedFixtureHeadRef.Length == "r4-trusted-proof/".Length + 64 &&
        Sha256(target.ExpectedFixtureHeadRef["r4-trusted-proof/".Length..]) &&
        target.ExpectedFixtureRepository == repository && Hex40(target.ExpectedFixtureBaseSha) &&
        target.ExpectedFixtureBaseSha == target.ExpectedHeadSha;

    private static bool ValidMutationIntent(
        ProducerMutationIntent intent,
        ProducerTargetAuthority target) =>
        intent.Role == target.Role && intent.OperationId == target.OperationId &&
        intent.ExpectedEvent == target.ExpectedEvent &&
        intent.ExpectedFixtureHeadSha == target.ExpectedFixtureHeadSha &&
        intent.ExpectedFixtureHeadRef == target.ExpectedFixtureHeadRef &&
        intent.ExpectedFixtureRepository == target.ExpectedFixtureRepository &&
        intent.ExpectedPullRequestNumber == target.ExpectedPullRequestNumber &&
        intent.RunAttempt == "1" &&
        intent.ExpectedDisplayTitle == EnrollmentDisplayTitle(target) &&
        intent.CreatedNotBeforeUnixMilliseconds >= 0;

    private static string EnrollmentDisplayTitle(ProducerTargetAuthority target) =>
        target.ExpectedEvent == "workflow_dispatch"
            ? $"apr-r4-e2p-{target.OperationId}"
            : $"apr-r4-e2p-{target.ExpectedFixtureHeadRef}";

    private bool RunMatchesTarget(DiscoveredRun run, ProducerTargetAuthority target)
    {
        if (ValidEnrollmentTarget(target, authority.Repository))
        {
            return run.Event == target.ExpectedEvent && run.RunAttempt == "1" &&
                run.Repository == target.ExpectedFixtureRepository &&
                run.WorkflowPath == ".github/workflows/r4-trusted-proof.yml" &&
                run.HeadSha == target.ExpectedHeadSha &&
                run.HeadBranch == target.ExpectedHeadBranch &&
                run.DisplayTitle == EnrollmentDisplayTitle(target) &&
                (target.ExpectedEvent == "workflow_dispatch"
                    ? run.PullRequestNumbers.Length == 0
                    : run.PullRequestNumbers.Contains(
                        target.ExpectedPullRequestNumber, StringComparer.Ordinal));
        }
        return run.Event == target.ExpectedEvent &&
            run.Repository.Length > 0 &&
            run.WorkflowPath == ".github/workflows/r4-trusted-proof.yml" &&
            (target.ExpectedHeadSha.Length == 0 || run.HeadSha == target.ExpectedHeadSha) &&
            (target.ExpectedHeadBranch.Length == 0 || run.HeadBranch == target.ExpectedHeadBranch) &&
            (target.ExpectedEvent == "workflow_dispatch" || target.ExpectedPullRequestNumber.Length == 0
                ? run.PullRequestNumbers.Length == 0
                : run.PullRequestNumbers.Contains(target.ExpectedPullRequestNumber, StringComparer.Ordinal));
    }

    private sealed record DiscoveredRun(
        string RunId,
        string RunAttempt,
        string Event,
        string HeadSha,
        string HeadBranch,
        string[] PullRequestNumbers,
        string Repository,
        string WorkflowPath,
        string Status,
        string? Conclusion,
        long CreatedUnixMilliseconds,
        string DisplayTitle = "");
}
