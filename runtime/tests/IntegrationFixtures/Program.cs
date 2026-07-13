using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var scenario = Option(args, "--scenario") ?? "success";
var inputPath = Option(args, "--input");
var outputPath = Option(args, "--output");
var tracePath = Option(args, "--trace");
var probePathArgument = Option(args, "--probe");

if (scenario is "timeout" or "hang")
{
    await Task.Delay(TimeSpan.FromMinutes(10));
    return 0;
}

if (scenario.StartsWith("exit-", StringComparison.Ordinal))
{
    var exitCode = int.Parse(scenario[5..]);
    var diagnostic = exitCode switch
    {
        2 => "APR_USAGE_INVALID: integration fixture usage failure.",
        10 => "APR_INPUT_SCHEMA_INVALID: integration fixture contract failure.",
        20 => "APR_RUNTIME_INTERNAL: integration fixture runtime failure.",
        30 => "APR_PROVIDER_FAILED: integration fixture provider failure.",
        40 => "APR_RESULT_WRITE_FAILED: integration fixture file failure.",
        _ => "integration fixture exit failure.",
    };
    Console.Error.WriteLine(diagnostic);
    return exitCode;
}

if (scenario == "unknown-exit")
{
    return 77;
}

if (inputPath is null || outputPath is null || tracePath is null)
{
    Console.Error.WriteLine("APR_USAGE_INVALID: integration fixture paths are required.");
    return 2;
}

if (scenario is "invalid-json" or "schema-invalid-input" or "protocol-version")
{
    Console.Error.WriteLine(scenario switch
    {
        "invalid-json" => "APR_INPUT_JSON_INVALID: integration fixture input failure.",
        "schema-invalid-input" => "APR_INPUT_SCHEMA_INVALID: integration fixture input failure.",
        _ => "APR_PROTOCOL_VERSION_UNSUPPORTED: integration fixture protocol failure.",
    });
    return 10;
}

if (scenario is "missing-result" or "missing-trace")
{
    var valid = BuildValidFiles(inputPath);
    if (scenario == "missing-result")
    {
        await File.WriteAllTextAsync(tracePath, valid.Trace);
    }
    else
    {
        await File.WriteAllTextAsync(outputPath, valid.Result);
    }
    return 0;
}

if (scenario is "malformed-result" or "partial-result" or "truncated-result")
{
    await File.WriteAllTextAsync(outputPath, "{\"protocolVersion\":1");
    await File.WriteAllTextAsync(tracePath, BuildValidFiles(inputPath).Trace);
    return 0;
}

if (scenario is "partial-trace" or "truncated-trace")
{
    var valid = BuildValidFiles(inputPath);
    await File.WriteAllTextAsync(outputPath, valid.Result);
    await File.WriteAllTextAsync(tracePath, "{\"protocolVersion\":1");
    return 0;
}

if (scenario is "schema-invalid-result" or "semantic-invalid-result")
{
    await File.WriteAllTextAsync(outputPath, scenario == "schema-invalid-result" ? "{}" : BuildSemanticInvalidResult());
    await File.WriteAllTextAsync(tracePath, BuildValidFiles(inputPath).Trace);
    return 0;
}

if (scenario is "schema-invalid-trace" or "semantic-invalid-trace")
{
    var valid = BuildValidFiles(inputPath);
    await File.WriteAllTextAsync(outputPath, valid.Result);
    await File.WriteAllTextAsync(tracePath, scenario == "schema-invalid-trace" ? "{}" : valid.Trace.Replace("\"mode\":\"deterministic-fixture\"", "\"mode\":\"live-provider\""));
    return 0;
}

if (scenario.StartsWith("missing-result-", StringComparison.Ordinal) || scenario is "result-trace-path" or "trace-result-sha")
{
    var contractFiles = BuildValidFiles(inputPath);
    var result = JsonNode.Parse(contractFiles.Result)!.AsObject();
    var trace = JsonNode.Parse(contractFiles.Trace)!.AsObject();
    switch (scenario)
    {
        case "missing-result-inputsha": result.Remove("inputSha256"); break;
        case "missing-result-trace": result.Remove("trace"); break;
        case "missing-result-trace-sha": result["trace"]!.AsObject().Remove("sha256"); break;
        case "result-trace-path": result["trace"]!["path"] = "trace.json"; break;
        case "trace-result-sha": trace["resultSha256"] = new string('0', 64); break;
    }
    await File.WriteAllTextAsync(outputPath, result.ToJsonString());
    await File.WriteAllTextAsync(tracePath, trace.ToJsonString());
    return 0;
}

if (scenario is "unsafe-result-directory" or "unsafe-trace-directory" or "unsafe-result-symlink" or "unsafe-trace-symlink" or "unsafe-result-oversized" or "unsafe-trace-oversized")
{
    var valid = BuildValidFiles(inputPath);
    if (scenario.Contains("directory", StringComparison.Ordinal))
    {
        Directory.CreateDirectory(scenario.StartsWith("unsafe-result", StringComparison.Ordinal) ? outputPath : tracePath);
    }
    else if (scenario.Contains("symlink", StringComparison.Ordinal))
    {
        File.CreateSymbolicLink(scenario.StartsWith("unsafe-result", StringComparison.Ordinal) ? outputPath : tracePath, inputPath);
    }
    else
    {
        var oversizedPath = scenario.StartsWith("unsafe-result", StringComparison.Ordinal) ? outputPath : tracePath;
        var size = scenario.StartsWith("unsafe-result", StringComparison.Ordinal) ? 8 * 1024 * 1024 + 1 : 4 * 1024 * 1024 + 1;
        await File.WriteAllBytesAsync(oversizedPath, new byte[size]);
    }
    if (scenario.StartsWith("unsafe-result", StringComparison.Ordinal))
    {
        await File.WriteAllTextAsync(tracePath, valid.Trace);
    }
    else
    {
        await File.WriteAllTextAsync(outputPath, valid.Result);
    }
    return 0;
}

