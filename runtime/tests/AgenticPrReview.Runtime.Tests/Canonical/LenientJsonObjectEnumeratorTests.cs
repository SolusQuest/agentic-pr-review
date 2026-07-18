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

    [Fact]
    public void LongCommonPrefixSortingHasLinearDecodedWork()
    {
        const int entryCount = 64;
        var prefix = new string('x', 20_000);
        var names = Enumerable.Range(0, entryCount)
            .Select(index => prefix + (char)(0x100 + index))
            .Reverse()
            .ToArray();
        var json = "{" + string.Join(",", names.Select((name, index) =>
            JsonSerializer.Serialize(name) + ":" + index)) + "}";
        using var document = JsonDocument.Parse(json);
        var counter = new LenientJsonObjectEnumerator.TokenWorkCounter();
        var entries = LenientJsonObjectEnumerator.Enumerate(document.RootElement, workCounter: counter).ToArray();

        var sorted = LenientJsonObjectEnumerator.SortEntries(entries, counter);

        var expected = names.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected.Length, sorted.Count);
        for (var index = 0; index < expected.Length; index++)
        {
            Assert.Equal(0, LenientJsonObjectEnumerator.CompareNameTo(sorted[index], expected[index]));
        }
        var inputCodeUnits = names.Sum(static name => (long)name.Length);
        Assert.True(counter.CodeUnitsRead <= inputCodeUnits * 3,
            $"decoded work {counter.CodeUnitsRead} exceeded the linear bound for {inputCodeUnits} input units");
    }

    [Fact]
    public void ClosedInvalidNamesUseOneFixedSentinelSortPosition()
    {
        var (document, entries) = Enumerate("{\"a\\ud800\":1,\"\\udc00z\":2,\"b\":3,\"😀\":4}");
        using (document)
        {
            Assert.Equal(4, entries.Length);
            Assert.Equal(0, LenientJsonObjectEnumerator.CompareClosedNames(entries[0], entries[1]));
            Assert.True(LenientJsonObjectEnumerator.CompareClosedNames(entries[2], entries[0]) < 0);
            Assert.True(LenientJsonObjectEnumerator.CompareClosedNames(entries[0], entries[3]) < 0);
        }
    }
}
