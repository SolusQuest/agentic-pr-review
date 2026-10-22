using AgenticPrReview.Runtime;
using System.Runtime.InteropServices;

if (args is not ["run"])
{
    return await RuntimeEntrypoint.RunAsync(args, Console.Out, Console.Error);
}

// The explicit product command owns framed stdin and process signals;
// retained direct-runtime commands keep their own lifecycle.
using var cancellation = new CancellationTokenSource();
PosixSignalRegistration? sigterm = null;
PosixSignalRegistration? sigint = null;
ConsoleCancelEventHandler? consoleCancellation = null;
try
{
    if (OperatingSystem.IsWindows())
    {
        consoleCancellation = (_, context) =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += consoleCancellation;
    }
    else
    {
        sigterm = Register(PosixSignal.SIGTERM);
        sigint = Register(PosixSignal.SIGINT);
    }

    return await RuntimeEntrypoint.RunAsync(
        args, Console.OpenStandardInput(), Console.OpenStandardOutput(),
        Console.Error, cancellation.Token);
}
catch
{
    // Neither initialization errors nor signal/stream failures may expose the
    // private launch or manufacture a successful completion.
    return await RuntimeApplication.ActionHostFailureAsync(Console.Error);
}
finally
{
    sigint?.Dispose();
    sigterm?.Dispose();
    if (consoleCancellation is not null)
    {
        Console.CancelKeyPress -= consoleCancellation;
    }
}

PosixSignalRegistration Register(PosixSignal signal) =>
    PosixSignalRegistration.Create(signal, context =>
    {
        context.Cancel = true;
        cancellation.Cancel();
    });
