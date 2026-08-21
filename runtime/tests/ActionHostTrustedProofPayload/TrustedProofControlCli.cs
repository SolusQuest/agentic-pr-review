namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal static class TrustedProofControlCli
{
    internal static Task<int> RunAsync(string[] args) =>
        TrustedProofControlService.RunFromEnvironmentAsync(
            args,
            CancellationToken.None);
}
