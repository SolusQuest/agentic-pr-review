using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Materialization history (issue #50, D8). <see cref="BootstrapHistory"/>
/// also covers recovery-root materialization: no prior records enter the
/// stream in either case.
/// </summary>
public abstract record MaterializationHistory
{
    private MaterializationHistory() { }

    public sealed record BootstrapHistory : MaterializationHistory
    {
        public static readonly BootstrapHistory Instance = new();

        private BootstrapHistory() { }
    }

    public sealed record ContinuationHistory(ValidatedLedger Prior) : MaterializationHistory;

    public sealed record ResetHistory(ValidatedLedger AcceptedPredecessor) : MaterializationHistory;
}

/// <summary>Raw JSON cache-contract envelopes; validated inside the materializer.</summary>
public sealed record RawCacheContractEnvelopes(
    JsonElement Template,
    JsonElement Policy,
    JsonElement Tools,
    JsonElement CacheConfig,
    JsonElement Adapter);
