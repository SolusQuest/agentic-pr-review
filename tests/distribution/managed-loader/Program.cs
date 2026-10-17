using System;
using System.Buffers.Binary;
using System.Collections;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    [DllImport("aprprobe")]
    private static extern int apr_probe_identity();

    private static async Task Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        var input = Console.OpenStandardInput();
        var header = new byte[4];
        await input.ReadExactlyAsync(header);
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length == 0 || length > 159542) return;
        var bytes = new byte[length];
        await input.ReadExactlyAsync(bytes);
        using var launch = JsonDocument.Parse(bytes);
        var mode = launch.RootElement.TryGetProperty("mode", out var selected) ? selected.GetString() : "normal";
        using var signal = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            if (mode != "hang") cancellation.Cancel();
        });
        if (mode is "hold" or "cancel" or "hang")
        {
            File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "ready"), "ready");
            try
            {
                while (mode != "hold" || !File.Exists(Path.Combine(Environment.CurrentDirectory, "release")))
                    await Task.Delay(10, cancellation.Token);
            }
            catch (OperationCanceledException) { }
        }
        if (mode == "empty") return;
        Console.Error.WriteLine("PRIVATE_MANAGED_STDERR_CANARY");
        var output = Console.OpenStandardOutput();
        if (mode == "oversized")
        {
            BinaryPrimitives.WriteUInt32BigEndian(header, 16385);
            await output.WriteAsync(header);
            await Task.Delay(60000);
            return;
        }
        var env = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(item => (string)item.Key, item => (string?)item.Value);
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
#if POISON
            entry = "poison-entry",
#else
            entry = "verified-entry",
#endif
            managed = Dependency.Identity.Managed,
            resource = Dependency.Identity.Resource,
            native = apr_probe_identity(),
            framework = typeof(object).Assembly.Location,
            assembly = typeof(Program).Assembly.Location,
            args,
            env,
            cancelled = cancellation.IsCancellationRequested,
            input = launch.RootElement.Clone()
        });
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)body.Length);
        await output.WriteAsync(header);
        await output.WriteAsync(body);
        if (mode == "trailing") await output.WriteAsync(new byte[] { 32 });
    }
}
