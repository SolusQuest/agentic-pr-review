using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public sealed record ProducerOutcomeObservation(
    long RequestStartedUnixMilliseconds,
    long ResponseReceivedUnixMilliseconds);

public sealed record ProducerOutcomeEntry(
    string Kind,
    string DestinationIdentitySha256,
    string[] OperationIds,
    int Sequence,
    string? PredecessorSha256,
    string Producer,
    string Target,
    int Attempt,
    string Outcome,
    ProducerOutcomeObservation Observation,
    string[] ReconciliationSourceIds,
    bool Finalized);

public sealed class ProducerOutcomeJournal
{
    public const string EntryKind = "apr-r4-e3-producer-outcome-v1";
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
    private readonly string[] operationIds;
    private readonly List<ProducerOutcomeEntry> entries;
    private readonly List<string> digests;

    private ProducerOutcomeJournal(
        RestrictedEvidenceRoot root,
        string directoryName,
        string[] operationIds,
        List<ProducerOutcomeEntry> entries,
        List<string> digests)
    {
        this.root = root;
        this.directoryName = directoryName;
        this.operationIds = operationIds;
        this.entries = entries;
        this.digests = digests;
    }

    public static ProducerOutcomeJournal CreateNew(
        RestrictedEvidenceRoot root,
        string directoryName,
        string[] operationIds)
    {
        ValidateCoordinates(directoryName, operationIds);
        root.CreateExclusiveDirectory(directoryName);
        return new ProducerOutcomeJournal(root, directoryName, [.. operationIds], [], []);
    }

    public static ProducerOutcomeJournal Open(
        RestrictedEvidenceRoot root,
        string directoryName,
        string[] operationIds)
    {
        ValidateCoordinates(directoryName, operationIds);
        var absolute = RestrictedEvidenceRoot.ResolveChildPath(root.Path, directoryName);
        var directory = new DirectoryInfo(absolute);
        if (!directory.Exists || directory.LinkTarget is not null ||
            (directory.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        var names = Directory.EnumerateFiles(absolute)
            .Select(System.IO.Path.GetFileName)
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
            try
            {
                var entry = System.Text.Json.JsonSerializer.Deserialize<ProducerOutcomeEntry>(
                    lease.Bytes,
                    EvidenceJson.Options) ?? throw new InvalidDataException("producer_journal_invalid");
                var canonical = CanonicalEvidence.Encode(entry, EvidenceJson.Options);
                try
                {
                    if (!lease.Bytes.AsSpan().SequenceEqual(canonical))
                    {
                        throw new InvalidDataException("producer_journal_invalid");
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(canonical);
                }
                entries.Add(entry);
                digests.Add(CanonicalEvidence.Sha256(lease.Bytes));
            }
            catch (System.Text.Json.JsonException)
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
        }
        ValidateEntries(entries, digests, root.DestinationIdentitySha256, operationIds);
        return new ProducerOutcomeJournal(root, directoryName, [.. operationIds], entries, digests);
    }

    public IReadOnlyList<ProducerOutcomeEntry> Entries => entries;

    public void AppendCreateNew(
        string producer,
        string target,
        int attempt,
        string outcome,
        long requestStartedUnixMilliseconds,
        long responseReceivedUnixMilliseconds,
        string[] reconciliationSourceIds)
    {
        var entry = new ProducerOutcomeEntry(
            EntryKind,
            root.DestinationIdentitySha256,
            [.. operationIds],
            entries.Count + 1,
            digests.Count == 0 ? null : digests[^1],
            producer,
            target,
            attempt,
            outcome,
            new ProducerOutcomeObservation(
                requestStartedUnixMilliseconds,
                responseReceivedUnixMilliseconds),
            reconciliationSourceIds,
            Finalized: true);
        var proposedEntries = entries.Append(entry).ToArray();
        var bytes = CanonicalEvidence.Encode(entry, EvidenceJson.Options);
        var digest = CanonicalEvidence.Sha256(bytes);
        try
        {
            ValidateEntries(
                proposedEntries,
                digests.Append(digest).ToArray(),
                root.DestinationIdentitySha256,
                operationIds);
            root.WritePinnedFileCreateNew($"{directoryName}/entry-{entry.Sequence:D4}.json", bytes);
            entries.Add(entry);
            digests.Add(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static void ValidateEntries(
        IReadOnlyList<ProducerOutcomeEntry> entries,
        IReadOnlyList<string> digests,
        string destinationIdentitySha256,
        IReadOnlyList<string> operationIds)
    {
        if (entries.Count != digests.Count)
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
        var byTarget = new Dictionary<string, ProducerOutcomeEntry>(StringComparer.Ordinal);
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var prior = byTarget.GetValueOrDefault(entry.Target);
            if (entry.Kind != EntryKind || !entry.Finalized ||
                entry.DestinationIdentitySha256 != destinationIdentitySha256 ||
                !entry.OperationIds.SequenceEqual(operationIds, StringComparer.Ordinal) ||
                entry.Sequence != index + 1 ||
                entry.PredecessorSha256 != (index == 0 ? null : digests[index - 1]) ||
                string.IsNullOrWhiteSpace(entry.Producer) || string.IsNullOrWhiteSpace(entry.Target) ||
                entry.Attempt < 1 || !Outcomes.Contains(entry.Outcome, StringComparer.Ordinal) ||
                entry.Observation.RequestStartedUnixMilliseconds < 0 ||
                entry.Observation.ResponseReceivedUnixMilliseconds <
                    entry.Observation.RequestStartedUnixMilliseconds ||
                entry.ReconciliationSourceIds.Distinct(StringComparer.Ordinal).Count() !=
                    entry.ReconciliationSourceIds.Length ||
                entry.ReconciliationSourceIds.Any(source => string.IsNullOrWhiteSpace(source)) ||
                !ValidTransition(prior, entry))
            {
                throw new InvalidDataException("producer_journal_invalid");
            }
            byTarget[entry.Target] = entry;
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
            operationIds.Any(value => value.Length != 64 ||
                value.Any(character => character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
        {
            throw new InvalidDataException("producer_journal_invalid");
        }
    }
}
