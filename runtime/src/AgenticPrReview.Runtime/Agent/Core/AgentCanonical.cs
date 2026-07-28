using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Core;

internal static class AgentCanonical
{
    internal const string LimitsDomain = "apr.limits.r2";
    internal const string ToolsetDomain = "apr.toolset.r2";
    internal const string StablePlanDomain = "apr.stable-plan.r2";
    internal const string QueryDomain = "apr.query.r2";
    internal const string ReadObservationDomain = "apr.observation.read.r2";
    internal const string SearchObservationDomain = "apr.observation.search.r2";
    internal const string TerminalDomain = "apr.terminal.r2";
    internal const string ContinuationDomain = "apr.continuation.r2";
    internal const string SessionDomain = "apr.session.r2";
    internal const string StateEnvelopeDomain = "apr.state-envelope.r2";

    internal static byte[] LimitsBytes()
    {
        var writer = new Rfc8785Writer(4_096);
        writer.WriteObjectStart();
        writer.WriteProperty("limits");
        writer.WriteArrayStart();
        for (var index = 0; index < AgentLimits.Registry.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            var row = AgentLimits.Registry[index];
            writer.WriteObjectStart();
            writer.WriteProperty("ordinal");
            writer.WriteNumber(row.Ordinal);
            writer.WriteProperty("name");
            writer.WriteString(row.Name);
            writer.WriteProperty("value");
            writer.WriteNumber(row.Value);
            writer.WriteProperty("unit");
            writer.WriteString(row.Unit);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    internal static string LimitsSha256() => HashDomain(LimitsDomain, LimitsBytes());

    internal static byte[] ToolsetBytes(IReadOnlyList<ProjectToolDefinition> tools)
    {
        var writer = new Rfc8785Writer(4_096);
        writer.WriteObjectStart();
        writer.WriteProperty("tools");
        writer.WriteArrayStart();
        for (var index = 0; index < tools.Count; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            var tool = tools[index];
            writer.WriteObjectStart();
            writer.WriteProperty("name");
            writer.WriteString(tool.Name);
            writer.WriteProperty("description");
            writer.WriteString(tool.Description);
            writer.WriteProperty("schema_json");
            writer.WriteString(tool.SchemaJson);
            writer.WriteObjectEnd();
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    internal static string ToolsetSha256(IReadOnlyList<ProjectToolDefinition> tools) =>
        HashDomain(ToolsetDomain, ToolsetBytes(tools));

    internal static byte[] StablePlanBytes(StableAgentPlan plan)
    {
        var writer = new Rfc8785Writer(1_024);
        writer.WriteObjectStart();
        writer.WriteProperty("repository_id");
        writer.WriteString(plan.RepositoryId);
        writer.WriteProperty("review_target");
        writer.WriteNumber(plan.ReviewTarget);
        writer.WriteProperty("workflow_identity");
        writer.WriteString(plan.WorkflowIdentity);
        writer.WriteProperty("policy_sha256");
        writer.WriteString(plan.PolicySha256);
        writer.WriteProperty("toolset_sha256");
        writer.WriteString(plan.ToolsetSha256);
        writer.WriteProperty("limits_sha256");
        writer.WriteString(plan.LimitsSha256);
        writer.WriteProperty("build_id");
        writer.WriteString(plan.BuildId);
        writer.WriteProperty("provider_id");
        writer.WriteString(plan.ProviderId);
        writer.WriteProperty("model_id");
        writer.WriteString(plan.ModelId);
        writer.WriteProperty("adapter_id");
        writer.WriteString(plan.AdapterId);
        writer.WriteProperty("thinking");
        writer.WriteBoolean(true);
        writer.WriteProperty("prior_session_sha256");
        if (plan.PriorSessionSha256 is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(plan.PriorSessionSha256);
        }

        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    internal static string StablePlanSha256(StableAgentPlan plan) =>
        HashDomain(StablePlanDomain, StablePlanBytes(plan));

    internal static byte[] QueryBytes(string query)
    {
        var writer = new Rfc8785Writer(Encoding.UTF8.GetByteCount(query) + 16);
        writer.WriteObjectStart();
        writer.WriteProperty("query");
        writer.WriteString(query);
        writer.WriteObjectEnd();
        return writer.ToImmutableArray().ToArray();
    }

    internal static string QuerySha256(string query) =>
        HashDomain(QueryDomain, QueryBytes(query));

    internal static void WriteReviewedIdentity(
        ref Rfc8785Writer writer,
        ReviewedIdentity identity)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("repository_id");
        writer.WriteString(identity.RepositoryId);
        writer.WriteProperty("review_target");
        writer.WriteNumber(identity.ReviewTarget);
        writer.WriteProperty("base_sha");
        writer.WriteString(identity.BaseSha);
        writer.WriteProperty("head_sha");
        writer.WriteString(identity.HeadSha);
        writer.WriteObjectEnd();
    }

    internal static string HashDomain(string domainTag, ReadOnlySpan<byte> canonicalBytes)
    {
        var domain = Encoding.ASCII.GetBytes(domainTag);
        var preimage = new byte[domain.Length + 1 + canonicalBytes.Length];
        domain.CopyTo(preimage, 0);
        canonicalBytes.CopyTo(preimage.AsSpan(domain.Length + 1));
        return LowerHex(SHA256.HashData(preimage));
    }

    internal static string HashRaw(ReadOnlySpan<byte> bytes) =>
        LowerHex(SHA256.HashData(bytes));

    private static string LowerHex(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(bytes);
}
