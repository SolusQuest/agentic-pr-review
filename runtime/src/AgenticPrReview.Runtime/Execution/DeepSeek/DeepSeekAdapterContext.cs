using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

// Frozen APR authorization/state identities. These descriptors do not implement a provider.
internal enum DeepSeekRequestProfile
{
    Current = 0,
    Output8192 = 1,
    Output65536 = 2,
}

internal sealed class DeepSeekAdapterContext(
    string providerId,
    string modelId,
    string adapterId,
    string sessionId)
{
    internal const string Provider = "deepseek";
    internal const string Endpoint = "https://api.deepseek.com/chat/completions";
    internal const string Model = "deepseek-flash";
    internal const string ReasoningFraming = "deepseek.reasoning_content.utf8.v1";
    internal const string RetainedAdapterDescriptor =
        "{\"schema_version\":1,\"provider\":\"deepseek\",\"model\":" +
        "\"deepseek-flash\",\"endpoint\":" +
        "\"https://api.deepseek.com/chat/completions\",\"stream\":false," +
        "\"thinking\":\"enabled\",\"reasoning_effort\":\"high\"," +
        "\"max_tokens\":4096,\"tool_choice\":\"omitted\"," +
        "\"request_cap_bytes\":8388608,\"response_cap_bytes\":2097152," +
        "\"context_policy\":\"dsv41-utf8-upper-v1\",\"context_cap_tokens\":1000000," +
        "\"content_rule\":\"zero-or-one-exact\",\"response_rule\":" +
        "\"reasoning,text-if-nonempty,calls\",\"codec_id\":" +
        "\"deepseek-reasoning-content\",\"codec_discriminator\":" +
        "\"deepseek-flash-thinking-v1\",\"encoding\":\"utf8\"," +
        "\"framing\":\"deepseek.reasoning_content.utf8.v1\"}";
    internal static string AdapterDescriptor { get; } = RetainedAdapterDescriptor.Replace(
        "\"max_tokens\":4096,", "\"max_tokens\":65536,\"max_tokens_policy\":\"remaining-known-output-v1\",", StringComparison.Ordinal);
    internal static string Adapter { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(AdapterDescriptor))).ToLowerInvariant();
    internal static string CandidateAdapterDescriptor { get; } = RetainedAdapterDescriptor.Replace(
        "\"max_tokens\":4096,", "\"max_tokens\":8192,", StringComparison.Ordinal);
    internal static string CandidateAdapter { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CandidateAdapterDescriptor))).ToLowerInvariant();
    internal static string Output65536AdapterDescriptor { get; } = RetainedAdapterDescriptor.Replace(
        "\"max_tokens\":4096,", "\"max_tokens\":65536,", StringComparison.Ordinal);
    internal static string Output65536Adapter { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Output65536AdapterDescriptor))).ToLowerInvariant();

    internal static bool TryResolveProfile(string? adapterId, out DeepSeekRequestProfile profile)
    {
        profile = DeepSeekRequestProfile.Current;
        if (StringComparer.Ordinal.Equals(adapterId, Adapter)) return true;
        if (StringComparer.Ordinal.Equals(adapterId, CandidateAdapter))
        {
            profile = DeepSeekRequestProfile.Output8192;
            return true;
        }
        if (StringComparer.Ordinal.Equals(adapterId, Output65536Adapter))
        {
            profile = DeepSeekRequestProfile.Output65536;
            return true;
        }
        return false;
    }

    internal static string AdapterFor(DeepSeekRequestProfile profile) => profile switch
    {
        DeepSeekRequestProfile.Current => Adapter,
        DeepSeekRequestProfile.Output8192 => CandidateAdapter,
        DeepSeekRequestProfile.Output65536 => Output65536Adapter,
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal static AgentLimitAuthority LimitAuthorityFor(DeepSeekRequestProfile profile) => profile switch
    {
        DeepSeekRequestProfile.Current => new(Adapter, AgentLimitProfile.Current),
        DeepSeekRequestProfile.Output8192 => new(CandidateAdapter, AgentLimitProfile.Output8192),
        DeepSeekRequestProfile.Output65536 => new(Output65536Adapter, AgentLimitProfile.Output65536),
        _ => throw new ArgumentOutOfRangeException(nameof(profile)),
    };

    internal string ProviderId { get; } = providerId;
    internal string ModelId { get; } = modelId;
    internal string AdapterId { get; } = adapterId;
    internal string SessionId { get; } = sessionId;

    internal bool IsValid =>
        StringComparer.Ordinal.Equals(ProviderId, Provider) &&
        StringComparer.Ordinal.Equals(ModelId, Model) &&
        TryResolveProfile(AdapterId, out _) &&
        AgentValueDomains.IsIdentifier(SessionId);

    internal bool TryProfile(out DeepSeekRequestProfile profile)
    {
        profile = DeepSeekRequestProfile.Current;
        return IsValid && TryResolveProfile(AdapterId, out profile);
    }

    public override string ToString() => "deepseek_adapter_context";
}

