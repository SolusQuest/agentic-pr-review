using System.Buffers.Binary;
using System.Text;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

// Linked stream-boundary coverage. The actual executable is separately invoked
// by the NEW ActionHostFixture supervisor in both framework and Native AOT gates.
public sealed class ActionHostEntrypointTests
{
    internal const string PrivateCanary = "PRIVATE_ENTRYPOINT_EXCEPTION_CANARY";

    public static IEnumerable<object[]> RejectedFrames()
    {
        var wire = LaunchBytes();
        var text = Encoding.UTF8.GetString(wire);
        yield return ["empty", Array.Empty<byte>()];
        yield return ["short_header", new byte[] { 0, 0, 1 }];
        yield return ["zero", new byte[4]];
        var oversized = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(oversized,
            ActionHostContractBounds.MaximumLaunchDocumentBytes + 1);
        yield return ["oversized", oversized];
        yield return ["truncated_body", Frame(wire)[..^1]];
        yield return ["trailing", Frame(wire).Concat(new byte[] { 0 }).ToArray()];
        yield return ["second_frame", Frame(wire).Concat(Frame(wire)).ToArray()];
        yield return ["invalid_utf8", Frame([0xff])];
        yield return ["bom", Frame(new byte[] { 0xef, 0xbb, 0xbf }.Concat(wire).ToArray())];
        yield return ["invalid_json", Frame(Encoding.UTF8.GetBytes(PrivateCanary))];
        yield return ["null", Frame(Encoding.UTF8.GetBytes("null"))];
        yield return ["unknown_member", Frame(Encoding.UTF8.GetBytes(
            "{\"private_canary\":\"" + PrivateCanary + "\"," + text[1..]))];
        yield return ["duplicate", Frame(Encoding.UTF8.GetBytes(text.Replace(
            "\"build_discriminator\":\"r7-d0\"",
            "\"build_discriminator\":\"r7-d0\",\"build_discriminator\":\"r7-d0\"")))];
        foreach (var build in new[] { "other-build", "r4-w2", "r4-h1" })
        {
            yield return ["foreign_build_" + build, Frame(Encoding.UTF8.GetBytes(text.Replace("r7-d0", build)))];
        }
    }

