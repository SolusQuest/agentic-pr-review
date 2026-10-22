using System.Reflection;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class PrivateLaunchLifetimeTests
{
    [Theory]
    [InlineData("complete")]
    [InlineData("failure")]
    [InlineData("cancel")]
    [InlineData("output-failure")]
    public async Task EntryOwnsAllFourCredentialBuffersThroughHostAndOutput(string outcome)
    {
        var document = JsonNode.Parse(ActionHostEntrypointTests.LaunchBytes())!;
        document["inputs"] = new JsonArray(new[] {
            "github-token", "provider-api-key", "state-key", "previous-state-key"
        }.Select(name => (JsonNode)new JsonObject {
            ["name"] = name, ["value"] = "PRIVATE_" + name
        }).ToArray());
        using var input = new MemoryStream(ActionHostEntrypointTests.Frame(
            System.Text.Encoding.UTF8.GetBytes(document.ToJsonString())));
        using var cancellation = new CancellationTokenSource();
        using var stderr = new StringWriter();
        ActionHostOpaqueSecret[] secrets = [];
        byte[][] owned = [];
        var entered = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        using var output = new ObservingOutput(() => {
            foreach (var secret in secrets) Assert.StartsWith("PRIVATE_", secret.ExportForPrivateLaunch());
            if (outcome == "output-failure") throw new IOException();
        });
        var running = RuntimeApplication.RunActionHostAsync(input, output, stderr, cancellation.Token,
            async (launch, token) => {
                secrets = [launch.Inputs.GitHubToken!, launch.Inputs.ProviderApiKey!,
                    launch.Inputs.StateKey!, launch.Inputs.PreviousStateKey!];
                owned = secrets.Select(Bytes).ToArray();
                entered.SetResult();
                await release.Task;
                foreach (var secret in secrets) Assert.StartsWith("PRIVATE_", secret.ExportForPrivateLaunch());
                if (outcome == "failure") throw new InvalidOperationException();
                if (outcome == "cancel") {
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                }
                return await new ActionHostComposition().RunAsync(launch, new CancellationToken(true));
            });
        await entered.Task;
        Assert.All(owned, bytes => Assert.Contains(bytes, value => value != 0));
        release.SetResult();
        Assert.Equal(1, await running);
        Assert.All(owned, bytes => Assert.All(bytes, value => Assert.Equal(0, value)));
        foreach (var secret in secrets) {
            Assert.Throws<ObjectDisposedException>(() => secret.ExportForPrivateLaunch());
            secret.Dispose();
            Assert.Equal("[REDACTED]", secret.ToString());
        }
    }

    [Fact]
    public async Task CancelledReadRetainsBorrowedDocumentUntilIgnoringReaderSettles()
    {
        using var input = new PendingBodyInput();
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var running = RuntimeApplication.RunActionHostAsync(input, output, stderr, cancellation.Token);
        await input.Entered.Task;
        cancellation.Cancel();
        Assert.Equal(1, await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.All(input.Borrowed.ToArray(), value => Assert.Equal(0x61, value));
        // The actual reader can write after its caller has stopped waiting.
        input.Borrowed.Span.Fill(0x62);
        input.Pending.SetResult(input.Borrowed.Length);
        Assert.All(input.Borrowed.ToArray(), value => Assert.Equal(0, value));
        Assert.Empty(output.ToArray());
    }

    private static byte[] Bytes(ActionHostOpaqueSecret secret) =>
        (byte[])typeof(ActionHostOpaqueSecret).GetField("utf8Bytes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(secret)!;

    private sealed class ObservingOutput(System.Action observe) : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            observe();
            return base.WriteAsync(buffer, token);
        }
    }

    private sealed class PendingBodyInput : MemoryStream
    {
        internal Memory<byte> Borrowed { get; private set; }
        internal TaskCompletionSource Entered { get; } = new();
        internal TaskCompletionSource<int> Pending { get; } = new();
        private bool headerRead;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (!headerRead) {
                headerRead = true;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(buffer.Span, 16);
                return ValueTask.FromResult(4);
            }
            Borrowed = buffer;
            buffer.Span.Fill(0x61);
            Entered.SetResult();
            return new(Pending.Task);
        }
    }
}
