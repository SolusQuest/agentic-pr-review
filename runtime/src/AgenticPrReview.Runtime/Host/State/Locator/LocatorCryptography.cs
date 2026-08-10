using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal static class LocatorCryptography
{
    internal const string KeyIdDomain = "apr.locator-key-id.s3";
    internal const string InitialRootDomain = "apr.locator-root.s3";
    internal const string OpaqueNameDomain = "apr.locator-name.s3";
    internal const string CorrelationDomain = "apr.locator-correlation.s3";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static string KeyId(ReadOnlySpan<byte> key)
    {
        var framed = Frame(KeyIdDomain, key);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(framed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    internal static byte[] InitialRoot(
        ReadOnlySpan<byte> key,
        string repositoryId)
    {
        var repository = StrictUtf8.GetBytes(repositoryId);
        var framed = Frame(InitialRootDomain, repository);
        try
        {
            return HMACSHA256.HashData(key, framed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(repository);
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    internal static string OpaqueName(
        ReadOnlySpan<byte> root,
        string objectClass,
        ReadOnlySpan<byte> canonicalScope)
    {
        var objectClassBytes = StrictUtf8.GetBytes(objectClass);
        var framed = Frame(
            OpaqueNameDomain,
            objectClassBytes,
            canonicalScope);
        try
        {
            return string.Concat(
                "apr-state-",
                Convert.ToHexStringLower(HMACSHA256.HashData(root, framed)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(objectClassBytes);
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    internal static string CorrelationId(ReadOnlySpan<byte> envelopeDigest)
    {
        var framed = Frame(CorrelationDomain, envelopeDigest);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(framed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    private static byte[] Frame(
        string domain,
        ReadOnlySpan<byte> value)
    {
        var domainBytes = StrictUtf8.GetBytes(string.Concat(domain, "\0"));
        var capacity = checked(
            sizeof(uint) + domainBytes.Length +
            sizeof(uint) + value.Length);
        var writer = new ArrayBufferWriter<byte>(capacity);
        WriteLengthPrefixed(writer, domainBytes);
        WriteLengthPrefixed(writer, value);

        CryptographicOperations.ZeroMemory(domainBytes);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] Frame(
        string domain,
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        var domainBytes = StrictUtf8.GetBytes(string.Concat(domain, "\0"));
        var capacity = checked(
            sizeof(uint) + domainBytes.Length +
            sizeof(uint) + first.Length +
            sizeof(uint) + second.Length);
        var writer = new ArrayBufferWriter<byte>(capacity);
        WriteLengthPrefixed(writer, domainBytes);
        WriteLengthPrefixed(writer, first);
        WriteLengthPrefixed(writer, second);

        CryptographicOperations.ZeroMemory(domainBytes);
        return writer.WrittenMemory.ToArray();
    }

    private static void WriteLengthPrefixed(
        IBufferWriter<byte> writer,
        ReadOnlySpan<byte> value)
    {
        var length = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32BigEndian(
            length,
            checked((uint)value.Length));
        writer.Advance(sizeof(uint));
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }
}
