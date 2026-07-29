using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateEnvelopeTests
{
    private const string GoldenBase64 =
        "QVBSLVNUQVRFLUFBRC0xAEFQUkFTVDAxAQABAB8AYWdlbnRpYy1wci1yZXZpZXcvYWdlbnQtc2Vzc2lvbgwAcjItY3VycmVudC0xCAB0ZXN0LWtleQwAAAECAwQFBgcICQoLAwAAAAQAcmVwbwgAd29ya2Zsb3cBAAAAAAAAAAkAc2Vzc2lvbl8wCABwcm92aWRlcgUAbW9kZWwHAGFkYXB0ZXIRERERERERERERERERERERERERERERERERERERERERESIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiIiMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMzMFAGJ1aWxkRERERERERERERERERERERERERERVVVVVVVVVVVVVVVVVVVVVVVVVVQAAAAAAAAAAAADxU2UAAAAAgCtdZQAAAAA=";

    [Fact]
    public void AadGoldenIsByteExact()
    {
        var binding = RestrictedStateTestData.Binding();
        var nonce = Enumerable.Range(0, 12)
            .Select(value => (byte)value)
            .ToArray();

        Assert.True(RestrictedStateEnvelope.TryBuildAadVector(
            "test-key",
            nonce,
            3,
            binding,
            out var aad));

        Assert.Equal(332, aad!.Length);
        Assert.Equal(
            "799f5bc81cd564fec6d781d540dc4b940461161b69efc4c7b8bfd20c1ac3ce7b",
            Convert.ToHexStringLower(SHA256.HashData(aad)));
        Assert.Equal(GoldenBase64, Convert.ToBase64String(aad));
    }

    [Fact]
    public void IdenticalPlaintextUsesDistinctAuthenticatedEnvelopes()
    {
        var access = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var keys = new TestKeyResolver();
        var plaintext = Encoding.UTF8.GetBytes("synthetic-session");

        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            plaintext,
            keys,
            out var first,
            out var firstCode),
            firstCode);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            plaintext,
            keys,
            out var second,
            out var secondCode),
            secondCode);

        Assert.NotEqual(first, second);
        Assert.NotEqual(
            RestrictedStateEnvelope.EnvelopeSha256(first!),
            RestrictedStateEnvelope.EnvelopeSha256(second!));
        Assert.True(RestrictedStateEnvelope.TryParse(first, out var parsed));
        Assert.True(RestrictedStateEnvelope.TryDecrypt(
            access,
            binding,
            first,
            keys,
            out var restored,
            out var restoreCode),
            restoreCode);
        Assert.Equal(plaintext, restored);
        Assert.Equal(12, parsed!.Nonce.Length);
        Assert.Equal(16, parsed.Tag.Length);
    }

    [Fact]
    public void HeaderCiphertextTagAndBindingMutationsFailClosed()
    {
        var access = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var keys = new TestKeyResolver();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            [1, 2, 3],
            keys,
            out var envelope,
            out _));
        Assert.True(RestrictedStateEnvelope.TryParse(
            envelope,
            out var parsed));

        var mutationOffsets = new[]
        {
            0,
            8,
            10,
            parsed!.Header.Length - 1,
            parsed.Header.Length,
            envelope!.Length - 1,
        };
        foreach (var offset in mutationOffsets)
        {
            var mutated = envelope.ToArray();
            mutated[offset] ^= 1;
            var decrypted = RestrictedStateEnvelope.TryDecrypt(
                access,
                binding,
                mutated,
                keys,
                out _,
                out var code);

            Assert.False(decrypted);
            Assert.Contains(
                code,
                new[]
                {
                    RestrictedStateCodes.EnvelopeInvalid,
                    RestrictedStateCodes.AuthenticationFailed,
                });
        }

        var wrongBinding = binding with
        {
            ProducerHeadSha = new string('6', 40),
        };
        Assert.False(RestrictedStateEnvelope.TryDecrypt(
            access,
            wrongBinding,
            envelope,
            keys,
            out _,
            out var bindingCode));
        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            bindingCode);
    }

    [Fact]
    public void UnknownKeyAndWrongApprovedMaterialAreDistinct()
    {
        var access = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var writeKeys = new TestKeyResolver();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            [1, 2, 3],
            writeKeys,
            out var envelope,
            out _));

        var unavailable = new TestKeyResolver("other");
        Assert.False(RestrictedStateEnvelope.TryDecrypt(
            access,
            binding,
            envelope,
            unavailable,
            out _,
            out var unavailableCode));
        Assert.Equal(
            RestrictedStateCodes.KeyUnavailable,
            unavailableCode);

        var wrong = new TestKeyResolver(
            "test-key",
            Enumerable.Repeat((byte)0xff, 32).ToArray());
        Assert.False(RestrictedStateEnvelope.TryDecrypt(
            access,
            binding,
            envelope,
            wrong,
            out _,
            out var wrongCode));
        Assert.Equal(
            RestrictedStateCodes.AuthenticationFailed,
            wrongCode);
    }

    [Fact]
    public void ApprovedPreviousKeyReadsButNextWriteUsesCurrentKey()
    {
        var access = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var oldKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var newKey = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var oldResolver = new TestKeyResolver("old", oldKey);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            [1, 2, 3],
            oldResolver,
            out var oldEnvelope,
            out _));

        var rotated = new TestKeyResolver("new", newKey);
        rotated.AddApproved("old", oldKey);
        Assert.True(RestrictedStateEnvelope.TryDecrypt(
            access,
            binding,
            oldEnvelope,
            rotated,
            out _,
            out var readCode),
            readCode);
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            [1, 2, 3],
            rotated,
            out var newEnvelope,
            out var writeCode),
            writeCode);
        Assert.True(RestrictedStateEnvelope.TryParse(
            newEnvelope,
            out var parsed));
        Assert.Equal("new", parsed!.KeyId);
    }

    [Fact]
    public void MalformedTruncatedTrailingAndOversizedEnvelopesAreRejected()
    {
        var access = RestrictedStateTestData.Access();
        var binding = RestrictedStateTestData.Binding();
        var keys = new TestKeyResolver();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            binding,
            [1, 2, 3],
            keys,
            out var envelope,
            out _));

        Assert.False(RestrictedStateEnvelope.TryParse(
            envelope![..^1],
            out _));
        Assert.False(RestrictedStateEnvelope.TryParse(
            [.. envelope, 0],
            out _));
        Assert.False(RestrictedStateEnvelope.TryParse(
            new byte[
                AgenticPrReview.Runtime.Agent.AgentLimits
                    .StateEnvelopeBytes + 1],
            out _));
    }

    [Fact]
    public void StateKeyCanaryIsNotPersistedOrReturned()
    {
        var canary = Encoding.ASCII.GetBytes(
            "STATE-KEY-CANARY-DO-NOT-PERSIST!");
        Assert.Equal(32, canary.Length);
        var keys = new TestKeyResolver("canary-key", canary);
        var access = RestrictedStateTestData.Access();
        Assert.True(RestrictedStateEnvelope.TryEncrypt(
            access,
            RestrictedStateTestData.Binding(),
            Encoding.UTF8.GetBytes("public-safe"),
            keys,
            out var envelope,
            out _));

        Assert.DoesNotContain(
            Convert.ToHexString(canary),
            Convert.ToHexString(envelope!),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "STATE-KEY-CANARY",
            Encoding.ASCII.GetString(envelope!),
            StringComparison.Ordinal);
    }
}
