using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime;

// Readonly historical fixture source; never a product execution seam.
internal interface IHistoricalResponseFactory
{
    IAuthoredResponseSource Create(DeepSeekAdapterContext context);
}

