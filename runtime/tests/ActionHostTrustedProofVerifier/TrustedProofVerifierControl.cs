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
        if (!TrustedProofRequestBudgetProfile.TrySelectProduction(
                Environment.GetEnvironmentVariable, out var requestBudgetProfile) ||
            requestBudgetProfile is null)
        {
            return 1;
        }

        var scenarioRoot = Directory.GetCurrentDirectory();
        var primaryBucket = FrameworkPrimaryRateLimitBucket.OpenForScenario(
            scenarioRoot);
        var requestBudget = new TrustedProofControlRequestBudget(
            remainingTailGuard: requestBudgetProfile.ControlRemainingTailGuard(args));
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
                                coordinates.PayloadSha256,
                                observePrimaryRemaining:
                                    primaryBucket.ObserveAsync)),
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
