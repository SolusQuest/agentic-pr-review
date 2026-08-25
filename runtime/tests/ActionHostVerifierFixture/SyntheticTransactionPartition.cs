using System.Text.Json.Nodes;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class SyntheticTransactionPartition
{
    internal static JsonObject? Derive(
        string root,
        SyntheticTransactionPartitionBinding binding) =>
        SyntheticSemanticTransactionPartition.Derive(root, binding);

    internal static JsonObject? Derive(
        string root,
        SyntheticTransactionPartitionBinding binding,
        string stateKey) =>
        SyntheticSemanticTransactionPartition.Derive(root, binding, stateKey);
}
