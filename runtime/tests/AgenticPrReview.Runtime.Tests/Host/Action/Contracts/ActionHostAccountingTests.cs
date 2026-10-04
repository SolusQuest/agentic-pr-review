using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Contracts;

public sealed class ActionHostAccountingTests
{
    [Fact]
    public void SharedNumericFactsAndSemanticMutationsAgreeWithStrictWireContract()
    {
        using var cases = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        foreach (var item in cases.RootElement.EnumerateArray())
        {
            var name = item.GetProperty("name").GetString();
            var bytes = Encoding.UTF8.GetBytes(item.GetProperty("document").GetRawText());
            var valid = item.GetProperty("valid").GetBoolean();
            Assert.True(ActionHostJsonCodec.TryReadCompletion(bytes, out var result, out _) == valid, name);
            if (!valid) { Assert.Null(result); continue; }
            Assert.True(ActionHostJsonCodec.TryWriteCompletion(result, out var wire), name);
            using var roundTrip = JsonDocument.Parse(wire);
            var actual = roundTrip.RootElement.GetProperty("accounting");
            foreach (var field in item.GetProperty("document").GetProperty("accounting").EnumerateObject())
                Assert.Equal(field.Value.GetRawText(), actual.GetProperty(field.Name).GetRawText());
            Assert.DoesNotContain("CANARY", Encoding.UTF8.GetString(wire));
        }
    }

    [Fact]
    public void DuplicateEscapedAccountingMembersAndWireCapRemainStrict()
    {
        using var cases = JsonDocument.Parse(File.ReadAllBytes(FixturePath()));
        var document = cases.RootElement[0].GetProperty("document").GetRawText();
        var duplicate = document.Replace("\"model_calls\": \"1\"", "\"model_calls\": \"1\",\"\\u006dodel_calls\":\"1\"");
        Assert.False(ActionHostJsonCodec.TryReadCompletion(Encoding.UTF8.GetBytes(duplicate), out _, out _));
        var padded = Encoding.UTF8.GetBytes(document.PadRight(16 * 1024));
        Assert.True(ActionHostJsonCodec.TryReadCompletion(padded, out _, out _));
        Assert.False(ActionHostJsonCodec.TryReadCompletion([.. padded, (byte)' '], out _, out _));
        foreach (var member in new[] { "accounting", "termination_reason" })
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(document)!;
            values.Remove(member);
            Assert.False(ActionHostJsonCodec.TryReadCompletion(JsonSerializer.SerializeToUtf8Bytes(values), out _, out _));
        }
    }

    private static string FixturePath([CallerFilePath] string path = "") =>
        Path.Join(Path.GetDirectoryName(path), "Fixtures", "provider-accounting.json");
}
