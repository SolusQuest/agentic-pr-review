using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.Publishing.Inline;

internal static class InlineCandidateMapper
{
    internal const string InlineKeyDomain =
        "agentic-pr-review/r4/inline-key/v1";

    private const int CandidateCap = 5;

    internal static InlineCandidateMap Map(
        ActionHostTrustedPolicy policy,
        ImmutableArray<R4FindingIdentityV1> orderedFindings,
        InlineDiffCoordinates coordinates)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(coordinates);
        ValidatePolicy(policy);
        ValidateOrderedFindings(orderedFindings);

        var candidates = ImmutableArray.CreateBuilder<InlineCandidate>();
        var stickyOnly = ImmutableArray.CreateBuilder<InlineStickyOnlyFinding>();
        var noLocationCount = 0;
        var capCount = 0;

        if (policy.PublicationMode == ActionHostPublicationMode.StickyAndInline)
        {
            foreach (var findingIdentity in orderedFindings)
            {
                if (!PolicyIncludes(
                        policy.InlineMinSeverity,
                        findingIdentity.Finding.Severity))
                {
                    continue;
                }

                if (!TryLocate(
                        findingIdentity.Finding,
                        coordinates,
                        out var path,
                        out var line))
                {
                    stickyOnly.Add(new InlineStickyOnlyFinding(
                        findingIdentity,
                        InlineStickyOnlyReason.NoCurrentRightSideLocation));
                    noLocationCount = checked(noLocationCount + 1);
                    continue;
                }

                if (candidates.Count >= CandidateCap)
                {
                    stickyOnly.Add(new InlineStickyOnlyFinding(
                        findingIdentity,
                        InlineStickyOnlyReason.CandidateCap));
                    capCount = checked(capCount + 1);
                    continue;
                }

                var inlineKey = R4CanonicalUtf8Framing.Hash(
                    InlineKeyDomain,
                    [
                        findingIdentity.FingerprintSha256,
                        path,
                        line.ToString(CultureInfo.InvariantCulture),
                    ]);
                candidates.Add(new InlineCandidate(
                    findingIdentity,
                    path,
                    line,
                    inlineKey));
            }
        }

        return new InlineCandidateMap(
            coordinates.ReviewedIdentity,
            policy.PolicySha256,
            coordinates.DiffSha256,
            candidates.ToImmutable(),
            stickyOnly.ToImmutable(),
            new InlineCandidateReasonCounts(noLocationCount, capCount));
    }

    private static bool TryLocate(
        AgentFinding finding,
        InlineDiffCoordinates coordinates,
        out string path,
        out int line)
    {
        foreach (var evidence in finding.Evidence)
        {
            if (coordinates.TryFindFirst(
                    evidence.Path,
                    evidence.StartLine,
                    evidence.EndLine,
                    out line))
            {
                path = evidence.Path;
                return true;
            }
        }

        path = string.Empty;
        line = 0;
        return false;
    }

    private static bool PolicyIncludes(
        ActionHostInlineSeverity minimumSeverity,
        string severity) =>
        minimumSeverity switch
        {
            ActionHostInlineSeverity.High => severity is "critical" or "high",
            ActionHostInlineSeverity.Critical => severity == "critical",
            _ => throw new ArgumentOutOfRangeException(nameof(minimumSeverity)),
        };

    private static void ValidatePolicy(ActionHostTrustedPolicy policy)
    {
        if (policy.PublicationMode is not ActionHostPublicationMode.Sticky and
                not ActionHostPublicationMode.StickyAndInline ||
            policy.InlineMinSeverity is not ActionHostInlineSeverity.High and
                not ActionHostInlineSeverity.Critical ||
            policy.MaximumInlineComments != CandidateCap ||
            !IsLowerHexSha256(policy.PolicySha256))
        {
            throw new ArgumentException(
                "Trusted inline publication policy is invalid.",
                nameof(policy));
        }
    }

    private static void ValidateOrderedFindings(
        ImmutableArray<R4FindingIdentityV1> orderedFindings)
    {
        if (orderedFindings.IsDefault ||
            orderedFindings.Length > AgentLimits.Findings)
        {
            throw new ArgumentException(
                "Ordered finding projection is invalid.",
                nameof(orderedFindings));
        }

        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        R4FindingIdentityV1? previous = null;
        foreach (var identified in orderedFindings)
        {
            if (identified is null ||
                !IsLowerHexSha256(identified.FingerprintSha256) ||
                !fingerprints.Add(identified.FingerprintSha256) ||
                !FindingMappingShapeIsValid(identified.Finding))
            {
                throw new ArgumentException(
                    "Ordered finding projection is invalid.",
                    nameof(orderedFindings));
            }

            if (previous is not null && !IsAfter(previous, identified))
            {
                throw new ArgumentException(
                    "Ordered finding projection is not in P1 order.",
                    nameof(orderedFindings));
            }

            previous = identified;
        }
    }

    private static bool IsAfter(
        R4FindingIdentityV1 previous,
        R4FindingIdentityV1 current)
    {
        var previousRank = SeverityRank(previous.Finding.Severity);
        var currentRank = SeverityRank(current.Finding.Severity);
        return currentRank > previousRank ||
            currentRank == previousRank &&
            StringComparer.Ordinal.Compare(
                current.FingerprintSha256,
                previous.FingerprintSha256) > 0;
    }

    private static bool FindingMappingShapeIsValid(AgentFinding? finding)
    {
        if (finding is null ||
            SeverityRank(finding.Severity) < 0 ||
            finding.Evidence.IsDefault ||
            finding.Evidence.Length is < 1 or > AgentLimits.EvidencePerFinding)
        {
            return false;
        }

        var evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in finding.Evidence)
        {
            if (evidence is null ||
                !IsLowerHexSha256(evidence.ObservationId) ||
                !RepositoryPath.IsValid(evidence.Path) ||
                evidence.StartLine < 1 ||
                evidence.EndLine < evidence.StartLine ||
                !evidenceKeys.Add(string.Concat(
                    evidence.ObservationId,
                    "\0",
                    evidence.Path,
                    "\0",
                    evidence.StartLine.ToString(CultureInfo.InvariantCulture),
                    "\0",
                    evidence.EndLine.ToString(CultureInfo.InvariantCulture))))
            {
                return false;
            }
        }

        return true;
    }

    private static int SeverityRank(string? severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => -1,
    };

    private static bool IsLowerHexSha256(string? value)
    {
        if (value is null || value.Length != 64)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (!(character is >= '0' and <= '9') &&
                !(character is >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
