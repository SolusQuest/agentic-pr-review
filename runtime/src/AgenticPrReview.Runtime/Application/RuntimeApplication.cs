using System.Reflection;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgenticPrReview.Runtime;

public sealed class RuntimeApplication
{
    private static readonly string RuntimeVersion = GetRuntimeVersion();
    private const string Summary = "Deterministic fixture runtime completed without findings.";
    private const string Limitation = "No live provider was invoked.";
    private readonly IRuntimeFileSystem fileSystem;
    private readonly IRuntimeExecutor executor;
    private readonly SchemaContracts schemas;

    public RuntimeApplication(
        IRuntimeFileSystem? fileSystem = null,
        IRuntimeExecutor? executor = null,
        SchemaContracts? schemas = null)
    {
        this.fileSystem = fileSystem ?? new PhysicalRuntimeFileSystem();
        this.executor = executor ?? new DeterministicRuntimeExecutor();
        this.schemas = schemas ?? SchemaContracts.Load(typeof(RuntimeApplication).Assembly);
    }

    public async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        _ = stdout;
        try
        {
            if (args.Length > 0 && StringComparer.Ordinal.Equals(args[0], "review-live"))
            {
                return await LiveRuntimeApplication.RunAsync(args, fileSystem, schemas, stderr);
            }
            var invocation = ParseInvocation(args);
            return await RunInvocationAsync(invocation, stderr);
        }
        catch (RuntimeFailure failure)
        {
            await WriteDiagnosticAsync(stderr, failure.Code, failure.Message);
            return failure.ExitCode;
        }
        catch
        {
            await WriteDiagnosticAsync(stderr, "APR_RUNTIME_INTERNAL", "Runtime execution failed.");
            return 20;
        }
    }

    private async Task<int> RunInvocationAsync(Invocation invocation, TextWriter stderr)
    {
        byte[] inputBytes;
        try
        {
            inputBytes = await fileSystem.ReadAllBytesAsync(invocation.InputPath);
        }
        catch
        {
            throw new RuntimeFailure(40, "APR_INPUT_READ_FAILED", "Input could not be read.");
        }

        JsonDocument input;
        try
        {
            input = JsonDocument.Parse(inputBytes);
        }
        catch (JsonException)
        {
            throw new RuntimeFailure(10, "APR_INPUT_JSON_INVALID", "Input is not valid JSON.");
        }

        using (input)
        {
            var versionIsOne = ValidateInputVersion(input.RootElement);
            if (!IsInputSchemaValid(input.RootElement, versionIsOne))
            {
                throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");
            }

            var typedInput = JsonSerializer.Deserialize(input.RootElement, RuntimeJsonContext.Default.ReviewInput)
                ?? throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");

            var inputHash = Convert.ToHexString(SHA256.HashData(inputBytes)).ToLowerInvariant();
            if (typedInput.RequestedRuntimeVersion is not null &&
                !StringComparer.Ordinal.Equals(typedInput.RequestedRuntimeVersion, RuntimeVersion))
            {
                var failure = new RuntimeFailure(10, "APR_RUNTIME_VERSION_MISMATCH", "Requested runtime version does not match this binary.", true);
                return await FinishFailureAsync(invocation, inputHash, failure, stderr);
            }

            ExecutionOutcome execution;
            try
            {
                execution = await executor.ExecuteAsync(typedInput);
            }
            catch (ProviderFailureException)
            {
                var failure = new RuntimeFailure(30, "APR_PROVIDER_FAILED", "Provider execution failed.", true);
                return await FinishFailureAsync(invocation, inputHash, failure, stderr);
            }
            catch
            {
                var failure = new RuntimeFailure(20, "APR_RUNTIME_INTERNAL", "Runtime execution failed.", true);
                return await FinishFailureAsync(invocation, inputHash, failure, stderr);
            }

            var trace = CreateTrace(inputHash, execution.Diagnostics);
            byte[] traceBytes;
            try
            {
                traceBytes = RuntimeJson.SerializeTrace(trace);
                ValidateOutput(SchemaKind.Trace, traceBytes, "trace");
            }
            catch
            {
                throw new RuntimeFailure(20, "APR_OUTPUT_SELF_VALIDATION_FAILED", "Runtime trace failed self-validation.");
            }

            var result = CreateResult(inputHash, Convert.ToHexString(SHA256.HashData(traceBytes)).ToLowerInvariant(), execution);
            byte[] resultBytes;
            try
            {
                resultBytes = RuntimeJson.SerializeResult(result);
                ValidateOutput(SchemaKind.Result, resultBytes, "result");
            }
            catch
            {
                var failure = new RuntimeFailure(20, "APR_OUTPUT_SELF_VALIDATION_FAILED", "Runtime result failed self-validation.", true);
                return await FinishFailureAsync(invocation, inputHash, failure, stderr);
            }

            StagedFile stagedTrace;
            try
            {
                stagedTrace = await fileSystem.StageAsync(invocation.TracePath, traceBytes);
            }
            catch
            {
                throw new RuntimeFailure(40, "APR_TRACE_WRITE_FAILED", "Trace could not be staged.");
            }

            StagedFile stagedResult;
            try
            {
                stagedResult = await fileSystem.StageAsync(invocation.OutputPath, resultBytes);
            }
            catch
            {
                await TryDeleteAsync(stagedTrace.TempPath);
                throw new RuntimeFailure(40, "APR_RESULT_WRITE_FAILED", "Result could not be staged.");
            }

            try
            {
                await fileSystem.CommitNoReplaceAsync(stagedTrace);
            }
            catch
            {
                await TryDeleteAsync(stagedTrace.TempPath);
                await TryDeleteAsync(stagedResult.TempPath);
                throw new RuntimeFailure(40, "APR_TRACE_WRITE_FAILED", "Trace could not be committed.");
            }

            try
            {
                await fileSystem.CommitNoReplaceAsync(stagedResult);
            }
            catch
            {
                await TryDeleteAsync(stagedResult.TempPath);
                throw new RuntimeFailure(40, "APR_RESULT_WRITE_FAILED", "Result could not be committed.");
            }

            return 0;
        }
    }

    private async Task<int> FinishFailureAsync(Invocation invocation, string inputHash, RuntimeFailure failure, TextWriter stderr)
    {
        if (failure.AttemptFailureTrace)
        {
            StagedFile? staged = null;
            try
            {
                var trace = CreateTrace(inputHash, [new RuntimeDiagnostic(failure.Code, failure.Message, "error")], "failure");
                var bytes = RuntimeJson.SerializeTrace(trace);
                ValidateOutput(SchemaKind.Trace, bytes, "failure trace");
                staged = await fileSystem.StageAsync(invocation.TracePath, bytes);
                await fileSystem.CommitNoReplaceAsync(staged);
            }
            catch
            {
                // The first pipeline error remains authoritative.
                if (staged is not null)
                {
                    await TryDeleteAsync(staged.TempPath);
                }
            }
        }

        await WriteDiagnosticAsync(stderr, failure.Code, failure.Message);
        return failure.ExitCode;
    }

    private void ValidateOutput(SchemaKind kind, byte[] bytes, string name)
    {
        using var document = JsonDocument.Parse(bytes);
        if (!schemas.IsValid(kind, document.RootElement) ||
            (kind == SchemaKind.Result && !SemanticValidation.HasValidFindingLocations(document.RootElement)))
        {
            throw new InvalidOperationException($"The {name} is invalid.");
        }
    }

    private bool IsInputSchemaValid(JsonElement input, bool versionIsOne)
    {
        if (schemas.IsValid(SchemaKind.Input, input))
        {
            return true;
        }

        if (!versionIsOne)
        {
            return false;
        }

        var normalized = JsonNode.Parse(input.GetRawText())!.AsObject();
        normalized["protocolVersion"] = 1;
        using var document = JsonDocument.Parse(normalized.ToJsonString());
        return schemas.IsValid(SchemaKind.Input, document.RootElement);
    }

    private static bool ValidateInputVersion(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("protocolVersion", out var version) ||
            version.ValueKind != JsonValueKind.Number || !TryClassifyJsonInteger(version.GetRawText(), out var isOne))
        {
            throw new RuntimeFailure(10, "APR_INPUT_SCHEMA_INVALID", "Input does not satisfy ReviewInputV1.");
        }

        if (!isOne)
        {
            throw new RuntimeFailure(10, "APR_PROTOCOL_VERSION_UNSUPPORTED", "Input protocol version is unsupported.");
        }

        return true;
    }

    private static bool TryClassifyJsonInteger(string number, out bool isOne)
    {
        isOne = false;
        var exponentIndex = number.IndexOfAny(['e', 'E']);
        var mantissa = exponentIndex < 0 ? number : number[..exponentIndex];
        var exponentText = exponentIndex < 0 ? "0" : number[(exponentIndex + 1)..];
        var negativeExponent = exponentText.StartsWith('-');
        var exponentDigits = exponentText.TrimStart('+', '-').TrimStart('0');
        if (exponentDigits.Length == 0)
        {
            exponentDigits = "0";
            negativeExponent = false;
        }

        var decimalIndex = mantissa.IndexOf('.');
        var whole = decimalIndex < 0 ? mantissa : mantissa[..decimalIndex];
        var fraction = decimalIndex < 0 ? "" : mantissa[(decimalIndex + 1)..];
        if (whole.Length == 0 || (decimalIndex >= 0 && fraction.Length == 0) ||
            !whole.All(character => character is '-' or >= '0' and <= '9') || !fraction.All(char.IsDigit))
        {
            return false;
        }

        var negative = whole.StartsWith('-');
        var coefficient = (negative ? whole[1..] : whole) + fraction;
        coefficient = coefficient.TrimStart('0');
        if (coefficient.Length == 0)
        {
            return true;
        }

        var trailingZeros = coefficient.Length - coefficient.TrimEnd('0').Length;
        var exponentComparedToFraction = CompareNatural(exponentDigits, fraction.Length);
        var integral = !negativeExponent
            ? exponentComparedToFraction >= 0 || trailingZeros >= fraction.Length - int.Parse(exponentDigits, CultureInfo.InvariantCulture)
            : trailingZeros >= fraction.Length && CompareNatural(exponentDigits, trailingZeros - fraction.Length) <= 0;
        if (!integral)
        {
            return false;
        }

        if (!negative && exponentComparedToFraction == 0 && coefficient == "1")
        {
            isOne = true;
        }
        else if (!negative && !negativeExponent && exponentComparedToFraction < 0)
        {
            var scale = fraction.Length - int.Parse(exponentDigits, CultureInfo.InvariantCulture);
            isOne = coefficient.Length == scale + 1 && coefficient[0] == '1' && coefficient[1..].All(character => character == '0');
        }
        else if (!negative && negativeExponent)
        {
            var scale = fraction.Length + int.Parse(exponentDigits, CultureInfo.InvariantCulture);
            isOne = trailingZeros == scale && coefficient.TrimEnd('0') == "1";
        }

        return true;
    }

    private static int CompareNatural(string digits, int value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        return digits.Length != text.Length ? digits.Length.CompareTo(text.Length) : StringComparer.Ordinal.Compare(digits, text);
    }

    private Invocation ParseInvocation(string[] args)
    {
        if (args.Length == 0 || !StringComparer.Ordinal.Equals(args[0], "review"))
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Expected review --input <path> --output <path> --trace <path>.");
        }

        string? input = null;
        string? output = null;
        string? trace = null;
        for (var index = 1; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length)
            {
                throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Expected review --input <path> --output <path> --trace <path>.");
            }

            var value = args[index + 1];
            switch (args[index])
            {
                case "--input" when input is null:
                    input = value;
                    break;
                case "--output" when output is null:
                    output = value;
                    break;
                case "--trace" when trace is null:
                    trace = value;
                    break;
                default:
                    throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Expected review --input <path> --output <path> --trace <path>.");
            }
        }

        if (input is null || output is null || trace is null)
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Expected review --input <path> --output <path> --trace <path>.");
        }

        Invocation invocation;
        try
        {
            invocation = new Invocation(
                Path.GetFullPath(input),
                Path.GetFullPath(output),
                Path.GetFullPath(trace),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        }
        catch (ArgumentException)
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Input, output, and trace paths must be valid.");
        }
        catch (NotSupportedException)
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Input, output, and trace paths must be valid.");
        }
        catch (PathTooLongException)
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Input, output, and trace paths must be valid.");
        }
        catch (System.Security.SecurityException)
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Input, output, and trace paths must be valid.");
        }

        if (invocation.HasConflictingPaths || fileSystem.Exists(invocation.OutputPath) || fileSystem.Exists(invocation.TracePath))
        {
            throw new RuntimeFailure(2, "APR_USAGE_INVALID", "Input, output, and trace paths must be distinct and new.");
        }

        return invocation;
    }

    private static string GetRuntimeVersion() =>
        typeof(RuntimeApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("Runtime version metadata is missing.");

    private async Task TryDeleteAsync(string path)
    {
        try
        {
            await fileSystem.DeleteIfExistsAsync(path);
        }
        catch
        {
            // Cleanup never replaces the first pipeline failure.
        }
    }

    private static ReviewTrace CreateTrace(string inputHash, RuntimeDiagnostic[] diagnostics, string fixture = "bootstrap") =>
        new(1, RuntimeVersion, inputHash, "deterministic-fixture", fixture, [], [], diagnostics);

    private static ReviewResult CreateResult(string inputHash, string traceHash, ExecutionOutcome outcome) =>
        new(1, RuntimeVersion, inputHash, Summary, outcome.Findings, [Limitation], [], [], new ReviewTraceReference(null, traceHash));

    private static Task WriteDiagnosticAsync(TextWriter stderr, string code, string message) =>
        stderr.WriteLineAsync($"{code}: {message}");
}

public static class RuntimeEntrypoint
{
    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr, Func<RuntimeApplication>? createApplication = null)
    {
        try
        {
            return await (createApplication ?? (() => new RuntimeApplication()))().RunAsync(args, stdout, stderr);
        }
        catch
        {
            await stderr.WriteLineAsync("APR_RUNTIME_INTERNAL: Runtime initialization failed.");
            return 20;
        }
    }
}

internal sealed record Invocation(string InputPath, string OutputPath, string TracePath, StringComparer PathComparer)
{
    public bool HasConflictingPaths =>
        PathComparer.Equals(InputPath, OutputPath) ||
        PathComparer.Equals(InputPath, TracePath) ||
        PathComparer.Equals(OutputPath, TracePath);
}

internal sealed class RuntimeFailure(int exitCode, string code, string message, bool attemptFailureTrace = false) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
    public string Code { get; } = code;
    public bool AttemptFailureTrace { get; } = attemptFailureTrace;
}
