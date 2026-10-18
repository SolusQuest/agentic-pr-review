// S4's read-only encrypted-store port references these two canary names. Bind
// them to V2's own data; no S4 provider implementation is compiled or executed.
global using ScriptedHandler = AgenticPrReview.Runtime.SolusEconomicsFixture.PortCanaries;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal static class PortCanaries
{
    internal const string PriorOnly = HistoryOracle.Fact;
    internal const string CurrentOnly = ControlledProvider.CurrentOnly;
}
