using System.Diagnostics;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal sealed record EconomicsProcessResult(string Code, EconomicsChildReady? Ready, EconomicsReceipt? Receipt);

internal static class EconomicsProcess
{
    internal static Task<EconomicsProcessResult> RunAsync(EconomicsChildInput input,
        Func<string?> credential, CancellationToken token) => RunWithStartAsync(input, credential, token, null);

    // Test-only launch substitution; the command never exposes this hook.
    internal static async Task<EconomicsProcessResult> RunWithStartAsync(EconomicsChildInput input,
        Func<string?> credential, CancellationToken token, Action<ProcessStartInfo>? configure,
        Action<EconomicsChildReady>? onReady = null)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(input.Plan.ChildSeconds));
        Process? process = null;
        Task<byte[]>? errors = null;
        EconomicsChildReady? ready = null;
        try
        {
            var start = ReplayProcess.StartInfo(input.Root);
            start.ArgumentList[^1] = "economics-child";
            if (input.Fault == EconomicsFault.StartFailure) start.FileName = Path.Combine(input.Root, "missing-economics-child");
            configure?.Invoke(start);
            process = Process.Start(start);
            if (process is null) return new("process_failed", null, null);
            async Task<byte[]> ReadErrors()
            {
                try { return await ReplayWire.ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token); }
                catch { await deadline.CancelAsync(); throw; }
            }
            errors = ReadErrors();
            await EconomicsWire.WriteAsync(process.StandardInput.BaseStream, input,
                EconomicsLiveJson.Default.EconomicsChildInput, EconomicsLiveLimits.InputBytes, deadline.Token);
            ready = await EconomicsWire.ReadAsync(process.StandardOutput.BaseStream,
                EconomicsLiveJson.Default.EconomicsChildReady, 16384, deadline.Token);
            if (!ValidReady(input, ready, process.Id)) return new("ready_invalid", ready, null);
            onReady?.Invoke(ready);
            // Only the validated ready frame opens the secret ingress. This delegate
            // is not invoked at all on dry-run or source/state/lease admission failure.
            string? secret = null;
            if (input.Transport == "live" || input.Fault == EconomicsFault.CredentialProbe)
            {
                secret = credential();
                if (secret is null) return new("credential_invalid", ready, null);
                _ = DeepSeekCredential.Create(secret);
            }
            await EconomicsWire.WriteAsync(process.StandardInput.BaseStream, new EconomicsSecretFrame(input.Operation,
                input.Lease.Id, secret), EconomicsLiveJson.Default.EconomicsSecretFrame, 16384, deadline.Token);
            process.StandardInput.Close();
            var reply = await EconomicsWire.ReadAsync(process.StandardOutput.BaseStream,
                EconomicsLiveJson.Default.EconomicsReceipt, EconomicsLiveLimits.ReplyBytes, deadline.Token);
            var extra = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(extra, deadline.Token) != 0) return new("receipt_invalid", ready, null);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0 || (await errors).Length != 0 || reply.ProcessId != process.Id || reply.Startup != ready.Startup)
                return new("process_failed", ready, null);
            return new("received", ready, reply);
        }
        catch (OperationCanceledException) { return new(token.IsCancellationRequested ? "caller_cancelled" : "deadline", ready, null); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        { return new("process_failed", ready, null); }
        finally
        {
            await deadline.CancelAsync();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); }
                        catch (Exception) when (process.HasExited) { }
                    }
                    using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(reap.Token);
                }
                catch { throw new ReplayProcessUnreaped(); }
                finally { process.Dispose(); }
            }
            if (errors is not null) { try { await errors; } catch { /* Private stderr is never published. */ } }
        }
    }

    internal static bool ValidReady(EconomicsChildInput input, EconomicsChildReady ready, int process) =>
        ready.Code == "ready" && ready.Operation == input.Operation && ready.PlanSha256 == input.PlanSha256 &&
        ready.Index == input.Slot.Index && ready.LeaseId == input.Lease.Id && ready.ProcessId == process &&
        Guid.TryParseExact(ready.Startup, "N", out _) && ready.SourceCommit == input.Plan.Source.Commit &&
        ready.SourceTree == input.Plan.Source.Tree && ready.SourceClean == input.Plan.Source.Clean &&
        ready.BuildSha256 == input.Plan.BuildSha256 && ready.Restored == (input.Predecessor is not null);
}
