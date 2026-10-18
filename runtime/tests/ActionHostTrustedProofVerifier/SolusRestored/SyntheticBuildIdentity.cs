namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// These are synthetic Host authorization facts, matching the native V2 fixture.
// The driver independently records the actual tested APR commit and packages.
internal static class TrustedProofPayloadBuildIdentity
{
    internal const string SourceCommit = "1111111111111111111111111111111111111111";
    internal const string SourceTree = "2222222222222222222222222222222222222222";
}
