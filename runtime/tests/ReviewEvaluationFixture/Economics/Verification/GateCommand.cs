using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GateCommand
{
    internal static async Task<int> InvokeAsync(string[] args)
    {
        try
        {
            if (args is ["r6-gate", "select", "--fixtures", var fixtures, "--commit", var commit, "--tree", var tree, "--mode", var mode])
            {
                var selected = Select(fixtures);
                Require(selected.SourceCommit == commit && selected.SourceTree == tree && selected.SourceClean && selected.Mode == mode);
                Console.WriteLine(JsonSerializer.Serialize(selected, GateJson.Default.GateSelection));
            }
            else if (args is ["r6-gate", "produce", "--fixtures", var corpus])
            {
                var report = await GateProducer.RunAsync(corpus);
                Console.WriteLine(JsonSerializer.Serialize(report, GateJson.Default.GateReport));
            }
            else if (args is ["r6-gate", "verify", "--fixtures", var inputs, "--selection", var selectedPath,
                "--report", var reportPath, "--stderr", var errorPath, "--projection", var projectionPath])
            {
                Require(ReadFile(errorPath, 4096, allowEmpty: true).Length == 0);
                var selected = PricingJson.ReadValue(ReadFile(selectedPath, 4096), GateJson.Default.GateSelection, 4096, 4);
                Require(selected is not null && selected == Select(inputs) && selected.SourceClean);
                var (verdict, projection) = GateVerifier.Verify(ReadFile(reportPath, MaximumBytes), selected!);
                File.WriteAllBytes(projectionPath, projection);
                Console.WriteLine(JsonSerializer.Serialize(verdict, GateJson.Default.GateVerdict));
            }
            else if (args is ["r6-gate", "mutate", "--report", var original, "--mutation", var mutation])
            {
                // A deliberately successful producer of unacceptable output.
                Console.Write(Encoding.UTF8.GetString(GateMutations.Create(ReadFile(original, MaximumBytes), mutation)));
            }
            else if (args is ["r6-gate", "network-probe"])
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try { await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, 9), deadline.Token); }
                catch (Exception error) when (error is SocketException or OperationCanceledException) { }
                Console.WriteLine("r6_gate_network_probe");
            }
            else throw new InvalidOperationException("r6_gate_arguments_invalid");
            return 0;
        }
        catch (Exception error)
        {
            // Never expose candidate bytes, filesystem paths or exceptions.
            // The supervisor can retain bounded diagnostics privately without
            // turning untrusted exception text into a public log channel.
            if (Environment.GetEnvironmentVariable("R6_GATE_DIAGNOSTICS_FILE") is { Length: > 0 } diagnostic)
            {
                try
                {
                    var detail = error.ToString();
                    File.WriteAllText(diagnostic, detail[..Math.Min(detail.Length, 16384)]);
                }
                catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            Console.Error.WriteLine("r6_gate_rejected");
            return 1;
        }
    }

    private static byte[] ReadFile(string path, int maximum, bool allowEmpty = false)
    {
        using var stream = File.OpenRead(path);
        Require(stream.Length <= maximum && (allowEmpty || stream.Length > 0));
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        Require(stream.ReadByte() == -1);
        return bytes;
    }
}
