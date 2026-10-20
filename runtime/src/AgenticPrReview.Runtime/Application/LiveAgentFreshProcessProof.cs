namespace AgenticPrReview.Runtime;

internal interface ILiveAgentFreshProcessProof
{
    int RequestCount { get; }

    string? SecondProcessFirstRequestSha256 { get; }

    bool IsSatisfiedBy(string? terminalSha256);
}

internal sealed record LiveAgentFreshProcessTransportProof(
    bool Succeeded,
    int RequestCount,
    string? SecondProcessFirstRequestSha256,
    string? PriorFactSha256,
    string? ExpectedTerminalSha256) : ILiveAgentFreshProcessProof
{
    public bool IsSatisfiedBy(string? terminalSha256) =>
        Succeeded &&
        LiveAgentFreshProcessDomain.IsSha256(ExpectedTerminalSha256) &&
        StringComparer.Ordinal.Equals(
            ExpectedTerminalSha256,
            terminalSha256);
}

