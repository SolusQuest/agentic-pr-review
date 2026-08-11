using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State.Locator;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed record AcceptedStatePolicyBinding(
    string PolicyIdentitySha256,
    string ConfigSha256,
    string InstructionsSha256,
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

    internal AcceptedStateContext(
        RestrictedStateAdmittedSession admitted,
        string logicalGenerationIdentity,
        string selectedLineageHeadIdentity)
    {
        this.admitted = admitted;
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
        if (!MatchesPolicy(generation, policyBinding) ||
            !AcceptedStatePublicationPayloadCodec.TryDecode(
                generation.PublicationPayloadBytes.AsSpan(),
                out var publication) ||
            publication is null ||
            publication.RepositoryId != selected.Receipt.RepositoryId ||
            publication.PullRequestNumber !=
                selected.Receipt.PullRequestNumber ||
            !StringComparer.Ordinal.Equals(
                publication.ScopeSha256,
                selected.Receipt.ScopeSha256) ||
            !StringComparer.Ordinal.Equals(
                publication.BodySha256,
                selected.Receipt.BodySha256) ||
            !StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                selected.Receipt.ReviewedHeadSha) ||
            !StringComparer.Ordinal.Equals(
                publication.PolicyIdentitySha256,
                policyBinding.PolicyIdentitySha256) ||
            !StringComparer.Ordinal.Equals(
                publication.PayloadSha256,
                policyBinding.PayloadSha256) ||
            !StringComparer.Ordinal.Equals(
                publication.BuildDiscriminator,
                policyBinding.BuildDiscriminator) ||
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
                    generation.StateEnvelopeSha256))
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
