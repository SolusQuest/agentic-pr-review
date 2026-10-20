namespace AgenticPrReview.Runtime.Host.State;

internal static class SolusRestrictedStateAdmission
{
    internal static bool IsSupported(SolusRestrictedStateCompatibility? value) =>
        value is not null &&
        value.PackageVersion == SolusRestrictedStateFormat.PackageVersion &&
        value.Profile == SolusRestrictedStateFormat.Profile &&
        value.Mode == SolusRestrictedStateFormat.CompletedCandidateMode &&
        value.ImplementationId == SolusRestrictedStateFormat.Implementation &&
        value.FormatVersion == SolusRestrictedStateFormat.ContextFormat &&
        value.CompatibilityVersion == SolusRestrictedStateFormat.ContextCompatibility &&
        value.PackageClosureSha256 == SolusRestrictedStateFormat.PackageClosureSha256 &&
        RestrictedStateValidation.IsLowerHex(value.RuntimeConfigurationSha256, 64) &&
        RestrictedStateValidation.IsLowerHex(value.ProviderConfigurationSha256, 64);

    internal static bool IsValidCheckpoint(SolusRestrictedStateCheckpoint? value) =>
        value is not null && value.CheckpointId != Guid.Empty &&
        value.LogicalWorkId != Guid.Empty && value.RoundId != Guid.Empty &&
        value.ExecutionId != Guid.Empty;
}
