using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Loop;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

// Independent typed callback observer. No HTTP object, credential or endpoint enters
// this seam. Captured logical bytes remain private and are zeroed on disposal.
internal sealed class EconomicsCredentialProbe(ReplayScript script, DeepSeekAdapterContext context) : IProjectChatClient, IDisposable
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

    public async Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        if (captures.Count >= EconomicsLiveLimits.Calls) throw new IOException();
        var logical = AgentRequestWriter.Write(request);
        if (logical.Length > AgentLimits.RequestBytes) throw new IOException();
        var ordinal = captures.Count;
        captures.Add((logical, "entered"));
        try
        {
            token.ThrowIfCancellationRequested();
            var response = await replay.GetResponseAsync(request, token);
            captures[ordinal] = (logical, "succeeded");
            return response;
        }
        catch (OperationCanceledException) { captures[ordinal] = (logical, "cancelled"); throw; }
        catch { captures[ordinal] = (logical, "failed"); throw; }
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
        return new(ObservationDomain, captures.Count, true, restored is not null, true, files.Length);
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
        foreach (var capture in captures) CryptographicOperations.ZeroMemory(capture.Logical);
        foreach (var request in replay.Requests) CryptographicOperations.ZeroMemory(request);
        captures.Clear(); replay.Requests.Clear(); replay.Dispose();
    }
}
