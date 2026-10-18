namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal sealed class ProofFailure(string code) : Exception
{
    internal string Code { get; } = code;
}
internal static class Proof
{
    internal static int Checks, Sends;
    internal static string Stage = "startup";
    internal static void Require(bool condition, string code)
    { Interlocked.Increment(ref Checks); if (!condition) throw new ProofFailure(code); }
    internal static readonly string[] DecisiveFacts = [
        "    public static string? Find(string key) => null;",
        "    public static string Label(string key) => Lookup.Find(key).Trim();",
        "export const config = { timeoutMs: 0 }; // zero disables the timeout",
        "export const timeout = config.timeoutMs || 3000;",
        "Never log an upload access token, including during failure handling.",
        "    public static void Failed(string accessToken) => Log(accessToken);",
        "    private static void Log(string value) => System.Console.Error.WriteLine(value);",
    ];
    internal static string[] RequiredFacts(string id) => id switch {
        "cs-defect" or "sticky-only" => DecisiveFacts[..2],
        "cs-safe" => [DecisiveFacts[0], "    public static string Label(string key) => Lookup.Find(key)?.Trim() ?? string.Empty;"],
        "ts-defect" => DecisiveFacts[2..4],
        "ts-safe" => [DecisiveFacts[2], "export const timeout = config.timeoutMs ?? 3000;"],
        "repository-rule" => DecisiveFacts[4..], _ => [],
    };
}