    [Theory]
    [MemberData(nameof(RejectedFrames))]
    public async Task LinkedInvalidFrameCannotStartHostOrExposePrivateData(string _, byte[] bytes)
    {
        var calls = 0;
        using var input = new MemoryStream(bytes);
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var exit = await RuntimeApplication.RunActionHostAsync(input, output, stderr, default,
            (_, _) => { calls++; throw new InvalidOperationException(PrivateCanary); });
        Assert.Equal(1, exit);
        Assert.Equal(0, calls);
        Assert.Empty(output.ToArray());
        Assert.Equal("APR_ACTION_HOST_INPUT_INVALID" + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task LinkedCancellationReachesHostAndDoesNotSuppressItsAdmittedCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new MemoryStream(Frame(LaunchBytes()));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var observed = false;
        var exit = await RuntimeApplication.RunActionHostAsync(input, output, stderr, cancellation.Token,
            (launch, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                observed = token.IsCancellationRequested;
                return new ActionHostComposition().RunAsync(launch, token);
            });
        Assert.True(observed);
        var completion = ReadCompletion(output);
        Assert.Equal(1, exit);
        Assert.Equal(exit, completion.ProcessExitCode);
        Assert.Equal(ActionHostStatus.Cancelled, completion.Status);
        Assert.True(completion.Accounting.IsCompleteZero);
        Assert.Equal(ActionHostTerminationReason.NotStarted, completion.TerminationReason);
        Assert.Empty(stderr.ToString());
    }

    [Fact]
    public async Task LinkedSignalTokenInterruptsInputEvenWhenItsReadIgnoresCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new IgnoringCancellationInput();
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var calls = 0;
        var run = RuntimeApplication.RunActionHostAsync(input, output, stderr, cancellation.Token,
            (_, _) => { calls++; throw new InvalidOperationException(PrivateCanary); });
        await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(5)));
        input.Pending.SetException(new IOException(PrivateCanary));
        Assert.Equal(0, calls);
        Assert.Empty(output.ToArray());
        Assert.Equal("APR_ACTION_HOST_CANCELLED" + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task LinkedEscapedHostFailureCannotInventAccountingOrState()
    {
        using var input = new MemoryStream(Frame(LaunchBytes()));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        Assert.Equal(1, await RuntimeApplication.RunActionHostAsync(input, output, stderr, default,
            (_, _) => throw new InvalidOperationException(PrivateCanary)));
        Assert.Empty(output.ToArray());
        Assert.Equal("APR_ACTION_HOST_INTERNAL" + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task LinkedForeignCompletionBuildIsRejectedBeforeOutput()
    {
        using var input = new MemoryStream(Frame(LaunchBytes()));
        using var output = new MemoryStream();
        using var stderr = new StringWriter();
        var exit = await RuntimeApplication.RunActionHostAsync(input, output, stderr, default, async (launch, _) =>
        {
            var completion = await new ActionHostComposition().RunAsync(launch, new CancellationToken(true));
            Assert.True(ActionHostCompletion.TryCreate("other-build", completion.Status, completion.Summary,
                completion.Annotations, out var foreign, completion.Accounting, completion.TerminationReason));
            return foreign!;
        });
        Assert.Equal(1, exit);
        Assert.Empty(output.ToArray());
        Assert.Equal("APR_ACTION_HOST_INTERNAL" + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task LinkedPartialOutputFailureIsNotRetriedOrReplaced()
    {
        using var input = new MemoryStream(Frame(LaunchBytes()));
        using var output = new PartialFailureOutput();
        using var stderr = new StringWriter();
        var exit = await RuntimeApplication.RunActionHostAsync(input, output, stderr, default,
            (launch, _) => new ActionHostComposition().RunAsync(launch, new CancellationToken(true)));
        Assert.Equal(1, exit);
        Assert.Equal(2, output.Writes);
        Assert.Equal(4, output.Length);
        Assert.Equal("APR_ACTION_HOST_INTERNAL" + Environment.NewLine, stderr.ToString());
    }

    internal static byte[] LaunchBytes()
    {
        var launch = WithCurrentBuild(ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch,
            includeToken: false).Launch);
        Assert.True(ActionHostJsonCodec.TryWriteLaunch(launch, out var bytes));
        Assert.True(ActionHostJsonCodec.TryReadLaunch(bytes, out _, out _));
        return bytes;
    }

    internal static ActionHostLaunchContract WithCurrentBuild(ActionHostLaunchContract launch)
    {
        Assert.True(ActionHostLaunchContract.TryCreate(launch.Inputs,
            launch.EventJsonPath, launch.EventJsonSha256, launch.RepositoryName,
            launch.RepositoryId, launch.RunId, launch.RunAttempt, launch.WorkflowPath,
            launch.WorkflowRef, launch.WorkflowSha, launch.ActionSourceSha, launch.PayloadSha256,
            RuntimeApplication.ActionHostBuildDiscriminator, launch.Cancellation,
            launch.ArtifactBridgeEndpoint, out var current));
        return current!;
    }

    internal static byte[] Frame(byte[] bytes)
    {
        var frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(frame, checked((uint)bytes.Length));
        bytes.CopyTo(frame, 4);
        return frame;
    }

    internal static ActionHostCompletion ReadCompletion(MemoryStream output)
    {
        var bytes = output.ToArray();
        Assert.True(bytes.Length > 4);
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        Assert.InRange(length, 1u, (uint)ActionHostContractBounds.MaximumCompletionDocumentBytes);
        Assert.Equal(bytes.Length - 4, checked((int)length));
        Assert.True(ActionHostJsonCodec.TryReadCompletion(bytes[4..], out var completion, out _));
        Assert.Equal(RuntimeApplication.ActionHostBuildDiscriminator, completion!.BuildDiscriminator);
        return completion;
    }

    private sealed class IgnoringCancellationInput : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> Pending { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            return new(Pending.Task);
        }
    }

    private sealed class PartialFailureOutput : MemoryStream
    {
        internal int Writes { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            if (Writes > 1) throw new IOException(PrivateCanary);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }
}
