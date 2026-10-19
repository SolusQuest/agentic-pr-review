using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Host.State.Restore;

// Immutable configuration data, not authorization. Only a genuine current Host
// target can supply it to runtime construction. Action policy identity remains
// the original object; direct authorization never fabricates an Action policy.
internal sealed record SolusRuntimePolicy(
    string ProviderId, string ModelId, string AdapterId, string Endpoint,
    string ConfigSha256, string LimitsSha256, string ToolsetSha256,
    AgentLimitAuthority LimitAuthority, ImmutableArray<byte> ConfigBytes,
    ImmutableArray<byte> InstructionBytes)
{
    internal static SolusRuntimePolicy FromAction(ActionHostTrustedPolicy policy) => new(
        policy.ProviderId, policy.ModelId, policy.AdapterId, policy.Endpoint,
        policy.ConfigSha256, policy.LimitsSha256, policy.ToolsetSha256,
        policy.LimitAuthority, policy.ConfigBytes, policy.InstructionBytes);

    internal static SolusRuntimePolicy FromDirect(SolusDirectReviewAuthorization authorization)
    {
        var scope = authorization.Root.Access.Scope;
        // The direct contract has trusted instruction text and no numeric
        // config fields. A real JSON object selects V3's managed defaults.
        var config = ImmutableArray.CreateRange("{}"u8.ToArray());
        return new(scope.ProviderId, scope.ModelId, scope.AdapterId,
            "https://api.deepseek.com/chat/completions",
            AgentCanonical.HashRaw(config.AsSpan()), scope.LimitsSha256, scope.ToolsetSha256,
            new(scope.AdapterId, AgentLimitProfile.Current), config,
            ImmutableArray.CreateRange(authorization.Input.TrustedRequest.TrustedPolicyBytes));
    }

    public override string ToString() => nameof(SolusRuntimePolicy);
}

// Issued from the genuine filesystem owner and original layout lifetime. Each
// public input is re-read and admitted here; caller-supplied parsed documents,
// snapshot objects and saved state cannot manufacture this capability.
internal sealed class SolusDirectReviewAuthorization : IDisposable
{
    private readonly LiveAgentFreshProcessFileSystem files;
    private readonly LiveAgentFreshProcessSnapshotInput materialized;
    private readonly byte[][] publicInputs;
    private int disposed;

    private SolusDirectReviewAuthorization(LiveAgentFreshProcessFileSystem files,
        LiveAgentFreshProcessAuthorizedRoot root, LiveAgentFreshProcessAuthorizedInput input,
        ReviewedSnapshot snapshot, ProjectChatMessage currentContext,
        LiveAgentFreshProcessSnapshotInput materialized, byte[][] publicInputs)
    {
        this.files = files;
        Root = root;
        Input = input;
        Snapshot = snapshot;
        CurrentContext = currentContext;
        this.materialized = materialized;
        this.publicInputs = publicInputs;
    }

    internal LiveAgentFreshProcessAuthorizedRoot Root { get; }
    internal LiveAgentFreshProcessAuthorizedInput Input { get; }
    internal ReviewedSnapshot Snapshot { get; }
    internal ProjectChatMessage CurrentContext { get; }
    internal IReadOnlyList<byte[]> PublicInputs => publicInputs;
    internal IReviewedFileAccess CreateFileAccess() => IsLive
        ? materialized.FileAccessFactory.Create()
        : throw new InvalidOperationException("APR_SOLUS_DIRECT_AUTHORITY");

    internal bool IsLive
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0 || !files.IsCurrentAuthority(Root) ||
                Root.ContextExclusion?.IsLive != true || Root.SourceAuthorization is not { } source ||
                !source.BelongsTo(files)) return false;
            var current = files.ReadAuthorization();
            return current is not null && current.BelongsTo(files) && current.Version == source.Version &&
                LiveAgentFreshProcessDomain.RawSha256(current.Bytes) == Root.SourceAuthorizationSha256;
        }
    }

    internal static SolusDirectReviewAuthorization? TryCreate(
        LiveAgentFreshProcessFileSystem files, LiveAgentFreshProcessAuthorizedRoot root)
    {
        if (!OperatingSystem.IsLinux() || !files.IsCurrentAuthority(root) ||
            root.ContextExclusion?.IsLive != true || root.SourceAuthorization is not { } source ||
            !source.BelongsTo(files)) return null;
        var authorizationRead = files.ReadAuthorization();
        var reviewedRead = files.ReadReviewedInput(root);
        var manifestRead = files.ReadSnapshotManifest(root);
        if (authorizationRead is null || reviewedRead is null || manifestRead is null ||
            !authorizationRead.BelongsTo(files) || authorizationRead.Version != source.Version ||
            LiveAgentFreshProcessDomain.RawSha256(authorizationRead.Bytes) != root.SourceAuthorizationSha256)
            return null;
        var document = LiveAgentFreshProcessCodec.ReadAuthorization(authorizationRead.Bytes);
        var reviewed = LiveAgentFreshProcessCodec.ReadReviewedInput(reviewedRead.Bytes);
        var manifest = LiveAgentFreshProcessCodec.ReadSnapshotManifest(manifestRead.Bytes);
        if (document is null || reviewed is null || manifest is null ||
            !LiveAgentFreshProcessCommand.TryAdmitAuthorization(document, out var input, out var result,
                out var access) || result?.Action != StateAction.Authorized || access?.Scope != root.Access.Scope ||
            input is null || input.Scope != root.Access.Scope ||
            !LiveAgentFreshProcessCommand.TryAdmitReviewedInput(input, reviewed, out var identity, out var context) ||
            identity!.BaseSha != input.Document.Transition.ToBaseSha ||
            identity.HeadSha != input.Document.Transition.ToHeadSha ||
            !LiveAgentFreshProcessManifest.TryAdmit(manifest, identity!, root.RootPath, out var materialized))
            return null;
        try
        {
            var snapshot = new ReviewedSnapshot(identity!, root.RootPath, materialized!.TrackedFiles,
                materialized.ChangedFiles, materialized.DiffSources);
            var issued = new SolusDirectReviewAuthorization(files, root, input!, snapshot, context!, materialized,
                [authorizationRead.Bytes.ToArray(), reviewedRead.Bytes.ToArray(), manifestRead.Bytes.ToArray()]);
            if (issued.IsLive) return issued;
            issued.Dispose();
            return null;
        }
        catch
        {
            (materialized!.FileAccessFactory as IDisposable)?.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        (materialized.FileAccessFactory as IDisposable)?.Dispose();
        foreach (var bytes in publicInputs) CryptographicOperations.ZeroMemory(bytes);
    }

    public override string ToString() => nameof(SolusDirectReviewAuthorization);
}
