using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.Tests.Execution.DeepSeek;

public sealed class DeepSeekContextAdmissionTests
{
    [Theory]
    [InlineData(0, 4096)]
    [InlineData(1, 8192)]
    [InlineData(2, 65536)]
    public void ContextAndReservedOutputHaveAnExactIndependentBoundary(int profile, int reserved)
    {
        var selected = (DeepSeekRequestProfile)profile;
        Assert.True(DeepSeekContextAdmission.Allows(1_000_000 - reserved, selected));
        Assert.False(DeepSeekContextAdmission.Allows(1_000_001 - reserved, selected));
        Assert.False(DeepSeekContextAdmission.Allows(-1, selected));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ByteAdmittedContextOverflowNeverDispatches(int profile)
    {
        var selected = (DeepSeekRequestProfile)profile;
        var request = Request(new string('x', 999_000));
        Assert.Equal(DeepSeekRequestWriteOutcome.Success, DeepSeekRequestWriter.Write(request, selected).Outcome);
        var transport = new RejectDispatch();
        var backend = new DeepSeekChatBackend(new(DeepSeekAdapterContext.Provider,
            DeepSeekAdapterContext.Model, DeepSeekAdapterContext.AdapterFor(selected), "context_test"), transport);
        var failure = await Assert.ThrowsAsync<ProjectChatNormalizationException>(() => backend.GetResponseAsync(request, default));
        Assert.Equal(AgentFailureCodes.ContextLimit, failure.DiagnosticCode);
        Assert.Equal(0, transport.Sends);
    }

    [Fact]
    public void EscapedControlsAndUtf8UseDecodedPromptBytes()
    {
        var controls = string.Concat(Enumerable.Range(0, 32).Select(i => (char)i));
        var text = string.Concat(Enumerable.Repeat(controls + "中文😀<｜end▁of▁sentence｜>", 2000));
        var plain = Request(text);
        var wire = DeepSeekRequestWriter.Write(plain);
        Assert.Equal(DeepSeekRequestWriteOutcome.Success, wire.Outcome);
        Assert.True(DeepSeekContextAdmission.TryEstimate(wire.Body.AsSpan(), out var estimate));
        Assert.InRange(estimate, Encoding.UTF8.GetByteCount(text), Encoding.UTF8.GetByteCount(text) + 8192L);
        Assert.True(wire.Body.Length > estimate);
        Assert.True(DeepSeekContextAdmission.Allows(estimate, DeepSeekRequestProfile.Current));
    }

    [Theory]
    [InlineData("{\"x\":1e9}", true)]
    [InlineData("{\"x\":[1e9,1e-9,-1e20]}", true)]
    [InlineData("{\"x\":1e9999}", false)]
    [InlineData("{\"x\":1,\"x\":2}", false)]
    [InlineData("[]", false)]
    public void NumericAndUnsupportedArgumentShapesHaveDefensibleBounds(string arguments, bool valid)
    {
        var wire = JsonSerializer.SerializeToUtf8Bytes(new
        {
            messages = new[] { new { role = "assistant", content = "", reasoning_content = "kept",
                tool_calls = new[] { new { function = new { name = "test", arguments } } } } },
            tools = new[] { new { function = new { name = "test", description = "\0\u0001\n", parameters = new { type = "object" } } } },
        });
        Assert.Equal(valid, DeepSeekContextAdmission.TryEstimate(wire, out var estimate));
        if (valid) Assert.True(estimate > DeepSeekContextAdmission.FixedBytes + 32);
    }

    [Fact]
    public void RetainedReasoningAndControlRichToolDescriptionsAreCounted()
    {
        byte[] Wire(string reasoning, string description) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            messages = new[] { new { role = "assistant", content = "x", reasoning_content = reasoning } },
            tools = new[] { new { function = new { name = "test", description, parameters = new { type = "object" } } } },
        });
        Assert.True(DeepSeekContextAdmission.TryEstimate(Wire("", ""), out var baseline));
        Assert.True(DeepSeekContextAdmission.TryEstimate(Wire("中文", new string('\0', 100)), out var changed));
        Assert.Equal(606, changed - baseline);
    }

    private static MinimalChatRequest Request(string text) => new(
        [new("user", [new("text", null, null, text, null, null, null, 0, 0)])],
        [new("test", "test tool", "{\"type\":\"object\",\"properties\":{}}")], null, true);

    private sealed class RejectDispatch : IDeepSeekTransport
    {
        internal int Sends;
        public Task<DeepSeekTransportResult> SendAsync(ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken)
        {
            Sends++;
            throw new InvalidOperationException("Unexpected provider dispatch.");
        }
        public void Dispose() { }
    }
}
