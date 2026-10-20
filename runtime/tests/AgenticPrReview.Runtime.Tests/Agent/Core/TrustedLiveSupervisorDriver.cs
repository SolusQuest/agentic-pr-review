using System.Diagnostics;

namespace AgenticPrReview.Runtime.Tests.Agent.Core;

// Test driver only. Capturing output never establishes supervisor success.
internal sealed class TrustedLiveSupervisorDriver
{
    private readonly Process process;
    private readonly Task<string> output;
    private readonly Task<string> error;

    internal TrustedLiveSupervisorDriver(Process process)
    {
        this.process = process;
        output = process.StandardOutput.ReadToEndAsync();
        error = process.StandardError.ReadToEndAsync();
    }

    internal async Task<(string Output, string Error)> CollectAsync(TimeSpan exitTimeout, TimeSpan outputTimeout)
    {
        try { await WaitForExitOnlyAsync(process, exitTimeout); }
        catch (TimeoutException) { throw new TimeoutException("APR_R3_PROBE_EXIT_TIMEOUT"); }
        try { await Task.WhenAll(output, error).WaitAsync(outputTimeout); }
        catch (TimeoutException) { throw new TimeoutException("APR_R3_PROBE_OUTPUT_TIMEOUT"); }
        return (await output, await error);
    }

    internal static async Task WaitForExitOnlyAsync(Process process, TimeSpan timeout)
    {
        // WaitForExitAsync also waits for redirected pipe EOF, which descendants can retain.
        var elapsed = Stopwatch.StartNew();
        while (!process.HasExited)
        {
            if (elapsed.Elapsed >= timeout) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    internal static async Task ReapOwnedAsync(Process process)
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await WaitForExitOnlyAsync(process, TimeSpan.FromSeconds(5));
    }
}
