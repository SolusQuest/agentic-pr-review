using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekCredentialTests
{
    [Theory]
    [InlineData("")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\0")]
    public void CredentialRejectsInvalidTextWithoutEchoingIt(string value)
    {
        var error = Assert.Throws<ArgumentException>(() =>
            DeepSeekCredential.Create(value));

        if (value.Length > 0)
        {
            Assert.DoesNotContain(
                value,
                error.Message,
                StringComparison.Ordinal);
        }
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void CredentialUsesStrictUtf8ByteBoundsAndSanitizedFormatting()
    {
        var maximum = DeepSeekCredential.Create(new string('k', 256));
        var tooLarge = string.Concat(Enumerable.Repeat("😀", 65));
        var malformed = new string(['\ud800']);

        Assert.Equal(nameof(DeepSeekCredential), maximum.ToString());
        Assert.Throws<ArgumentException>(() =>
            DeepSeekCredential.Create(tooLarge));
        Assert.Throws<ArgumentException>(() =>
            DeepSeekCredential.Create(malformed));
    }

}