if (scenario == "privacy-diagnostic")
{
    await File.WriteAllTextAsync(tracePath, BuildPrivacyFailureTrace(inputPath));
    Console.Error.WriteLine("APR_RUNTIME_INTERNAL: Authorization=Bearer privacy_authorization_secret token=ghp_privacy_fixture_token path=[C:\\private\\raw.json]");
    return 20;
}

if (scenario == "privacy-host-sinks")
{
    await File.WriteAllTextAsync(tracePath, BuildPrivacyFailureTrace(inputPath));
    Console.Error.WriteLine("APR_RUNTIME_INTERNAL: Authorization=Bearer privacy_authorization_secret token=ghp_privacy_fixture_token path=[C:\\private\\raw.json]");
    return 20;
}

var files = BuildValidFiles(inputPath, scenario);
await File.WriteAllTextAsync(outputPath, files.Result);
await File.WriteAllTextAsync(tracePath, files.Trace);

if (scenario == "env-probe")
{
    var probePath = probePathArgument ?? Environment.GetEnvironmentVariable("AGENTIC_REVIEW_ENV_PROBE_PATH");
    if (probePath is null) throw new InvalidOperationException("env probe path is required");
    var probe = new
    {
        githubToken = Environment.GetEnvironmentVariable("GITHUB_TOKEN") is not null,
        githubAction = Environment.GetEnvironmentVariable("GITHUB_ACTION") is not null,
        agenticReviewApiKey = Environment.GetEnvironmentVariable("AGENTIC_REVIEW_API_KEY") is not null,
        anthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") is not null,
        sentinel = Environment.GetEnvironmentVariable("INTEGRATION_SECRET_SENTINEL") is not null,
    };
    await File.WriteAllTextAsync(probePath, JsonSerializer.Serialize(probe));
}

return 0;

static string? Option(string[] values, string name)
{
    var index = Array.IndexOf(values, name);
    return index >= 0 && index + 1 < values.Length ? values[index + 1] : null;
}

static (string Result, string Trace) BuildValidFiles(string inputPath, string scenario = "success")
{
    var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))).ToLowerInvariant();
    var traceObject = new
    {
        protocolVersion = 1,
        runtimeVersion = scenario == "version-mismatch" ? "0.1.0-other" : "0.1.0-dev",
        inputSha256 = scenario == "input-hash-mismatch" ? new string('0', 64) : inputHash,
        mode = "deterministic-fixture",
        fixture = "integration",
        toolCalls = Array.Empty<object>(),
        warnings = Array.Empty<string>(),
        diagnostics = Array.Empty<object>(),
    };
    var trace = JsonSerializer.Serialize(traceObject);
    var traceHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trace))).ToLowerInvariant();
    var resultObject = new
    {
        protocolVersion = 1,
        runtimeVersion = "0.1.0-dev",
        inputSha256 = scenario == "input-hash-mismatch" ? new string('0', 64) : inputHash,
        summary = "Integration fixture completed without findings.",
        findings = Array.Empty<object>(),
        limitations = new[] { "No live provider was invoked." },
        warnings = Array.Empty<string>(),
        diagnostics = Array.Empty<object>(),
        trace = new { sha256 = scenario == "trace-hash-mismatch" ? new string('0', 64) : traceHash },
    };
    return (JsonSerializer.Serialize(resultObject), trace);
}

static string BuildSemanticInvalidResult()
{
    return JsonSerializer.Serialize(new
    {
        protocolVersion = 1,
        runtimeVersion = "0.1.0-dev",
        summary = "Semantic-invalid integration fixture.",
        findings = new[]
        {
            new
            {
                severity = "medium",
                confidence = "high",
                category = "correctness",
                title = "Invalid range",
                body = "The end line precedes the start line.",
                path = "src/fixture.cs",
                startLine = 2,
                endLine = 1,
            },
        },
        limitations = Array.Empty<string>(),
        warnings = Array.Empty<string>(),
        diagnostics = Array.Empty<object>(),
    });
}

static string BuildPrivacyFailureTrace(string inputPath)
{
    var inputHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(inputPath))).ToLowerInvariant();
    return JsonSerializer.Serialize(new
    {
        protocolVersion = 1,
        runtimeVersion = "0.1.0-dev",
        inputSha256 = inputHash,
        mode = "deterministic-fixture",
        fixture = "privacy",
        toolCalls = Array.Empty<object>(),
        warnings = Array.Empty<string>(),
        diagnostics = new[]
        {
            new
            {
                code = "APR_RUNTIME_INTERNAL",
                message = "Authorization: *** token: *** path: <path>",
                level = "error",
            },
        },
    });
}
