using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime;

internal interface IR3LiveAgentChatFactory
{
    IProjectChatClient Create(DeepSeekAdapterContext context);
}

internal interface IR3LiveAgentReviewedFileAccessFactory
{
    IReviewedFileAccess Create();
}
