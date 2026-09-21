namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Allocation is not usage. No reservation is recycled, even after a cheap or failed child.
internal sealed class EconomicsLedger(EconomicsAllocation ceiling)
{
    private readonly object gate = new();
    private bool sealedLedger;
    private readonly Dictionary<int, EconomicsLease> allocated = [];
    private readonly HashSet<int> receipted = [];
    private EconomicsAllocation total = new(0, 0, 0, 0, 0, 0, 0);

    internal EconomicsAllocation Total { get { lock (gate) return total; } }
    internal EconomicsLease? Reserve(int index, EconomicsAllocation amount)
    {
        lock (gate)
        {
            if (sealedLedger || index != allocated.Count || allocated.ContainsKey(index) || !Positive(amount)) return null;
            var next = Add(total, amount);
            if (next is null || !Within(next, ceiling)) return null;
            var lease = new EconomicsLease(Guid.NewGuid().ToString("N"), index, amount);
            allocated.Add(index, lease); total = next;
            return lease;
        }
    }

    internal bool Receipt(EconomicsLease lease)
    {
        lock (gate) return !sealedLedger && allocated.TryGetValue(lease.Index, out var selected) &&
            selected == lease && receipted.Add(lease.Index);
    }
    internal EconomicsAllocation Seal() { lock (gate) { sealedLedger = true; return total; } }
    internal static bool Positive(EconomicsAllocation value) => value.Attempts > 0 && value.Calls > 0 &&
        value.InputTokens > 0 && value.OutputTokens > 0 && value.CombinedTokens > 0 && value.SpendMicroUsd > 0 && value.Milliseconds > 0;
    internal static bool Within(EconomicsAllocation value, EconomicsAllocation limit) =>
        value.Attempts <= limit.Attempts && value.Calls <= limit.Calls && value.InputTokens <= limit.InputTokens &&
        value.OutputTokens <= limit.OutputTokens && value.CombinedTokens <= limit.CombinedTokens &&
        value.SpendMicroUsd <= limit.SpendMicroUsd && value.Milliseconds <= limit.Milliseconds;
    internal static EconomicsAllocation? Add(EconomicsAllocation left, EconomicsAllocation right)
    {
        try { return new(checked(left.Attempts + right.Attempts), checked(left.Calls + right.Calls),
            checked(left.InputTokens + right.InputTokens), checked(left.OutputTokens + right.OutputTokens),
            checked(left.CombinedTokens + right.CombinedTokens), checked(left.SpendMicroUsd + right.SpendMicroUsd),
            checked(left.Milliseconds + right.Milliseconds)); }
        catch (OverflowException) { return null; }
    }
}
