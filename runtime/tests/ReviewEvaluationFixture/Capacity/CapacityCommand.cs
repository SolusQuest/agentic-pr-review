using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Capacity;

internal static class CapacityCommand
{
    internal static async Task<int> InvokeAsync(string[] args)
    {
        try
        {
            if (args is ["r7-capacity-child"])
            {
                var bytes = await ReplayWire.ReadBoundedAsync(Console.OpenStandardInput(), CapacitySpec.IpcBytes, default);
                CapacityInput? input;
                try { input = JsonSerializer.Deserialize(bytes, CapacityJson.Default.CapacityInput); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                CapacitySpec.Require(input is not null, "ipc_input");
                try
                {
                    var reply = await CapacityChild.RunAsync(input!);
                    var output = JsonSerializer.SerializeToUtf8Bytes(reply, CapacityJson.Default.CapacityReply);
                    CapacitySpec.Require(output.Length <= CapacitySpec.IpcBytes, "ipc_output");
                    await Console.OpenStandardOutput().WriteAsync(output);
                    return 0;
                }
                finally { CryptographicOperations.ZeroMemory(input!.Key); }
            }
            if (args is ["r7-capacity-run", "--corpus", { } corpusPath, "--out", { } outputPath])
            {
                var corpusBytes = File.ReadAllBytes(corpusPath);
                var corpus = CapacitySpec.Admit(corpusBytes);
                var receipts = new List<CapacityReceipt>();
                using var executionDeadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                var roots = new List<string>();
                CapacityHostReceipt[] host;
                var cleanup = true;
                try
                {
                    foreach (var group in new[] { corpus.Cases[..13], corpus.Cases[13..] })
                    {
                        var root = ReplayProcess.CreatePrivateRoot(); roots.Add(root);
                        var key = RandomNumberGenerator.GetBytes(32);
                        AcceptedLineage? lineage = null;
                        string? plaintextSha256 = null;
                        var history = new List<CapacityHistory>(); var fresh = false;
                        try
                        {
                            foreach (var selected in group)
                            {
                                var input = new CapacityInput(root, key, selected, lineage, history.ToArray(), fresh, plaintextSha256);
                                var reply = await RunProcessAsync(input, executionDeadline.Token);
                                var receipt = reply.Receipt;
                                CapacitySpec.Require(receipt.Id == selected.Id && receipt.ProcessId != Environment.ProcessId &&
                                    receipt.SourceCommit == EvaluationSource.Commit && receipt.SourceTree == EvaluationSource.Tree &&
                                    receipt.SourceClean == EvaluationSource.Clean, "child_identity");
                                receipts.Add(receipt);
                                Console.WriteLine($"r7_capacity_case id={selected.Id} code={receipt.Code} calls={receipt.Calls} tools={receipt.Tools}");
                                if (selected.Mode is "success" or "reset")
                                {
                                    CapacitySpec.Require(reply.Lineage is not null, "child_lineage");
                                    lineage = reply.Lineage;
                                    plaintextSha256 = reply.PlaintextSha256;
                                    if (selected.Mode == "reset") { history.Clear(); fresh = true; }
                                    history.Add(new(selected.Id, selected.Calls, selected.ToolsPerTurn));
                                }
                                else CapacitySpec.Require(reply.Lineage == lineage && reply.PlaintextSha256 == plaintextSha256, "child_preservation");
                            }
                        }
                        finally { CryptographicOperations.ZeroMemory(key); }
                    }
                    host = await new CapacityHostProbe().RunAsync(executionDeadline.Token);
                }
                finally
                {
                    foreach (var root in roots) cleanup &= ReplayProcess.Cleanup(root);
                }
                var report = new CapacityReport(CapacitySpec.Schema, "r7_capacity_passed",
                    Convert.ToHexStringLower(SHA256.HashData(corpusBytes)), EvaluationSource.Commit, EvaluationSource.Tree,
                    EvaluationSource.Clean, receipts.ToArray(), host, cleanup);
                if (CapacityVerifier.Verify(report, corpusBytes, requireClean: false) is { } reportFailure)
                    throw new InvalidOperationException(reportFailure);
                var reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, CapacityJson.Default.CapacityReport);
                CapacitySpec.Require(CapacitySpec.IsPrivate(reportBytes) && reportBytes.Length <= CapacitySpec.IpcBytes, "report_privacy");
                File.WriteAllBytes(outputPath, reportBytes);
                Console.WriteLine("APR_R7_CAPACITY_EXECUTED");
                return 0;
            }
            if (args is ["r7-capacity-verify", "--corpus", { } verifyCorpus, "--report", { } reportPath])
            {
                var bytes = File.ReadAllBytes(reportPath);
                CapacitySpec.Require(bytes.Length <= CapacitySpec.IpcBytes && CapacitySpec.IsPrivate(bytes), "report_size_privacy");
                var report = JsonSerializer.Deserialize(bytes, CapacityJson.Default.CapacityReport);
                CapacitySpec.Require(report is not null && report.SourceCommit == EvaluationSource.Commit &&
                    report.SourceTree == EvaluationSource.Tree && report.SourceClean == EvaluationSource.Clean, "verifier_source");
                var error = CapacityVerifier.Verify(report, File.ReadAllBytes(verifyCorpus));
                Console.WriteLine(error ?? "APR_R7_CAPACITY_VERIFIED");
                return error is null ? 0 : 1;
            }
            if (args is ["r7-capacity-parity", "--corpus", { } parityCorpus, { } framework, { } aot])
            {
                var first = ReadReport(framework); var second = ReadReport(aot);
                var corpus = File.ReadAllBytes(parityCorpus);
                CapacitySpec.Require(CapacityVerifier.Verify(first, corpus) is null && CapacityVerifier.Verify(second, corpus) is null,
                    "parity_admission");
                CapacitySpec.Require(CapacityVerifier.Equivalent(first, second), "parity");
                Console.WriteLine("APR_R7_CAPACITY_PARITY_VERIFIED");
                return 0;
            }
            CapacitySpec.Require(false, "arguments");
        }
        catch (Exception error)
        {
            // No arbitrary exception text, filenames, transcript or credential may become gate output.
            Console.Error.WriteLine(error is InvalidOperationException && error.Message.StartsWith("r7_capacity_", StringComparison.Ordinal)
                ? error.Message : "r7_capacity_failed");
        }
        return 1;
    }

