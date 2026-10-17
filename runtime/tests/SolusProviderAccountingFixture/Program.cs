try
{
    foreach (var key in new[] { "GITHUB_TOKEN", "GH_TOKEN", "NUGET_AUTH_TOKEN", "NuGetPackageSourceCredentials_solus" })
        F.Check(Environment.GetEnvironmentVariable(key) is null, "credential_free_start");
    F.Stage = "provider";
    await ProviderBridgeTests.Run();
    F.Stage = "accounting";
    await AccountingBridgeTests.Run();
    F.Stage = "budget";
    await RuntimeBudgetTests.Run();
    F.Stage = "supplemental_retry";
    await SupplementalRetryTests.Run();
    Console.WriteLine($"APR_SOLUS_PROVIDER_PROOF checks={F.Checks} controlled_sends={F.Sends} live_calls=0 credential_free=true candidate_gate=true");
    return 0;
}
catch (Exception exception)
{
    // Only authored closed case IDs, never provider bodies or raw exceptions.
    var code = exception is ProofFailure failure ? failure.Code : "unexpected_" + F.Stage;
    Console.Error.WriteLine("APR_SOLUS_PROVIDER_PROOF_FAILED " + code);
    return 1;
}
