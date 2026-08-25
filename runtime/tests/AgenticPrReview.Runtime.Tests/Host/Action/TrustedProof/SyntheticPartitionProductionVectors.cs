using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHostVerifierFixture;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

internal static class SyntheticPartitionProductionVectors
{
    internal static readonly SyntheticTransactionPartitionBinding Binding = new(
        new string('1', 40),
        new string('2', 40),
        new string('3', 64),
        new string('4', 64));
    internal static string StateKey => LocatorTestData.CurrentBase64;

    internal static string CreateLifecycleRoot()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            "apr-r4-e2p-partition-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var fixture = JsonDocument.Parse(File.ReadAllBytes(Path.Join(
            FindRepositoryRoot(),
            "runtime", "tests", "fixtures", "action-host", "trusted-proof",
            "templates", "host-restricted-evidence.json")));
        foreach (var item in fixture.RootElement.GetProperty("state")
                     .GetProperty("created").EnumerateArray())
        {
            var scenario = item.GetProperty("creation_phase").GetString() switch
            {
                "bootstrap" => "dispatch-bootstrap",
                "continuation" => "dispatch-continuation",
                "stale-setup" => "stale-head",
                _ => throw new InvalidOperationException(
                    "Unexpected lifecycle phase."),
            };
            var scenarioRoot = Path.Join(root, scenario);
            Directory.CreateDirectory(scenarioRoot);
            var envelope = item.GetProperty("object_class").GetString() ==
                    "locator_root"
                ? CreateLocatorEnvelope()
                : Convert.FromBase64String(item
                    .GetProperty("encrypted_envelope_base64").GetString()!);
            var encryptedDigest = Convert.ToHexString(SHA256.HashData(envelope))
                .ToLowerInvariant();
            var objectId = item.GetProperty("physical_artifact_id").GetString()!;
            var name = item.GetProperty("object_class").GetString() ==
                    "locator_root"
                ? LocatorRootFormat.StoreName
                : item.GetProperty("opaque_name").GetString()!;
            var archiveDigest = item.GetProperty("archive_sha256").GetString()!;
            var runId = item.GetProperty("producing_run_id").GetString()!;
            var attempt = item.GetProperty("producing_run_attempt").GetInt64();
            var expires = item.GetProperty("expires_at_unix_seconds").GetInt64();
            var correlation = item.GetProperty("object_class").GetString() ==
                    "locator_root"
                ? LocatorCryptography.CorrelationId(
                    Convert.FromHexString(encryptedDigest))
                : LineageCryptography.CorrelationId(envelope);
            var lines = new List<string>
            {
                string.Join('\t',
                    "upload", name, correlation, encryptedDigest, "None",
                    "Committed", objectId, archiveDigest, encryptedDigest,
                    expires.ToString(CultureInfo.InvariantCulture), runId,
                    attempt.ToString(CultureInfo.InvariantCulture),
                    envelope.Length.ToString(CultureInfo.InvariantCulture),
                    Convert.ToBase64String(envelope)),
            };
            if (item.GetProperty("terminal_disposition").GetString() !=
                "e4-deleted")
            {
                lines.Add(string.Join('\t',
                    "delete", name, objectId, encryptedDigest, "None",
                    "Committed", "-", "-", "-", "-", "-", "-", "-", "-"));
            }
            File.AppendAllLines(
                Path.Join(scenarioRoot, "state-lifecycle-evidence.tsv"),
                lines);
        }
        return root;
    }

    internal static void AssertStableIdentities(
        System.Text.Json.Nodes.JsonObject partition,
        string property,
        int count)
    {
        var identities = partition[property]!.AsArray()
            .Select(value => value!.GetValue<string>())
            .ToArray();
        Assert.Equal(count, identities.Length);
        Assert.Equal(count, identities.Distinct(StringComparer.Ordinal).Count());
        Assert.All(identities,
            identity => Assert.Matches("^[0-9a-f]{64}$", identity));
        Assert.DoesNotContain(identities, identity =>
            long.TryParse(identity, NumberStyles.None,
                CultureInfo.InvariantCulture, out _));
    }

    private static byte[] CreateLocatorEnvelope()
    {
        using var access = LocatorTestData.Access("42");
        using var keys = LocatorTestData.KeyRing(
            access,
            repositoryId: "42",
            currentBase64: StateKey);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            LocatorTestData.Sentinel(keys),
            out var envelope,
            out var failure),
            failure);
        return envelope!;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Join(directory.FullName, "package.json")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ??
            throw new InvalidOperationException("Repository root not found.");
    }
}
