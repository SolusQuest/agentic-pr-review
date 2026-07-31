using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;

namespace AgenticPrReview.Runtime.Execution.DeepSeek;

internal sealed class DeepSeekReasoningContinuationCodec :
    IAgentContinuationCodec,
    IAgentContinuationStructurePolicy
{
    internal const string Id = "deepseek-reasoning-content";
    internal const string Discriminator = "deepseek-v4-flash-thinking-v1";
    internal const string EncodingName = "utf8";
    internal const string FramingName =
        "deepseek.reasoning_content.utf8.v1";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static DeepSeekReasoningContinuationCodec Instance { get; } =
        new();

    private DeepSeekReasoningContinuationCodec()
    {
    }

    public string CodecId => Id;

    public string CodecDiscriminator => Discriminator;

    public bool TryEncode(
        AgentContinuationCodecValue value,
        out AgentContinuationEncodedPayload? payload)
    {
        payload = null;
        if (value is null ||
            !AgentValueDomains.IsUtf8(
                value.Readable,
                1,
                AgentLimits.ContinuationItemBytes) ||
            value.Opaque is not { Length: 0 } ||
            !StringComparer.Ordinal.Equals(value.Framing, FramingName))
        {
            return false;
        }

        try
        {
            payload = new AgentContinuationEncodedPayload(
                EncodingName,
                StrictUtf8.GetBytes(value.Readable));
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public bool TryDecode(
        string encoding,
        ReadOnlySpan<byte> payload,
        out AgentContinuationCodecValue? value)
    {
        value = null;
        if (!StringComparer.Ordinal.Equals(encoding, EncodingName) ||
            payload.Length is < 1 or > AgentLimits.ContinuationItemBytes)
        {
            return false;
        }

        try
        {
            var readable = StrictUtf8.GetString(payload);
            if (!AgentValueDomains.IsUtf8(
                    readable,
                    1,
                    AgentLimits.ContinuationItemBytes) ||
                !StrictUtf8.GetBytes(readable).AsSpan().SequenceEqual(payload))
            {
                return false;
            }

            value = new AgentContinuationCodecValue(
                readable,
                string.Empty,
                FramingName);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    public bool TryValidate(AgentContinuationStructure structure)
    {
        if (structure is null ||
            structure.Messages.IsDefault ||
            structure.Items.IsDefault ||
            structure.Messages.Length == 0 ||
            structure.Messages.Length != structure.Items.Length)
        {
            return false;
        }

        for (var index = 0; index < structure.Messages.Length; index++)
        {
            var message = structure.Messages[index];
            var item = structure.Items[index];
            if (message is null ||
                item is null ||
                item.Value is null ||
                message.MessageOrdinal != index ||
                message.CallCount is < 1 or > AgentLimits.ToolCallsPerResponse ||
                message.ContinuationPositions.Length != 1 ||
                message.ContinuationPositions[0] != 0 ||
                item.ItemOrdinal != index ||
                item.MessageOrdinal != message.MessageOrdinal ||
                item.ContentPosition != 0 ||
                item.AssociatedCallId is not null ||
                item.Value.Opaque is not { Length: 0 } ||
                !StringComparer.Ordinal.Equals(
                    item.Value.Framing,
                    FramingName) ||
                !AgentValueDomains.IsUtf8(
                    item.Value.Readable,
                    1,
                    AgentLimits.ContinuationItemBytes))
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() =>
        "deepseek_reasoning_continuation_codec";
}
