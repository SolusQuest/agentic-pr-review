using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

internal static class CredentialMaterializer
{
    private const int PrefixBytes = 4;

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] == "credential-materialize";

    public static int Run(string[] args)
    {
        RestrictedEvidenceRoot? root = null;
        CredentialFileRepresentations? token = null;
        CredentialFileRepresentations? current = null;
        CredentialFileRepresentations? previous = null;
        var guardianLaunchStarted = false;
        try
        {
            var options = Parse(args.Skip(1).ToArray());
            root = RestrictedEvidenceRoot.Open(
                options["--restricted-root"],
                options["--destination-identity"],
                [options["--repository-root"], options["--worktree-root"]]);
            using var executionAuthorization = root.AcquirePinnedFile(
                options["--execution-authorization"],
                EvidenceLimits.MaximumDocumentBytes);
            var authorization = ReadAuthorization(
                executionAuthorization.Bytes,
                options["--execution-authorization-sha256"],
                options["--destination-identity"]);
            var correctionGateReceipt = CorrectionGateReceipt.Read(
                root,
                options["--correction-gate-receipt"],
                options["--execution-authorization-sha256"],
                authorization.CorrectionGateSha256);
            if (AssemblySha256() != authorization.MaterializerBuildSha256)
            {
                throw new InvalidDataException("credential_materializer_build_invalid");
            }

            var input = ReadInput(Console.OpenStandardInput());
            try
            {
                var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                root.WritePinnedFileCreateNew("github-token", input.Token);
                token = root.ReadCredentialFileRepresentations(
                    "github-token", base64Key: false, deleteExactIdentityOnFailure: true);
                root.WritePinnedFileCreateNew("current-state-key", input.CurrentStateKey);
                current = root.ReadCredentialFileRepresentations(
                    "current-state-key", base64Key: true, deleteExactIdentityOnFailure: true);
                if (input.PreviousStateKey is not null)
                {
                    root.WritePinnedFileCreateNew("previous-state-key", input.PreviousStateKey);
                    previous = root.ReadCredentialFileRepresentations(
                        "previous-state-key", base64Key: true, deleteExactIdentityOnFailure: true);
                }
                var actualSlotNames = previous is null
                    ? new[] { "github-token", "current-state-key" }
                    : ["github-token", "current-state-key", "previous-state-key"];
                if (!actualSlotNames.SequenceEqual(
                        authorization.CredentialSlotNames,
                        StringComparer.Ordinal))
                {
                    throw new InvalidDataException("credential_materializer_profile_invalid");
                }
                var created = new Dictionary<string, CredentialFileRepresentations>(StringComparer.Ordinal)
                {
                    ["github-token"] = token,
                    ["current-state-key"] = current,
                };
                if (previous is not null) created.Add("previous-state-key", previous);
                CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                    root,
                    options["--destination-identity"],
                    [options["--repository-root"], options["--worktree-root"]],
                    CredentialLeaseAuthorityClient.GitHubDescriptorName,
                    [new CredentialLeaseSpec("github-token", Base64Key: false)],
                    [token],
                    timeouts: CredentialLeaseAuthorityTimeouts.OperationSuccessor);
                var keySpecs = previous is null
                    ? [new CredentialLeaseSpec("current-state-key", Base64Key: true)]
                    : new[]
                    {
                        new CredentialLeaseSpec("current-state-key", Base64Key: true),
                        new CredentialLeaseSpec("previous-state-key", Base64Key: true),
                    };
                var keys = previous is null
                    ? [current]
                    : new[] { current, previous };
                try
                {
                    CredentialLeaseAuthorityClient.LaunchCurrentProcess(
                        root,
                        options["--destination-identity"],
                        [options["--repository-root"], options["--worktree-root"]],
                        CredentialLeaseAuthorityClient.StateKeyDescriptorName,
                        keySpecs,
                        keys,
                        timeouts: CredentialLeaseAuthorityTimeouts.OperationSuccessor);
                }
                catch
                {
                    TryDeleteAbandoned(
                        root,
                        CredentialLeaseAuthorityClient.GitHubDescriptorName,
                        [new CredentialLeaseSpec("github-token", Base64Key: false)]);
                    throw;
                }

                CredentialAdmissionMaterialization admission;
                try
                {
                    var finished = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    admission = CredentialAdmissionReceipt.MaterializeCreateNew(
                        root,
                        options["--credential-admission-receipt-output"],
                        authorization.OperationIds,
                        created,
                        options["--execution-authorization-sha256"],
                        authorization.CorrectionGateSha256,
                        correctionGateReceipt.Sha256,
                        correctionGateReceipt.PhysicalIdentitySha256,
                        authorization.MaterializerSourceSha256,
                        authorization.MaterializerBuildSha256,
                        authorization.Consumers,
                        started,
                        finished);
                }
                catch
                {
                    TryDeleteAbandoned(
                        root,
                        CredentialLeaseAuthorityClient.GitHubDescriptorName,
                        [new CredentialLeaseSpec("github-token", Base64Key: false)]);
                    TryDeleteAbandoned(root, CredentialLeaseAuthorityClient.StateKeyDescriptorName, keySpecs);
                    throw;
                }
                guardianLaunchStarted = true;

                Console.Out.WriteLine(
                    $"APR_R4_E3_CREDENTIAL_ADMISSION_OK {admission.Sha256} {admission.PhysicalIdentitySha256}");
                return 0;
            }
            finally
            {
                input.Dispose();
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or CryptographicException or System.Text.Json.JsonException or
            KeyNotFoundException or FormatException)
        {
            return Invalid();
        }
        finally
        {
            if (!guardianLaunchStarted)
            {
                TryDeleteExact(token);
                TryDeleteExact(current);
                TryDeleteExact(previous);
            }
            token?.Dispose();
            current?.Dispose();
            previous?.Dispose();
        }
    }

    private static CredentialInput ReadInput(Stream stream)
    {
        var token = ReadBytes(stream, 1, EvidenceLimits.MaximumCredentialBytes);
        byte[] current = [];
        byte[]? previous = null;
        try
        {
            current = ReadBytes(stream, 44, 44);
            previous = ReadOptionalBytes(stream, 44);
            if (stream.ReadByte() != -1)
            {
                throw new InvalidDataException("credential_materializer_input_invalid");
            }
            return new CredentialInput(token, current, previous);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(token);
            CryptographicOperations.ZeroMemory(current);
            if (previous is not null) CryptographicOperations.ZeroMemory(previous);
            throw;
        }
    }

    private static byte[] ReadBytes(Stream stream, int minimum, int maximum)
    {
        Span<byte> prefix = stackalloc byte[PrefixBytes];
        ReadExact(stream, prefix);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length < minimum || length > maximum)
        {
            throw new InvalidDataException("credential_materializer_input_invalid");
        }
        var bytes = new byte[length];
        try
        {
            ReadExact(stream, bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static byte[]? ReadOptionalBytes(Stream stream, int exactLength)
    {
        Span<byte> prefix = stackalloc byte[PrefixBytes];
        ReadExact(stream, prefix);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length == 0) return null;
        if (length != exactLength) throw new InvalidDataException("credential_materializer_input_invalid");
        var bytes = new byte[length];
        try
        {
            ReadExact(stream, bytes);
            return bytes;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw;
        }
    }

    private static void ReadExact(Stream stream, Span<byte> destination)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(destination[offset..]);
            if (read == 0) throw new InvalidDataException("credential_materializer_input_invalid");
            offset += read;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var names = new[]
        {
            "--restricted-root", "--destination-identity", "--repository-root", "--worktree-root",
            "--execution-authorization", "--execution-authorization-sha256",
            "--correction-gate-receipt",
            "--credential-admission-receipt-output",
        };
        if (args.Length != names.Length * 2) throw new InvalidDataException("arguments_invalid");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!names.Contains(args[index], StringComparer.Ordinal) ||
                !result.TryAdd(args[index], args[index + 1]))
            {
                throw new InvalidDataException("arguments_invalid");
            }
        }
        if (names.Any(name => !result.ContainsKey(name)) ||
            !Sha256(result["--execution-authorization-sha256"]))
        {
            throw new InvalidDataException("arguments_invalid");
        }
        return result;
    }

    private static string AssemblySha256()
    {
        var location = Assembly.GetExecutingAssembly().Location;
        if (string.IsNullOrWhiteSpace(location)) throw new InvalidDataException("credential_materializer_build_invalid");
        using var stream = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    private static bool Sha256(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static AuthorizationBindings ReadAuthorization(
        byte[] bytes,
        string expectedSha256,
        string destinationIdentitySha256)
    {
        if (CanonicalEvidence.Sha256(bytes) != expectedSha256)
        {
            throw new InvalidDataException("credential_materializer_authorization_invalid");
        }
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Disallow,
            AllowTrailingCommas = false,
            MaxDepth = 64,
        });
        var execution = document.RootElement;
        if (execution.GetProperty("kind").GetString() != "apr-r4-e3-execution-authorization-v1")
        {
            throw new InvalidDataException("credential_materializer_authorization_invalid");
        }
        var privateDestination = execution.GetProperty("destinations").GetProperty("private");
        if (privateDestination.GetProperty("identity_sha256").GetString() != destinationIdentitySha256)
        {
            throw new InvalidDataException("credential_materializer_authorization_invalid");
        }
        var operationIds = execution.GetProperty("operation_ids").EnumerateArray()
            .Select(item => item.GetString() ?? string.Empty).ToArray();
        var slots = execution.GetProperty("active_secret_profile").GetProperty("credential_slot_names")
            .EnumerateArray().Select(item => item.GetString() ?? string.Empty).ToArray();
        if (operationIds.Length != 2 || operationIds.Any(item => !Sha256(item)) ||
            slots.Length is < 2 or > 3)
        {
            throw new InvalidDataException("credential_materializer_authorization_invalid");
        }
        var materializer = execution.GetProperty("credential_materializer");
        var correctionGate = execution.GetProperty("correction_gate");
        var correctionBytes = CanonicalEvidence.Encode(correctionGate, EvidenceJson.Options);
        string correctionGateSha256;
        try
        {
            correctionGateSha256 = CanonicalEvidence.Sha256(correctionBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(correctionBytes);
        }
        var identities = correctionGate.GetProperty("authority_identities").EnumerateArray()
            .ToDictionary(
                item => item.GetProperty("component").GetString() ?? string.Empty,
                item => new
                {
                    Source = item.GetProperty("source_sha256").GetString() ?? string.Empty,
                    Build = item.GetProperty("build_sha256").GetString() ?? string.Empty,
                },
                StringComparer.Ordinal);
        var credentialIdentity = identities["credential-materializer"];
        if (materializer.GetProperty("source_sha256").GetString() != credentialIdentity.Source ||
            materializer.GetProperty("build_sha256").GetString() != credentialIdentity.Build)
        {
            throw new InvalidDataException("credential_materializer_authorization_invalid");
        }
        var consumers = new[]
        {
            "producer-journal-materializer",
            "phase-fragment-materializer",
            "capture",
            "oracle",
            "assembler",
        }.Select(component =>
        {
            if (!identities.TryGetValue(component, out var identity) || !Sha256(identity.Build))
            {
                throw new InvalidDataException("credential_materializer_authorization_invalid");
            }
            return new CredentialConsumerIdentity(component, identity.Build);
        }).ToArray();
        return new AuthorizationBindings(
            operationIds,
            slots,
            correctionGateSha256,
            credentialIdentity.Source,
            credentialIdentity.Build,
            consumers);
    }

    private static void TryDeleteExact(CredentialFileRepresentations? value)
    {
        if (value is null) return;
        try { value.DeleteExactIdentity(); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or ObjectDisposedException) { }
    }

    private static void TryDeleteAbandoned(
        RestrictedEvidenceRoot root,
        string descriptor,
        IReadOnlyList<CredentialLeaseSpec> specs)
    {
        try { CredentialLeaseAuthorityClient.DeleteAbandoned(root, descriptor, specs); }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException) { }
    }

    private static int Invalid()
    {
        Console.Error.WriteLine("APR_R4_E3_CREDENTIAL_MATERIALIZER_INVALID");
        return 1;
    }

    private sealed class CredentialInput(byte[] token, byte[] currentStateKey, byte[]? previousStateKey)
        : IDisposable
    {
        public byte[] Token { get; } = token;
        public byte[] CurrentStateKey { get; } = currentStateKey;
        public byte[]? PreviousStateKey { get; } = previousStateKey;

        public void Dispose()
        {
            CryptographicOperations.ZeroMemory(Token);
            CryptographicOperations.ZeroMemory(CurrentStateKey);
            if (PreviousStateKey is not null) CryptographicOperations.ZeroMemory(PreviousStateKey);
        }
    }

    private sealed record AuthorizationBindings(
        string[] OperationIds,
        string[] CredentialSlotNames,
        string CorrectionGateSha256,
        string MaterializerSourceSha256,
        string MaterializerBuildSha256,
        CredentialConsumerIdentity[] Consumers);
}
