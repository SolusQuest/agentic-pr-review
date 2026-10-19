using System.Globalization;
using System.Runtime.CompilerServices;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Materialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

// Package-independent Host transaction tests use structural records. They do
// not establish SDK context grammar/import or installed-package qualification.
// Construction still crosses the genuine compiled Host authority, physical
// immutable reviewed root and invocation exclusion boundaries.
internal sealed class SolusTransactionTestSupport : IDisposable
{
    private sealed record Binding(Guid Execution, SolusRestrictedStateCompatibility Compatibility, SolusTransactionTestSupport Owner);
    private static readonly ConditionalWeakTable<AuthorizedAcceptedStateRestoreContext, Binding> Bindings = new();
    private readonly string parent;
    private readonly BoundedReviewedSnapshotLease snapshot;
    private readonly SolusFreshHostContext targets;
    private readonly SharedExclusion exclusion;
    private bool disposed;
    private AuthorizedAcceptedStateRestoreContext? context;
    internal ArtifactStateRestoreRequest Request { get; }
    internal bool CanShareInvocation(ArtifactStateRestoreRequest request) =>
        !disposed && exclusion.Value.MatchesInvocation(request);

    private sealed class SharedExclusion(SolusContextExclusion value)
    {
        internal SolusContextExclusion Value { get; } = value;
        internal int References = 1;
    }

    private SolusTransactionTestSupport(string parent, BoundedReviewedSnapshotLease snapshot,
        SolusFreshHostContext targets, SharedExclusion exclusion, ArtifactStateRestoreRequest request)
    {
        this.parent = parent; this.snapshot = snapshot; this.targets = targets;
        this.exclusion = exclusion; Request = request;
    }

    internal static SolusRestrictedStateCompatibility Compatibility() => new(
        SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
        SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
        SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
        SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));

    internal static async Task<SolusTransactionTestSupport> CreateAsync(ArtifactStateRestoreRequest request,
        SolusTransactionTestSupport? previous = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Solus invocation authority requires Linux.");
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        BoundedReviewedSnapshotLease? snapshot = null;
        SharedExclusion? shared = null;
        SolusFreshHostContext? targets = null;
        try
        {
            await using var tree = await H5SnapshotTestSupport.TreeAsync(request.Invocation, parent);
            var materialized = await ReviewedRootMaterializer.MaterializeAsync(tree, parent, default);
            var root = Assert.IsType<ReviewedRootLease>(materialized.Lease);
            var invocation = request.Invocation;
            var identity = new ReviewedIdentity(request.Launch.RepositoryId.ToString(CultureInfo.InvariantCulture),
                invocation.PullRequest.Number, invocation.PullRequest.BaseSha, invocation.PullRequest.HeadSha);
            var reviewed = new ReviewedSnapshot(identity, root.AbsoluteRoot, root.RegularPaths);
            var empty = AgentCanonical.HashRaw([]);
            snapshot = new(reviewed, new(request.Launch.RepositoryId, invocation.PullRequest.Number,
                identity.BaseSha, identity.HeadSha, tree.Identity.Sha256, empty, empty, root.Identity.Sha256), root);
            if (previous is not null && previous.exclusion.Value.MatchesInvocation(request))
            {
                shared = previous.exclusion;
                Interlocked.Increment(ref shared.References);
            }
            else shared = new(Assert.IsType<SolusContextExclusion>(
                await SolusContextExclusion.AcquireInvocationAsync(request, default)));
            targets = Assert.IsType<SolusFreshHostContext>(SolusFreshHostContext.TryCreate(request, snapshot, shared.Value));
            var selected = request with { SolusTransaction = new(targets, Compatibility(), Guid.NewGuid(), SolusTransactionMode.FreshOrCompletedContextReview) };
            return new(parent, snapshot, targets, shared, selected);
        }
        catch
        {
            targets?.Dispose();
            if (shared is not null && Interlocked.Decrement(ref shared.References) == 0) shared.Value.Dispose();
            if (snapshot is not null) await snapshot.DisposeAsync();
            Directory.Delete(parent, true);
            throw;
        }
    }

    internal void Bind(AuthorizedAcceptedStateRestoreContext context)
    {
        this.context = context;
        Bindings.Add(context, new(Request.SolusTransaction!.ExecutionId, Compatibility(), this));
    }

    internal static async Task<RetainedStateTransactionResult<RetainedStatePreparedCandidate>> PrepareAsync(
        AuthorizedAcceptedStateRestoreContext context, R4PreparedPublication publication, CancellationToken token)
    {
        if (context is null || !Bindings.TryGetValue(context, out var binding) ||
            !publication.TryProject(out var outcome, out _, out var scope) || outcome?.ReviewedIdentity is null ||
            outcome.Review is null || !R4ValidatedPublicationReview.TryCreateProjection(outcome.Review,
                outcome.ReviewedIdentity, scope!, out var review) || review is null)
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.AccessDenied);
        if (RestrictedStateService.CanPrepareFreshSolus(context))
        {
            Assert.True(SolusRestrictedStateRecord.TryCreate(binding.Compatibility,
                new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), binding.Execution), [1, 2, 3], out var record));
            return await RestrictedStateService.PrepareRetainedSolusCandidateAsync(context, record!, review, token);
        }
        if (!RestrictedStateService.CanPrepareRestoredSolus(context))
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.AccessDenied);
        var owner = binding.Owner;
        var selected = await RestrictedStateService.RestoreAuthorizedArtifactStateAsync(owner.Request with {
            SolusTransaction = null, SolusSourceSelection = new(owner.targets, binding.Compatibility) }, token);
        using var source = selected.SolusSource;
        if (source is null)
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.AccessDenied);
        using var consumed = await source.PrepareAsync(binding.Execution, Guid.NewGuid(), token);
        if (consumed is null || !consumed.TryClaim(owner.targets, binding.Compatibility, consumed.Claim.Source,
                consumed.Claim.GrantId, binding.Execution, consumed.Claim.RoundId, consumed.CopyPayload()))
            return RetainedStateTransactionResult<RetainedStatePreparedCandidate>.Fail(RetainedStateTransactionCodes.AccessDenied);
        Assert.True(SolusRestrictedStateRecord.TryCreate(binding.Compatibility,
            new(Guid.NewGuid(), Guid.NewGuid(), consumed.Claim.RoundId, binding.Execution), [1, 2, 3], out var successor));
        return await RestrictedStateService.PrepareRetainedRestoredSolusCandidateAsync(context, consumed, successor!, review, token);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        context?.Dispose();
        targets.Dispose();
        if (Interlocked.Decrement(ref exclusion.References) == 0) exclusion.Value.Dispose();
        snapshot.DisposeAsync().AsTask().GetAwaiter().GetResult();
        Directory.Delete(parent, true);
    }
}
