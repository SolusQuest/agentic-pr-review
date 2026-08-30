using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofVerifier;

internal static class TrustedProofVerifierControl
{
    internal static async Task<int> RunAsync(string[] args)
    {
        if (!OperatingSystem.IsLinux() ||
            !TrustedProofControlCoordinates.TryReadEnvironment(
                Environment.GetEnvironmentVariable,
                out var coordinates,
                out var token) ||
            coordinates is null || token is null)
        {
            return 1;
        }

        var scenarioRoot = Directory.GetCurrentDirectory();
        var requestBudget = new TrustedProofControlRequestBudget();
        try
        {
            return await TrustedProofControlService.RunAsync(
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
                                coordinates.PayloadSha256)),
                        requestBudget),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            requestBudget.WriteReceipt(Console.Error);
        }
    }
}
