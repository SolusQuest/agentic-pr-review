using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime;

namespace AgenticPrReview.Runtime.Tests;

public sealed class RuntimeApplicationTests
{
    [Fact]
    public async Task SuccessfulRunProducesDeterministicGoldenCompatibleFiles()
    {
        using var files = new TemporaryFiles();
        var input = files.InputPath;
        var first = await RunAsync(input, files.OutputPath, files.TracePath);

        Assert.Equal(0, first.ExitCode);
        Assert.Empty(first.StandardOutput);
        Assert.Empty(first.StandardError);
        var trace = await File.ReadAllBytesAsync(files.TracePath);
        var result = await File.ReadAllBytesAsync(files.OutputPath);
        var goldenRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "deterministic", "bootstrap");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(goldenRoot, "expected-trace.json")), trace);
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(goldenRoot, "expected-result.json")), result);
        Assert.False(trace.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.False(result.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.False(trace.EndsWith("\n"u8));
        Assert.False(result.EndsWith("\n"u8));

        using var resultDocument = JsonDocument.Parse(result);
        using var traceDocument = JsonDocument.Parse(trace);
        Assert.Equal(Hash(trace), resultDocument.RootElement.GetProperty("trace").GetProperty("sha256").GetString());
        Assert.Equal(Hash(await File.ReadAllBytesAsync(input)), resultDocument.RootElement.GetProperty("inputSha256").GetString());
        Assert.Equal(Hash(await File.ReadAllBytesAsync(input)), traceDocument.RootElement.GetProperty("inputSha256").GetString());
        Assert.False(traceDocument.RootElement.TryGetProperty("resultSha256", out _));
        Assert.False(resultDocument.RootElement.GetProperty("trace").TryGetProperty("path", out _));
    }

    [Fact]
    public async Task FrameworkDependentProcessProducesRepeatedExactGoldenBytes()
    {
        using var first = new TemporaryFiles();
        using var second = new TemporaryFiles();

        var firstProcess = await RunProcessAsync(first.InputPath, first.OutputPath, first.TracePath);
        var secondProcess = await RunProcessAsync(second.InputPath, second.OutputPath, second.TracePath);

        Assert.Equal(0, firstProcess.ExitCode);
        Assert.Equal(0, secondProcess.ExitCode);
        Assert.Empty(firstProcess.StandardOutput);
        Assert.Empty(secondProcess.StandardOutput);
        Assert.Empty(firstProcess.StandardError);
        Assert.Empty(secondProcess.StandardError);
        Assert.Equal(await File.ReadAllBytesAsync(first.TracePath), await File.ReadAllBytesAsync(second.TracePath));
        Assert.Equal(await File.ReadAllBytesAsync(first.OutputPath), await File.ReadAllBytesAsync(second.OutputPath));
        var goldenRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "deterministic", "bootstrap");
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(goldenRoot, "expected-trace.json")), await File.ReadAllBytesAsync(first.TracePath));
        Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(goldenRoot, "expected-result.json")), await File.ReadAllBytesAsync(first.OutputPath));
    }

    public static IEnumerable<object[]> InvalidInvocations =>
    [
        [new[] { "bad" }],
        [new[] { "review", "--input", "a", "--input", "b", "--output", "c", "--trace", "d" }],
        [new[] { "review", "--input", "a", "--output", "b" }],
    ];

    [Theory]
    [MemberData(nameof(InvalidInvocations))]
    public async Task InvalidInvocationReturnsStableUsageError(string[] args)
    {
        var application = new RuntimeApplication();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(args, stdout, stderr);

        Assert.Equal(2, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.StartsWith("APR_USAGE_INVALID:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InitializationFailureUsesTheRuntimeInternalContract()
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await RuntimeEntrypoint.RunAsync([], stdout, stderr, () => throw new InvalidOperationException("sentinel"));

        Assert.Equal(20, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.Equal("APR_RUNTIME_INTERNAL: Runtime initialization failed." + Environment.NewLine, stderr.ToString());
    }

    [Fact]
    public async Task RuntimeVersionMismatchWritesAValidFailureTrace()
    {
        using var files = new TemporaryFiles();
        var input = await files.CopyInputAsync(requestedRuntimeVersion: "incompatible");
        var result = await RunAsync(input, files.OutputPath, files.TracePath);

        Assert.Equal(10, result.ExitCode);
        Assert.False(File.Exists(files.OutputPath));
        Assert.True(File.Exists(files.TracePath));
        Assert.StartsWith("APR_RUNTIME_VERSION_MISMATCH:", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnsupportedAndSchemaInvalidInputsFailBeforeCreatingOutputs()
    {
        using var unsupportedFiles = new TemporaryFiles();
        await unsupportedFiles.UpdateInputAsync(node => node["protocolVersion"] = 2);
        var unsupported = await RunAsync(unsupportedFiles.InputPath, unsupportedFiles.OutputPath, unsupportedFiles.TracePath);
        Assert.Equal(10, unsupported.ExitCode);
        Assert.StartsWith("APR_PROTOCOL_VERSION_UNSUPPORTED:", unsupported.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(unsupportedFiles.OutputPath));
        Assert.False(File.Exists(unsupportedFiles.TracePath));

        using var invalidFiles = new TemporaryFiles();
        await invalidFiles.UpdateInputAsync(node => node.Remove("host"));
        var invalid = await RunAsync(invalidFiles.InputPath, invalidFiles.OutputPath, invalidFiles.TracePath);
        Assert.Equal(10, invalid.ExitCode);
        Assert.StartsWith("APR_INPUT_SCHEMA_INVALID:", invalid.StandardError, StringComparison.Ordinal);
        Assert.False(File.Exists(invalidFiles.OutputPath));
        Assert.False(File.Exists(invalidFiles.TracePath));
    }

    [Fact]
    public async Task SchemaValidUnboundedIntegersSurviveTheTypedInputBoundary()
    {
        using var files = new TemporaryFiles();
        await files.UpdateInputAsync(node =>
            node["subject"]!["changedFiles"]![0]!["additions"] = JsonValue.Create(2_147_483_648L));

        var result = await RunAsync(files.InputPath, files.OutputPath, files.TracePath);

        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(files.OutputPath));
        Assert.True(File.Exists(files.TracePath));
    }

    [Fact]
    public async Task ExistingDestinationIsNeverOverwritten()
    {
        using var files = new TemporaryFiles();
        await File.WriteAllTextAsync(files.OutputPath, "sentinel");

        var result = await RunAsync(files.InputPath, files.OutputPath, files.TracePath);

        Assert.Equal(2, result.ExitCode);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(files.OutputPath));
        Assert.False(File.Exists(files.TracePath));
    }

    [Fact]
    public async Task InvalidRuntimeResultReturnsInternalFailureAndFailureTrace()
    {
        using var files = new TemporaryFiles();
        var invalidFinding = new RuntimeFinding("low", "high", "correctness", "title", "body", null, 2, 1);
        var application = new RuntimeApplication(executor: new ReturningExecutor(new ExecutionOutcome([invalidFinding], [])));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(["review", "--input", files.InputPath, "--output", files.OutputPath, "--trace", files.TracePath], stdout, stderr);

        Assert.Equal(20, exitCode);
        Assert.False(File.Exists(files.OutputPath));
        Assert.True(File.Exists(files.TracePath));
        Assert.StartsWith("APR_OUTPUT_SELF_VALIDATION_FAILED:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderFailurePreservesThePrimaryErrorWhenFailureTraceCannotCommit()
    {
        using var files = new TemporaryFiles();
        var fileSystem = new FailingFileSystem(new PhysicalRuntimeFileSystem(), failCommit: true);
        var application = new RuntimeApplication(fileSystem, new ThrowingExecutor(new ProviderFailureException()));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(["review", "--input", files.InputPath, "--output", files.OutputPath, "--trace", files.TracePath], stdout, stderr);

        Assert.Equal(30, exitCode);
        Assert.Empty(stdout.ToString());
        Assert.StartsWith("APR_PROVIDER_FAILED:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultCommitFailureMayLeaveOnlyAnOrphanTrace()
    {
        using var files = new TemporaryFiles();
        var application = new RuntimeApplication(new FailingFileSystem(new PhysicalRuntimeFileSystem(), failSecondCommit: true));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(["review", "--input", files.InputPath, "--output", files.OutputPath, "--trace", files.TracePath], stdout, stderr);

        Assert.Equal(40, exitCode);
        Assert.True(File.Exists(files.TracePath));
        Assert.False(File.Exists(files.OutputPath));
        Assert.StartsWith("APR_RESULT_WRITE_FAILED:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DestinationCreatedAfterPreflightIsNotOverwritten()
    {
        using var files = new TemporaryFiles();
        var application = new RuntimeApplication(new FailingFileSystem(new PhysicalRuntimeFileSystem(), createDestinationOnSecondCommit: true));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(["review", "--input", files.InputPath, "--output", files.OutputPath, "--trace", files.TracePath], stdout, stderr);

        Assert.Equal(40, exitCode);
        Assert.Equal("sentinel", await File.ReadAllTextAsync(files.OutputPath));
        Assert.True(File.Exists(files.TracePath));
        Assert.StartsWith("APR_RESULT_WRITE_FAILED:", stderr.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "APR_TRACE_WRITE_FAILED:")]
    [InlineData(2, "APR_RESULT_WRITE_FAILED:")]
    public async Task StagingFailuresKeepTheirPrimaryDiagnostic(int failStageAt, string diagnostic)
    {
        using var files = new TemporaryFiles();
        var application = new RuntimeApplication(new FailingFileSystem(new PhysicalRuntimeFileSystem(), failStageAt: failStageAt, failDelete: true));
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = await application.RunAsync(["review", "--input", files.InputPath, "--output", files.OutputPath, "--trace", files.TracePath], stdout, stderr);

        Assert.Equal(40, exitCode);
        Assert.StartsWith(diagnostic, stderr.ToString(), StringComparison.Ordinal);
    }

    private static async Task<RunResult> RunAsync(string input, string output, string trace)
    {
        var application = new RuntimeApplication();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var exitCode = await application.RunAsync(["review", "--input", input, "--output", output, "--trace", trace], stdout, stderr);
        return new RunResult(exitCode, stdout.ToString(), stderr.ToString());
    }

    private static async Task<RunResult> RunProcessAsync(string input, string output, string trace)
    {
        var start = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "AgenticPrReview.Runtime.Tests.runtimeconfig.json"));
        start.ArgumentList.Add(typeof(RuntimeApplication).Assembly.Location);
        start.ArgumentList.Add("review");
        start.ArgumentList.Add("--input");
        start.ArgumentList.Add(input);
        start.ArgumentList.Add("--output");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--trace");
        start.ArgumentList.Add(trace);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start runtime process.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new RunResult(process.ExitCode, await standardOutput, await standardError);
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed record RunResult(int ExitCode, string StandardOutput, string StandardError);

    private sealed class ThrowingExecutor(Exception exception) : IRuntimeExecutor
    {
        public Task<ExecutionOutcome> ExecuteAsync(ReviewInput input) => Task.FromException<ExecutionOutcome>(exception);
    }

    private sealed class ReturningExecutor(ExecutionOutcome outcome) : IRuntimeExecutor
    {
        public Task<ExecutionOutcome> ExecuteAsync(ReviewInput input) => Task.FromResult(outcome);
    }

    private sealed class FailingFileSystem(
        IRuntimeFileSystem inner,
        bool failCommit = false,
        bool failSecondCommit = false,
        bool createDestinationOnSecondCommit = false,
        int failStageAt = 0,
        bool failDelete = false) : IRuntimeFileSystem
    {
        private int commits;
        private int stages;
        public bool Exists(string path) => inner.Exists(path);
        public Task<byte[]> ReadAllBytesAsync(string path) => inner.ReadAllBytesAsync(path);
        public Task<StagedFile> StageAsync(string finalPath, byte[] bytes)
        {
            stages++;
            return stages == failStageAt ? Task.FromException<StagedFile>(new IOException("Injected staging failure.")) : inner.StageAsync(finalPath, bytes);
        }
        public Task DeleteIfExistsAsync(string path) => failDelete ? Task.FromException(new IOException("Injected cleanup failure.")) : inner.DeleteIfExistsAsync(path);
        public Task CommitNoReplaceAsync(StagedFile stagedFile)
        {
            commits++;
            if (createDestinationOnSecondCommit && commits == 2)
            {
                File.WriteAllText(stagedFile.FinalPath, "sentinel");
            }

            if (failCommit || (failSecondCommit && commits == 2))
            {
                throw new IOException("Injected commit failure.");
            }

            return inner.CommitNoReplaceAsync(stagedFile);
        }
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), $"apr-runtime-tests-{Guid.NewGuid():N}");
        public TemporaryFiles()
        {
            Directory.CreateDirectory(root);
            InputPath = Path.Combine(root, "input.json");
            var source = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1", "cases", "bootstrap", "input.json");
            File.Copy(source, InputPath);
        }

        public string InputPath { get; }
        public string OutputPath => Path.Combine(root, "result.json");
        public string TracePath => Path.Combine(root, "trace.json");

        public async Task<string> CopyInputAsync(string requestedRuntimeVersion)
        {
            await UpdateInputAsync(node => node["requestedRuntimeVersion"] = requestedRuntimeVersion);
            return InputPath;
        }

        public async Task UpdateInputAsync(Action<JsonObject> update)
        {
            using var input = JsonDocument.Parse(await File.ReadAllBytesAsync(InputPath));
            var node = JsonNode.Parse(input.RootElement.GetRawText())!.AsObject();
            update(node);
            await File.WriteAllTextAsync(InputPath, node.ToJsonString());
        }

        public void Dispose()
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
