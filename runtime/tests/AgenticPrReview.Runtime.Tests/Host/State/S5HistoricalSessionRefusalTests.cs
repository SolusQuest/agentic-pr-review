using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class S5HistoricalSessionRefusalTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AuthenticatedGenuineOldStateCannotEnterCurrentReader(
        bool adversarialCurrentDomainRebind)
    {
        var root = Path.Join(AppContext.BaseDirectory, "fixtures", "agent", "r3-live-agent", "s5-historical-session");
        var plaintext = File.ReadAllBytes(Path.Join(root, "session.bin"));
        var envelope = File.ReadAllBytes(Path.Join(root, "envelope.bin"));
        var envelopeBefore = envelope.ToArray();
        Assert.Equal("ca5cc437ca7424f9ba03fb1f387fdac1aed36d188a521a4d7f319b2cad3303eb",
            Convert.ToHexStringLower(SHA256.HashData(plaintext)));
        Assert.Equal("c060f8199dc706a6a5d9b80f8dfb1bf6898d213affd151a49ab744562985b494",
            RestrictedStateEnvelope.EnvelopeSha256(envelope));
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Join(root, "accepted-lineage.bin")));
        var original = document.RootElement;
        var s = original.GetProperty("scope");
        string Field(string name) => s.GetProperty(name).GetString()!;
        var scope = new RestrictedStateScope(Field("repository_id"), Field("workflow_identity"),
            s.GetProperty("review_target").GetInt64(), Field("session_id"), Field("provider_id"),
            Field("model_id"), Field("adapter_id"), Field("policy_sha256"), Field("limits_sha256"),
            Field("toolset_sha256"), Field("build_id"));
        Assert.Equal(StateAction.Authorized,
            AuthorizedStateAccess.Authorize(new(scope, scope, true, true, false), out var access).Action);
        var binding = new RestrictedStateBinding(scope, original.GetProperty("producer_base_sha").GetString()!,
            original.GetProperty("producer_head_sha").GetString()!, 0, null,
            original.GetProperty("accepted_at_unix_seconds").GetInt64(),
            original.GetProperty("expires_at_unix_seconds").GetInt64());
        var lineage = new AcceptedLineage(scope, 0, original.GetProperty("session_sha256").GetString()!,
            original.GetProperty("envelope_sha256").GetString()!, null,
            binding.AcceptedAtUnixSeconds, binding.ExpiresAtUnixSeconds, true);
        var keys = new TestKeyResolver("r3-live-state-v1", Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        // Authenticate the original envelope independently, without a SESSION parser.
        Assert.True(RestrictedStateEnvelope.TryDecrypt(access!, binding, envelope, keys, out var decrypted, out _));
        Assert.Equal(plaintext, decrypted);
        if (adversarialCurrentDomainRebind)
            lineage = lineage with { SessionSha256 = SolusRestrictedStateSnapshotCodec.RecordSha256(plaintext) };
        var compatibility = new SolusRestrictedStateCompatibility(
            SolusRestrictedStateFormat.PackageVersion, SolusRestrictedStateFormat.Profile,
            SolusRestrictedStateFormat.CompletedCandidateMode, SolusRestrictedStateFormat.Implementation,
            SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
            SolusRestrictedStateFormat.PackageClosureSha256, new string('a', 64), new string('b', 64));
        Assert.False(SolusRestrictedStateSnapshotCodec.TryRestore(access, binding, lineage, envelope,
            compatibility, binding.AcceptedAtUnixSeconds, keys, out var record, out var failure));
        Assert.Null(record);
        Assert.Equal(adversarialCurrentDomainRebind ? SolusRestrictedStateFailure.Incompatible
            : SolusRestrictedStateFailure.LineageMismatch, failure);
        Assert.Equal(0, keys.WriteCalls);
        Assert.Equal(envelopeBefore, envelope);
        CryptographicOperations.ZeroMemory(decrypted!);
    }
}
