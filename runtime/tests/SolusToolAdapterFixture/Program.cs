using AgenticPrReview.Runtime.SolusToolAdapterFixture;

try
{
    Proof.Check(OperatingSystem.IsLinux() && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture ==
        System.Runtime.InteropServices.Architecture.X64);
    Proof.Check(!Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(key =>
        key.Equals("GITHUB_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("GH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.Equals("NUGET_AUTH_TOKEN", StringComparison.OrdinalIgnoreCase) ||
        key.StartsWith("NuGetPackageSourceCredentials_", StringComparison.OrdinalIgnoreCase)));
    await ToolTests.Run();
    await RuntimeTests.Run();
    await TransportTests.Run();
    Console.WriteLine($"APR_SOLUS_TOOLS_PROOF tools=5 terminal=true correction_variants=21 checks={Proof.Checks} credential_free=true outbound_http=0");
    return 0;
}
catch
{
    // A static stage identifier only: never print exception, model/tool/context
    // bytes, paths, keys or controlled transport bodies.
    Console.Error.WriteLine("APR_SOLUS_TOOLS_FAILED stage=" + Proof.Stage);
    return 1;
}
