using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal sealed record ReplayProcessResult(string Code, ReplayChildReply? Reply);

internal static class ReplayProcess
{
    internal static readonly string[] EnvironmentNames = OperatingSystem.IsWindows()
        ? ["HOME", "TMP", "TEMP", "DOTNET_CLI_HOME", "DOTNET_EnableDiagnostics", "SystemRoot"]
        : ["HOME", "TMP", "TEMP", "TMPDIR", "DOTNET_CLI_HOME", "DOTNET_EnableDiagnostics", "LANG", "LC_ALL"];

    internal static string CreatePrivateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "apr-r5-replay-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            var user = WindowsIdentity.GetCurrent().User ?? throw new IOException();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(user);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).Create(security);
        }
        else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Directory.CreateDirectory(Path.Combine(root, "home"));
        Directory.CreateDirectory(Path.Combine(root, "tmp"));
        return root;
    }

    internal static bool Cleanup(string root)
    {
        try
        {
            var path = Path.GetFullPath(root);
            if (Path.GetDirectoryName(path) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) ||
                !Path.GetFileName(path).StartsWith("apr-r5-replay-", StringComparison.Ordinal) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            Directory.Delete(path, true);
            return !Directory.Exists(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    [UnconditionalSuppressMessage("SingleFile", "IL3000", Justification = "Assembly location is used only by the framework branch; Native AOT relaunches its own executable.")]
    internal static ProcessStartInfo StartInfo(string root)
    {
        var start = new ProcessStartInfo
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root,
        };
        // The framework fixture also disables dynamic code in runtimeconfig; that flag is not an AOT discriminator.
        var assembly = typeof(Program).Assembly.Location;
        if (string.IsNullOrEmpty(assembly))
            start.FileName = Environment.ProcessPath ?? throw new IOException();
        else
        {
            // Resolve the installed runtime host, never caller PATH or DOTNET_HOST_PATH.
            start.FileName = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "../../..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));
            var config = Path.ChangeExtension(assembly, ".runtimeconfig.json");
            if (!File.Exists(config)) config = Path.Combine(AppContext.BaseDirectory, "AgenticPrReview.Runtime.Tests.runtimeconfig.json");
            if (!File.Exists(config) || !File.Exists(assembly)) throw new IOException();
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(config);
            start.ArgumentList.Add(assembly);
        }
        start.ArgumentList.Add("replay-child");
        start.Environment.Clear();
        start.Environment["HOME"] = Path.Combine(root, "home");
        start.Environment["DOTNET_CLI_HOME"] = Path.Combine(root, "home");
        start.Environment["TMP"] = Path.Combine(root, "tmp");
        start.Environment["TEMP"] = Path.Combine(root, "tmp");
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        if (OperatingSystem.IsWindows()) start.Environment["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        else
        {
            start.Environment["TMPDIR"] = Path.Combine(root, "tmp");
            start.Environment["LANG"] = "C.UTF-8";
            start.Environment["LC_ALL"] = "C.UTF-8";
        }
        return start;
    }

    internal static async Task<ReplayProcessResult> RunAsync(ReplayChildInput input, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        Process? process = null;
        Task<byte[]>? output = null, error = null;
        var captureFailed = 0;
        try
        {
            process = Process.Start(StartInfo(input.Root));
            if (process is null) return new("process_failed", null);
            async Task<byte[]> Capture(Stream stream, int limit)
            {
                try { return await ReplayWire.ReadBoundedAsync(stream, limit, deadline.Token); }
                catch (Exception failure)
                {
                    if (failure is not OperationCanceledException) Interlocked.Exchange(ref captureFailed, 1);
                    await deadline.CancelAsync();
                    throw;
                }
            }
            output = Capture(process.StandardOutput.BaseStream, ReplayWire.ReplyLimit);
            error = Capture(process.StandardError.BaseStream, 4096);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(input, ReplayExecutionJson.Default.ReplayChildInput);
            if (bytes.Length > ReplayWire.InputLimit) throw new IOException();
            await process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var captured = await output;
            var errors = await error;
            if (process.ExitCode != 0 || errors.Length != 0) return new("process_failed", null);
            var reply = ReplayWire.Read(captured, ReplayExecutionJson.Default.ReplayChildReply, ReplayWire.ReplyLimit);
            return reply is null || reply.ProcessId != process.Id ? new("result_invalid", null) : new("completed", reply);
        }
        catch (OperationCanceledException)
        { return new(token.IsCancellationRequested ? "cancelled" : Volatile.Read(ref captureFailed) != 0 ? "process_failed" : "process_timeout", null); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return new("process_failed", null); }
        finally
        {
            await deadline.CancelAsync();
            if (process is not null)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(reap.Token);
                }
                finally { process.Dispose(); }
            }
            if (output is not null) { try { await output; } catch { /* Bounded capture is discarded, never logged. */ } }
            if (error is not null) { try { await error; } catch { /* See stdout capture. */ } }
        }
    }
}
