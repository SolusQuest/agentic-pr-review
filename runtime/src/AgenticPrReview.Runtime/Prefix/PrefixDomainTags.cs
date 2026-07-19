using System.Text;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// The eight domain tags from the prefix contract (## Prefix Contract of the
/// design document). Each tag byte array ends with exactly one NUL octet
/// (0x00), never the two-character sequence backslash + '0'.
/// </summary>
internal static class PrefixDomainTags
{
    public static readonly byte[] Template = Tag("agentic-pr-review/cache-contract/template/v1");
    public static readonly byte[] Policy = Tag("agentic-pr-review/cache-contract/policy/v1");
    public static readonly byte[] Tools = Tag("agentic-pr-review/cache-contract/tools/v1");
    public static readonly byte[] Config = Tag("agentic-pr-review/cache-contract/config/v1");
    public static readonly byte[] Adapter = Tag("agentic-pr-review/cache-contract/adapter/v1");
    public static readonly byte[] LogicalPrefix = Tag("agentic-pr-review/logical-prefix/v1");
    public static readonly byte[] ProviderPrefix = Tag("agentic-pr-review/provider-prefix/v1");
    public static readonly byte[] Interaction = Tag("agentic-pr-review/interaction/v1");

    private static byte[] Tag(string ascii)
    {
        var bytes = Encoding.UTF8.GetBytes(ascii);
        var tagged = new byte[bytes.Length + 1];
        bytes.CopyTo(tagged, 0);
        tagged[^1] = 0x00;
        return tagged;
    }
}
