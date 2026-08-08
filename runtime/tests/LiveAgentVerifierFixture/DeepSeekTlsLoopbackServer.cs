using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed class DeepSeekTlsLoopbackServer : IDisposable
{
    private readonly VerifierScenario scenario;
    private readonly R3QualityCase testCase;
    private readonly byte[][] currentPublicInputs;
    private readonly string authorizationHash;
    private readonly bool providerCanaryValid;
    private readonly TcpListener listener;
    private readonly X509Certificate2 certificate;
    private readonly CancellationTokenSource timeout =
        new(TimeSpan.FromSeconds(15));
    private readonly Task run;
    private readonly VerifierProviderScript script;
    private readonly List<string> requestHashes = [];
    private string? failureCode;
    private int requestCount;
    private bool disposed;

    internal DeepSeekTlsLoopbackServer(
        VerifierScenario scenario,
        R3QualityCase testCase,
        ReviewedIdentity reviewedIdentity,
        string? expectedHistorySha256,
        IReadOnlyList<byte[]> currentPublicInputs,
        string authorizationHash,
        bool providerCanaryValid)
    {
        this.scenario = scenario;
        this.testCase = testCase;
        this.currentPublicInputs = currentPublicInputs
            .Select(value => value.ToArray())
            .ToArray();
        this.authorizationHash = authorizationHash;
        this.providerCanaryValid = providerCanaryValid;
        script = new VerifierProviderScript(
            scenario,
            testCase,
            reviewedIdentity,
            expectedHistorySha256);
        certificate = CreateCertificate();
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(backlog: 1);
        run = RunAsync();
    }

    internal VerifierWireProof Proof
    {
        get
        {
            var expected = script.ExpectedRequests;
            return new VerifierWireProof(
                failureCode is null &&
                    requestCount == expected &&
                    script.Completed,
                requestCount,
                FactoryCreateCount: 1,
                scenario == VerifierScenario.ContinuationRestore &&
                    requestHashes.Count > 0
                    ? requestHashes[0]
                    : null,
                script.ExpectedTerminalSha256,
                script.PriorFactSha256,
                script.HistoricalMessagesSha256,
                script.ExactReplayValidated,
                script.ReplayMutationMatrixValidated,
                failureCode,
                scenario == VerifierScenario.CanaryRouting &&
                    providerCanaryValid &&
                    script.CanaryRoutesValidated);
        }
    }

    internal async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext _,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(
            AddressFamily.InterNetwork,
            SocketType.Stream,
            ProtocolType.Tcp);
        try
        {
            await socket.ConnectAsync(
                (IPEndPoint)listener.LocalEndpoint,
                cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal void PinCertificateFor(SocketsHttpHandler handler)
    {
        var expected = certificate.RawDataMemory.ToArray();
        handler.SslOptions.RemoteCertificateValidationCallback =
            (_, presented, _, errors) =>
            {
                if (presented is null ||
                    errors == SslPolicyErrors.RemoteCertificateNameMismatch)
                {
                    return false;
                }

                var actual = presented.GetRawCertData();
                return actual.Length == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(actual, expected);
            };
    }

    internal async Task CompleteAsync()
    {
        try
        {
            await run;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            failureCode ??= "wire_server_failed";
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        timeout.Cancel();
        listener.Stop();
        timeout.Dispose();
        certificate.Dispose();
        foreach (var input in currentPublicInputs)
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            for (var index = 0; index < script.ExpectedRequests; index++)
            {
                using var client = await listener.AcceptTcpClientAsync(
                    timeout.Token);
                await using var tls = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false);
                await tls.AuthenticateAsServerAsync(
                    new SslServerAuthenticationOptions
                    {
                        EnabledSslProtocols =
                            SslProtocols.Tls12 | SslProtocols.Tls13,
                        ServerCertificate = certificate,
                    },
                    timeout.Token);
                var request = await ReadRequestAsync(tls, timeout.Token);
                requestCount++;
                requestHashes.Add(
                    LiveAgentFreshProcessDomain.RawSha256(request.Body));
                byte[] response;
                try
                {
                    VerifierWireOracle.ValidateHttp(
                        request,
                        authorizationHash);
                    VerifierWireOracle.ValidateBody(
                        request.Body,
                        currentPublicInputs,
                        scenario);
                    response = script.Respond(index, request.Body);
                }
                catch (VerifierWireException exception)
                {
                    failureCode = exception.Code;
                    response = "{}"u8.ToArray();
                }

                await WriteResponseAsync(
                    tls,
                    failureCode is null ? 200 : 500,
                    response,
                    timeout.Token);
                if (failureCode is not null)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            failureCode ??= "wire_timeout";
        }
        catch (AuthenticationException)
        {
            failureCode ??= "wire_tls_failed";
        }
        catch (IOException)
        {
            failureCode ??= "wire_io_failed";
        }
        catch (SocketException)
        {
            failureCode ??= "wire_socket_failed";
        }
        catch (Exception exception) when (exception is JsonException or
            InvalidOperationException or ArgumentException)
        {
            failureCode ??= "wire_protocol_failed";
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<CapturedDeepSeekRequest> ReadRequestAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var request = new MemoryStream();
        var buffer = new byte[4 * 1024];
        while (request.Length <= DeepSeekTransportPolicy.RequestBodyMaxBytes +
            32 * 1024)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                throw new IOException("The request ended before its body.");
            }

            request.Write(buffer, 0, read);
            var bytes = request.ToArray();
            var headerEnd = FindSequence(bytes, "\r\n\r\n"u8);
            if (headerEnd < 0)
            {
                continue;
            }

            var headerLength = headerEnd + 4;
            var lines = Encoding.ASCII.GetString(bytes, 0, headerEnd)
                .Split("\r\n", StringSplitOptions.None);
            var contentLengths = lines.Skip(1)
                .Where(line => line.StartsWith(
                    "Content-Length:",
                    StringComparison.OrdinalIgnoreCase))
                .Select(line => int.Parse(
                    line["Content-Length:".Length..].Trim(),
                    System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            if (contentLengths.Length != 1 || contentLengths[0] < 0)
            {
                throw new IOException("The request length is invalid.");
            }

            var contentLength = contentLengths[0];
            if (bytes.Length < headerLength + contentLength)
            {
                continue;
            }

            if (bytes.Length != headerLength + contentLength)
            {
                throw new IOException("The request has trailing bytes.");
            }

            return new CapturedDeepSeekRequest(
                lines[0],
                lines.Skip(1).ToArray(),
                bytes.AsSpan(headerLength, contentLength).ToArray());
        }

        throw new IOException("The request exceeded the bound.");
    }

    private static async Task WriteResponseAsync(
        Stream stream,
        int status,
        byte[] body,
        CancellationToken cancellationToken)
    {
        var reason = status == 200 ? "OK" : "Error";
        var headers = Encoding.ASCII.GetBytes(
            string.Concat(
                "HTTP/1.1 ",
                status.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                " ",
                reason,
                "\r\nContent-Type: application/json\r\nContent-Length: ",
                body.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                "\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(headers, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static int FindSequence(
        byte[] value,
        ReadOnlySpan<byte> sequence)
    {
        for (var offset = 0; offset <= value.Length - sequence.Length; offset++)
        {
            if (value.AsSpan(offset, sequence.Length).SequenceEqual(sequence))
            {
                return offset;
            }
        }

        return -1;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=api.deepseek.com",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("api.deepseek.com");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow.AddHours(1));
        const string password = "issue-111-loopback";
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12, password),
            password,
            X509KeyStorageFlags.Exportable |
                X509KeyStorageFlags.UserKeySet);
    }
}

internal sealed record CapturedDeepSeekRequest(
    string RequestLine,
    string[] HeaderLines,
    byte[] Body);

internal sealed class VerifierWireException(string code) : Exception
{
    internal string Code { get; } = code;
}

internal static class VerifierWireOracle
{
    private static readonly string[] TopLevelProperties =
    [
        "model",
        "messages",
        "stream",
        "thinking",
        "reasoning_effort",
        "max_tokens",
        "tools",
    ];

    private static readonly string[] AllowedHeaders =
    [
        "Authorization",
        "Content-Length",
        "Content-Type",
        "Host",
    ];

    internal static void ValidateHttp(
        CapturedDeepSeekRequest request,
        string authorizationHash)
    {
        if (!StringComparer.Ordinal.Equals(
                request.RequestLine,
                "POST /chat/completions HTTP/1.1"))
        {
            throw new VerifierWireException("wire_target_invalid");
        }

        var headers = request.HeaderLines.Select(line =>
        {
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                throw new VerifierWireException("wire_header_invalid");
            }

            return (
                Name: line[..separator],
                Value: line[(separator + 1)..].TrimStart());
        }).ToArray();
        if (!headers.Select(item => item.Name)
                .Order(StringComparer.OrdinalIgnoreCase)
                .SequenceEqual(
                    AllowedHeaders,
                    StringComparer.OrdinalIgnoreCase) ||
            headers.GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .Any(group => group.Count() != 1))
        {
            throw new VerifierWireException("wire_header_set_invalid");
        }

        string Header(string name) => headers.Single(item =>
            StringComparer.OrdinalIgnoreCase.Equals(item.Name, name)).Value;
        var authorization = Header("Authorization");
        var actualAuthorizationHash =
            LiveAgentFreshProcessDomain.RawSha256(
                Encoding.UTF8.GetBytes(authorization));
        if (!StringComparer.Ordinal.Equals(Header("Host"), "api.deepseek.com") ||
            !StringComparer.Ordinal.Equals(
                Header("Content-Type"),
                "application/json") ||
            !StringComparer.Ordinal.Equals(
                actualAuthorizationHash,
                authorizationHash) ||
            !int.TryParse(
                Header("Content-Length"),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out var contentLength) ||
            contentLength != request.Body.Length)
        {
            throw new VerifierWireException("wire_header_value_invalid");
        }
    }

    internal static void ValidateBody(
        byte[] body,
        IReadOnlyList<byte[]> currentPublicInputs,
        VerifierScenario? scenario = null)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.EnumerateObject().Select(item => item.Name)
                .SequenceEqual(TopLevelProperties, StringComparer.Ordinal) ||
            !StringComparer.Ordinal.Equals(
                root.GetProperty("model").GetString(),
                DeepSeekRequestWriter.Model) ||
            root.GetProperty("stream").GetBoolean() ||
            !StringComparer.Ordinal.Equals(
                root.GetProperty("thinking").GetProperty("type").GetString(),
                "enabled") ||
            !StringComparer.Ordinal.Equals(
                root.GetProperty("reasoning_effort").GetString(),
                "high") ||
            root.GetProperty("max_tokens").GetInt32() !=
                DeepSeekRequestWriter.MaxTokens)
        {
            throw new VerifierWireException("wire_body_shape_invalid");
        }

        var tools = root.GetProperty("tools").EnumerateArray().ToArray();
        var definitions = AgentToolRegistry.Definitions;
        if (tools.Length != definitions.Length)
        {
            throw new VerifierWireException("wire_tools_invalid");
        }

        for (var index = 0; index < tools.Length; index++)
        {
            var tool = tools[index];
            var function = tool.GetProperty("function");
            if (!tool.EnumerateObject().Select(item => item.Name)
                    .SequenceEqual(["type", "function"], StringComparer.Ordinal) ||
                !function.EnumerateObject().Select(item => item.Name)
                    .SequenceEqual(
                        ["name", "description", "parameters"],
                        StringComparer.Ordinal) ||
                !StringComparer.Ordinal.Equals(
                    tool.GetProperty("type").GetString(),
                    "function") ||
                !StringComparer.Ordinal.Equals(
                    function.GetProperty("name").GetString(),
                    definitions[index].Name) ||
                !StringComparer.Ordinal.Equals(
                    function.GetProperty("description").GetString(),
                    definitions[index].Description) ||
                !StringComparer.Ordinal.Equals(
                    function.GetProperty("parameters").GetRawText(),
                    definitions[index].SchemaJson))
            {
                throw new VerifierWireException("wire_tool_contract_invalid");
            }
        }

        var text = Encoding.UTF8.GetString(body);
        var forbidden = new[]
        {
            "APR111_PROVIDER_SECRET_CANARY",
            "APR111_STATE_KEY_CANARY",
            "APR111_GITHUB_CANARY",
            "APR111_ACTIONS_CANARY",
            "APR111_UNRELATED_WORKFLOW_CANARY",
            VerifierCanaries.PublicResult,
        };
        var untrustedRouteCanaries = new[]
        {
            VerifierCanaries.Repository,
            VerifierCanaries.Path,
            VerifierCanaries.Prompt,
        };
        if (forbidden.Any(value => text.Contains(value, StringComparison.Ordinal)) ||
            scenario != VerifierScenario.CanaryRouting &&
                untrustedRouteCanaries.Any(value =>
                    text.Contains(value, StringComparison.Ordinal)) ||
            currentPublicInputs.Any(input =>
                Encoding.UTF8.GetString(input).Contains(
                    "APR111_RANDOM_",
                    StringComparison.Ordinal)))
        {
            throw new VerifierWireException("wire_canary_invalid");
        }
    }
}

