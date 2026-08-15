using System.Text;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Inline;

public sealed class InlineCommentCodecTests
{
    private static readonly string Key = new('a', 64);
    private const string Exact422 =
        "{\"message\":\"Validation Failed\"," +
        "\"documentation_url\":\"https://docs.github.com/rest/pulls/" +
        "reviews#create-a-review-for-a-pull-request\"," +
        "\"errors\":[{\"resource\":\"PullRequestReview\"," +
        "\"field\":\"comments\",\"code\":\"invalid\"}]}";

    [Fact]
    public void MarkerIsTerminalExactAndOrdinaryCommentsAreIgnored()
    {
        var body = InlineCommentMarker.Append("safe body", Key);

        var valid = InlineCommentMarker.Inspect(body);
        var ordinary = InlineCommentMarker.Inspect("ordinary review comment");
        var malformed = InlineCommentMarker.Inspect(
            body + "\ntrailing content");
        var duplicate = InlineCommentMarker.Inspect(body + "\n\n" + body);

        Assert.Equal(InlineMarkerInspectionKind.Valid, valid.Kind);
        Assert.Equal(Key, valid.InlineKey);
        Assert.Equal(InlineMarkerInspectionKind.NoMarker, ordinary.Kind);
        Assert.Equal(InlineMarkerInspectionKind.Invalid, malformed.Kind);
        Assert.Equal(InlineMarkerInspectionKind.Invalid, duplicate.Kind);
    }

    [Fact]
    public void ExactBoundedValidationShapeAloneAuthorizesFallback()
    {
        Assert.True(InlineBatchValidationParser.IsExactKnownNotSent(
            Encoding.UTF8.GetBytes(Exact422)));
    }

    [Theory]
    [InlineData("{\"id\":7,\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\",\"comment_id\":7}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}],\"extra\":true}")]
    [InlineData("{\"message\":\"Validation Failed\",\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"Validation Failed, or the endpoint has been spammed.\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"repository policy rejected this review\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"protected branch\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"forbidden\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"custom\"}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReviewComment\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    [InlineData("{\"message\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[]}")]
    [InlineData("{\"m\\u0065ssage\":\"Validation Failed\",\"documentation_url\":\"https://docs.github.com/rest/pulls/reviews#create-a-review-for-a-pull-request\",\"errors\":[{\"resource\":\"PullRequestReview\",\"field\":\"comments\",\"code\":\"invalid\"}]}")]
    public void UnknownDuplicateIdentityBearingOrAlternate422IsRejected(
        string json)
    {
        Assert.False(InlineBatchValidationParser.IsExactKnownNotSent(
            Encoding.UTF8.GetBytes(json)));
    }
}
