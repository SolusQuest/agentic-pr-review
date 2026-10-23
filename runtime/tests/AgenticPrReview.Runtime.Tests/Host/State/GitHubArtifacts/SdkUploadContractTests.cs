using System.Text.Json;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

public sealed class SdkUploadContractTests
{
    private static readonly SdkUploadRequest Request = new(SdkUploadClient.Protocol, "test", "once",
        "1", "1", "apr-object-" + new string('a', 64) + "-" + new string('b', 64),
        "source/carrier", "1", new string('a', 64), "1", "120000");
    private const string Refusal = "{\"protocol\":\"apr.sdk.upload.cr2\",\"build_discriminator\":\"test\",\"correlation_id\":\"once\",\"dispatch\":\"not_dispatched\",\"failure\":\"invalid\",\"receipt\":null}";

    [Fact]
    public void WritesOnlyTheElevenCapabilityFields()
    {
        using var json = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(Request, SdkUploadJsonContext.Default.SdkUploadRequest));
        Assert.Equal(new[] { "protocol", "build_discriminator", "correlation_id", "run_id", "run_attempt", "physical_name", "source_relative_path", "source_size", "source_sha256", "retention_days", "timeout_ms" },
            json.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("source_size").ValueKind);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("number")]
    [InlineData("bom")]
    [InlineData("trailing")]
    public void RejectsNonClosedReplyJson(string fault)
    {
        var json = fault switch
        {
            "unknown" => Refusal.Replace("\"receipt\":null", "\"receipt\":null,\"purpose\":\"recovery\""),
            "duplicate" => Refusal.Replace("\"receipt\":null", "\"receipt\":null,\"receipt\":null"),
            "missing" => Refusal.Replace(",\"receipt\":null", ""),
            "number" => Refusal.Replace("\"once\"", "1"),
            "bom" => "\uFEFF" + Refusal,
            _ => Refusal + "{}",
        };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize(json, SdkUploadJsonContext.Default.SdkUploadReply));
    }

    [Theory]
    [InlineData("not_dispatched", "none")]
    [InlineData("not_dispatched", "outcome_unknown")]
    [InlineData("dispatched", "cleanup")]
    [InlineData("dispatched", "invalid")]
    [InlineData("dispatched", "none")]
    public void RejectsContradictoryReplyCombinations(string dispatch, string failure)
    {
        Assert.False(SdkUploadClient.ValidReply(new(SdkUploadClient.Protocol, "test", "once", dispatch, failure, null), Request));
    }
}
