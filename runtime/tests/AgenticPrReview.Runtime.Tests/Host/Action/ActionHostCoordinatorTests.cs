using System.Reflection;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostCoordinatorTests
{
    [Fact]
    public void TransactionJournalSeparatesCancellationFromMutationResolution()
    {
        var journal = new ActionHostTransactionJournal(
            ActionHostCancellationState.Requested);

        Assert.True(journal.ObserveCancellation(CancellationToken.None));
        Assert.Equal(
            ActionHostExecutionMode.ReconciliationOnly,
            journal.ExecutionMode);
        Assert.False(journal.HasCurrentRunMutation);
        Assert.Equal(ActionHostStatus.Cancelled, journal.CancellationStatus);

        journal.BeforeMutationDispatch(CancellationToken.None);
        journal.Resolve(OpaqueStoreMutationState.Committed);

        Assert.True(journal.HasCurrentRunMutation);
        Assert.Equal(
            ActionHostOperationResolution.ResolvedCommitted,
            journal.LatestResolution);
        Assert.Equal(
            ActionHostStatus.ProviderFailed,
            journal.CancellationStatus);

        journal.Resolve(OpaqueStoreMutationState.OutcomeUnknown);

        Assert.Equal(
            ActionHostStatus.OutcomeAmbiguous,
            journal.CancellationStatus);

        var transactionOnly = new ActionHostTransactionJournal(
            ActionHostCancellationState.Active);
        Assert.Equal(ActionHostExecutionMode.Normal,
            transactionOnly.ExecutionMode);
        transactionOnly.RecordTransactionAdvance();
        Assert.True(transactionOnly.ObserveCancellation(
            new CancellationToken(canceled: true)));
        Assert.False(transactionOnly.HasCurrentRunMutation);
        Assert.Equal(
            ActionHostStatus.ProviderFailed,
            transactionOnly.CancellationStatus);
    }

    [Fact]
    public void CancelledMutationTokenDoesNotCrossTheDispatchBoundary()
    {
        var journal = new ActionHostTransactionJournal(
            ActionHostCancellationState.Active);
        var cancellationToken = new CancellationToken(canceled: true);

        Assert.Throws<OperationCanceledException>(
            () => journal.BeforeMutationDispatch(cancellationToken));

        Assert.True(journal.ObserveCancellation(CancellationToken.None));
        Assert.False(journal.HasCurrentRunMutation);
        Assert.Equal(
            ActionHostOperationResolution.ResolvedNoCommit,
            journal.LatestResolution);
        Assert.Equal(ActionHostStatus.Cancelled, journal.CancellationStatus);
    }

    [Fact]
    public void HostOutcomeEnumsRemainClosed()
    {
        Assert.Equal(
        [
            "None",
            "AuthorityMismatch",
            "InvalidConfigPath",
            "InvalidInstructionsPath",
            "SourceMissing",
            "SourceNonRegular",
            "SourceIncomplete",
            "SourceIdentityMismatch",
            "ConfigTooLarge",
            "InstructionsTooLarge",
            "MalformedConfig",
            "MalformedInstructions",
            "CredentialDenied",
            "TransportFailure",
            "RequestLimit",
            "AggregateLimit",
            "Deadline",
            "Cancelled",
            "InternalInvariant",
        ],
        Enum.GetNames<ActionHostTrustedPolicyFailure>());
        Assert.Equal(
        [
            "None",
            "UnsupportedSize",
            "InvalidGraph",
            "GitHubUnavailable",
            "MissingObject",
            "IdentityMismatch",
            "Cancelled",
            "InternalFailure",
        ],
        Enum.GetNames<ReviewedTreeFailure>());
        Assert.Equal(
        [
            "None",
            "InvalidRequest",
            "UnsupportedSize",
            "NotFound",
            "Unauthorized",
            "Forbidden",
            "RateLimited",
            "UpstreamUnavailable",
            "InvalidResponse",
            "IdentityMismatch",
            "TransportFailure",
            "StagingFailure",
            "Cancelled",
        ],
        Enum.GetNames<ReviewedSnapshotReadFailure>());
        Assert.Equal(
        [
            "Exact",
            "HeadChanged",
            "PullRequestIneligible",
            "PullRequestMissing",
            "Unauthorized",
            "Forbidden",
            "RateLimited",
            "UpstreamUnavailable",
            "InvalidResponse",
            "TransportFailure",
            "DeadlineExceeded",
            "Cancelled",
        ],
        Enum.GetNames<ExactHeadRevalidationStatus>());
    }

    [Fact]
    public void DispatcherNamesEveryP5RecoveryAction()
    {
        var source = File.ReadAllText(Path.Join(
            FindRepositoryRoot(),
            "runtime",
            "src",
            "AgenticPrReview.Runtime",
            "Host",
            "Action",
            "ActionHostCoordinator.cs"));
        var compactSource = string.Concat(
            source.Where(character => !char.IsWhiteSpace(character)));

        Assert.Equal(15, Enum.GetValues<PublicationRecoveryAction>().Length);
        foreach (var action in Enum.GetNames<PublicationRecoveryAction>())
        {
            Assert.Contains(
                "PublicationRecoveryAction." + action,
                compactSource,
                StringComparison.Ordinal);
        }

        Assert.DoesNotContain(
            "IActionHostProviderRunnerFactory providerFactory,\n        " +
            "PublicationRecoveryDecision",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "ClassifyBeforeProviderAsync",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderProjectionCannotCarryGitHubStateOrPublicationAuthority()
    {
        var properties = typeof(ActionHostProviderPolicy)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["AdapterId", "ModelId", "ProviderId"],
            properties);
        var create = Assert.Single(
            typeof(IActionHostProviderRunnerFactory).GetMethods());
        var parameterTypes = create.GetParameters()
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(
            parameterTypes,
            type => type.Name.Contains("GitHub", StringComparison.Ordinal) ||
                type.Name.Contains("State", StringComparison.Ordinal) ||
                type.Name.Contains("Publication", StringComparison.Ordinal));
    }

    [Fact]
    public void PostAcceptanceAuthorizationIsPrivateAndCoordinatorOwned()
    {
        var capability = typeof(ActionHostCoordinator)
            .GetNestedType(
                "PostAcceptanceInlineAuthorization",
                BindingFlags.NonPublic);
        Assert.NotNull(capability);
        Assert.True(capability.IsNestedPrivate);
        Assert.NotEmpty(capability.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Empty(capability.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));

        Assert.Null(capability.GetMethod(
            "Mint",
            BindingFlags.Static | BindingFlags.Public |
                BindingFlags.NonPublic));
        Assert.Null(capability.GetMethod(
            "TryConsume",
            BindingFlags.Instance | BindingFlags.Public));

        var transaction = typeof(ActionHostCoordinator)
            .GetNestedType(
                "PostAcceptanceInlineTransactionIdentity",
                BindingFlags.NonPublic);
        Assert.NotNull(transaction);
        Assert.True(transaction.IsNestedPrivate);

        var request = typeof(ActionHostCoordinator)
            .GetNestedType(
                "PostAcceptanceInlineRequest",
                BindingFlags.NonPublic);
        Assert.NotNull(request);
        Assert.Empty(request.GetConstructors(
            BindingFlags.Instance | BindingFlags.Public));
        Assert.NotEmpty(request.GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic));
        Assert.Null(request.GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.Public));
        var create = request.GetMethod(
            "Create",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(create);
        Assert.True(create.IsAssembly);

        var mint = typeof(ActionHostCoordinator).GetMethod(
            "MintPostAcceptanceInlineAuthorization",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(mint);
        Assert.True(mint.IsPrivate);
        Assert.Equal(capability, mint.ReturnType);

        var issuer = typeof(ActionHostCoordinator).GetField(
            "PostAcceptanceIssuer",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(issuer);
        Assert.True(issuer.IsPrivate);
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Join(current.FullName, "package.json")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("repository root not found");
    }
}
