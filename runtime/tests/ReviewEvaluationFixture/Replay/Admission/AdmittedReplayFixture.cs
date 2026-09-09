using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

internal sealed class AdmittedReplayFixture
{
    internal AdmittedReplayFixture(string corpusSha256, ImmutableArray<ReplayFile> files, ImmutableArray<AdmittedReplayRun> runs)
    { CorpusSha256 = corpusSha256; Files = files; Runs = runs; }
    internal string CorpusSha256 { get; }
    internal ImmutableArray<ReplayFile> Files { get; }
    internal ImmutableArray<AdmittedReplayRun> Runs { get; }
    public override string ToString() => "admitted_replay_fixture";
}

internal sealed class AdmittedReplayRun
{
    private readonly ImmutableDictionary<string, ImmutableArray<byte>> repository;
    private readonly ImmutableArray<ReviewedDiffSource> diffs;
    private readonly ReplayConfiguration configuration;
    private readonly ImmutableArray<byte> policy;

    internal AdmittedReplayRun(ReplayRun input, ReplayConfiguration configuration,
        ImmutableDictionary<string, ImmutableArray<byte>> repository, ImmutableArray<ReviewedDiffSource> diffs,
        ImmutableArray<byte> policy, string context, ReplayScript script, EvaluationCase expected, EvaluationCode expectedCode)
    {
        Input = input; this.configuration = configuration; this.repository = repository; this.diffs = diffs;
        this.policy = policy; InitialContext = context; Script = script; Expected = expected; ExpectedCode = expectedCode;
        ProviderConfigurationSha256 = EvaluationAttempt.Hash("replay-provider-settings",
            configuration.ProviderId, configuration.ModelId, configuration.AdapterId);
        if (!AgentStableRequestMaterializer.TryMaterialize(CreateTrustedRequest("r5-replay-admission"), null, out var stable))
            throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
        ConfigurationSha256 = EvaluationAttempt.ConfigurationIdentity(stable!.StablePlan,
            new(input.Id, "deterministic", EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean, ProviderConfigurationSha256));
        _ = CreateSnapshot(Directory.GetCurrentDirectory());
    }

    internal ReplayRun Input { get; }
    internal string InitialContext { get; }
    internal ReplayScript Script { get; }
    internal EvaluationCase Expected { get; }
    internal EvaluationCode ExpectedCode { get; }
    internal string ConfigurationSha256 { get; }
    internal string ProviderConfigurationSha256 { get; }
    internal AgentSessionTrustedRequest CreateTrustedRequest(string buildId) => new(Input.ReviewedIdentity.RepositoryId,
        Input.ReviewedIdentity.ReviewTarget, configuration.WorkflowIdentity, policy.ToArray(), buildId,
        configuration.ProviderId, configuration.ModelId, configuration.AdapterId);

    internal ReviewedSnapshot CreateSnapshot(string logicalRoot) => new(Input.ReviewedIdentity.Runtime, logicalRoot,
        repository.Keys, diffs.Select(source => new ReviewedChangedFile(source.Path, source.PreviousPath, source.Status,
            source.RepresentedAdditions, source.RepresentedDeletions, source.RepresentedAdditions + source.RepresentedDeletions,
            "available", source.PatchSha256, source.SourceTruncated)), diffs);

    // The physical bundle is never reopened. The tool adapter exposes repository members only.
    internal IReviewedFileAccess CreateFileAccess(ReviewedSnapshot snapshot)
    {
        if (snapshot.Identity != Input.ReviewedIdentity.Runtime ||
            !snapshot.OrderedTrackedFiles.SequenceEqual(repository.Keys.Order(StringComparer.Ordinal)))
            throw new ArgumentException("replay_snapshot_mismatch");
        return new ReplayMemoryFiles(snapshot, repository);
    }

    public override string ToString() => "admitted_replay_run";
}

internal sealed class ReplayMemoryFiles(ReviewedSnapshot admitted, ImmutableDictionary<string, ImmutableArray<byte>> files) : IReviewedFileAccess
{
    private bool TryGet(ReviewedSnapshot snapshot, string path, out ImmutableArray<byte> bytes)
    {
        bytes = default;
        return ReferenceEquals(snapshot, admitted) && snapshot.Contains(path) && files.TryGetValue(path, out bytes);
    }

    public ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path) => TryGet(snapshot, path, out var bytes)
        ? new(ReviewedFileAccessStatus.Success, bytes.Length) : ReviewedFileMetadata.Unsafe();

    public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path)
    {
        if (!TryGet(snapshot, path, out var bytes)) return ReviewedFileProbe.Unsafe();
        var hash = SHA256.HashData(bytes.AsSpan());
        return new(ReviewedFileAccessStatus.Success, bytes.Length,
            new(BinaryPrimitives.ReadUInt64LittleEndian(hash), BinaryPrimitives.ReadUInt64LittleEndian(hash.AsSpan(8))));
    }

    public ValueTask<ReviewedFileRead> ReadAsync(ReviewedSnapshot snapshot, string path, ReviewedFileProbe expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var current = Probe(snapshot, path);
        return ValueTask.FromResult(current.Status == ReviewedFileAccessStatus.Success && current == expected &&
            TryGet(snapshot, path, out var bytes) ? new ReviewedFileRead(ReviewedFileAccessStatus.Success, bytes.ToArray()) : ReviewedFileRead.Unsafe());
    }
}
