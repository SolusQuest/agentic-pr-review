using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Independent typed callback observer. No HTTP object, credential or endpoint enters
// this seam. Captured logical bytes remain private and are zeroed on disposal.
internal sealed class EconomicsCredentialProbe(ReplayScript script, DeepSeekAdapterContext context,
    EconomicsChildInput input, string startup) : IAuthoredResponseSource, IDisposable
{
    internal const string ObservationDomain = "apr-historical-typed-credential-isolation-v1";
    internal const string Provider = "APR278_SYNTHETIC_PROVIDER_ONLY";
    internal static readonly IReadOnlyDictionary<string, string> Canaries = new Dictionary<string, string>
    {
        [LiveEnvironmentSecretSource.ProviderVariable] = Provider,
        ["GITHUB_TOKEN"] = "APR278_SYNTHETIC_GITHUB",
        ["ACTIONS_RUNTIME_TOKEN"] = "APR278_SYNTHETIC_ACTIONS",
        ["AGENTIC_REVIEW_R3_STATE_KEY_B64"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("APR278_SYNTHETIC_STATE_KEY_00000")),
        ["APR278_UNRELATED_SECRET"] = "APR278_SYNTHETIC_UNRELATED",
        ["RUNNER_TOKEN"] = "APR278_SYNTHETIC_RUNNER",
        ["NPM_TOKEN"] = "APR278_SYNTHETIC_PACKAGE",
        ["AWS_SECRET_ACCESS_KEY"] = "APR278_SYNTHETIC_CLOUD",
        ["SIGNING_KEY"] = "APR278_SYNTHETIC_SIGNING",
    };
    private readonly ReplayScriptClient replay = new(script, ReplayFault.None, context);
    private readonly List<(byte[] Logical, string Outcome)> captures = [];
    private readonly CredentialTrace trace = new(input, startup);

    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        if (captures.Count >= EconomicsLiveLimits.Calls) throw new IOException();
        var logical = AgentRequestWriter.Write(request);
        if (logical.Length > AgentLimits.RequestBytes) throw new IOException();
        var ordinal = captures.Count;
        captures.Add((logical, "entered"));
        var digest = Convert.ToHexStringLower(SHA256.HashData(logical));
        trace.Entry(ordinal + 1, digest, "entered");
        try
        {
            token.ThrowIfCancellationRequested();
            var response = await replay.GetResponseAsync(request, token);
            captures[ordinal] = (logical, "succeeded");
            return response;
        }
        catch (OperationCanceledException) { captures[ordinal] = (logical, "cancelled"); throw; }
        catch { captures[ordinal] = (logical, "failed"); throw; }
        finally { trace.Entry(ordinal + 1, digest, captures[ordinal].Outcome); }
    }

    internal EconomicsCredentialProof Verify(byte[] key, byte[]? restored, byte[] completed, string stateRoot)
    {
        if (captures.Count is < 1 or > EconomicsLiveLimits.Calls ||
            Canaries.Keys.Any(name => Environment.GetEnvironmentVariable(name) is not null)) throw new IOException();
        foreach (var capture in captures)
        {
            if (capture.Outcome != "succeeded") throw new IOException();
            AssertProtected(capture.Logical, key);
        }
        if (restored is not null) AssertProtected(restored, key);
        AssertProtected(completed, key);
        var files = Directory.EnumerateFiles(Path.Combine(stateRoot, "state"), "*", SearchOption.AllDirectories).Take(17).ToArray();
        if (files.Length is < 1 or > 16) throw new IOException();
        long total = 0;
        foreach (var file in files)
        {
            var length = new FileInfo(file).Length;
            total = checked(total + length);
            if (length > 8 * 1024 * 1024 || total > 32 * 1024 * 1024) throw new IOException();
            var bytes = File.ReadAllBytes(file);
            try { AssertProtected(bytes, key); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        return new(ObservationDomain, captures.Count, true, restored is not null, true, files.Length, trace.Digest());
    }

    internal static void AssertProtected(ReadOnlySpan<byte> bytes, byte[] key)
    {
        if (key.Length != 32 || bytes.IndexOf(key) >= 0) throw new IOException();
        foreach (var value in Canaries.Values.Append(Convert.ToBase64String(key)).Append(Convert.ToHexString(key))
            .Append(Convert.ToHexString(key).ToLowerInvariant()))
            if (bytes.IndexOf(Encoding.UTF8.GetBytes(value)) >= 0) throw new IOException();
        // Also check the configured state canary's decoded key, not just its environment spelling.
        if (bytes.IndexOf(Convert.FromBase64String(Canaries["AGENTIC_REVIEW_R3_STATE_KEY_B64"])) >= 0) throw new IOException();
    }

    public void Dispose()
    {
        try
        {
            foreach (var capture in captures) CryptographicOperations.ZeroMemory(capture.Logical);
            foreach (var request in replay.Requests) CryptographicOperations.ZeroMemory(request);
            captures.Clear(); replay.Requests.Clear(); replay.Dispose();
        }
        finally { trace.Dispose(); }
    }

    internal const int MaximumTraceBytes = 16 * 1024;
    internal static string TraceName(int index) => index is >= 0 and < EconomicsLiveLimits.Slots
        ? "credential-typed-trace-" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".jsonl"
        : throw new IOException();

    // I/O only. The independent gate owns parsing and admission of these bytes.
    internal static byte[] ReadTrace(string root, int index)
    {
        using var parent = ReplayDirectoryGuard.Open(root);
        RequirePrivate(parent.Handle);
        if (parent.OpenFile(TraceName(index), out var handle) != RestrictedStateOpenResult.Success || handle is null)
        { handle?.Dispose(); throw new IOException(); }
        using var stream = new FileStream(handle, FileAccess.Read, 4096, false);
        if (!NativeRestrictedStateFiles.TryGetIdentity(handle, false, out _) ||
            (File.GetAttributes(handle) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0 ||
            stream.Length is < 1 or > MaximumTraceBytes) throw new IOException();
        RequirePrivate(handle);
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1 || !parent.StillMatches()) throw new IOException();
        return bytes;
    }

    private static void RequirePrivate(Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(handle) &
            (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw new IOException();
    }

    // Fixed private root/slot path, exclusive create, descriptor-held parent. The
    // authored script has no writer or sink. Only real callback entry/exit append.
    private sealed class CredentialTrace : IDisposable
    {
        private readonly ReplayDirectoryGuard parent;
        private readonly FileStream stream;
        internal CredentialTrace(EconomicsChildInput input, string startup)
        {
            parent = ReplayDirectoryGuard.Open(input.Root);
            try
            {
                RequirePrivate(parent.Handle);
                var name = TraceName(input.Slot.Index);
                if (OperatingSystem.IsMacOS())
                    stream = new FileStream(DarwinFiles.OpenChild(parent.Handle, name, create: true), FileAccess.ReadWrite, 4096, false);
                else
                {
                    var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite,
                        Share = FileShare.None, Options = FileOptions.WriteThrough };
                    if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                    stream = new FileStream(Path.Join(parent.AnchoredPath, name), options);
                }
                try
                {
                    Write(writer =>
                    {
                        writer.WriteString("kind", "apr-r6-historical-typed-credential-observations-v1");
                        writer.WriteString("observation_domain", ObservationDomain);
                        writer.WriteString("operation", input.Operation);
                        writer.WriteString("plan_sha256", input.PlanSha256);
                        writer.WriteString("workload_sha256", input.WorkloadSha256);
                        writer.WriteString("source_commit", input.Plan.Source.Commit);
                        writer.WriteString("source_tree", input.Plan.Source.Tree);
                        writer.WriteString("build_sha256", input.Plan.BuildSha256);
                        writer.WriteNumber("index", input.Slot.Index);
                        writer.WriteNumber("phase", input.Slot.Phase);
                        writer.WriteString("startup", startup);
                        writer.WriteBoolean("restored", input.Predecessor is not null);
                        writer.WriteString("session_sha256", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input.Session))));
                    });
                }
                catch { stream.Dispose(); throw; }
            }
            catch { parent.Dispose(); throw; }
        }
        internal void Entry(int ordinal, string digest, string outcome) => Write(writer =>
        {
            writer.WriteNumber("ordinal", ordinal);
            writer.WriteString("request_sha256", digest);
            writer.WriteString("outcome", outcome);
        });
        private void Write(Action<Utf8JsonWriter> append)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) { writer.WriteStartObject(); append(writer); writer.WriteEndObject(); }
            if (!parent.StillMatches() || stream.Length + buffer.Length + 1 > MaximumTraceBytes) throw new IOException();
            stream.Write(buffer.GetBuffer(), 0, checked((int)buffer.Length)); stream.WriteByte((byte)'\n'); stream.Flush(true);
        }
        internal string Digest()
        {
            stream.Flush(true);
            if (!parent.StillMatches() || stream.Length is < 1 or > MaximumTraceBytes) throw new IOException();
            stream.Position = 0;
            var digest = Convert.ToHexStringLower(SHA256.HashData(stream));
            stream.Position = stream.Length;
            return digest;
        }
        public void Dispose() { try { stream.Dispose(); } finally { parent.Dispose(); } }
    }
}
