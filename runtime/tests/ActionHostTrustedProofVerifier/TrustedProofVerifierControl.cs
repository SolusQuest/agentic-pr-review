using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofVerifier;

internal static class TrustedProofVerifierControl
{
    internal static Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux() ||
            !TrustedProofControlCoordinates.TryReadEnvironment(
                Environment.GetEnvironmentVariable,
                out var coordinates,
                out var token) ||
            coordinates is null || token is null)
        {
            return Task.FromResult(1);
        }

        var scenarioRoot = Directory.GetCurrentDirectory();
        return TrustedProofControlService.RunAsync(
            args,
            coordinates,
            TrustedProofControlTransport.Create(
                coordinates,
                token,
                new VerifierRecordingHandler(
                    scenarioRoot,
                    "control",
                    new FrameworkGitHubHandler(
                        scenarioRoot,
                        coordinates.PayloadSha256))),
            CancellationToken.None);
    }
}
