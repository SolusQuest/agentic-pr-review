using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

// Ephemeral test evidence, not a public protocol or a restricted-state reader.
internal sealed record SourceFact(string Commit, string Tree, bool Clean, string BuildSha256, string CorpusSha256);
internal sealed record DomainFact(string PackageVersion, string Profile, string ClosureSha256,
    Guid ImplementationId, int FormatVersion, int CompatibilityVersion,
    string RuntimeConfigurationSha256, string ProviderConfigurationSha256);
internal sealed record DetailFact(ProviderTokenCounterKind Kind, long? Value, TokenCounterRelationship Relationship);
internal sealed record AttemptFact(Guid ExecutionId, Guid LogicalCallId, Guid PhysicalAttemptId, int AttemptNumber,
    DispatchExposure Exposure, long? InputTokens, long? OutputTokens, DetailFact[] Details)
{
    internal static AttemptFact From(UsageAttemptObservation value) => new(value.ExecutionId, value.LogicalCallId,
        value.PhysicalAttemptId, value.AttemptNumber, value.Exposure, value.Usage.InputTokens, value.Usage.OutputTokens,
        value.Usage.ProviderCounters.Select(c => new DetailFact(c.Kind, c.Value, c.Relationship)).ToArray());
    internal UsageAttemptObservation ToObservation() => new(ExecutionId, LogicalCallId, PhysicalAttemptId,
        AttemptNumber, Exposure, new(InputTokens, OutputTokens,
            Details.Select(d => new ProviderTokenCounter(d.Kind, d.Value, d.Relationship)).ToArray()));
}
internal sealed record TokenFact(long? ObservedTokens, TokenObservationCoverage Coverage);
internal sealed record RunFact(Guid ExecutionId, UsageInventoryCoverage Coverage, int ContinuedCalls,
    AttemptFact[] Attempts, TokenFact Input, TokenFact Output)
{
    internal static RunFact From(AgentRunUsage value) => new(value.ExecutionId, value.Coverage,
        value.ContinuedCalls.Count, value.Attempts.Select(AttemptFact.From).ToArray(),
        new(value.InputTokens.ObservedTokens, value.InputTokens.Coverage),
        new(value.OutputTokens.ObservedTokens, value.OutputTokens.Coverage));
}
internal sealed record RoundFact(Guid LogicalWorkId, Guid RoundId, RunFact Usage);
internal sealed record ExchangeFact(AttemptFact Observation, ProviderOutcome Outcome, ProviderError Error);
internal sealed record SegmentFact(string Sha256, int Bytes);
internal sealed record MessageFact(string Role, SegmentFact Token, string? ContentSha256,
    string? ReasoningSha256, string? ToolAssociationSha256);
internal sealed record WireFact(Guid ExecutionId, Guid LogicalCallId, Guid PhysicalAttemptId, int AttemptNumber,
    SegmentFact Whole, SegmentFact Settings, MessageFact[] Messages);
// This matches normalized response fields, not a fabricated serialized request.
internal sealed record FinalFact(string ContentSha256, string ReasoningSha256, int Calls);
internal sealed record EconomicsDocument(string Format, string ExecutionKind, string Scenario, SourceFact Source, DomainFact? Domain,
    int ProcessId, string StartupSha256, ContextAdmission? Admission, ContextCaptureStatus? Capture,
    Guid? LogicalWorkId, Guid? RoundId, bool HistoryReported, RunFact? Current, RoundFact[] History,
    ExchangeFact[] Exchanges, WireFact[] Wire, FinalFact? FinalResponse,
    bool ProductAccepted, bool DurableAccepted, bool RecoveredWithoutWork, bool HostAccountingZero,
    int CurrentTools, int NativeProviderCreates, PriceFact Price, string BindingSha256);
internal sealed record PriceView(decimal? KnownInput, decimal? KnownOutput, bool InputComplete,
    bool OutputComplete, decimal? Total);
internal sealed record PriceFact(string Reference, PriceView Observed, PriceView SameTokenAllMissCounterfactual);
internal sealed record ComparisonFact(string PrefixEligibility, bool? PrefixStable, string EconomicsEligibility, decimal? ReferenceDifference,
    string Billing, string CacheSavings, string Quality);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, AllowDuplicateProperties = false,
    RespectRequiredConstructorParameters = true, UseStringEnumConverter = true, MaxDepth = 32)]
[JsonSerializable(typeof(EconomicsDocument))]
[JsonSerializable(typeof(ComparisonFact))]
internal sealed partial class EconomicsJson : JsonSerializerContext;

internal static class Evidence
{
    internal const string Format = "apr.r6.solus-evidence.v1";
    internal const int MaximumReportBytes = 1024 * 1024;
    internal static readonly string[] Scenarios = ["seed", "known", "zero", "total-only", "hit-only", "miss-only",
        "missing-usage", "invalid-final", "invalid-usage", "capture-failure", "transport-failure", "missing-key", "recovery",
        "reject-import", "stale-publication", "cancel-after-usage"];

