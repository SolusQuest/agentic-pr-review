using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

public sealed class ActionHostEventJsonTests
{
    [Fact]
    public async Task ExactPathReaderCapturesOneBoundedSnapshot()
    {
        var path = Path.GetTempFileName();
        try
        {
            var expected = Encoding.UTF8.GetBytes("{\"event\":\"proof\"}");
            await File.WriteAllBytesAsync(path, expected);
            var reader = new ActionHostExactPathEventReader();

            var result = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Equal(expected, result.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExactPathReaderRejectsOversizedDocument()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(
                path,
                new byte[ActionHostAuthorizationBounds.MaximumEventBytes + 1]);
            var reader = new ActionHostExactPathEventReader();

            var result = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Null(result.Bytes);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void DuplicateAndCaseAliasedPropertiesAreRejected()
    {
        var duplicate = Encoding.UTF8.GetBytes("""
            {
              "inputs": { "pr-number": 147 },
              "repository": { "id": 42, "full_name": "owner/repo" },
              "repository": { "id": 42, "full_name": "owner/repo" },
              "sender": { "id": 7, "login": "maintainer" }
            }
            """);
        var caseAlias = Encoding.UTF8.GetBytes("""
            {
              "inputs": { "pr-number": 147 },
              "Repository": { "id": 42, "full_name": "owner/repo" },
              "sender": { "id": 7, "login": "maintainer" }
            }
            """);

        Assert.False(ActionHostEventParser.TryParse(
            duplicate,
            out _,
            out _));
        Assert.False(ActionHostEventParser.TryParse(
            caseAlias,
            out _,
            out _));
    }

    [Fact]
    public void InvalidUtf8AndBomAreRejected()
    {
        Assert.False(ActionHostEventParser.TryParse(
            [0xff, 0xfe, 0xfd],
            out _,
            out _));
        Assert.False(ActionHostEventParser.TryParse(
            [0xef, 0xbb, 0xbf, (byte)'{', (byte)'}'],
            out _,
            out _));
    }
}
