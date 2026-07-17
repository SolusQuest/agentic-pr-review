using System;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Canonical;

public sealed class CanonicalWriterTests
{
    [Theory]
    [InlineData(1.0, "1")]
    [InlineData(-0.0, "0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1e21, "1e+21")]
    [InlineData(1e20, "100000000000000000000")]
    [InlineData(1e-6, "0.000001")]
    [InlineData(1e-7, "1e-7")]
    [InlineData(5e-324, "5e-324")]
    [InlineData(1.7976931348623157e308, "1.7976931348623157e+308")]
    [InlineData(333333333.33333329, "333333333.3333333")]
    [InlineData(9007199254740991.0, "9007199254740991")]
    [InlineData(-123.5, "-123.5")]
    public void NumberFormattingMatchesEcmaScript(double value, string expected)
    {
        Assert.Equal(expected, EcmaScriptNumberFormatter.Format(value));
    }

    [Fact]
    public void MathematicalIntegerSpellingsAgree()
    {
        Assert.Equal("1", EcmaScriptNumberFormatter.Format(1.0));
        Assert.Equal("1", EcmaScriptNumberFormatter.Format(1.000));
        Assert.Equal(EcmaScriptNumberFormatter.Format(1.0), EcmaScriptNumberFormatter.Format(1e0));
    }

    [Fact]
    public void NonFiniteNumbersAreRejected()
    {
        Assert.Throws<Rfc8785CanonicalizationException>(() => EcmaScriptNumberFormatter.Format(double.NaN));
        Assert.Throws<Rfc8785CanonicalizationException>(() => EcmaScriptNumberFormatter.Format(double.PositiveInfinity));
        Assert.Throws<Rfc8785CanonicalizationException>(() => EcmaScriptNumberFormatter.Format(double.NegativeInfinity));
    }

    [Fact]
    public void StringEscapingMatchesRfc8785()
    {
        // Quote, backslash, short escapes, \uXXXX controls, raw non-ASCII.
        var writer = new Rfc8785Writer(64);
        writer.WriteString("q\"b\\\b\f\n\r\t é😀");
        var json = Encoding.UTF8.GetString(writer.ToImmutableArray().AsSpan());
        Assert.Equal("\"q\\\"b\\\\\\b\\f\\n\\r\\t\\u0001 é😀\"", json);
    }

    [Fact]
    public void LoneSurrogateIsRejected()
    {
        var writer = new Rfc8785Writer(64);
        Assert.Throws<Rfc8785CanonicalizationException>(() => writer.WriteString("bad\uD800x"));
    }

    [Fact]
    public void ObjectKeysSortByUtf16CodeUnits()
    {
        // U+10000 (first UTF-16 unit 0xD800 = 55296) sorts before U+E000 (57344)
        // in UTF-16 code-unit order, the reverse of code-point order.
        using var doc = JsonDocument.Parse("{\"\uE000\":1,\"𐀀\":2}");
        var canonical = JsonElementCanonicalizer.Canonicalize(doc.RootElement, 64, 256, 1024);
        Assert.Equal("{\"𐀀\":2,\"\uE000\":1}", Encoding.UTF8.GetString(canonical.AsSpan()));
    }
    [Fact]
    public void DuplicatePropertyIsDetected()
    {
        using var doc = JsonDocument.Parse("{\"a\":1,\"a\":2}");
        var ex = Assert.Throws<Rfc8785CanonicalizationException>(
            () => JsonElementCanonicalizer.Canonicalize(doc.RootElement, 64, 256, 1024));
        Assert.Equal(Rfc8785RejectionReason.DuplicateProperty, ex.Reason);
    }
}