    internal static string Hash(string domain, string value) => Hash(domain, Encoding.UTF8.GetBytes(value));
    internal static string Hash(string domain, ReadOnlySpan<byte> value)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("apr.r6.solus." + domain + "\0"));
        Span<byte> length = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, value.Length);
        hash.AppendData(length); hash.AppendData(value);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
    internal static bool Digest(string? value, int length = 64) => value is not null && value.Length == length &&
        value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    internal static bool Id(Guid value) => value != Guid.Empty;
    internal static bool Name(string? value, int maximum) => value is { Length: > 0 } && value.Length <= maximum &&
        value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    internal static byte[] Write(EconomicsDocument value) => JsonSerializer.SerializeToUtf8Bytes(value, EconomicsJson.Default.EconomicsDocument);
    internal static string Binding(EconomicsDocument value) => Hash("document", Write(value with { BindingSha256 = "" }));
    internal static EconomicsDocument Seal(EconomicsDocument value) => value with { BindingSha256 = Binding(value) };
    internal static EconomicsDocument? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > MaximumReportBytes) return null;
        try
        {
            var value = JsonSerializer.Deserialize(bytes, EconomicsJson.Default.EconomicsDocument);
            return value is not null && Admit(value) ? value : null;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or
            OverflowException or NullReferenceException) { return null; }
    }
    internal static bool Admit(EconomicsDocument value)
    {
        if (value.Format != Format || value.ExecutionKind != "controlled" || !Scenarios.Contains(value.Scenario) || value.Source is not { } source ||
            !Digest(source.Commit, 40) || !Digest(source.Tree, 40) || !Digest(source.BuildSha256) || !Digest(source.CorpusSha256) ||
            value.ProcessId <= 0 || !Digest(value.StartupSha256) || value.NativeProviderCreates != 0 ||
            value.CurrentTools is < 0 or > 6 || value.History is null || value.History.Length > 64 ||
            value.Exchanges is null || value.Exchanges.Length > 6 || value.Wire is null || value.Wire.Length > 6 ||
            value.Admission is { } admission && !Enum.IsDefined(admission) ||
            value.Capture is { } capture && !Enum.IsDefined(capture) ||
            value.LogicalWorkId == Guid.Empty || value.RoundId == Guid.Empty ||
            value.Domain is { } domain && (!Name(domain.PackageVersion, 64) || !Name(domain.Profile, 64) ||
                !Digest(domain.ClosureSha256) || !Id(domain.ImplementationId) || domain.FormatVersion <= 0 ||
                domain.CompatibilityVersion <= 0 || !Digest(domain.RuntimeConfigurationSha256) || !Digest(domain.ProviderConfigurationSha256))) return false;
        var physical = new HashSet<Guid>(); var executions = new HashSet<Guid>();
        var logical = new Dictionary<Guid, Guid>(); var rounds = new HashSet<Guid>(); var workIds = new HashSet<Guid>();
        bool LogicalOwners(RunFact run)
        {
            foreach (var attempt in run.Attempts)
            {
                if (logical.TryGetValue(attempt.LogicalCallId, out var owner) && owner != run.ExecutionId) return false;
                logical[attempt.LogicalCallId] = run.ExecutionId;
            }
            return true;
        }
        foreach (var history in value.History)
        {
            if (history is null || !Id(history.LogicalWorkId) || !Id(history.RoundId) || !Run(history.Usage) ||
                !executions.Add(history.Usage.ExecutionId) || !rounds.Add(history.RoundId) || !workIds.Add(history.LogicalWorkId) ||
                !LogicalOwners(history.Usage) || history.Usage.Attempts.Any(a => !physical.Add(a.PhysicalAttemptId))) return false;
        }
        if (value.Current is { } current)
        {
            if (!Run(current) || !executions.Add(current.ExecutionId) || !LogicalOwners(current) ||
                current.Attempts.Any(a => !physical.Add(a.PhysicalAttemptId)) || value.RecoveredWithoutWork) return false;
            if (value.HostAccountingZero && current.Attempts.Any(a => a.Exposure != DispatchExposure.NotDispatched)) return false;
        }
        else if (value.Exchanges.Length != 0 || value.Wire.Length != 0 || value.CurrentTools != 0 || value.ProductAccepted) return false;
        if (value.RoundId is { } roundId && !rounds.Add(roundId) || value.LogicalWorkId is { } workId && !workIds.Add(workId) ||
            value.RoundId.HasValue != value.LogicalWorkId.HasValue ||
            !value.HistoryReported && value.History.Length != 0 ||
            value.HistoryReported != (value.Admission is ContextAdmission.Fresh or ContextAdmission.Supplied) ||
            value.Admission == ContextAdmission.Fresh && value.History.Length != 0 ||
            value.Admission == ContextAdmission.Supplied && value.History.Length == 0 ||
            value.Admission == ContextAdmission.Rejected && value.Current is not null) return false;
        if (value.RecoveredWithoutWork && (!value.DurableAccepted || !value.HostAccountingZero || value.Current is not null)) return false;
        if (value.DurableAccepted && !value.RecoveredWithoutWork && (!value.ProductAccepted || value.Domain is null ||
            value.Capture != ContextCaptureStatus.Delivered || value.LogicalWorkId is null || value.RoundId is null)) return false;
        if (value.ProductAccepted && (value.Current is not { Coverage: UsageInventoryCoverage.Complete } accepted ||
            accepted.ContinuedCalls != 0 || accepted.Input.Coverage != TokenObservationCoverage.Complete ||
            accepted.Output.Coverage != TokenObservationCoverage.Complete || accepted.Attempts.Length == 0)) return false;
        if (!Associations(value) || value.Price != SyntheticPrice.Create(value.Current, value.HostAccountingZero) ||
            !Digest(value.BindingSha256) || value.BindingSha256 != Binding(value)) return false;
        return true;
    }
    internal static bool Run(RunFact? value)
    {
        if (value is null || !Id(value.ExecutionId) || !Enum.IsDefined(value.Coverage) || value.ContinuedCalls != 0 ||
            value.Attempts is null || value.Attempts.Length > 64 || value.Input is null || value.Output is null) return false;
        if (value.Attempts.Any(a => a is null || a.Details is null || a.Details.Length > 4)) return false;
        var reconstructed = new AgentRunUsage(value.ExecutionId, value.Coverage, value.Attempts.Select(a => a.ToObservation()).ToArray());
        return value.Input == new TokenFact(reconstructed.InputTokens.ObservedTokens, reconstructed.InputTokens.Coverage) &&
            value.Output == new TokenFact(reconstructed.OutputTokens.ObservedTokens, reconstructed.OutputTokens.Coverage);
    }
    private static bool Associations(EconomicsDocument value)
    {
        var exchangeIds = new HashSet<Guid>();
        foreach (var exchange in value.Exchanges)
        {
            if (exchange is null || exchange.Observation is not { } fact || !exchangeIds.Add(fact.PhysicalAttemptId) ||
                !Enum.IsDefined(exchange.Outcome) || !Enum.IsDefined(exchange.Error) ||
                (exchange.Outcome == ProviderOutcome.Succeeded) != (exchange.Error == ProviderError.None)) return false;
            var current = value.Current?.Attempts.SingleOrDefault(a => a.PhysicalAttemptId == fact.PhysicalAttemptId);
            if (current is null || !Same(current, fact)) return false;
        }
        var wireIds = new HashSet<Guid>();
        foreach (var wire in value.Wire)
        {
            if (wire is null || !wireIds.Add(wire.PhysicalAttemptId) || !Segment(wire.Whole) || !Segment(wire.Settings) ||
                wire.Messages is null || wire.Messages.Length is < 1 or > 32 ||
                wire.Messages[0].Role != "system" || wire.Messages.Skip(1).Any(m => m.Role == "system")) return false;
            var current = value.Current?.Attempts.SingleOrDefault(a => a.PhysicalAttemptId == wire.PhysicalAttemptId);
            if (current is null || current.ExecutionId != wire.ExecutionId || current.LogicalCallId != wire.LogicalCallId ||
                current.AttemptNumber != wire.AttemptNumber || current.Exposure == DispatchExposure.NotDispatched) return false;
            foreach (var message in wire.Messages)
                if (message is null || message.Role is not ("system" or "user" or "assistant" or "tool") || !Segment(message.Token) ||
                    message.ContentSha256 is { } content && !Digest(content) ||
                    message.ReasoningSha256 is { } reasoning && !Digest(reasoning) ||
                    message.ToolAssociationSha256 is { } association && !Digest(association) ||
                    message.Role == "assistant" && message.ReasoningSha256 is null ||
                    message.Role != "assistant" && (message.ContentSha256 is null || message.ReasoningSha256 is not null) ||
                    message.Role == "tool" && message.ToolAssociationSha256 is null ||
                    message.Role is "system" or "user" && message.ToolAssociationSha256 is not null) return false;
        }
        if (value.ProductAccepted && (value.Current!.Attempts.Length != exchangeIds.Count ||
            value.Exchanges.Any(e => e.Outcome != ProviderOutcome.Succeeded))) return false;
        if (value.Current?.Coverage == UsageInventoryCoverage.Complete &&
            value.Current.Attempts.Any(a => a.Exposure == DispatchExposure.Dispatched && !wireIds.Contains(a.PhysicalAttemptId))) return false;
        return value.FinalResponse is null || Digest(value.FinalResponse.ContentSha256) &&
            Digest(value.FinalResponse.ReasoningSha256) && value.FinalResponse.Calls == 0;
    }
    internal static bool Same(AttemptFact left, AttemptFact right) => left.ExecutionId == right.ExecutionId &&
        left.LogicalCallId == right.LogicalCallId && left.PhysicalAttemptId == right.PhysicalAttemptId &&
        left.AttemptNumber == right.AttemptNumber && left.Exposure == right.Exposure &&
        left.InputTokens == right.InputTokens && left.OutputTokens == right.OutputTokens && left.Details.SequenceEqual(right.Details);
    private static bool Segment(SegmentFact? value) => value is not null && Digest(value.Sha256) && value.Bytes is > 0 and <= 1024 * 1024;
}
