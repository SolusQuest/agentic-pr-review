using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Canonical;

public sealed class LenientJsonObjectEnumeratorTests
{
    private static ImmutableArray<LenientJsonObjectEnumerator.Entry> Enumerate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return LenientJsonObjectEnumerator.Enumerate(doc.RootElement);
    }

    [Fact]
    public void DirectNonAsciiWithEscapesDecodesCorrectly()
    {
        // Direct é (2 UTF-8 bytes) followed by an escaped newline and an
        // escaped backslash: the run before the escape must decode as UTF-8.
        var entries = Enumerate("{\"é\\n\\\\\":1}");
        var entry = Assert.Single(entries);
        Assert.True(entry.NameValid);
        Assert.Equal("é\n\\", entry.Name);
    }

    [Fact]
    public void DirectCjkWithUnicodeEscapeDecodesCorrectly()
    {
        var entries = Enumerate("{\"界面\\u000a\":1}");
        var entry = Assert.Single(entries);
        Assert.True(entry.NameValid);
        Assert.Equal("界面\n", entry.Name);
    }

    [Fact]
    public void DirectNonAsciiPrefixWithLoneSurrogateIsMarkedInvalidButPreservesName()
    {
        var entries = Enumerate("{\"é\\ud800\":1}");
        var entry = Assert.Single(entries);
        Assert.False(entry.NameValid);
        Assert.Equal("é\ud800", entry.Name);
    }

    [Fact]
    public void DirectUtf8AndUnicodeEscapeSpellingsAreDuplicates()
    {
        var entries = Enumerate("{\"é\":1,\"\\u00e9\":2}");
        Assert.Equal(2, entries.Length);
        Assert.Equal(entries[0].Name, entries[1].Name);
    }

    [Fact]
    public void CanonicalBytesMatchForMixedNames()
    {
        // The canonical output for a mixed direct-UTF8 + escape name must be
        // byte-identical to the name spelled with the direct character.
        using var directDoc = JsonDocument.Parse("{\"é\":1}");
        using var escapedDoc = JsonDocument.Parse("{\"\\u00e9\":1}");
        var direct = JsonElementCanonicalizer.Canonicalize(
            directDoc.RootElement, 64, 256, 1024, long.MaxValue, out _);
        var escaped = JsonElementCanonicalizer.Canonicalize(
            escapedDoc.RootElement, 64, 256, 1024, long.MaxValue, out _);
        Assert.Equal(Encoding.UTF8.GetString(direct.AsSpan()), Encoding.UTF8.GetString(escaped.AsSpan()));
    }
}
