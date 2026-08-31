using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceOracle;

public static class OracleCaptureAdmission
{
    public static void Validate(CaptureManifestDocument manifest)
    {
        var success = manifest.Disposition == "success-candidate";
        var recovery = manifest.Disposition == "recovery-only";
        if (manifest.Kind != "apr-r4-e3-capture-manifest-v1" ||
            manifest.OperationIds.Length != 2 ||
            manifest.OperationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
            (!success && !recovery) ||
            (success && manifest.ExpectedRoles.Length != 4) ||
            (recovery && manifest.ExpectedRoles.Length > 4) ||
            manifest.ExpectedRoles.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() !=
                manifest.ExpectedRoles.Length ||
            manifest.ExpectedRoles.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() !=
                manifest.ExpectedRoles.Length ||
            manifest.ObservedRuns.Length > 64 ||
            manifest.ObservedRuns.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() !=
                manifest.ObservedRuns.Length ||
            manifest.ObservedRuns.Any(item =>
                !manifest.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                item.Scope is not ("normal" or "stale") ||
                item.Ownership is not ("operation-owned" or "ownership-ambiguous") ||
                !PositiveDecimal(item.RunId) || !PositiveDecimal(item.RunAttempt)) ||
            (success && manifest.ObservedRuns.Any(item => item.RunAttempt != "1")) ||
            (success && manifest.ExpectedRoles.GroupBy(item => item.OperationId, StringComparer.Ordinal)
                .Any(group => group.Count() != 2 ||
                    group.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Count() != 1)) ||
            manifest.ExpectedRoles.Any(role => !manifest.ObservedRuns.Any(run =>
                run.OperationId == role.OperationId && run.Scope == role.Scope &&
                run.RunId == role.RunId && run.RunAttempt == role.RunAttempt &&
                run.Ownership == "operation-owned")) ||
            manifest.Sources.Length == 0 ||
            (success && manifest.Artifacts.Length == 0) ||
            manifest.Artifacts.Length > EvidenceLimits.MaximumRecords ||
            manifest.Artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() !=
                manifest.Artifacts.Length ||
            manifest.Artifacts.Any(item =>
                !PositiveDecimal(item.ArtifactId) ||
                !PositiveDecimal(item.ProducingRunId) ||
                !PositiveDecimal(item.ProducingRunAttempt)) ||
            manifest.Artifacts.Select(item => item.ArtifactName)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Artifacts.Length)
        {
            throw new InvalidDataException("capture_manifest_invalid");
        }
    }

    private static bool PositiveDecimal(string value) =>
        value.Length > 0 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        long.TryParse(value, out var parsed) && parsed > 0;
}
