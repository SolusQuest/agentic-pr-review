using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

// Plan admission is complete before any corpus, credential or provider access.
internal static class LivePlanAdmission
{
    internal const string ProviderConfigurationDomain = "live-provider-settings";
    private const string DigestDomain = "apr.r5.live-plan";

    internal static string ProviderConfigurationSha256(DeepSeekRequestProfile profile = DeepSeekRequestProfile.Current) =>
        EvaluationAttempt.Hash(ProviderConfigurationDomain,
            DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.AdapterFor(profile));

    internal static LivePlan Load(string path, bool execute, CancellationToken token)
    {
        LivePlanInput? input;
        try
        {
            token.ThrowIfCancellationRequested();
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is < 1 or > LiveLimits.PlanBytes)
                throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);
            input = JsonSerializer.Deserialize(File.ReadAllBytes(path), LiveJsonContext.Default.LivePlanInput);
        }
        catch (LivePlanRejected) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new LivePlanRejected(LiveAdmissionCode.IoFailure);
        }
        catch { throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan); }
        return Admit(input, execute);
    }

    internal static LivePlan Admit(LivePlanInput? input, bool execute)
    {
        if (input?.Source is not { } source || input.Corpus is not { } corpus ||
            input.Provider is not { } provider || input.Bounds is not { } bounds ||
            bounds.PerCall is not { } perCall ||
            !StringComparer.Ordinal.Equals(input.Format, LiveLimits.PlanFormat) ||
            input.Schedule.IsDefaultOrEmpty || input.Schedule.Length > LiveLimits.ScheduleEntries)
            throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);

        // Source binds the exact compiled evaluator identity.
        if (!EvaluationLimits.Hash(source.Commit, 40) || !EvaluationLimits.Hash(source.Tree, 40))
            throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);
        if (!StringComparer.Ordinal.Equals(source.Commit, EvaluationSource.Commit) ||
            !StringComparer.Ordinal.Equals(source.Tree, EvaluationSource.Tree) ||
            source.Clean != EvaluationSource.Clean ||
            execute && (!source.Clean || !EvaluationSource.Clean))
            throw new LivePlanRejected(LiveAdmissionCode.InvalidSource);

        if (!AgentValueDomains.IsUtf8(corpus.Path, 1, AgentLimits.PathBytes) ||
            !EvaluationLimits.Hash(corpus.Sha256))
            throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);

        // Only the frozen DeepSeek thinking adapter is a supported configuration.
        if (!TryProviderProfile(provider, out var profile))
            throw new LivePlanRejected(LiveAdmissionCode.UnsupportedConfiguration);

        var expanded = Expand(input.Schedule);
        if (expanded.Length is < 1 or > LiveLimits.ExpandedEvaluations ||
            expanded.Length > bounds.MaxEvaluations ||
            !ValidBounds(bounds, expanded.Length, profile))
            throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);
        if (bounds.SpendCeilingMicroUsd < perCall.MaxChargeMicroUsd)
            throw new LivePlanRejected(LiveAdmissionCode.Unpriceable);

        var digest = Digest(new(LiveLimits.PlanFormat, source, corpus.Sha256, provider, expanded, bounds));
        return new(corpus, provider, bounds, expanded, digest);
    }

    // Structural admission of the path-free normalized selection, also used
    // by offline journal readers. Current-build and credential authorization
    // remain exclusively in Admit/Load; historical source identities are data.
    internal static bool ValidProjection(LivePlanDigestInput? input)
    {
        if (input?.Source is not { } source || input.Provider is not { } provider ||
            input.Bounds is not { PerCall: not null } bounds ||
            input.Format != LiveLimits.PlanFormat ||
            !EvaluationLimits.Hash(source.Commit, 40) || !EvaluationLimits.Hash(source.Tree, 40) ||
            !EvaluationLimits.Hash(input.CorpusSha256) || !TryProviderProfile(provider, out var profile) ||
            input.Schedule.IsDefaultOrEmpty || input.Schedule.Length > LiveLimits.ExpandedEvaluations ||
            input.Schedule.Any(id => !EvaluationLimits.Id(id)) ||
            input.Schedule.Length > bounds.MaxEvaluations || !ValidBounds(bounds, input.Schedule.Length, profile))
            return false;
        return bounds.SpendCeilingMicroUsd >= bounds.PerCall.MaxChargeMicroUsd;
    }

    internal static bool TryProviderProfile(LivePlanProvider provider, out DeepSeekRequestProfile profile)
    {
        profile = DeepSeekRequestProfile.Current;
        return StringComparer.Ordinal.Equals(provider.ProviderId, DeepSeekAdapterContext.Provider) &&
            StringComparer.Ordinal.Equals(provider.ModelId, DeepSeekAdapterContext.Model) &&
            DeepSeekAdapterContext.TryResolveProfile(provider.AdapterId, out profile) &&
            StringComparer.Ordinal.Equals(provider.ConfigurationSha256, ProviderConfigurationSha256(profile));
    }

    private static bool ValidBounds(LivePlanBounds bounds, int evaluations, DeepSeekRequestProfile profile)
    {
        var perCall = bounds.PerCall;
        if (bounds.MaxEvaluations < 1 || bounds.MaxEvaluations > LiveLimits.ExpandedEvaluations ||
            bounds.MaxModelCalls < 1 || bounds.MaxModelCalls > (long)AgentLimits.ModelCalls * evaluations ||
            bounds.MaxInputTokens < 1 || bounds.MaxInputTokens > (long)AgentLimits.InputTokens * evaluations ||
            bounds.MaxOutputTokens < 1 || bounds.MaxOutputTokens > AgentLimits.OutputTokensFor(
                DeepSeekAdapterContext.LimitAuthorityFor(profile).Profile) * evaluations ||
            bounds.MaxCombinedTokens < 1 || bounds.MaxCombinedTokens > AgentLimits.CombinedTokensFor(
                DeepSeekAdapterContext.LimitAuthorityFor(profile).Profile) * evaluations ||
            bounds.MaxSeconds is < 1 or > 86400 || bounds.SpendCeilingMicroUsd < 1 ||
            perCall.MaxInputTokens is < 1 || perCall.MaxInputTokens > Math.Min(bounds.MaxInputTokens, AgentLimits.InputTokens) ||
            perCall.MaxOutputTokens is < 1 || perCall.MaxOutputTokens > Math.Min(bounds.MaxOutputTokens,
                profile == DeepSeekRequestProfile.Current ? DeepSeekRequestWriter.MaxTokens : DeepSeekRequestWriter.CandidateMaxTokens) ||
            profile == DeepSeekRequestProfile.Output8192 && perCall.MaxOutputTokens != DeepSeekRequestWriter.CandidateMaxTokens ||
            perCall.MaxInputTokens + perCall.MaxOutputTokens > bounds.MaxCombinedTokens ||
            perCall.MaxChargeMicroUsd < 1)
            return false;
        return true;
    }

    private static ImmutableArray<string> Expand(ImmutableArray<LivePlanScheduleEntry> schedule)
    {
        var expanded = ImmutableArray.CreateBuilder<string>();
        foreach (var entry in schedule)
        {
            if (entry is null || !EvaluationLimits.Id(entry.CaseId) || entry.Repeats is < 1 or > LiveLimits.Repeats)
                throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);
            for (var repeat = 0; repeat < entry.Repeats; repeat++) expanded.Add(entry.CaseId);
            if (expanded.Count > LiveLimits.ExpandedEvaluations)
                throw new LivePlanRejected(LiveAdmissionCode.InvalidPlan);
        }
        return expanded.ToImmutable();
    }

    internal static string Digest(LivePlanDigestInput input)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(input, LiveJsonContext.Default.LivePlanDigestInput);
        return AgentCanonical.HashDomain(DigestDomain, bytes);
    }
}
