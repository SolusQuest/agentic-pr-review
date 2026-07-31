using System.Text;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal sealed class DeepSeekCredential
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private readonly string _value;

    private DeepSeekCredential(string value)
    {
        _value = value;
    }

    internal static DeepSeekCredential Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\r') ||
            value.Contains('\n') ||
            value.Contains('\0'))
        {
            throw InvalidCredential();
        }

        try
        {
            var byteCount = StrictUtf8.GetByteCount(value);
            if (byteCount is < 1 or > DeepSeekTransportPolicy.CredentialMaxBytes)
            {
                throw InvalidCredential();
            }
        }
        catch (EncoderFallbackException)
        {
            throw InvalidCredential();
        }

        return new DeepSeekCredential(value);
    }

    internal string Value => _value;

    public override string ToString() => nameof(DeepSeekCredential);

    private static ArgumentException InvalidCredential() =>
        new("The DeepSeek credential is invalid.", "value");
}