    private static CapacityReport ReadReport(string path)
    {
        var bytes = File.ReadAllBytes(path);
        CapacitySpec.Require(bytes.Length <= CapacitySpec.IpcBytes && CapacitySpec.IsPrivate(bytes), "parity_input");
        return JsonSerializer.Deserialize(bytes, CapacityJson.Default.CapacityReport) ?? throw new InvalidOperationException("r7_capacity_parity_input");
    }

    internal static async Task<CapacityReply> RunProcessAsync(CapacityInput input, CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        var start = ReplayProcess.StartInfo(input.Root);
        start.ArgumentList[^1] = "r7-capacity-child";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("r7_capacity_process_start");
        async Task<byte[]> Capture(Stream stream, int limit)
        {
            try { return await ReplayWire.ReadBoundedAsync(stream, limit, timeout.Token); }
            catch { await timeout.CancelAsync(); throw; }
        }
        var output = Capture(process.StandardOutput.BaseStream, CapacitySpec.IpcBytes);
        var error = Capture(process.StandardError.BaseStream, 4096);
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(input, CapacityJson.Default.CapacityInput);
            CapacitySpec.Require(bytes.Length <= CapacitySpec.IpcBytes, "ipc_size");
            try { await process.StandardInput.BaseStream.WriteAsync(bytes, timeout.Token); }
            finally { CryptographicOperations.ZeroMemory(bytes); process.StandardInput.Close(); }
            await process.WaitForExitAsync(timeout.Token);
            var stdout = await output; var stderr = await error;
            if (process.ExitCode != 0 || stderr.Length != 0)
            {
                var stage = Encoding.UTF8.GetString(stderr).Trim();
                // Only a single bounded identifier from our child is eligible for public diagnostics.
                CapacitySpec.Require(stage.Length < 160 && stage.StartsWith("r7_capacity_", StringComparison.Ordinal) &&
                    stage.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'), "child_failed");
                throw new InvalidOperationException(stage);
            }
            var reply = JsonSerializer.Deserialize(stdout, CapacityJson.Default.CapacityReply);
            CapacitySpec.Require(reply is not null && reply.Receipt.ProcessId == process.Id, "ipc_identity");
            return reply!;
        }
        catch (OperationCanceledException) { throw new InvalidOperationException("r7_capacity_harness_timeout"); }
        finally
        {
            await timeout.CancelAsync();
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (process.HasExited) { }
            }
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(reap.Token);
            try { await output; } catch { }
            try { await error; } catch { }
        }
    }
}
