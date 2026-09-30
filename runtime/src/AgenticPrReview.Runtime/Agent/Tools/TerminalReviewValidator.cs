using System.Text;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Agent.Tools;

internal static class TerminalReviewValidator
{
    private static readonly string[] Severities =
    [
        "critical",
        "high",
        "medium",
        "low",
    ];

    internal static bool TryValidate(
        FinishReviewArguments arguments,
        ReviewedIdentity expectedIdentity,
        IReadOnlyList<AgentObservation> observations,
        out AgentTerminalReview? review) =>
        TryValidate(arguments, expectedIdentity, observations, out review, out _);

    internal static bool TryValidate(
        FinishReviewArguments arguments,
        ReviewedIdentity expectedIdentity,
        IReadOnlyList<AgentObservation> observations,
        out AgentTerminalReview? review,
        out string? reason)
    {
        review = null;
        reason = "terminal_bounds_invalid";
        if (!NonWhitespaceWithin(arguments.Summary, AgentLimits.SummaryBytes) ||
            arguments.Findings.Length > AgentLimits.Findings ||
            arguments.CanonicalBytes.Length > AgentLimits.TerminalBytes)
        {
            return false;
        }

        var findingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var finding in arguments.Findings)
        {
            reason = "finding_fields_invalid";
            if (!Severities.Contains(finding.Severity, StringComparer.Ordinal) ||
                !NonWhitespaceWithin(finding.Title, AgentLimits.FindingTitleBytes) ||
                !NonWhitespaceWithin(finding.Message, AgentLimits.FindingMessageBytes) ||
                finding.Evidence.Length is < 1 or > AgentLimits.EvidencePerFinding)
            {
                return false;
            }

            var evidenceKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var evidence in finding.Evidence)
            {
                reason = "evidence_fields_invalid";
                if (!IsLowerHex(evidence.ObservationId, 64) ||
                    !RepositoryPath.IsValid(evidence.Path) ||
                    evidence.StartLine < 1 ||
                    evidence.EndLine < evidence.StartLine)
                {
                    return false;
                }

                var key = string.Concat(
                    evidence.ObservationId,
                    "\0",
                    evidence.Path,
                    "\0",
                    evidence.StartLine,
                    "\0",
                    evidence.EndLine);
                if (!evidenceKeys.Add(key))
                {
                    reason = "evidence_duplicate";
                    return false;
                }

                var grounded = observations.Any(observation =>
                    observation.Identity == expectedIdentity &&
                    observation.Grounds(evidence));
                if (!grounded)
                {
                    var named = observations.Where(observation =>
                        StringComparer.Ordinal.Equals(observation.ObservationId, evidence.ObservationId)).ToArray();
                    var current = named.Where(observation => observation.Identity == expectedIdentity).ToArray();
                    reason = named.Length == 0 ? "evidence_observation_unknown" :
                        current.Length == 0 ? "evidence_identity_mismatch" :
                        !current.Any(observation => observation.ReturnedLines.ContainsKey(evidence.Path))
                            ? "evidence_path_unobserved" : "evidence_lines_unobserved";
                    return false;
                }
            }

            var findingBytes = AgentToolArguments.WriteFinishReview(
                string.Empty,
                [finding]);
            if (!findingKeys.Add(AgentCanonical.HashRaw(findingBytes)))
            {
                reason = "finding_duplicate";
                return false;
            }
        }

        var terminalSha256 = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            arguments.CanonicalBytes);
        review = new AgentTerminalReview(
            arguments.Summary,
            arguments.Findings,
            terminalSha256,
            arguments.CanonicalBytes);
        reason = null;
        return true;
    }

    // Fixed diagnostic vocabulary only; never carry provider text through this boundary.
    internal static bool IsReason(string? reason) => reason is "arguments_invalid" or
        "terminal_bounds_invalid" or "finding_fields_invalid" or "evidence_fields_invalid" or
        "evidence_duplicate" or "evidence_observation_unknown" or "evidence_identity_mismatch" or
        "evidence_path_unobserved" or "evidence_lines_unobserved" or "finding_duplicate";

    private static bool NonWhitespaceWithin(string value, int maximumBytes)
    {
        if (value.Length == 0 || AgentTextValidation.IsOnlyFixedWhitespace(value))
        {
            return false;
        }

        try
        {
            return new UTF8Encoding(false, true).GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsLowerHex(string value, int length)
    {
        if (value.Length != length)
        {
            return false;
        }

        return value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }
}
