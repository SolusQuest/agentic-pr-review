using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record ProducerOutcomeObservation(
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds);

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
    bool Finalized);

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
    string RunAttempt);

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

    public ProducerJournalCheckpoint CurrentCheckpoint()
    {
        var document = new ProducerJournalCheckpointDocument(
            CheckpointKind,
            root.DestinationIdentitySha256,
            authority.ExecutionAuthorizationSha256,
            authoritySha256,
            digests.Count,
            digests.Count == 0 ? authoritySha256 : digests[^1],
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
        string? committedRunId = null)
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
            Finalized: true);
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
                expected.RunId != actual.RunId || expected.RunAttempt != actual.RunAttempt)
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
            var window = windows.SingleOrDefault(candidate =>
                committed.GetValueOrDefault(candidate.Target.AuthorityId)?.CommittedRunId == run.RunId);
            if (window is null)
            {
                window = windows.LastOrDefault(candidate =>
                    candidate.Target.ExpectedEvent != "workflow_dispatch" &&
                    candidate.Start is not null &&
                    run.CreatedUnixMilliseconds + 999 >= candidate.Start.Value);
            }
            if (window is null)
            {
                if (run.CreatedUnixMilliseconds + 999 < authority.CreatedUnixMilliseconds) continue;
                window = windows.FirstOrDefault(candidate =>
                    candidate.Target.ExpectedEvent != "workflow_dispatch");
                if (window is null) continue;
            }
            if (window.End is not null && run.CreatedUnixMilliseconds >= window.End.Value) continue;
            if (!RunMatchesTarget(run, window.Target)) continue;
            mapped.Add((window.Target, run));
        }
        var runs = mapped
            .Select(item => new ProducerJournalObservedRun(
                item.Target.OperationId,
                item.Target.Scope,
                item.Run.RunId,
                item.Run.RunAttempt))
            .ToArray();
        var producerSourceIds = discoverySources.Select(item => item.SourceId).ToArray();
        var roles = windows.Select(window =>
            {
                var candidates = mapped.Where(item =>
                    item.Target.AuthorityId == window.Target.AuthorityId &&
                    item.Run.Event == window.Target.ExpectedEvent).ToArray();
                return committed.ContainsKey(window.Target.AuthorityId) && candidates.Length == 1
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
                    var createdText = run.GetProperty("created_at").GetString() ?? string.Empty;
                    if (!PositiveDecimal(runId) || !PositiveDecimal(runAttempt) ||
                        runEvent is not ("workflow_run" or "workflow_dispatch") ||
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
                        created.ToUnixTimeMilliseconds()));
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
                    : entry.CommittedRunId is not null) ||
                !ValidTransition(prior, entry))
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
            byTarget[entry.AuthorityId] = entry;
        }
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

    private static bool RunMatchesTarget(DiscoveredRun run, ProducerTargetAuthority target) =>
        run.Event == target.ExpectedEvent &&
        run.Repository.Length > 0 &&
        run.WorkflowPath == ".github/workflows/r4-trusted-proof.yml" &&
        (target.ExpectedHeadSha.Length == 0 || run.HeadSha == target.ExpectedHeadSha) &&
        (target.ExpectedHeadBranch.Length == 0 || run.HeadBranch == target.ExpectedHeadBranch) &&
        (target.ExpectedEvent == "workflow_dispatch"
            ? run.PullRequestNumbers.Length == 0
            : target.ExpectedPullRequestNumber.Length == 0 ||
                run.PullRequestNumbers.Contains(target.ExpectedPullRequestNumber, StringComparer.Ordinal));

    private sealed record DiscoveredRun(
        string RunId,
        string RunAttempt,
        string Event,
        string HeadSha,
        string HeadBranch,
        string[] PullRequestNumbers,
        string Repository,
        string WorkflowPath,
        long CreatedUnixMilliseconds);
}
