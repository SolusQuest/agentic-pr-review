using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Canonical;

public sealed class LenientJsonObjectEnumeratorTests
{
    private static (JsonDocument Document, ImmutableArray<LenientJsonObjectEnumerator.Entry> Entries) Enumerate(string json)
    {
        var document = JsonDocument.Parse(json);
        return (document, LenientJsonObjectEnumerator.Enumerate(document.RootElement).ToImmutableArray());
    }

    [Fact]
    public void DirectNonAsciiWithEscapesDecodesCorrectly()
    {
        // Direct é (2 UTF-8 bytes) followed by an escaped newline and an
        // escaped backslash: the run before the escape must decode as UTF-8.
        var (document, entries) = Enumerate("{\"é\\n\\\\\":1}");
        using (document)
        {
            var entry = Assert.Single(entries);
            Assert.True(LenientJsonObjectEnumerator.NameIsWellFormed(entry));
            Assert.Equal(0, LenientJsonObjectEnumerator.CompareNameTo(entry, "é\n\\"));
        }
    }

    [Fact]
    public void DirectCjkWithUnicodeEscapeDecodesCorrectly()
    {
        var (document, entries) = Enumerate("{\"界面\\u000a\":1}");
        using (document)
        {
            var entry = Assert.Single(entries);
            Assert.True(LenientJsonObjectEnumerator.NameIsWellFormed(entry));
            Assert.Equal(0, LenientJsonObjectEnumerator.CompareNameTo(entry, "界面\n"));
        }
    }

    [Fact]
    public void DirectNonAsciiPrefixWithLoneSurrogateIsMarkedInvalidButPreservesName()
    {
        var (document, entries) = Enumerate("{\"é\\ud800\":1}");
        using (document)
        {
            var entry = Assert.Single(entries);
            Assert.False(LenientJsonObjectEnumerator.NameIsWellFormed(entry));
            Assert.Equal(0, LenientJsonObjectEnumerator.CompareNameTo(entry, "é\ud800"));
        }
    }

    [Fact]
    public void DirectUtf8AndUnicodeEscapeSpellingsAreDuplicates()
    {
        var (document, entries) = Enumerate("{\"é\":1,\"\\u00e9\":2}");
        using (document)
        {
            Assert.Equal(2, entries.Length);
            Assert.Equal(0, LenientJsonObjectEnumerator.CompareNames(entries[0], entries[1]));
        }
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
