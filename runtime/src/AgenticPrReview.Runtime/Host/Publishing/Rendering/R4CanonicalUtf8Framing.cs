using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Host.Publishing.Rendering;

internal static class R4CanonicalUtf8Framing
{
    private const int MaximumFields = 128;
    private const int MaximumPreimageBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static byte[] BuildPreimage(
        string domain,
        IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(domain);
        ArgumentNullException.ThrowIfNull(fields);
        if (!IsAsciiDomain(domain) || fields.Count > MaximumFields)
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        var fieldLengths = new int[fields.Count];
        var totalBytes = domain.Length;
        try
        {
            for (var index = 0; index < fields.Count; index++)
            {
                var field = fields[index];
                if (field is null)
                {
                    throw new R4PublicationException(
                        R4PublicationFailureCodes.IdentityInvalid);
                }

                fieldLengths[index] = StrictUtf8.GetByteCount(field);
                totalBytes = checked(totalBytes + 8 + fieldLengths[index]);
                if (totalBytes > MaximumPreimageBytes)
                {
                    throw new R4PublicationException(
                        R4PublicationFailureCodes.IdentityInvalid);
                }
            }
        }
        catch (EncoderFallbackException)
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }
        catch (OverflowException)
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        var preimage = new byte[totalBytes];
        for (var index = 0; index < domain.Length; index++)
        {
            preimage[index] = (byte)domain[index];
        }

        var offset = domain.Length;
        try
        {
            for (var index = 0; index < fields.Count; index++)
            {
                var length = fieldLengths[index];
                BinaryPrimitives.WriteUInt64BigEndian(
                    preimage.AsSpan(offset, 8),
                    (ulong)length);
                offset += 8;
                StrictUtf8.GetBytes(
                    fields[index].AsSpan(),
                    preimage.AsSpan(offset, length));
                offset += length;
            }
        }
        catch (EncoderFallbackException)
        {
            throw new R4PublicationException(
                R4PublicationFailureCodes.IdentityInvalid);
        }

        return preimage;
    }

    internal static string Hash(
        string domain,
        IReadOnlyList<string> fields) =>
        Convert.ToHexStringLower(
            SHA256.HashData(BuildPreimage(domain, fields)));

    private static bool IsAsciiDomain(string domain)
    {
        if (domain.Length is < 1 or > 128)
        {
            return false;
        }

        foreach (var character in domain)
        {
            if (character is < '!' or > '~')
            {
                return false;
            }
        }

        return true;
    }
}
