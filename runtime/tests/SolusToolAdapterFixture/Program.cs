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
    Proof.Check(RuntimeTests.RestoredNullBatches == 4);
    Proof.Check(Proof.MaxRequest <= 65536 && Proof.MaxContext <= 196608 && Proof.MaxWireRequest <= 1048576 &&
        Proof.MaxResult == 4096 && Proof.MaxSchema <= 8192 && Proof.MaxProspectiveRequest > 0);
    Console.WriteLine($"APR_SOLUS_TOOLS_PROOF tools=5 terminal=true correction_variants=21 restored_null_batches={RuntimeTests.RestoredNullBatches} checks={Proof.Checks} credential_free=true outbound_http=0 max_request={Proof.MaxRequest} max_prospective_request={Proof.MaxProspectiveRequest} max_wire_request={Proof.MaxWireRequest} max_context={Proof.MaxContext} max_result={Proof.MaxResult} max_schema={Proof.MaxSchema}");
    return 0;
}
catch
{
    // A static stage identifier only: never print exception, model/tool/context
    // bytes, paths, keys or controlled transport bodies.
    Console.Error.WriteLine("APR_SOLUS_TOOLS_FAILED stage=" + Proof.Stage);
    return 1;
}