internal sealed class VerifierProviderScript
{
    private const string RandomPrefix = "APR111_RANDOM_";
    private readonly VerifierScenario scenario;
    private readonly R3QualityCase testCase;
    private readonly ReviewedIdentity reviewedIdentity;
    private readonly string? expectedHistorySha256;
    private string? randomFact;

    internal VerifierProviderScript(
        VerifierScenario scenario,
        R3QualityCase testCase,
        ReviewedIdentity reviewedIdentity,
        string? expectedHistorySha256)
    {
        this.scenario = scenario;
        this.testCase = testCase;
        this.reviewedIdentity = reviewedIdentity;
        this.expectedHistorySha256 = expectedHistorySha256;
        if (scenario == VerifierScenario.ContinuationSeed)
        {
            randomFact = RandomPrefix + Convert.ToHexString(
                RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        }
    }

    internal VerifierProviderScript(
        VerifierScenario scenario,
        R3QualityCase testCase)
        : this(
            scenario,
            testCase,
            testCase.ReviewedIdentity,
            expectedHistorySha256: null)
    {
    }

    internal int ExpectedRequests => scenario switch
    {
        VerifierScenario.MustFind => 3,
        VerifierScenario.MustNotFind => 4,
        VerifierScenario.ContinuationSeed => 1,
        VerifierScenario.ContinuationRestore => 1,
        VerifierScenario.CanaryRouting => 2,
        VerifierScenario.ProviderHttpFailure or
            VerifierScenario.ProviderMalformedResponse or
            VerifierScenario.ToolArgumentsInvalid or
            VerifierScenario.TerminalUngrounded => 1,
        VerifierScenario.QualityFailedAfterCommit => 3,
        VerifierScenario.PublicResultCanary => 4,
        VerifierScenario.OuterAuthorizationDenied or
            VerifierScenario.InnerAuthorizationDenied or
            VerifierScenario.TransitionFromHeadInvalid or
            VerifierScenario.LineageTampered => 0,
        _ => throw new InvalidOperationException(),
    };

    internal bool Completed { get; private set; }

    internal string? ExpectedTerminalSha256 { get; private set; }

    internal string? PriorFactSha256 => randomFact is null
        ? null
        : LiveAgentFreshProcessDomain.RawSha256(
            Encoding.UTF8.GetBytes(randomFact));

    internal string? HistoricalMessagesSha256 { get; private set; }

    internal bool ExactReplayValidated { get; private set; }

    internal bool ReplayMutationMatrixValidated { get; private set; }

    internal bool CanaryRoutesValidated { get; private set; }

    internal byte[] Respond(int index, byte[] body) => scenario switch
    {
        VerifierScenario.MustFind => MustFind(index, body),
        VerifierScenario.MustNotFind => MustNotFind(index, body),
        VerifierScenario.ContinuationSeed => Seed(index, body),
        VerifierScenario.ContinuationRestore => Restore(index, body),
        VerifierScenario.CanaryRouting => Canary(index, body),
        VerifierScenario.ProviderHttpFailure => throw new VerifierWireException(
            "provider_http_injected"),
        VerifierScenario.ProviderMalformedResponse => "{}"u8.ToArray(),
        VerifierScenario.ToolArgumentsInvalid => Tool(
            "Attempt an invalid tool call.",
            "negative_invalid_arguments",
            AgentToolRegistry.ReadFileName,
            "{\"path\":\"src/CacheGate.cs\",\"unknown\":true}"),
        VerifierScenario.TerminalUngrounded => Finish(
            "Return an ungrounded terminal finding.",
            string.Concat(
                "{\"summary\":\"invalid\",\"findings\":[{",
                "\"severity\":\"high\",\"title\":\"ungrounded\",",
                "\"message\":\"invalid\",\"evidence\":[{",
                "\"observation_id\":\"",
                new string('0', 64),
                "\",\"path\":\"src/CacheGate.cs\",",
                "\"start_line\":1,\"end_line\":1}]}]}")),
        VerifierScenario.QualityFailedAfterCommit => MustFind(index, body),
        VerifierScenario.PublicResultCanary => MustNotFind(index, body),
        _ => throw new InvalidOperationException(),
    };

    private byte[] MustFind(int index, byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        return index switch
        {
            0 => Tool(
                "Inspect changed-file metadata first.",
                "must_find_changed",
                AgentToolRegistry.ListChangedFilesName,
                "{}"),
            1 => Tool(
                "Read the exact changed diff.",
                "must_find_diff",
                AgentToolRegistry.ReadDiffName,
                "{\"path\":\"src/CacheGate.cs\",\"start_hunk\":1,\"hunk_count\":20}"),
            2 => Finish(
                "Report the grounded null-forgiving regression.",
                string.Concat(
                    "{\"summary\":\"Grounded synthetic defect.\",",
                    "\"findings\":[{\"severity\":\"high\",",
                    "\"title\":\"APR110_FINDING_Q7M4 null-forgiving regression\",",
                    "\"message\":\"The change hides a nullable failure.\",",
                    "\"evidence\":[{\"observation_id\":\"",
                    LastObservation(messages, "must_find_diff"),
                    "\",\"path\":\"src/CacheGate.cs\",",
                    "\"start_line\":4,\"end_line\":4}]}]}")),
            _ => throw new VerifierWireException("script_index_invalid"),
        };
    }

    private byte[] MustNotFind(int index, byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        return index switch
        {
            0 => Tool(
                "List the bounded repository paths.",
                "must_not_list",
                AgentToolRegistry.ListFilesName,
                "{}"),
            1 => Tool(
                "Search for the safe marker.",
                "must_not_search",
                AgentToolRegistry.SearchTextName,
                "{\"query\":\"APR110_SAFE_K2R8\",\"path\":\"src/ReadyGate.cs\"}"),
            2 => Tool(
                "Read the safe implementation.",
                "must_not_read",
                AgentToolRegistry.ReadFileName,
                "{\"path\":\"src/ReadyGate.cs\",\"start_line\":1,\"line_count\":400}"),
            3 => Finish(
                "No actionable defect is present.",
                "{\"summary\":\"The bounded change is safe.\",\"findings\":[]}"),
            _ => throw new VerifierWireException("script_index_invalid"),
        };
    }

    private byte[] Seed(int index, byte[] body)
    {
        if (randomFact is null)
        {
            throw new VerifierWireException("seed_fact_missing");
        }

        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        return index == 0
            ? SeedFinish(messages)
            : throw new VerifierWireException("script_index_invalid");
    }

    private byte[] Restore(int index, byte[] body)
    {
        if (index != 0)
        {
            throw new VerifierWireException("script_index_invalid");
        }

        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        randomFact = ValidateRestoredFirstRequest(messages);
        ValidateRestoredPrefixMutationMatrix(messages);
        ExactReplayValidated = true;
        ReplayMutationMatrixValidated = true;
        return Finish(
            "Use only the restored prior fact.",
            string.Concat(
                "{\"summary\":\"Restored ",
                ((R3QualityContinuationExpectation)testCase.Expectation)
                    .PriorOnlyMarker,
                " and ",
                randomFact,
                ".\",\"findings\":[]}"));
    }

    private byte[] Canary(int index, byte[] body)
    {
        using var document = JsonDocument.Parse(body);
        var messages = document.RootElement.GetProperty("messages");
        var path = string.Concat("src/", VerifierCanaries.Path, ".cs");
        if (index == 0)
        {
            var values = messages.EnumerateArray().ToArray();
            if (values.Length != 2 ||
                !HasNoReasoning(values[0]) ||
                values[0].GetRawText().Contains(
                    VerifierCanaries.Repository,
                    StringComparison.Ordinal) ||
                values[0].GetRawText().Contains(
                    VerifierCanaries.Path,
                    StringComparison.Ordinal) ||
                values[0].GetRawText().Contains(
                    VerifierCanaries.Prompt,
                    StringComparison.Ordinal) ||
                !values[1].GetRawText().Contains(
                    VerifierCanaries.Prompt,
                    StringComparison.Ordinal))
            {
                throw new VerifierWireException("canary_initial_route_invalid");
            }

            return Tool(
                "Read only the untrusted canary path.",
                "canary_read",
                AgentToolRegistry.ReadFileName,
                string.Concat(
                    "{\"path\":\"",
                    path,
                    "\",\"start_line\":1,\"line_count\":400}"));
        }

        if (index == 1)
        {
            var values = messages.EnumerateArray().ToArray();
            var system = values[0].GetRawText();
            var assistant = values[^2].GetRawText();
            var tool = values[^1].GetRawText();
            if (values.Length != 4 ||
                system.Contains(VerifierCanaries.Repository, StringComparison.Ordinal) ||
                system.Contains(VerifierCanaries.Path, StringComparison.Ordinal) ||
                system.Contains(VerifierCanaries.Prompt, StringComparison.Ordinal) ||
                !assistant.Contains(VerifierCanaries.Path, StringComparison.Ordinal) ||
                assistant.Contains(VerifierCanaries.Repository, StringComparison.Ordinal) ||
                assistant.Contains(VerifierCanaries.Prompt, StringComparison.Ordinal) ||
                !tool.Contains(VerifierCanaries.Repository, StringComparison.Ordinal) ||
                !tool.Contains(VerifierCanaries.Path, StringComparison.Ordinal) ||
                !tool.Contains(VerifierCanaries.Prompt, StringComparison.Ordinal))
            {
                throw new VerifierWireException("canary_tool_route_invalid");
            }

            CanaryRoutesValidated = true;
            return Finish(
                "Complete without promoting untrusted canaries.",
                "{\"summary\":\"Canary routing remained untrusted.\",\"findings\":[]}");
        }

        throw new VerifierWireException("script_index_invalid");
    }

    private byte[] SeedFinish(JsonElement messages)
    {
        var values = messages.EnumerateArray().ToArray();
        if (randomFact is null ||
            !HasExactInitialMessages(
                messages,
                testCase.ProcessOneContext ?? string.Empty) ||
            values[0].GetRawText().Contains(randomFact, StringComparison.Ordinal) ||
            values[1].GetRawText().Contains(randomFact, StringComparison.Ordinal))
        {
            throw new VerifierWireException("seed_history_invalid");
        }

        var response = Finish(
            string.Empty,
            SeedFinishArguments(randomFact));
        HistoricalMessagesSha256 = HashSeedHistory(values, response);
        return response;
    }

    private byte[] Finish(string reasoning, string arguments)
    {
        var bytes = Encoding.UTF8.GetBytes(arguments);
        ExpectedTerminalSha256 = AgentCanonical.HashDomain(
            AgentCanonical.TerminalDomain,
            bytes);
        Completed = true;
        var callId = VerifierScenarioDomain.ProviderBehavior(scenario) switch
        {
            VerifierScenario.MustFind => "must_find_finish",
            VerifierScenario.MustNotFind => "must_not_finish",
            VerifierScenario.ContinuationSeed => "seed_finish",
            VerifierScenario.ContinuationRestore => "restore_finish",
            VerifierScenario.CanaryRouting => "canary_finish",
            _ => throw new InvalidOperationException(),
        };
        return Tool(
            reasoning,
            callId,
            AgentToolRegistry.FinishReviewName,
            arguments);
    }

    private static byte[] Tool(
        string reasoning,
        string callId,
        string name,
        string arguments)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("choices");
            writer.WriteStartObject();
            writer.WriteNumber("index", 0);
            writer.WriteStartObject("message");
            writer.WriteString("role", "assistant");
            writer.WriteString("content", string.Empty);
            writer.WriteString("reasoning_content", reasoning);
            writer.WriteStartArray("tool_calls");
            writer.WriteStartObject();
            writer.WriteString("id", callId);
            writer.WriteString("type", "function");
            writer.WriteStartObject("function");
            writer.WriteString("name", name);
            writer.WriteString("arguments", arguments);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteString("finish_reason", "tool_calls");
            writer.WriteEndObject();
            writer.WriteEndArray();
            writer.WriteString("model", DeepSeekAdapterContext.Model);
            writer.WriteStartObject("usage");
            writer.WriteNumber("prompt_tokens", 3);
            writer.WriteNumber("completion_tokens", 2);
            writer.WriteNumber("total_tokens", 5);
            writer.WriteNumber("prompt_cache_hit_tokens", 1);
            writer.WriteNumber("prompt_cache_miss_tokens", 2);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    private static string LastObservation(
        JsonElement messages,
        string callId)
    {
        var result = messages.EnumerateArray()
            .Last(message =>
                StringComparer.Ordinal.Equals(
                    message.GetProperty("role").GetString(),
                    "tool") &&
                StringComparer.Ordinal.Equals(
                    message.GetProperty("tool_call_id").GetString(),
                    callId));
        using var document = JsonDocument.Parse(
            result.GetProperty("content").GetString()!);
        return document.RootElement.GetProperty("observation_id").GetString() ??
            throw new VerifierWireException("observation_missing");
    }

    private string ValidateRestoredFirstRequest(JsonElement messages)
    {
        var values = messages.EnumerateArray().ToArray();
        if (!TryValidateRestoredPrefix(values, out var fact) ||
            fact is null)
        {
            throw new VerifierWireException("restore_history_shape_invalid");
        }

        var actualHistorySha256 = HashMessagePrefix(values, 4);
        if (!StringComparer.Ordinal.Equals(
                actualHistorySha256,
                expectedHistorySha256))
        {
            throw new VerifierWireException("restore_history_digest_invalid");
        }

        HistoricalMessagesSha256 = actualHistorySha256;
        return fact;
    }

    private void ValidateRestoredPrefixMutationMatrix(JsonElement messages)
    {
        Action<JsonArray>[] mutations =
        [
            values => values[2]!["role"] = "user",
            values => values[1]!["content"] = "changed initial request",
            values => values[2]!.AsObject().Remove("reasoning_content"),
            values => values[2]!["reasoning_content"] = null,
            values => values[2]!["reasoning_content"] = "changed reasoning",
            values => values[2]!["tool_calls"]![0]!["id"] = "changed",
            values => values[2]!["tool_calls"]![0]!["function"]!["name"] =
                AgentToolRegistry.ReadDiffName,
            values => values[2]!["tool_calls"]![0]!["function"]!["arguments"] =
                "{}",
            values => values[3]!["content"] = "{\"changed\":true}",
            values => values[4]!["content"] = "changed current request",
        ];
        foreach (var mutate in mutations)
        {
            var values = JsonNode.Parse(messages.GetRawText())!.AsArray();
            mutate(values);
            using var document = JsonDocument.Parse(values.ToJsonString());
            var rejected = false;
            try
            {
                ValidateRestoredFirstRequest(document.RootElement);
            }
            catch (VerifierWireException)
            {
                rejected = true;
            }

            if (!rejected)
            {
                throw new VerifierWireException(
                    "restore_prefix_mutation_accepted");
            }
        }
    }

    private bool TryValidateRestoredPrefix(
        JsonElement[] values,
        out string? fact)
    {
        fact = null;
        if (values.Length != 5 ||
            !Roles(values).SequenceEqual(
                [
                    "system",
                    "user",
                    "assistant",
                    "tool",
                    "user",
                ],
                StringComparer.Ordinal) ||
            !HasNoReasoning(values[0]) ||
            !HasExactUser(values[1], testCase.ProcessOneContext) ||
            !HasNoReasoning(values[3]) ||
            !HasExactUser(values[4], testCase.InitialContext) ||
            !TryExtractRandomFact(values[2], out fact) ||
            fact is null ||
            !HasExactToolCall(
                values[2],
                string.Empty,
                "seed_finish",
                AgentToolRegistry.FinishReviewName,
                SeedFinishArguments(fact)) ||
            !IsExactToolResult(values[3], "seed_finish", "{}") ||
            values[0].GetRawText().Contains(fact, StringComparison.Ordinal) ||
            values[1].GetRawText().Contains(fact, StringComparison.Ordinal) ||
            values[3].GetRawText().Contains(fact, StringComparison.Ordinal) ||
            values[4].GetRawText().Contains(fact, StringComparison.Ordinal))
        {
            fact = null;
            return false;
        }

        return true;
    }

    private bool TryValidateReadFileResult(
        JsonElement message,
        string callId,
        string path,
        ReviewedIdentity identity,
        out string? observation)
    {
        observation = null;
        var file = testCase.Files.SingleOrDefault(item =>
            StringComparer.Ordinal.Equals(item.Path, path));
        if (file is null)
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(file.Content);
        var text = file.Content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var values = text.Length == 0
            ? []
            : text.Split('\n');
        var length = values.Length;
        if (text.EndsWith('\n'))
        {
            length--;
        }

        var lines = values[..length]
            .Select((value, index) => new ReadFileLine(index + 1, value))
            .ToImmutableArray();
        var withoutObservation = new ReadFileResult(
            "ok",
            identity,
            path,
            AgentCanonical.HashRaw(bytes),
            RequestedStartLine: 1,
            RequestedLineCount: 400,
            ReturnedStartLine: lines.Length == 0 ? null : 1,
            ReturnedEndLine: lines.Length == 0 ? null : lines.Length,
            lines,
            Truncated: false,
            TruncationReason: null,
            ObservationId: null);
        observation = AgentCanonical.HashDomain(
            AgentCanonical.ReadObservationDomain,
            ReadFileResultWriter.Write(
                withoutObservation,
                includeObservationId: false));
        var expected = Encoding.UTF8.GetString(
            ReadFileResultWriter.Write(
                withoutObservation with { ObservationId = observation }));
        return IsExactToolResult(message, callId, expected);
    }

    private bool TryValidateReadDiffResult(
        JsonElement message,
        string callId,
        string path,
        ReviewedIdentity identity,
        out string? observation)
    {
        observation = null;
        var source = testCase.DiffSource;
        if (!StringComparer.Ordinal.Equals(source.Path, path) ||
            source.Hunks.Count() == 0 ||
            source.Hunks.Count() > 20)
        {
            return false;
        }

        var rebound = new ReviewedDiffSource(
            identity,
            source.Path,
            source.PreviousPath,
            source.Status,
            source.SourceTruncated,
            source.Hunks);
        var hunks = rebound.Hunks.ToImmutableArray();
        var withoutObservation = new ReadDiffResult(
            "ok",
            identity,
            path,
            rebound.PatchSha256,
            rebound.SourceTruncated,
            RequestedStartHunk: 1,
            RequestedHunkCount: 20,
            ReturnedStartHunk: 1,
            ReturnedEndHunk: hunks.Length,
            hunks,
            Truncated: false,
            NextStartHunk: null,
            ObservationId: null);
        observation = AgentCanonical.HashDomain(
            AgentCanonical.ReadDiffObservationDomain,
            ReadDiffResultWriter.Write(
                withoutObservation,
                includeObservationId: false));
        var expected = Encoding.UTF8.GetString(
            ReadDiffResultWriter.Write(
                withoutObservation with { ObservationId = observation }));
        return IsExactToolResult(message, callId, expected);
    }

    private static bool HasExactInitialMessages(
        JsonElement messages,
        string currentContext)
    {
        var values = messages.EnumerateArray().ToArray();
        return values.Length == 2 &&
            Roles(values).SequenceEqual(
                ["system", "user"],
                StringComparer.Ordinal) &&
            HasNoReasoning(values[0]) &&
            HasExactUser(values[1], currentContext);
    }

    private static string[] Roles(IEnumerable<JsonElement> messages) =>
        messages.Select(message =>
            message.GetProperty("role").GetString()!).ToArray();

    private static bool HasExactUser(JsonElement message, string? content) =>
        content is not null &&
        message.EnumerateObject().Select(property => property.Name)
            .SequenceEqual(["role", "content"], StringComparer.Ordinal) &&
        StringComparer.Ordinal.Equals(
            message.GetProperty("role").GetString(),
            "user") &&
        StringComparer.Ordinal.Equals(
            message.GetProperty("content").GetString(),
            content) &&
        HasNoReasoning(message);

    private static bool HasExactToolCall(
        JsonElement message,
        string reasoning,
        string callId,
        string name,
        string arguments)
    {
        if (!message.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(
                    ["role", "content", "reasoning_content", "tool_calls"],
                    StringComparer.Ordinal) ||
            !StringComparer.Ordinal.Equals(
                message.GetProperty("role").GetString(),
                "assistant") ||
            !StringComparer.Ordinal.Equals(
                message.GetProperty("content").GetString(),
                string.Empty) ||
            !StringComparer.Ordinal.Equals(
                message.GetProperty("reasoning_content").GetString(),
                reasoning))
        {
            return false;
        }

        var calls = message.GetProperty("tool_calls");
        if (calls.GetArrayLength() != 1)
        {
            return false;
        }

        var call = calls[0];
        var function = call.GetProperty("function");
        return call.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(["id", "type", "function"], StringComparer.Ordinal) &&
            function.EnumerateObject().Select(property => property.Name)
                .SequenceEqual(["name", "arguments"], StringComparer.Ordinal) &&
            StringComparer.Ordinal.Equals(call.GetProperty("id").GetString(), callId) &&
            StringComparer.Ordinal.Equals(call.GetProperty("type").GetString(), "function") &&
            StringComparer.Ordinal.Equals(function.GetProperty("name").GetString(), name) &&
            StringComparer.Ordinal.Equals(
                function.GetProperty("arguments").GetString(),
                arguments);
    }

    private static bool IsExactToolResult(
        JsonElement message,
        string callId,
        string content) =>
        message.EnumerateObject().Select(property => property.Name)
            .SequenceEqual(
                ["role", "tool_call_id", "content"],
                StringComparer.Ordinal) &&
        StringComparer.Ordinal.Equals(message.GetProperty("role").GetString(), "tool") &&
        StringComparer.Ordinal.Equals(
            message.GetProperty("tool_call_id").GetString(),
            callId) &&
        StringComparer.Ordinal.Equals(
            message.GetProperty("content").GetString(),
            content) &&
        HasNoReasoning(message);

    private static bool HasNoReasoning(JsonElement message) =>
        !message.TryGetProperty("reasoning_content", out _);

    private bool TryExtractRandomFact(
        JsonElement message,
        out string? fact)
    {
        fact = null;
        if (!message.TryGetProperty("tool_calls", out var calls) ||
            calls.ValueKind != JsonValueKind.Array ||
            calls.GetArrayLength() != 1 ||
            !calls[0].TryGetProperty("function", out var function) ||
            function.ValueKind != JsonValueKind.Object ||
            !function.TryGetProperty("arguments", out var argumentsValue) ||
            argumentsValue.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        string? summary;
        try
        {
            using var arguments = JsonDocument.Parse(
                argumentsValue.GetString() ?? string.Empty);
            summary = arguments.RootElement.ValueKind == JsonValueKind.Object &&
                arguments.RootElement.TryGetProperty("summary", out var value) &&
                value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return false;
        }

        var prefix = string.Concat(
            "Seeded ",
            ((R3QualityContinuationExpectation)testCase.Expectation)
                .PriorOnlyMarker,
            " and ");
        if (summary is null ||
            !summary.StartsWith(prefix, StringComparison.Ordinal) ||
            !summary.EndsWith(".", StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = summary[prefix.Length..^1];
        if (candidate.Length != RandomPrefix.Length + 64 ||
            !candidate.StartsWith(RandomPrefix, StringComparison.Ordinal) ||
            candidate.AsSpan(RandomPrefix.Length).ToArray().Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
        {
            return false;
        }

        fact = candidate;
        return true;
    }

    private string SeedFinishArguments(string fact) => string.Concat(
        "{\"summary\":\"Seeded ",
        ((R3QualityContinuationExpectation)testCase.Expectation)
            .PriorOnlyMarker,
        " and ",
        fact,
        ".\",\"findings\":[]}");

    private static string HashSeedHistory(
        IReadOnlyList<JsonElement> messages,
        byte[] finishResponse)
    {
        using var response = JsonDocument.Parse(finishResponse);
        var finish = response.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var message in messages)
            {
                message.WriteTo(writer);
            }
            finish.WriteTo(writer);
            writer.WriteStartObject();
            writer.WriteString("role", "tool");
            writer.WriteString("tool_call_id", "seed_finish");
            writer.WriteString("content", "{}");
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        return LiveAgentFreshProcessDomain.RawSha256(stream.ToArray());
    }

    private static string HashMessagePrefix(
        IReadOnlyList<JsonElement> messages,
        int count)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            for (var index = 0; index < count; index++)
            {
                messages[index].WriteTo(writer);
            }
            writer.WriteEndArray();
        }

        return LiveAgentFreshProcessDomain.RawSha256(stream.ToArray());
    }
}
