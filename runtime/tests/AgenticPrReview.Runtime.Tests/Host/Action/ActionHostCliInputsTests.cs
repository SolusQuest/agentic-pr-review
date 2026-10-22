using System.Text;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostCliInputsTests
{
    [Theory]
    [InlineData("pr-number", "0")]
    [InlineData("pr-number", "01")]
    [InlineData("pr-number", "+1")]
    [InlineData("pr-number", "-1")]
    [InlineData("pr-number", "1.0")]
    [InlineData("pr-number", "9223372036854775808")]
    [InlineData("pr-number", " 1 ")]
    [InlineData("state-mode", "AUTO")]
    [InlineData("state-mode", "reset\n")]
    [InlineData("config-path", "private\0path")]
    [InlineData("unknown", "")]
    public Task BoundedProductFailuresHaveCanonicalCompletionBeforeComposition(string name, string value) =>
        AssertProductFailure([new(name, value)]);

    [Fact]
    public async Task RawCountDuplicateUtf8AndAggregateLimitsRemainCliOwned()
    {
        await AssertProductFailure([new("state-mode", ""), new("state-mode", "auto")]);
        await AssertProductFailure(Enumerable.Repeat(new ActionHostRawInput("state-mode", ""), 8).ToArray());
        await AssertProductFailure([new(new string('n', 65), "")]);
        await AssertProductFailure([new("config-path", new string('p', 1025))]);
        foreach (var name in new[] { "github-token", "provider-api-key", "state-key", "previous-state-key" })
            await AssertProductFailure([new(name, new string('é', 2049))]);
        await AssertProductFailure([
            new("github-token", new string('a', 4096)), new("provider-api-key", new string('b', 4096)),
            new("state-key", new string('c', 4096)), new("previous-state-key", new string('d', 4096)),
        ]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OmittedAndEmptyKnownInputsResolveDefaultsAtTheSameEntry(bool empty)
    {
        ActionHostRawInput[] entries = empty ? [
            new("github-token", ""), new("provider-api-key", ""), new("state-key", ""),
            new("previous-state-key", ""), new("config-path", ""), new("pr-number", ""), new("state-mode", ""),
        ] : [];
        var wire = WithInputs(entries);
        using var input = new MemoryStream(ActionHostEntrypointTests.Frame(wire));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var calls = 0;
        await RuntimeApplication.RunActionHostAsync(input, output, stderr, default, (launch, _) =>
        {
            calls++;
            Assert.Equal(ActionHostInputs.DefaultConfigPath, launch.Inputs.ConfigPath);
            Assert.Equal(ActionHostStateMode.Auto, launch.Inputs.StateMode);
            Assert.Null(launch.Inputs.PullRequestNumber);
            Assert.Null(launch.Inputs.GitHubToken);
            Assert.Null(launch.Inputs.ProviderApiKey);
            Assert.Null(launch.Inputs.StateKey);
            Assert.Null(launch.Inputs.PreviousStateKey);
            return new AgenticPrReview.Runtime.ActionHost.ActionHostComposition()
                .RunAsync(launch, new CancellationToken(true));
        });
        Assert.Equal(1, calls);
        Assert.Empty(stderr.ToString());
        Assert.Equal(ActionHostStatus.Cancelled, ActionHostEntrypointTests.ReadCompletion(output).Status);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("build_discriminator")]
    [InlineData("facts")]
    [InlineData("null_input")]
    [InlineData("null_value")]
    [InlineData("numeric_value")]
    [InlineData("missing_name")]
    [InlineData("unknown_input_member")]
    [InlineData("unknown_fact")]
    [InlineData("missing_fact")]
    [InlineData("missing_root")]
    [InlineData("typed_inputs")]
    public async Task WireOrIdentityFailureCannotBeHiddenByInvalidProductValues(string kind)
    {
        var root = JsonNode.Parse(WithInputs([new("pr-number", "0")]))!.AsObject();
        switch (kind)
        {
            case "protocol": root["protocol"] = "other"; break;
            case "build_discriminator": root["build_discriminator"] = "r4-w2"; break;
            case "facts": root["facts"]!["repository_id"] = "0"; break;
            case "null_input": root["inputs"]![0] = null; break;
            case "null_value": root["inputs"]![0]!["value"] = null; break;
            case "numeric_value": root["inputs"]![0]!["value"] = 1; break;
            case "missing_name": root["inputs"]![0]!.AsObject().Remove("name"); break;
            case "unknown_input_member": root["inputs"]![0]!["extra"] = "private"; break;
            case "unknown_fact": root["facts"]!["extra"] = "private"; break;
            case "missing_fact": root["facts"]!.AsObject().Remove("run_id"); break;
            case "missing_root": root.Remove("cancellation"); break;
            case "typed_inputs": root["inputs"] = new JsonObject { ["pr_number"] = "0" }; break;
        }
        await AssertWireFailure(Encoding.UTF8.GetBytes(root.ToJsonString()));
    }

    [Fact]
    public Task EscapedIllFormedUnicodeRemainsWireFailure() => AssertWireFailure(
        Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(WithInputs([new("state-key", "CANARY")]))
            .Replace("CANARY", "\\ud800", StringComparison.Ordinal)));

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("run --extra")]
    [InlineData("run run")]
    public async Task InvalidUsageDoesNotReadStdin(string command)
    {
        using var input = new UnreadableInput();
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var args = command.Length == 0 ? [] : command.Split(' ');
        Assert.Equal(2, await RuntimeEntrypoint.RunAsync(args, input, output, stderr, default));
        Assert.Empty(output.ToArray());
        Assert.StartsWith("APR_USAGE_INVALID:", stderr.ToString());
    }

    private static async Task AssertProductFailure(ActionHostRawInput[] entries)
    {
        var wire = WithInputs(entries);
        Assert.False(ActionHostJsonCodec.TryReadLaunch(wire, out var launch, out var error, "r7-d0"));
        Assert.Null(launch);
        Assert.Equal(ActionHostContractError.InvalidInputs, error);
        using var input = new MemoryStream(ActionHostEntrypointTests.Frame(wire));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var calls = 0;
        var exit = await RuntimeApplication.RunActionHostAsync(input, output, stderr, default,
            (_, _) => { calls++; throw new InvalidOperationException("PRIVATE_NO_COMPOSITION"); });
        Assert.Equal(0, calls);
        Assert.Equal(1, exit);
        var completion = ActionHostEntrypointTests.ReadCompletion(output);
        Assert.Equal(ActionHostStatus.ConfigurationInvalid, completion.Status);
        Assert.Equal(exit, completion.ProcessExitCode);
        Assert.True(completion.Accounting.IsCompleteZero);
        Assert.Equal(ActionHostTerminationReason.NotStarted, completion.TerminationReason);
        Assert.Equal(ActionHostStateDisposition.NotCommitted, completion.Summary.StateDisposition);
        Assert.Equal(ActionHostAnnotationCode.ConfigurationInvalid, Assert.Single(completion.Annotations).Code);
        Assert.Empty(stderr.ToString());
    }

    private static async Task AssertWireFailure(byte[] wire)
    {
        using var input = new MemoryStream(ActionHostEntrypointTests.Frame(wire));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        Assert.Equal(1, await RuntimeApplication.RunActionHostAsync(input, output, stderr, default,
            (_, _) => throw new InvalidOperationException("PRIVATE_NO_COMPOSITION")));
        Assert.Empty(output.ToArray());
        Assert.Equal("APR_ACTION_HOST_INPUT_INVALID" + Environment.NewLine, stderr.ToString());
    }

    private static byte[] WithInputs(ActionHostRawInput[] entries)
    {
        var root = JsonNode.Parse(ActionHostEntrypointTests.LaunchBytes())!;
        root["inputs"] = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject
        {
            ["name"] = entry.Name, ["value"] = entry.Value,
        }).ToArray());
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private sealed class UnreadableInput : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Usage must not read private stdin.");
    }
}
