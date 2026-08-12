using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed record AcceptedStatePolicyBinding(
    string PolicyIdentitySha256,
    string ConfigSha256,
    string InstructionsSha256,
    string PayloadSha256,
    string BuildDiscriminator);

internal sealed record AcceptedStatePublicationBinding(
    R4PublicationScopeV1 Scope,
    string ScopeSha256,
    string RepositoryName,
    string PayloadSha256,
    string BuildDiscriminator);

internal sealed record AcceptedStateRestoreResult(
    string Code,
    AcceptedStateContext? Context)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, AcceptedStateCodes.Ready) &&
        Context is not null;

    internal static AcceptedStateRestoreResult Success(
        AcceptedStateContext context) =>
        new(AcceptedStateCodes.Ready, context);

    internal static AcceptedStateRestoreResult Fail(string code) =>
        new(code, null);
}

internal sealed class AcceptedStateContext : IDisposable
{
    private RestrictedStateAdmittedSession? admitted;
    private readonly string stateEnvelopeSha256;
    private readonly AgentSessionHeadTransition transition;

    internal AcceptedStateContext(
        RestrictedStateAdmittedSession admitted,
        string stateEnvelopeSha256,
        AgentSessionHeadTransition transition,
        string logicalGenerationIdentity,
        string selectedLineageHeadIdentity)
    {
        this.admitted = admitted;
        this.stateEnvelopeSha256 = stateEnvelopeSha256;
        this.transition = transition;
        LogicalGenerationIdentity = logicalGenerationIdentity;
        SelectedLineageHeadIdentity = selectedLineageHeadIdentity;
    }

    internal string LogicalGenerationIdentity { get; }
    internal string SelectedLineageHeadIdentity { get; }

    internal bool TryGetAdmittedValue(
        out AgentSessionStateAdmittedValue? value)
    {
        value = Volatile.Read(ref admitted)?.Value;
        return value is not null;
    }

    internal bool TryCreateSuccessorPredecessor(
        out AgentSessionPredecessor? predecessor,
        out AgentSessionHeadTransition admittedTransition)
    {
        predecessor = null;
        admittedTransition = transition;
        var current = Volatile.Read(ref admitted);
        if (current is null ||
            !AcceptedStateRecordValidation.FixedDigest(
                current.SessionSha256,
                current.Value.Artifact.SessionSha256) ||
            !Lineage.LineageValidation.IsSha256(stateEnvelopeSha256))
        {
            return false;
        }

        predecessor = new AgentSessionPredecessor(
            current.Plaintext.ToArray(),
            current.SessionSha256,
            stateEnvelopeSha256,
            current.Generation,
            current.ProducerBaseSha,
            current.ProducerHeadSha,
            current.PredecessorEnvelopeSha256);
        return true;
    }

    internal bool AllowsExpiry(
        string logicalGenerationIdentity,
        string selectedLineageHeadIdentity) =>
        Volatile.Read(ref admitted) is not null &&
        StringComparer.Ordinal.Equals(
            LogicalGenerationIdentity,
            logicalGenerationIdentity) &&
        StringComparer.Ordinal.Equals(
            SelectedLineageHeadIdentity,
            selectedLineageHeadIdentity);

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref admitted, null);
        if (current is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(current.Plaintext);
        CryptographicOperations.ZeroMemory(current.Value.Artifact.Plaintext);
    }

    public override string ToString() => nameof(AcceptedStateContext);
}

internal sealed class AcceptedStateRestoreService
{
    private readonly IRestrictedStateSessionAdmission sessionAdmission;

    internal AcceptedStateRestoreService(
        IRestrictedStateSessionAdmission? sessionAdmission = null)
    {
        this.sessionAdmission = sessionAdmission ??
            new AgentSessionRestrictedStateAdmission();
    }

