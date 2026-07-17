using System;
using System.Text.Json;
using AgenticPrReview.Runtime.Prefix;
using Xunit;
using Xunit.Abstractions;

namespace AgenticPrReview.Runtime.Tests.Prefix;

public sealed class PrefixDebugDumpTests
{
    private readonly ITestOutputHelper _output;

    public PrefixDebugDumpTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DumpTemplateCanonical()
    {
        const string envelopeJson = "{\"schemaVersion\":1,\"templateVersion\":3,\"definition\":{\"role\":\"system\",\"text\":\"You are a precise code reviewer.\"}}";
        using var doc = JsonDocument.Parse(envelopeJson);
        var error = PrefixEnvelopeValidator.Validate(
            PrefixEnvelopeValidator.EnvelopeKind.Template, doc.RootElement, out var validated);
        _output.WriteLine("error: " + (error?.Code ?? "null"));
        if (validated is not null)
        {
            _output.WriteLine("canonical: " + Convert.ToHexString(validated.CanonicalBytes.AsSpan()).ToLowerInvariant());
            _output.WriteLine("digest: " + validated.Digest);
        }
    }
}
