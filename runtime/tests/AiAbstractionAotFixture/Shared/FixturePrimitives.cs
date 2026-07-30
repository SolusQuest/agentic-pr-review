using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal sealed class FixtureFailure(string code) : Exception(code)
{
    internal string Code { get; } = code;
}

internal static class FixtureHash
{
    internal static string Text(string value) => Bytes(Encoding.UTF8.GetBytes(value));

    internal static string Bytes(ReadOnlySpan<byte> value) =>
        Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
}

internal static class FixtureConstants
{
    internal const int ProofFormat = 1;
    internal const int MaxToolResultBytes = 4096;
    internal const int MaxContinuationBytes = 512;
    internal const string ReadableContinuation = "synthetic-readable-reasoning-continuation-81";
    internal const string OpaqueContinuation = "synthetic-opaque-continuation-81-4f9a2d";
    internal const string ReasoningFraming = "assistant_reasoning";
    internal const string ReadCallId = "call-read-001";
    internal const string SearchCallId = "call-search-002";
    internal const string FinishCallId = "call-finish-003";
    internal const string ResumeFinishCallId = "call-finish-resume-004";
    internal const string PrefixIdentity = "apr-ai-proof-prefix-81";
    internal const string TerminalSummary = "Synthetic review completed with no findings.";
    internal const string ResumeTerminalSummary = "Synthetic resumed review completed with no findings.";
}