    internal async Task<AcceptedStateRestoreResult> RestoreAsync(
        AuthorizedStateAccess stateAccess,
        AuthorizedLocatorAccess locatorAccess,
        LocatorContext locatorContext,
        AcceptedStateSelection selection,
        AcceptedStatePolicyBinding policyBinding,
        AcceptedStatePublicationBinding publicationBinding,
        AgentSessionTrustedRequest trustedRequest,
        ReviewedIdentity currentReviewedIdentity,
        ProjectChatMessage currentReviewContext,
        IAgentContinuationCodec continuationCodec,
        TrustedHeadAncestryClassifier ancestryClassifier,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        if (stateAccess is null ||
            locatorAccess is null ||
            locatorContext is null ||
            selection is null ||
            policyBinding is null ||
            publicationBinding is null ||
            trustedRequest is null ||
            currentReviewedIdentity is null ||
            !currentReviewedIdentity.IsValid() ||
            currentReviewContext is null ||
            continuationCodec is null ||
            ancestryClassifier is null)
        {
            return AcceptedStateRestoreResult.Fail(
                AcceptedStateCodes.AccessDenied);
        }

        var selected = selection.Current;
        var generation = selected.Generation;
        if (!MatchesPublication(
                selected,
                policyBinding,
                publicationBinding) ||
            (selection.ImmediatePredecessor is { } predecessor &&
                !MatchesPublication(
                    predecessor,
                    policyBinding,
                    publicationBinding)) ||
            !StringComparer.Ordinal.Equals(
                currentReviewedIdentity.RepositoryId,
                stateAccess.Scope.RepositoryId) ||
            currentReviewedIdentity.ReviewTarget !=
                stateAccess.Scope.ReviewTarget ||
            !StringComparer.Ordinal.Equals(
                trustedRequest.RepositoryId,
                stateAccess.Scope.RepositoryId) ||
            trustedRequest.ReviewTarget != stateAccess.Scope.ReviewTarget ||
            !StringComparer.Ordinal.Equals(
                trustedRequest.WorkflowIdentity,
                stateAccess.Scope.WorkflowIdentity) ||
            !StringComparer.Ordinal.Equals(
                stateAccess.Scope.SessionId,
                selection.LineageHead.Header.SessionId))
        {
            return AcceptedStateRestoreResult.Fail(
                AcceptedStateCodes.ScopeMismatch);
        }

        var transition = await ancestryClassifier.ClassifyAsync(
                repositoryName,
                generation.ProducerBaseSha,
                generation.ProducerHeadSha,
                currentReviewedIdentity.BaseSha,
                currentReviewedIdentity.HeadSha,
                cancellationToken)
            .ConfigureAwait(false);
        if (transition is not (
            AgentSessionHeadTransition.SameHead or
            AgentSessionHeadTransition.VerifiedAhead))
        {
            return AcceptedStateRestoreResult.Fail(
                transition == AgentSessionHeadTransition.Unknown
                    ? AcceptedStateCodes.OutcomeUnknown
                    : AcceptedStateCodes.AncestryFailed);
        }

        var binding = new RestrictedStateBinding(
            stateAccess.Scope,
            generation.ProducerBaseSha,
            generation.ProducerHeadSha,
            generation.Generation,
            generation.PredecessorEnvelopeSha256,
            generation.PreparedAtUnixSeconds,
            generation.PreparedExpiresAtUnixSeconds);
        var keyResolver = new LocatorRestrictedStateKeyResolver(
            stateAccess,
            locatorAccess,
            locatorContext);
        if (!RestrictedStateEnvelope.TryDecrypt(
                stateAccess,
                binding,
                generation.EncryptedStateEnvelope.AsSpan(),
                keyResolver,
                out var plaintext,
                out var failureCode) ||
            plaintext is null)
        {
            return AcceptedStateRestoreResult.Fail(
                StringComparer.Ordinal.Equals(
                    failureCode,
                    RestrictedStateCodes.KeyUnavailable)
                    ? AcceptedStateCodes.KeyUnavailable
                    : AcceptedStateCodes.AuthenticationFailed);
        }

        try
        {
            var sessionContext = new AgentSessionStateAdmissionContext(
                trustedRequest,
                stateAccess.Scope.SessionId,
                currentReviewedIdentity,
                currentReviewContext,
                transition,
                continuationCodec,
                generation.StateEnvelopeSha256);
            var admitted = sessionAdmission.Admit(
                stateAccess,
                plaintext,
                new RestrictedStateSessionAdmissionContext(
                    generation.ProducerBaseSha,
                    generation.ProducerHeadSha,
                    generation.Generation,
                    generation.PredecessorEnvelopeSha256,
                    sessionContext));
            if (!admitted.Succeeded ||
                admitted.Session is null ||
                !AcceptedStateRecordValidation.FixedDigest(
                    admitted.Session.SessionSha256,
                    generation.SessionSha256) ||
                !AcceptedStateRecordValidation.FixedDigest(
                    RestrictedStateEnvelope.EnvelopeSha256(
                        generation.EncryptedStateEnvelope.AsSpan()),
                    generation.StateEnvelopeSha256) ||
                (selection.ImmediatePredecessor is { } admittedPredecessor &&
                    !AcceptedStateRecordValidation.FixedDigest(
                        admitted.Session.Value.Artifact.Document
                            .PriorSessionSha256!,
                        admittedPredecessor.Generation.SessionSha256)))
            {
                if (admitted.Session is not null)
                {
                    Zero(admitted.Session);
                }

                return AcceptedStateRestoreResult.Fail(
                    AcceptedStateCodes.IncompatibleCurrent);
            }

            return AcceptedStateRestoreResult.Success(
                new AcceptedStateContext(
                    admitted.Session,
                    generation.StateEnvelopeSha256,
                    transition,
                    selected.LogicalGenerationIdentity,
                    selection.LineageHead.Header.ObjectIdentity));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static bool MatchesPolicy(
        StateGenerationRecordV1 generation,
        AcceptedStatePolicyBinding expected) =>
        StringComparer.Ordinal.Equals(
            generation.PolicyIdentitySha256,
            expected.PolicyIdentitySha256) &&
        StringComparer.Ordinal.Equals(
            generation.ConfigSha256,
            expected.ConfigSha256) &&
        StringComparer.Ordinal.Equals(
            generation.InstructionsSha256,
            expected.InstructionsSha256) &&
        StringComparer.Ordinal.Equals(
            generation.PayloadSha256,
            expected.PayloadSha256) &&
        StringComparer.Ordinal.Equals(
            generation.BuildDiscriminator,
            expected.BuildDiscriminator);

    private static bool MatchesPublication(
        SelectedAcceptedGeneration selected,
        AcceptedStatePolicyBinding policy,
        AcceptedStatePublicationBinding expected)
    {
        var generation = selected.Generation;
        var receipt = selected.Receipt;
        if (!MatchesPolicy(generation, policy) ||
            !R4PublicationIdentityV1.IsValidScope(expected.Scope) ||
            !AcceptedStateRecordValidation.FixedDigest(
                expected.Scope.PolicyIdentitySha256,
                policy.PolicyIdentitySha256) ||
            !AcceptedStateRecordValidation.FixedDigest(
                expected.PayloadSha256,
                policy.PayloadSha256) ||
            !StringComparer.Ordinal.Equals(
                expected.BuildDiscriminator,
                policy.BuildDiscriminator) ||
            !StringComparer.Ordinal.Equals(
                R4PublicationIdentityV1.ComputeScopeSha256(expected.Scope),
                expected.ScopeSha256) ||
            expected.Scope.RepositoryId > long.MaxValue ||
            expected.Scope.PullRequestNumber > long.MaxValue ||
            !AcceptedStatePublicationPayloadCodec.TryDecode(
                generation.PublicationPayloadBytes.AsSpan(),
                out var publication) ||
            publication is null)
        {
            return false;
        }

        var repositoryId = (long)expected.Scope.RepositoryId;
        var pullRequestNumber = (long)expected.Scope.PullRequestNumber;
        var canonicalCommentUrl =
            $"https://github.com/{expected.RepositoryName}/pull/" +
            $"{pullRequestNumber}#issuecomment-{receipt.CommentId}";
        return publication.RepositoryId == repositoryId &&
            receipt.RepositoryId == repositoryId &&
            publication.PullRequestNumber == pullRequestNumber &&
            receipt.PullRequestNumber == pullRequestNumber &&
            StringComparer.Ordinal.Equals(
                publication.RepositoryName,
                expected.RepositoryName) &&
            StringComparer.Ordinal.Equals(
                receipt.CommentUrl,
                canonicalCommentUrl) &&
            StringComparer.Ordinal.Equals(
                publication.ScopeSha256,
                expected.ScopeSha256) &&
            StringComparer.Ordinal.Equals(
                receipt.ScopeSha256,
                expected.ScopeSha256) &&
            StringComparer.Ordinal.Equals(
                publication.BodySha256,
                receipt.BodySha256) &&
            StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                generation.ProducerHeadSha) &&
            StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                receipt.ReviewedHeadSha) &&
            StringComparer.Ordinal.Equals(
                publication.PolicyIdentitySha256,
                expected.Scope.PolicyIdentitySha256) &&
            StringComparer.Ordinal.Equals(
                publication.PayloadSha256,
                expected.PayloadSha256) &&
            StringComparer.Ordinal.Equals(
                publication.BuildDiscriminator,
                expected.BuildDiscriminator) &&
            AcceptedStateRecordValidation.FixedDigest(
                generation.PublicationPayloadSha256,
                receipt.PublicationPayloadSha256);
    }

