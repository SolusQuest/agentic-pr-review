namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal sealed record SyntheticTransactionPartitionBinding(
    string PayloadSourceCommit,
    string PayloadSourceTree,
    string PayloadSha256,
    string VerifierSha256)
{
    internal static SyntheticTransactionPartitionBinding? TryCreate(
        IReadOnlyDictionary<string, string> values)
    {
        static bool Hex(string value, int length) =>
            value.Length == length && value.All(static character =>
                character is >= '0' and <= '9' or >= 'a' and <= 'f');

        return values.TryGetValue("payload-source-commit", out var commit) &&
            values.TryGetValue("payload-source-tree", out var tree) &&
            values.TryGetValue("production-payload-sha256", out var payload) &&
            values.TryGetValue("verifier-sha256", out var verifier) &&
            Hex(commit, 40) && Hex(tree, 40) &&
            Hex(payload, 64) && Hex(verifier, 64)
                ? new(commit, tree, payload, verifier)
                : null;
    }
}
