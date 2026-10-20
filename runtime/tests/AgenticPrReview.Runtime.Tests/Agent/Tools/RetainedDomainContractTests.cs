using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

// Live APR value predicates extracted from the retired native loop integration
// tests. These exercise domain code without constructing any execution engine.
public sealed class RetainedDomainContractTests
{

    [Fact]
    public void InvalidUnicodePathRuleUsesExactCanonicalRecoveryFeedback()
    {
        var rule = RepositoryPath.Failure("a\ud800");
        Assert.Equal(RepositoryPathFailure.InvalidUnicode, rule);
        var feedback = AgentRecoveryFeedback.ListFilesPathInvalid(
            new ListFilesPathRejection(ListFilesPathField.Prefix, rule));
        Assert.Equal(
            "{\"status\":\"error\",\"code\":\"list_files_path_invalid\",\"path_field\":\"prefix\",\"path_rule\":\"invalid_unicode\",\"retryable\":true}",
            feedback);
        Assert.True(AgentRecoveryFeedback.IsCanonicalPathError(feedback));
    }

    [Fact]
    public void AccountingMetadataDoesNotChangeCanonicalRequestBytes()
    {
        var request = new ProjectChatRequest(
            [new("system", [new ProjectTextContent("trusted policy")]),
             new("user", [new ProjectTextContent("current review")])], [], null);
        var original = AgentRequestWriter.Write(request);
        var capture = new ProviderAttemptCapture(0, 0);
        Assert.Equal(original, AgentRequestWriter.Write(request with { Accounting = capture }));
    }

    [Fact]
    public void EmptyTerminalPreservesItsCanonicalIdentity()
    {
        Assert.True(AgentToolArguments.TryFinishReviewProvider(
            "{\"summary\":\"clean\",\"findings\":[]}", out var arguments));
        Assert.True(TerminalReviewValidator.TryValidate(arguments!,
            new("repo", 1, new('0', 40), new('1', 40)), [], out var review));
        Assert.Equal("clean", review!.Summary);
        Assert.Equal("aa91b8e8da12f1c93848f1ec84223b0cc08125ea7f3442513bc303b5d353ffb9",
            review.TerminalSha256);
    }
}