    private static void Zero(RestrictedStateAdmittedSession admitted)
    {
        CryptographicOperations.ZeroMemory(admitted.Plaintext);
        CryptographicOperations.ZeroMemory(admitted.Value.Artifact.Plaintext);
    }

    private sealed class LocatorRestrictedStateKeyResolver(
        AuthorizedStateAccess stateAuthority,
        AuthorizedLocatorAccess locatorAuthority,
        LocatorContext context) : IRestrictedStateKeyResolver
    {
        public bool TryGetCurrentWriteKey(
            AuthorizedStateAccess access,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority))
            {
                return false;
            }

            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!context.TryCopyCurrentStateKey(
                        locatorAuthority,
                        material,
                        out var keyId))
                {
                    return false;
                }

                key = new RestrictedStateKey(keyId, material);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }

        public bool TryGetApprovedReadKey(
            AuthorizedStateAccess access,
            string keyId,
            long expiresAtUnixSeconds,
            out RestrictedStateKey? key)
        {
            key = null;
            if (!ReferenceEquals(access, stateAuthority) ||
                !Lineage.LineageValidation.IsSha256(keyId) ||
                !Lineage.LineageValidation.IsTime(expiresAtUnixSeconds))
            {
                return false;
            }

            Span<byte> material = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!context.TryCopyApprovedReadKey(
                        locatorAuthority,
                        keyId,
                        material))
                {
                    return false;
                }

                key = new RestrictedStateKey(keyId, material);
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(material);
            }
        }
    }
}
