using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

internal sealed partial record R4PublicationIdentityV1
{
    internal const string ScopeDomain =
        "agentic-pr-review/r4/publication-scope/v1";
    internal const string BodyDomain = "agentic-pr-review/r4/sticky-body/v1";
    internal const string FindingDomain = "agentic-pr-review/r4/finding/v1";

    private const int ScopeStringMaximumBytes = 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsValidScope(R4PublicationScopeV1 scope) =>
        scope.RepositoryId > 0 &&
        scope.WorkflowSourceRepositoryId > 0 &&
        scope.PullRequestNumber > 0 &&
        IsUtf8Within(scope.WorkflowPath, ScopeStringMaximumBytes) &&
        IsUtf8Within(scope.WorkflowRef, ScopeStringMaximumBytes) &&
        IsLowerHex(scope.PolicyIdentitySha256, 64) &&
        IsUtf8Within(
            scope.ActionContractPayloadIdentity,
            ScopeStringMaximumBytes);

    internal static string ComputeScopeSha256(R4PublicationScopeV1 scope)
    {
        if (!IsValidScope(scope))
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        return R4CanonicalUtf8Framing.Hash(
            ScopeDomain,
            [
                scope.RepositoryId.ToString(CultureInfo.InvariantCulture),
                scope.WorkflowSourceRepositoryId.ToString(
                    CultureInfo.InvariantCulture),
                scope.WorkflowPath,
                scope.WorkflowRef,
                scope.PullRequestNumber.ToString(CultureInfo.InvariantCulture),
                scope.PolicyIdentitySha256,
                scope.ActionContractPayloadIdentity,
            ]);
    }

    internal static string ComputeBodySha256(string body)
    {
        if (body.Contains('\r', StringComparison.Ordinal) ||
            !R4Markdown.TryMeasure(body, out _, out _))
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        return R4CanonicalUtf8Framing.Hash(BodyDomain, [body]);
    }

    internal static string ComputeFindingFingerprint(AgentFinding finding)
    {
        if (!IsValidFinding(finding))
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.ReviewInvalid);
        }

        var fields = new string[3 + finding.Evidence.Length * 4];
        fields[0] = finding.Severity;
        fields[1] = finding.Title;
        fields[2] = finding.Message;
        var offset = 3;
        foreach (var evidence in finding.Evidence)
        {
            fields[offset++] = evidence.ObservationId;
            fields[offset++] = evidence.Path;
            fields[offset++] = evidence.StartLine.ToString(
                CultureInfo.InvariantCulture);
            fields[offset++] = evidence.EndLine.ToString(
                CultureInfo.InvariantCulture);
        }

        return R4CanonicalUtf8Framing.Hash(FindingDomain, fields);
    }

    internal static ImmutableArray<R4FindingIdentityV1> IdentifyAndOrder(
        R4ValidatedPublicationReview publicationReview)
    {
        ArgumentNullException.ThrowIfNull(publicationReview);
        if (!IsValidReview(publicationReview.Review))
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.ReviewInvalid);
        }

        var identified = new List<R4FindingIdentityV1>(
            publicationReview.Review.Findings.Length);
        foreach (var finding in publicationReview.Review.Findings)
        {
            identified.Add(new R4FindingIdentityV1(
                finding,
                ComputeFindingFingerprint(finding)));
        }

        identified.Sort(static (left, right) =>
        {
            var severity = SeverityRank(left.Finding.Severity).CompareTo(
                SeverityRank(right.Finding.Severity));
            return severity != 0
                ? severity
                : StringComparer.Ordinal.Compare(
                    left.FingerprintSha256,
                    right.FingerprintSha256);
        });

        for (var index = 1; index < identified.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(
                    identified[index - 1].FingerprintSha256,
                    identified[index].FingerprintSha256))
            {
                throw new R4PublicationException(
                    R4PublicationFailureCodes.FingerprintDuplicate);
            }
        }

        return identified.ToImmutableArray();
    }

    private static bool IsValidReview(AgentTerminalReview review)
    {
        if (!IsNonWhitespaceUtf8Within(
                review.Summary,
                AgentLimits.SummaryBytes) ||
            review.Findings.IsDefault ||
            review.Findings.Length > AgentLimits.Findings)
        {
            return false;
        }

        foreach (var finding in review.Findings)
        {
            if (!IsValidFinding(finding))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidFinding(AgentFinding finding)
    {
        if (finding is null ||
            SeverityRank(finding.Severity) == int.MaxValue ||
            !IsNonWhitespaceUtf8Within(
                finding.Title,
                AgentLimits.FindingTitleBytes) ||
            !IsNonWhitespaceUtf8Within(
                finding.Message,
                AgentLimits.FindingMessageBytes) ||
            finding.Evidence.IsDefault ||
            finding.Evidence.Length is < 1 or > AgentLimits.EvidencePerFinding)
        {
            return false;
        }

        var evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var evidence in finding.Evidence)
        {
            if (evidence is null ||
                !IsLowerHex(evidence.ObservationId, 64) ||
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

    internal static int SeverityRank(string? severity) => severity switch
    {
        "critical" => 0,
        "high" => 1,
        "medium" => 2,
        "low" => 3,
        _ => int.MaxValue,
    };

    private static bool IsNonWhitespaceUtf8Within(
        string? value,
        int maximumBytes) =>
        value is not null &&
        !string.IsNullOrWhiteSpace(value) &&
        IsUtf8Within(value, maximumBytes);

    private static bool IsUtf8Within(string? value, int maximumBytes)
    {
        if (value is null || value.Length == 0)
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    internal static bool IsLowerHex(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
