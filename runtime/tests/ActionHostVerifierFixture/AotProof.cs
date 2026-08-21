using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

internal static class AotProof
{
    private const string Kind = "apr-r4-e2-action-host-build-pair-v1";
    private static readonly string[] Keys =
    [
        "kind",
        "execution_kind",
        "payload_sha256",
        "managed_intermediate_sha256",
        "build_pair_sha256",
    ];

    internal static AotProofValidation TryCreate(
        IReadOnlyDictionary<string, string> arguments,
        string payload)
    {
        var names = new[]
        {
            "execution-kind",
            "intermediate",
            "build-pair",
            "identity-output",
        };
        var present = names.Count(arguments.ContainsKey);
        if (present == 0) return new(null, true);
        if (present != names.Length ||
            arguments["execution-kind"] != "native-aot" ||
            JsonSerializer.IsReflectionEnabledByDefault ||
            RuntimeFeature.IsDynamicCodeSupported)
        {
            return new(null, false);
        }

        var intermediate = Path.GetFullPath(arguments["intermediate"]);
        var buildPair = Path.GetFullPath(arguments["build-pair"]);
        var identityOutput = Path.GetFullPath(arguments["identity-output"]);
        if (!File.Exists(payload) || !File.Exists(intermediate) ||
            !File.Exists(buildPair) || File.Exists(identityOutput) ||
            new FileInfo(buildPair).Length is < 2 or > 4096)
        {
            return new(null, false);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(buildPair));
            var root = document.RootElement;
            var properties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            if (properties.Length != Keys.Length ||
                properties.Select(property => property.Name)
                    .Distinct(StringComparer.Ordinal).Count() != Keys.Length ||
                !properties.Select(property => property.Name)
                    .SequenceEqual(Keys, StringComparer.Ordinal))
            {
                return new(null, false);
            }

            var kind = String(root, "kind");
            var executionKind = String(root, "execution_kind");
            var payloadSha256 = String(root, "payload_sha256");
            var intermediateSha256 = String(root,
                "managed_intermediate_sha256");
            var buildPairSha256 = String(root, "build_pair_sha256");
            var processPath = Environment.ProcessPath;
            if (kind != Kind || executionKind != "native-aot" ||
                !IsLowerHex(payloadSha256) ||
                !IsLowerHex(intermediateSha256) ||
                !IsLowerHex(buildPairSha256) ||
                Sha256(payload) != payloadSha256 ||
                processPath is null || Sha256(processPath) != payloadSha256 ||
                Sha256(intermediate) != intermediateSha256 ||
                PairDigest(payloadSha256, intermediateSha256) !=
                    buildPairSha256)
            {
                return new(null, false);
            }

            return new(new AotProofContext(identityOutput, payloadSha256,
                intermediateSha256, buildPairSha256), true);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or
            InvalidOperationException)
        {
            return new(null, false);
        }
    }

    private static string String(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool IsLowerHex(string value) => value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or
            >= 'a' and <= 'f');

    private static string PairDigest(
        string payloadSha256,
        string intermediateSha256) => Sha256Bytes(Encoding.UTF8.GetBytes(
            Kind + "\n" +
            "native-aot\n" +
            payloadSha256 + "\n" +
            intermediateSha256 + "\n"));

    private static string Sha256(string path) => Sha256Bytes(
        File.ReadAllBytes(path));

    private static string Sha256Bytes(byte[] value) => Convert.ToHexString(
        SHA256.HashData(value)).ToLowerInvariant();
}

internal sealed record AotProofValidation(
    AotProofContext? Context,
    bool IsValid);

internal sealed record AotProofContext(
    string IdentityOutput,
    string PayloadSha256,
    string ManagedIntermediateSha256,
    string BuildPairSha256);
