using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime;

internal static class R3LiveAgentCodes
{
    internal const string Completed = "r3_live_completed";
    internal const string InputInvalid = "r3_live_input_invalid";
    internal const string SecretInvalid = "r3_live_secret_invalid";
    internal const string CompositionFailed = "r3_live_composition_failed";
}

internal sealed class R3LiveAgentSecrets(
    string? providerCredential,
    string? stateKeyBase64)
{
    internal string? ProviderCredential { get; } = providerCredential;

    internal string? StateKeyBase64 { get; } = stateKeyBase64;

    public override string ToString() => "r3_live_agent_secrets";
}

internal interface IR3LiveAgentSecretSource
{
    R3LiveAgentSecrets TakeAndClear();
}

internal sealed class R3LiveAgentEnvironmentSecretSource
    : IR3LiveAgentSecretSource
{
    internal const string ProviderVariable =
        "AGENTIC_REVIEW_DEEPSEEK_API_KEY";
    internal const string StateKeyVariable =
        "AGENTIC_REVIEW_R3_STATE_KEY_B64";

    public R3LiveAgentSecrets TakeAndClear()
    {
        string? provider = null;
        string? stateKey = null;
        try
        {
            provider = Environment.GetEnvironmentVariable(
                ProviderVariable);
            stateKey = Environment.GetEnvironmentVariable(
                StateKeyVariable);
            return new R3LiveAgentSecrets(provider, stateKey);
        }
        finally
        {
            try
            {
                Environment.SetEnvironmentVariable(ProviderVariable, null);
            }
            finally
            {
                Environment.SetEnvironmentVariable(StateKeyVariable, null);
            }
        }
    }
}

internal interface IR3LiveAgentStateRestorer
{
    RestrictedStateRestoreResult Restore(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        AuthorizedStateAccess access,
        RestrictedStateRestoreRequest request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken);
}

internal sealed class R3LiveAgentStateRestorer : IR3LiveAgentStateRestorer
{
    public RestrictedStateRestoreResult Restore(
        string stateRoot,
        IRestrictedStateKeyResolver keyResolver,
        AuthorizedStateAccess access,
        RestrictedStateRestoreRequest request,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var store = new LocalRestrictedStateStore(stateRoot);
        var service = new RestrictedStateService(
            store,
            keyResolver,
            new AgentSessionRestrictedStateAdmission(),
            () => timeProvider.GetUtcNow().ToUnixTimeSeconds());
        return service.Restore(access, request, cancellationToken);
    }
}

internal interface IR3LiveAgentTransportFactory
{
    IDeepSeekTransport Create(DeepSeekCredential credential);
}

internal sealed class R3LiveAgentTransportFactory
    : IR3LiveAgentTransportFactory
{
    public IDeepSeekTransport Create(DeepSeekCredential credential) =>
        DeepSeekTransport.Create(credential);
}

internal interface IR3LiveAgentReviewedFileAccessFactory
{
    IReviewedFileAccess Create();
}

internal sealed class R3LiveAgentReviewedFileAccessFactory
    : IR3LiveAgentReviewedFileAccessFactory
{
    public IReviewedFileAccess Create() => new VerifiedReviewedFileAccess();
}

internal sealed class R3LiveAgentStateKeyResolver :
    IRestrictedStateKeyResolver,
    IDisposable
{
    internal const string KeyId = "r3-live-state-v1";

    private byte[]? material;

    private R3LiveAgentStateKeyResolver(byte[] material)
    {
        this.material = material.ToArray();
    }

    internal static bool TryCreate(
        string? encoded,
        out R3LiveAgentStateKeyResolver? resolver)
    {
        resolver = null;
        if (encoded is not { Length: 44 } ||
            encoded[^1] != '=')
        {
            return false;
        }

        var decoded = new byte[RestrictedStateFormat.KeyBytes];
        try
        {
            if (!Convert.TryFromBase64String(
                    encoded,
                    decoded,
                    out var written) ||
                written != RestrictedStateFormat.KeyBytes ||
                !StringComparer.Ordinal.Equals(
                    Convert.ToBase64String(decoded),
                    encoded))
            {
                return false;
            }

            resolver = new R3LiveAgentStateKeyResolver(decoded);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(decoded);
        }
    }

    public bool TryGetCurrentWriteKey(
        AuthorizedStateAccess access,
        out RestrictedStateKey? key) =>
        TryCreateKey(access, KeyId, out key);

    public bool TryGetApprovedReadKey(
        AuthorizedStateAccess access,
        string keyId,
        long expiresAtUnixSeconds,
        out RestrictedStateKey? key)
    {
        key = null;
        return StringComparer.Ordinal.Equals(keyId, KeyId) &&
            TryCreateKey(access, keyId, out key);
    }

    public void Dispose()
    {
        var current = Interlocked.Exchange(ref material, null);
        if (current is not null)
        {
            CryptographicOperations.ZeroMemory(current);
        }
    }

    public override string ToString() => "r3_live_agent_state_key_resolver";

    private bool TryCreateKey(
        AuthorizedStateAccess access,
        string keyId,
        out RestrictedStateKey? key)
    {
        key = null;
        var current = material;
        if (access is null ||
            current is null ||
            current.Length != RestrictedStateFormat.KeyBytes)
        {
            return false;
        }

        key = new RestrictedStateKey(keyId, current);
        return true;
    }
}

internal sealed class R3LiveAgentDependencies(
    IR3LiveAgentSecretSource secretSource,
    IR3LiveAgentStateRestorer stateRestorer,
    IR3LiveAgentTransportFactory transportFactory,
    IR3LiveAgentReviewedFileAccessFactory reviewedFileAccessFactory,
    TimeProvider timeProvider)
{
    internal IR3LiveAgentSecretSource SecretSource { get; } = secretSource;

    internal IR3LiveAgentStateRestorer StateRestorer { get; } = stateRestorer;

    internal IR3LiveAgentTransportFactory TransportFactory { get; } =
        transportFactory;

    internal IR3LiveAgentReviewedFileAccessFactory ReviewedFileAccessFactory {
        get;
    } = reviewedFileAccessFactory;

    internal TimeProvider TimeProvider { get; } = timeProvider;

    internal static R3LiveAgentDependencies CreateDefault() =>
        new(
            new R3LiveAgentEnvironmentSecretSource(),
            new R3LiveAgentStateRestorer(),
            new R3LiveAgentTransportFactory(),
            new R3LiveAgentReviewedFileAccessFactory(),
            TimeProvider.System);

    public override string ToString() => "r3_live_agent_dependencies";
}
