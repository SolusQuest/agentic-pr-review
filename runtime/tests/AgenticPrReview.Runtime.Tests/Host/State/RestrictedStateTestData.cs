using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

internal static class RestrictedStateTestData
{
    internal const long Now = 1_700_000_000;
    internal const long Expires = Now + 604_800;
    internal static readonly byte[] Key =
        Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    internal static RestrictedStateScope Scope() =>
        new(
            "repo",
            "workflow",
            1,
            "session_0",
            "provider",
            "model",
            "adapter",
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            "build");

    internal static AuthorizedStateAccess Access(
        RestrictedStateScope? scope = null)
    {
        var actual = scope ?? Scope();
        var result = AuthorizedStateAccess.Authorize(
            new RestrictedStateAccessRequest(
                actual,
                actual,
                IsTrustedWorkflow: true,
                IsSameRepository: true,
                IsForkOrigin: false),
            out var access);
        Assert.Equal(StateAction.Authorized, result.Action);
        return access!;
    }

    internal static RestrictedStateBinding Binding(
        long generation = 0,
        string? predecessor = null,
        RestrictedStateScope? scope = null) =>
        new(
            scope ?? Scope(),
            new string('4', 40),
            new string('5', 40),
            generation,
            predecessor,
            Now,
            Expires);

    internal static RestrictedStateSessionAdmissionContext SessionContext(
        long generation = 0,
        string? predecessor = null) =>
        new(
            new string('4', 40),
            new string('5', 40),
            generation,
            predecessor,
            null!);

    internal static RestrictedStateCandidate Candidate(
        AuthorizedStateAccess access,
        TestKeyResolver keys,
        long generation = 0,
        string? predecessor = null,
        byte[]? plaintext = null)
    {
        var binding = Binding(
            generation,
            predecessor,
            access.Scope);
        var session = plaintext ?? [1, 2, 3];
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            session,
            keys,
            out var envelope,
            out var code),
            code);
        var envelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(envelope!);
        return new RestrictedStateCandidate(
            binding,
            AgentCanonical.HashDomain(
                AgentCanonical.SessionDomain,
                session),
            envelopeSha,
            RestrictedStateEnvelope.ObjectIdentity(
                binding,
                AgentCanonical.HashDomain(
                    AgentCanonical.SessionDomain,
                    session),
                envelopeSha),
            envelope!);
    }
}

internal sealed class TestKeyResolver : IRestrictedStateKeyResolver
{
    private readonly Dictionary<string, byte[]> keys =
        new(StringComparer.Ordinal);

    internal TestKeyResolver(
        string currentKeyId = "test-key",
        byte[]? currentKey = null)
    {
        CurrentKeyId = currentKeyId;
        keys[currentKeyId] =
            (currentKey ?? RestrictedStateTestData.Key).ToArray();
    }

    internal int WriteCalls { get; private set; }
    internal int ReadCalls { get; private set; }
    internal string CurrentKeyId { get; set; }
    internal bool ThrowOnWrite { get; set; }
    internal bool ThrowOnRead { get; set; }

    internal void AddApproved(string keyId, byte[] key) =>
        keys[keyId] = key.ToArray();

    internal void Remove(string keyId) => keys.Remove(keyId);

    public bool TryGetCurrentWriteKey(
        AuthorizedStateAccess access,
        out RestrictedStateKey? key)
    {
        WriteCalls++;
        if (ThrowOnWrite)
        {
            throw new InvalidOperationException("Synthetic key write.");
        }

        if (!keys.TryGetValue(CurrentKeyId, out var material))
        {
            key = null;
            return false;
        }

        key = new RestrictedStateKey(CurrentKeyId, material);
        return true;
    }

    public bool TryGetApprovedReadKey(
        AuthorizedStateAccess access,
        string keyId,
        long expiresAtUnixSeconds,
        out RestrictedStateKey? key)
    {
        ReadCalls++;
        if (ThrowOnRead)
        {
            throw new InvalidOperationException("Synthetic key read.");
        }

        if (!keys.TryGetValue(keyId, out var material))
        {
            key = null;
            return false;
        }

        key = new RestrictedStateKey(keyId, material);
        return true;
    }
}

internal sealed class TestSessionAdmission
    : IRestrictedStateSessionAdmission
{
    internal int Calls { get; private set; }
    internal bool Reject { get; set; }
    internal bool Throw { get; set; }

    public RestrictedStateSessionAdmissionResult Admit(
        AuthorizedStateAccess access,
        ReadOnlyMemory<byte> plaintext,
        RestrictedStateSessionAdmissionContext context)
    {
        Calls++;
        if (Throw)
        {
            throw new InvalidOperationException(
                "Synthetic session admission.");
        }

        if (Reject || plaintext.IsEmpty)
        {
            return RestrictedStateSessionAdmissionResult.Failure();
        }

        var value = new AgentSessionStateAdmittedValue(null!, null!);
        return RestrictedStateSessionAdmissionResult.Success(
            new RestrictedStateAdmittedSession(
                plaintext.ToArray(),
                AgentCanonical.HashDomain(
                    AgentCanonical.SessionDomain,
                    plaintext.Span),
                context.ProducerBaseSha,
                context.ProducerHeadSha,
                context.Generation,
                context.PredecessorEnvelopeSha256,
                value));
    }
}

