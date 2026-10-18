using System.Text.Json;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Proof.Require(!Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key =>
                key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("NUGET_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("NuGetPackageSourceCredentials_", StringComparison.OrdinalIgnoreCase)), "credential_free");
            if (args is ["portable"])
            {
                await CapacityProbes.Run();
                Console.WriteLine(JsonSerializer.Serialize(new { proof = "v3-portable", checks = Proof.Checks,
                    probes = CapacityProbes.Results, live_calls = 0 }));
            }
            else
            {
                Proof.Require(OperatingSystem.IsLinux(), "host_platform_linux");
                await CapacityHostRun.Run(args);
            }
            return 0;
        }
        // The proof executable reports only fixed codes; raw exceptions can contain private Host inputs.
        catch (Exception error)
        {
            Console.Error.WriteLine("APR_SOLUS_CAPACITY_FAILED " + Proof.Stage + "_" +
                (error is ProofFailure failure ? failure.Code : "unexpected"));
            return 1;
        }
    }
}