internal sealed class MemoryRestrictedStateStore : IRestrictedStateStore
{
    private RestrictedStateSnapshot snapshot =
        RestrictedStateSnapshot.Empty;
    private RestrictedStateSnapshotVersion version =
        RestrictedStateSnapshotVersion.Absent;

    internal int ReadCalls { get; private set; }
    internal int WriteCalls { get; private set; }
    internal RestrictedStateStoreFailure ReadFailure { get; set; }
    internal RestrictedStateStoreFailure WriteFailure { get; set; }
    internal bool CommitOnWriteFailure { get; set; }
    internal bool PersistOnWriteFailure { get; set; }

    internal RestrictedStateSnapshot Snapshot
    {
        get => snapshot;
        set
        {
            snapshot = value;
            version = new RestrictedStateSnapshotVersion(
                Guid.NewGuid().ToString("N"),
                true);
        }
    }

    public RestrictedStateStoreRead Read(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        ReadCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        return ReadFailure == RestrictedStateStoreFailure.None
            ? new RestrictedStateStoreRead(
                RestrictedStateStoreFailure.None,
                snapshot,
                version)
            : new RestrictedStateStoreRead(ReadFailure, null, null);
    }

    public RestrictedStateStoreRawRead ReadRawVersion(
        AuthorizedStateAccess access,
        CancellationToken cancellationToken)
    {
        ReadCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (ReadFailure != RestrictedStateStoreFailure.None)
        {
            return new RestrictedStateStoreRawRead(
                ReadFailure,
                null);
        }

        return new RestrictedStateStoreRawRead(
            RestrictedStateStoreFailure.None,
            version.Exists
                ? new RestrictedStateRawVersion(
                    version.Sha256,
                    0,
                    Exists: true)
                : RestrictedStateRawVersion.Absent);
    }

    public RestrictedStateStoreWrite CompareExchange(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        RestrictedStateSnapshot replacement,
        CancellationToken cancellationToken)
    {
        WriteCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteFailure != RestrictedStateStoreFailure.None)
        {
            if (CommitOnWriteFailure || PersistOnWriteFailure)
            {
                snapshot = replacement;
                version = new RestrictedStateSnapshotVersion(
                    Guid.NewGuid().ToString("N"),
                    true);
            }

            return new RestrictedStateStoreWrite(
                WriteFailure,
                CommitOnWriteFailure ? version : null,
                CommitOnWriteFailure);
        }

        if (expected != version)
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Conflict,
                null,
                false);
        }

        snapshot = replacement;
        version = new RestrictedStateSnapshotVersion(
            Guid.NewGuid().ToString("N"),
            true);
        return new RestrictedStateStoreWrite(
            RestrictedStateStoreFailure.None,
            version,
            true);
    }

    public RestrictedStateStoreWrite CompareDelete(
        AuthorizedStateAccess access,
        RestrictedStateSnapshotVersion expected,
        CancellationToken cancellationToken)
    {
        WriteCalls++;
        cancellationToken.ThrowIfCancellationRequested();
        if (WriteFailure != RestrictedStateStoreFailure.None)
        {
            if (CommitOnWriteFailure)
            {
                snapshot = RestrictedStateSnapshot.Empty;
                version = RestrictedStateSnapshotVersion.Absent;
            }

            return new RestrictedStateStoreWrite(
                WriteFailure,
                CommitOnWriteFailure ? version : null,
                CommitOnWriteFailure);
        }

        if (expected != version)
        {
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Conflict,
                null,
                false);
        }

        snapshot = RestrictedStateSnapshot.Empty;
        version = RestrictedStateSnapshotVersion.Absent;
        return new RestrictedStateStoreWrite(
            RestrictedStateStoreFailure.None,
            version,
            true);
    }

    public RestrictedStateStoreWrite CompareDeleteRaw(
        AuthorizedStateAccess access,
        RestrictedStateRawVersion expected,
        CancellationToken cancellationToken)
    {
        var current = version.Exists
            ? new RestrictedStateRawVersion(
                version.Sha256,
                0,
                Exists: true)
            : RestrictedStateRawVersion.Absent;
        if (expected != current)
        {
            WriteCalls++;
            return new RestrictedStateStoreWrite(
                RestrictedStateStoreFailure.Conflict,
                null,
                false);
        }

        return CompareDelete(
            access,
            version,
            cancellationToken);
    }
}
