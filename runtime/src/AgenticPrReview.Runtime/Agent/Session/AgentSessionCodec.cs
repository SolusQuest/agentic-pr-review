using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Session;

internal static class AgentSessionCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string[] RootProperties =
    [
        "namespace",
        "discriminator",
        "session_id",
        "repository_id",
        "review_target",
        "workflow_identity",
        "provider_id",
        "model_id",
        "adapter_id",
        "policy_sha256",
        "build_id",
        "toolset_sha256",
        "limits_sha256",
        "producer_base_sha",
        "producer_head_sha",
        "generation",
        "predecessor_state_sha256",
        "prior_session_sha256",
        "completed_runs",
    ];
    private static ReadOnlySpan<byte> Magic => "APRSES01"u8;

    internal static bool TryWrite(
        AgentSessionDocument document,
        out AgentSessionArtifact? artifact,
        out string failureCode)
    {
        artifact = null;
        failureCode = AgentSessionCodes.CurrentMalformed;
        try
        {
            var writer = new Rfc8785Writer(16 * 1024)
            {
                DiscardLimit =
                    AgentLimits.SessionPlaintextBytes -
                    AgentSessionFormat.FramingBytes,
            };
            WriteDocument(ref writer, document);
            if (writer.Exceeded)
            {
                failureCode = AgentSessionCodes.ConstructionLimit;
                return false;
            }

            var json = writer.ToImmutableArray().ToArray();
            var plaintextLength = checked(
                AgentSessionFormat.FramingBytes + json.Length);
            if (plaintextLength > AgentLimits.SessionPlaintextBytes)
            {
                failureCode = AgentSessionCodes.ConstructionLimit;
                return false;
            }

            var plaintext = new byte[plaintextLength];
            Magic.CopyTo(plaintext);
            BinaryPrimitives.WriteUInt32LittleEndian(
                plaintext.AsSpan(Magic.Length, sizeof(uint)),
                checked((uint)json.Length));
            json.CopyTo(plaintext, AgentSessionFormat.FramingBytes);
            var sessionSha256 = AgentCanonical.HashDomain(
                AgentCanonical.SessionDomain,
                plaintext);
            artifact = new AgentSessionArtifact(
                plaintext,
                sessionSha256,
                document);
            failureCode = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is OverflowException or
            EncoderFallbackException or
            InvalidOperationException or
            Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    internal static bool TryParse(
        ReadOnlySpan<byte> plaintext,
        out AgentSessionArtifact? artifact,
        out string failureCode)
    {
        artifact = null;
        if (!TryParseEnvelope(
                plaintext,
                out var parsed,
                out failureCode))
        {
            return false;
        }

        return TryConvertEnvelope(parsed!, out artifact, out failureCode);
    }

    internal static bool TryParseEnvelope(
        ReadOnlySpan<byte> plaintext,
        out AgentSessionParsedEnvelope? parsed,
        out string failureCode)
    {
        parsed = null;
        if (plaintext.Length > AgentLimits.SessionPlaintextBytes)
        {
            failureCode = AgentSessionCodes.CurrentOversized;
            return false;
        }

        failureCode = AgentSessionCodes.CurrentMalformed;
        if (plaintext.Length < AgentSessionFormat.FramingBytes ||
            !plaintext[..Magic.Length].SequenceEqual(Magic))
        {
            return false;
        }

        var declared = BinaryPrimitives.ReadUInt32LittleEndian(
            plaintext.Slice(Magic.Length, sizeof(uint)));
        if (declared == 0 ||
            declared !=
                (uint)(plaintext.Length - AgentSessionFormat.FramingBytes))
        {
            return false;
        }

        try
        {
            var json = plaintext[AgentSessionFormat.FramingBytes..];
            using var document = JsonDocument.Parse(
                json.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement
                    .EnumerateObject()
                    .Select(property => property.Name)
                    .SequenceEqual(RootProperties))
            {
                return false;
            }

            var writer = new Rfc8785Writer(
                Math.Min(json.Length, 16 * 1024))
            {
                DiscardLimit =
                    AgentLimits.SessionPlaintextBytes -
                    AgentSessionFormat.FramingBytes,
            };
            WriteCanonicalPreservingOrder(
                ref writer,
                document.RootElement,
                depth: 0);
            if (writer.Exceeded ||
                !json.SequenceEqual(writer.ToImmutableArray().AsSpan()))
            {
                return false;
            }

            var dto = JsonSerializer.Deserialize(
                json,
                AgentSessionJsonContext.Default.AgentSessionEnvelopeRootDto);
            if (dto?.CompletedRuns is null)
            {
                return false;
            }

            parsed = new AgentSessionParsedEnvelope(
                plaintext.ToArray(),
                AgentCanonical.HashDomain(
                    AgentCanonical.SessionDomain,
                    plaintext),
                dto);
            failureCode = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            FormatException or
            InvalidOperationException or
            OverflowException or
            DecoderFallbackException or
            EncoderFallbackException or
            Rfc8785CanonicalizationException)
        {
            failureCode = AgentSessionCodes.CurrentMalformed;
            return false;
        }
    }

    internal static bool TryConvertEnvelope(
        AgentSessionParsedEnvelope parsed,
        out AgentSessionArtifact? artifact,
        out string failureCode)
    {
        artifact = null;
        failureCode = AgentSessionCodes.CurrentMalformed;
        if (!DeclaredContinuationTokensAreValid(parsed.Root.CompletedRuns!))
        {
            failureCode = AgentSessionCodes.ContinuationInvalid;
            return false;
        }

        try
        {
            var json = parsed.Plaintext.AsSpan(
                AgentSessionFormat.FramingBytes);
            var dto = JsonSerializer.Deserialize(
                json,
                AgentSessionJsonContext.Default.AgentSessionRootDto);
            if (dto is null ||
                !TryConvertRoot(dto, out var document, out failureCode) ||
                !TryWrite(document!, out var canonical, out _))
            {
                return false;
            }

            if (!parsed.Plaintext.SequenceEqual(canonical!.Plaintext))
            {
                failureCode = AgentSessionCodes.CurrentMalformed;
                return false;
            }

            artifact = canonical;
            failureCode = string.Empty;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            FormatException or
            OverflowException or
            EncoderFallbackException or
            Rfc8785CanonicalizationException)
        {
            failureCode = AgentSessionCodes.CurrentMalformed;
            return false;
        }
    }

    private static bool DeclaredContinuationTokensAreValid(
        IEnumerable<JsonElement> runs)
    {
        foreach (var run in runs)
        {
            if (run.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (run.TryGetProperty("records", out var records) &&
                records.ValueKind == JsonValueKind.Array &&
                !DeclaredContinuationSlotTokensAreValid(records))
            {
                return false;
            }

            if (run.TryGetProperty("continuation", out var continuation) &&
                !DeclaredContinuationObjectTokensAreValid(continuation))
            {
                return false;
            }
        }

        return true;
    }

    private static bool DeclaredContinuationSlotTokensAreValid(
        JsonElement records)
    {
        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object ||
                !record.TryGetProperty("kind", out var recordKind) ||
                recordKind.ValueKind != JsonValueKind.String ||
                !StringComparer.Ordinal.Equals(
                    recordKind.GetString(),
                    "assistant_message") ||
                !record.TryGetProperty("contents", out var contents) ||
                contents.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var content in contents.EnumerateArray())
            {
                if (content.ValueKind != JsonValueKind.Object ||
                    !content.TryGetProperty("kind", out var contentKind) ||
                    contentKind.ValueKind != JsonValueKind.String ||
                    !StringComparer.Ordinal.Equals(
                        contentKind.GetString(),
                        "continuation_slot"))
                {
                    continue;
                }

                if (!HasInt32(content, "content_position") ||
                    !HasKind(
                        content,
                        "continuation_item_id",
                        JsonValueKind.String))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool DeclaredContinuationObjectTokensAreValid(
        JsonElement continuation)
    {
        if (continuation.ValueKind != JsonValueKind.Object ||
            !HasKind(continuation, "codec_id", JsonValueKind.String) ||
            !HasKind(
                continuation,
                "codec_discriminator",
                JsonValueKind.String) ||
            !continuation.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !HasKind(item, "item_id", JsonValueKind.String) ||
                !HasKind(item, "encoding", JsonValueKind.String) ||
                !HasKind(item, "payload", JsonValueKind.String) ||
                !HasKind(item, "payload_sha256", JsonValueKind.String) ||
                !HasKind(item, "message_id", JsonValueKind.String) ||
                !HasInt32(item, "content_position") ||
                !item.TryGetProperty(
                    "associated_call_id",
                    out var associatedCallId) ||
                associatedCallId.ValueKind is not (
                    JsonValueKind.Null or JsonValueKind.String))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasKind(
        JsonElement element,
        string propertyName,
        JsonValueKind expectedKind) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == expectedKind;

    private static bool HasInt32(
        JsonElement element,
        string propertyName) =>
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind == JsonValueKind.Number &&
        property.TryGetInt32(out _);

    private static void WriteCanonicalPreservingOrder(
        ref Rfc8785Writer writer,
        JsonElement element,
        int depth)
    {
        if (depth > 64)
        {
            throw new Rfc8785CanonicalizationException(
                Rfc8785RejectionReason.DepthLimitExceeded,
                "Session JSON exceeds the canonical depth bound.");
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteObjectStart();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!seen.Add(property.Name))
                    {
                        throw new Rfc8785CanonicalizationException(
                            Rfc8785RejectionReason.DuplicateProperty,
                            "Session JSON contains a duplicate property.");
                    }

                    writer.WriteProperty(property.Name);
                    WriteCanonicalPreservingOrder(
                        ref writer,
                        property.Value,
                        depth + 1);
                }

                writer.WriteObjectEnd();
                return;
            case JsonValueKind.Array:
                writer.WriteArrayStart();
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (index > 0)
                    {
                        writer.WriteComma();
                    }

                    WriteCanonicalPreservingOrder(
                        ref writer,
                        item,
                        depth + 1);
                    index++;
                }

                writer.WriteArrayEnd();
                return;
            case JsonValueKind.String:
                writer.WriteString(element.GetString()!);
                return;
            case JsonValueKind.Number:
                if (element.TryGetInt64(out var integer))
                {
                    writer.WriteNumber(integer);
                }
                else
                {
                    writer.WriteNumber(element.GetDouble());
                }

                return;
            case JsonValueKind.True:
                writer.WriteBoolean(true);
                return;
            case JsonValueKind.False:
                writer.WriteBoolean(false);
                return;
            case JsonValueKind.Null:
                writer.WriteNull();
                return;
            default:
                throw new InvalidOperationException(
                    "Session JSON contains an unsupported value kind.");
        }
    }

    internal static string ContinuationPayloadSha256(
        string codecId,
        string codecDiscriminator,
        string itemId,
        string encoding,
        ReadOnlySpan<byte> payloadBytes)
    {
        var writer = new Rfc8785Writer(256);
        writer.WriteObjectStart();
        writer.WriteProperty("codec_id");
        writer.WriteString(codecId);
        writer.WriteProperty("codec_discriminator");
        writer.WriteString(codecDiscriminator);
        writer.WriteProperty("item_id");
        writer.WriteString(itemId);
        writer.WriteProperty("encoding");
        writer.WriteString(encoding);
        writer.WriteProperty("payload_bytes");
        writer.WriteString(Convert.ToBase64String(payloadBytes));
        writer.WriteObjectEnd();
        return AgentCanonical.HashDomain(
            AgentCanonical.ContinuationDomain,
            writer.ToImmutableArray().AsSpan());
    }

    internal static byte[] WriteRecordBytes(AgentSessionRecord record)
    {
        var writer = new Rfc8785Writer(1_024)
        {
            DiscardLimit = AgentLimits.SessionRecordBytes,
        };
        WriteRecord(ref writer, record);
        if (writer.Exceeded)
        {
            return [];
        }

        return writer.ToImmutableArray().ToArray();
    }

    internal static byte[] WriteContinuationItemBytes(
        AgentSessionContinuationItem item)
    {
        var writer = new Rfc8785Writer(256);
        WriteContinuationItem(ref writer, item);
        return writer.ToImmutableArray().ToArray();
    }

    private static bool TryConvertRoot(
        AgentSessionRootDto dto,
        out AgentSessionDocument? document,
        out string failureCode)
    {
        document = null;
        failureCode = AgentSessionCodes.CurrentMalformed;
        if (dto.Namespace is null ||
            dto.Discriminator is null ||
            dto.SessionId is null ||
            dto.RepositoryId is null ||
            dto.WorkflowIdentity is null ||
            dto.ProviderId is null ||
            dto.ModelId is null ||
            dto.AdapterId is null ||
            dto.PolicySha256 is null ||
            dto.BuildId is null ||
            dto.ToolsetSha256 is null ||
            dto.LimitsSha256 is null ||
            dto.ProducerBaseSha is null ||
            dto.ProducerHeadSha is null ||
            dto.CompletedRuns is null)
        {
            return false;
        }

        var runs = ImmutableArray.CreateBuilder<AgentSessionCompletedRun>(
            dto.CompletedRuns.Length);
        foreach (var run in dto.CompletedRuns)
        {
            if (run is null ||
                !TryConvertRun(run, out var converted, out failureCode))
            {
                return false;
            }

            runs.Add(converted!);
        }

        document = new AgentSessionDocument(
            dto.Namespace,
            dto.Discriminator,
            dto.SessionId,
            dto.RepositoryId,
            dto.ReviewTarget,
            dto.WorkflowIdentity,
            dto.ProviderId,
            dto.ModelId,
            dto.AdapterId,
            dto.PolicySha256,
            dto.BuildId,
            dto.ToolsetSha256,
            dto.LimitsSha256,
            dto.ProducerBaseSha,
            dto.ProducerHeadSha,
            dto.Generation,
            dto.PredecessorStateSha256,
            dto.PriorSessionSha256,
            runs.MoveToImmutable());
        return true;
    }

    private static bool TryConvertRun(
        AgentSessionRunDto dto,
        out AgentSessionCompletedRun? run,
        out string failureCode)
    {
        run = null;
        failureCode = AgentSessionCodes.RecordInvalid;
        if (dto.RunId is null ||
            dto.ReviewedIdentity is null ||
            !TryConvertIdentity(dto.ReviewedIdentity, out var identity) ||
            dto.StablePlanSha256 is null ||
            dto.Records is null ||
            dto.Continuation is null)
        {
            return false;
        }

        var records = ImmutableArray.CreateBuilder<AgentSessionRecord>(
            dto.Records.Length);
        foreach (var element in dto.Records)
        {
            if (!TryConvertRecord(element, out var record, out failureCode))
            {
                return false;
            }

            records.Add(record!);
        }

        if (!TryConvertContinuation(
                dto.Continuation,
                out var continuation,
                out failureCode))
        {
            return false;
        }

        run = new AgentSessionCompletedRun(
            dto.RunId,
            dto.RunOrdinal,
            identity!,
            dto.StablePlanSha256,
            records.MoveToImmutable(),
            continuation!);
        return true;
    }

    private static bool TryConvertRecord(
        JsonElement element,
        out AgentSessionRecord? record,
        out string failureCode)
    {
        record = null;
        failureCode = AgentSessionCodes.RecordInvalid;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var kind = kindElement.GetString();
        switch (kind)
        {
            case "review_context":
                var context = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default.AgentSessionReviewContextDto);
                if (context?.Kind is null ||
                    context.Id is null ||
                    context.ReviewedIdentity is null ||
                    !TryConvertIdentity(
                        context.ReviewedIdentity,
                        out var contextIdentity) ||
                    context.Text is null ||
                    context.Role is null ||
                    context.Framing is null ||
                    context.Classification is null)
                {
                    return false;
                }

                record = new AgentSessionReviewContextRecord(
                    context.Id,
                    context.Sequence,
                    contextIdentity!,
                    context.Text,
                    context.Role,
                    context.Framing,
                    context.Classification);
                return true;

            case "assistant_message":
                var message = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default
                        .AgentSessionAssistantMessageDto);
                if (message?.Kind is null ||
                    message.Id is null ||
                    message.Contents is null ||
                    message.Role is null ||
                    message.Framing is null ||
                    message.Classification is null)
                {
                    return false;
                }

                var contents =
                    ImmutableArray.CreateBuilder<AgentSessionAssistantContent>(
                        message.Contents.Length);
                foreach (var contentElement in message.Contents)
                {
                    if (!TryConvertContent(
                            contentElement,
                            out var content,
                            out failureCode))
                    {
                        return false;
                    }

                    contents.Add(content!);
                }

                record = new AgentSessionAssistantMessageRecord(
                    message.Id,
                    message.Sequence,
                    message.MessageOrdinal,
                    contents.MoveToImmutable(),
                    message.Role,
                    message.Framing,
                    message.Classification);
                return true;

            case "tool_result":
                var result = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default.AgentSessionToolResultDto);
                if (result?.Kind is null ||
                    result.Id is null ||
                    result.SourceMessageId is null ||
                    result.CallId is null ||
                    result.Name is null ||
                    result.ObservationId is null ||
                    result.ResultJson is null ||
                    result.Role is null ||
                    result.Framing is null ||
                    result.Classification is null)
                {
                    return false;
                }

                record = new AgentSessionToolResultRecord(
                    result.Id,
                    result.Sequence,
                    result.SourceMessageId,
                    result.CallId,
                    result.Name,
                    result.ObservationId,
                    result.ResultJson,
                    result.Role,
                    result.Framing,
                    result.Classification);
                return true;

            case "review_outcome":
                var outcome = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default.AgentSessionReviewOutcomeDto);
                if (outcome?.Kind is null ||
                    outcome.Id is null ||
                    outcome.TerminalMessageId is null ||
                    outcome.TerminalCallId is null ||
                    outcome.TerminalSha256 is null ||
                    outcome.Summary is null ||
                    outcome.FindingsJson is null ||
                    outcome.Role is null ||
                    outcome.Framing is null ||
                    outcome.Classification is null)
                {
                    return false;
                }

                record = new AgentSessionReviewOutcomeRecord(
                    outcome.Id,
                    outcome.Sequence,
                    outcome.TerminalMessageId,
                    outcome.TerminalCallId,
                    outcome.TerminalSha256,
                    outcome.Summary,
                    outcome.FindingsJson,
                    outcome.Role,
                    outcome.Framing,
                    outcome.Classification);
                return true;
            default:
                return false;
        }
    }

    private static bool TryConvertContent(
        JsonElement element,
        out AgentSessionAssistantContent? content,
        out string failureCode)
    {
        content = null;
        failureCode = AgentSessionCodes.RecordInvalid;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("kind", out var kindElement) ||
            kindElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        switch (kindElement.GetString())
        {
            case "text":
                var text = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default.AgentSessionTextContentDto);
                if (text?.Kind is null || text.Text is null)
                {
                    return false;
                }

                content = new AgentSessionTextContent(
                    text.ContentPosition,
                    text.Text);
                return true;
            case "continuation_slot":
                failureCode = AgentSessionCodes.ContinuationInvalid;
                var slot = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default
                        .AgentSessionContinuationSlotDto);
                if (slot?.Kind is null || slot.ContinuationItemId is null)
                {
                    return false;
                }

                content = new AgentSessionContinuationSlotContent(
                    slot.ContentPosition,
                    slot.ContinuationItemId);
                return true;
            case "tool_call":
                var call = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default.AgentSessionToolCallDto);
                if (call?.Kind is null ||
                    call.CallId is null ||
                    call.Name is null ||
                    call.ArgumentsJson is null)
                {
                    return false;
                }

                content = new AgentSessionToolCallContent(
                    call.ContentPosition,
                    call.CallId,
                    call.Name,
                    call.ArgumentsJson);
                return true;
            case "terminal_call":
                var terminal = JsonSerializer.Deserialize(
                    element,
                    AgentSessionJsonContext.Default.AgentSessionTerminalCallDto);
                if (terminal?.Kind is null ||
                    terminal.CallId is null ||
                    terminal.Name is null ||
                    terminal.ArgumentsJson is null ||
                    terminal.ArgumentsSha256 is null)
                {
                    return false;
                }

                content = new AgentSessionTerminalCallContent(
                    terminal.ContentPosition,
                    terminal.CallId,
                    terminal.Name,
                    terminal.ArgumentsJson,
                    terminal.ArgumentsSha256);
                return true;
            default:
                return false;
        }
    }

    private static bool TryConvertContinuation(
        AgentSessionContinuationDto dto,
        out AgentSessionContinuation? continuation,
        out string failureCode)
    {
        continuation = null;
        failureCode = AgentSessionCodes.ContinuationInvalid;
        if (dto.CodecId is null ||
            dto.CodecDiscriminator is null ||
            dto.Items is null)
        {
            return false;
        }

        var items = ImmutableArray.CreateBuilder<AgentSessionContinuationItem>(
            dto.Items.Length);
        foreach (var item in dto.Items)
        {
            if (item is null ||
                item.ItemId is null ||
                item.Encoding is null ||
                item.Payload is null ||
                item.PayloadSha256 is null ||
                item.MessageId is null ||
                !TryDecodePayload(
                    item.Encoding,
                    item.Payload,
                    out var payloadBytes))
            {
                return false;
            }

            items.Add(new AgentSessionContinuationItem(
                item.ItemId,
                item.Encoding,
                item.Payload,
                payloadBytes!,
                item.PayloadSha256,
                item.MessageId,
                item.ContentPosition,
                item.AssociatedCallId));
        }

        continuation = new AgentSessionContinuation(
            dto.CodecId,
            dto.CodecDiscriminator,
            items.MoveToImmutable());
        return true;
    }

    private static bool TryDecodePayload(
        string encoding,
        string payload,
        out byte[]? payloadBytes)
    {
        payloadBytes = null;
        switch (encoding)
        {
            case "utf8":
                payloadBytes = StrictUtf8.GetBytes(payload);
                return StringComparer.Ordinal.Equals(
                    StrictUtf8.GetString(payloadBytes),
                    payload);
            case "base64":
                try
                {
                    payloadBytes = Convert.FromBase64String(payload);
                    return StringComparer.Ordinal.Equals(
                        Convert.ToBase64String(payloadBytes),
                        payload);
                }
                catch (FormatException)
                {
                    payloadBytes = null;
                    return false;
                }
            default:
                return false;
        }
    }

    private static bool TryConvertIdentity(
        AgentSessionReviewedIdentityDto dto,
        out ReviewedIdentity? identity)
    {
        identity = null;
        if (dto.RepositoryId is null ||
            dto.BaseSha is null ||
            dto.HeadSha is null)
        {
            return false;
        }

        identity = new ReviewedIdentity(
            dto.RepositoryId,
            dto.ReviewTarget,
            dto.BaseSha,
            dto.HeadSha);
        return true;
    }

    private static void WriteDocument(
        ref Rfc8785Writer writer,
        AgentSessionDocument document)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("namespace");
        writer.WriteString(document.Namespace);
        writer.WriteProperty("discriminator");
        writer.WriteString(document.Discriminator);
        writer.WriteProperty("session_id");
        writer.WriteString(document.SessionId);
        writer.WriteProperty("repository_id");
        writer.WriteString(document.RepositoryId);
        writer.WriteProperty("review_target");
        writer.WriteNumber(document.ReviewTarget);
        writer.WriteProperty("workflow_identity");
        writer.WriteString(document.WorkflowIdentity);
        writer.WriteProperty("provider_id");
        writer.WriteString(document.ProviderId);
        writer.WriteProperty("model_id");
        writer.WriteString(document.ModelId);
        writer.WriteProperty("adapter_id");
        writer.WriteString(document.AdapterId);
        writer.WriteProperty("policy_sha256");
        writer.WriteString(document.PolicySha256);
        writer.WriteProperty("build_id");
        writer.WriteString(document.BuildId);
        writer.WriteProperty("toolset_sha256");
        writer.WriteString(document.ToolsetSha256);
        writer.WriteProperty("limits_sha256");
        writer.WriteString(document.LimitsSha256);
        writer.WriteProperty("producer_base_sha");
        writer.WriteString(document.ProducerBaseSha);
        writer.WriteProperty("producer_head_sha");
        writer.WriteString(document.ProducerHeadSha);
        writer.WriteProperty("generation");
        writer.WriteNumber(document.Generation);
        writer.WriteProperty("predecessor_state_sha256");
        WriteNullableString(ref writer, document.PredecessorStateSha256);
        writer.WriteProperty("prior_session_sha256");
        WriteNullableString(ref writer, document.PriorSessionSha256);
        writer.WriteProperty("completed_runs");
        writer.WriteArrayStart();
        for (var index = 0; index < document.CompletedRuns.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            WriteRun(ref writer, document.CompletedRuns[index]);
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteRun(
        ref Rfc8785Writer writer,
        AgentSessionCompletedRun run)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("run_id");
        writer.WriteString(run.RunId);
        writer.WriteProperty("run_ordinal");
        writer.WriteNumber(run.RunOrdinal);
        writer.WriteProperty("reviewed_identity");
        AgentCanonical.WriteReviewedIdentity(ref writer, run.ReviewedIdentity);
        writer.WriteProperty("stable_plan_sha256");
        writer.WriteString(run.StablePlanSha256);
        writer.WriteProperty("records");
        writer.WriteArrayStart();
        for (var index = 0; index < run.Records.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            WriteRecord(ref writer, run.Records[index]);
        }

        writer.WriteArrayEnd();
        writer.WriteProperty("continuation");
        WriteContinuation(ref writer, run.Continuation);
        writer.WriteObjectEnd();
    }

    private static void WriteRecord(
        ref Rfc8785Writer writer,
        AgentSessionRecord record)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("kind");
        writer.WriteString(record.Kind);
        writer.WriteProperty("id");
        writer.WriteString(record.Id);
        writer.WriteProperty("sequence");
        writer.WriteNumber(record.Sequence);
        switch (record)
        {
            case AgentSessionReviewContextRecord context:
                writer.WriteProperty("reviewed_identity");
                AgentCanonical.WriteReviewedIdentity(
                    ref writer,
                    context.ReviewedIdentity);
                writer.WriteProperty("text");
                writer.WriteString(context.Text);
                break;
            case AgentSessionAssistantMessageRecord message:
                writer.WriteProperty("message_ordinal");
                writer.WriteNumber(message.MessageOrdinal);
                writer.WriteProperty("contents");
                writer.WriteArrayStart();
                for (var index = 0; index < message.Contents.Length; index++)
                {
                    if (index > 0)
                    {
                        writer.WriteComma();
                    }

                    WriteContent(ref writer, message.Contents[index]);
                }

                writer.WriteArrayEnd();
                break;
            case AgentSessionToolResultRecord result:
                writer.WriteProperty("source_message_id");
                writer.WriteString(result.SourceMessageId);
                writer.WriteProperty("call_id");
                writer.WriteString(result.CallId);
                writer.WriteProperty("name");
                writer.WriteString(result.Name);
                writer.WriteProperty("observation_id");
                writer.WriteString(result.ObservationId);
                writer.WriteProperty("result_json");
                writer.WriteString(result.ResultJson);
                break;
            case AgentSessionReviewOutcomeRecord outcome:
                writer.WriteProperty("terminal_message_id");
                writer.WriteString(outcome.TerminalMessageId);
                writer.WriteProperty("terminal_call_id");
                writer.WriteString(outcome.TerminalCallId);
                writer.WriteProperty("terminal_sha256");
                writer.WriteString(outcome.TerminalSha256);
                writer.WriteProperty("summary");
                writer.WriteString(outcome.Summary);
                writer.WriteProperty("findings_json");
                writer.WriteString(outcome.FindingsJson);
                break;
            default:
                throw new InvalidOperationException("Unsupported session record.");
        }

        writer.WriteProperty("role");
        writer.WriteString(record.Role);
        writer.WriteProperty("framing");
        writer.WriteString(record.Framing);
        writer.WriteProperty("classification");
        writer.WriteString(record.Classification);
        writer.WriteObjectEnd();
    }

    private static void WriteContent(
        ref Rfc8785Writer writer,
        AgentSessionAssistantContent content)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("kind");
        writer.WriteString(content.Kind);
        writer.WriteProperty("content_position");
        writer.WriteNumber(content.ContentPosition);
        switch (content)
        {
            case AgentSessionTextContent text:
                writer.WriteProperty("text");
                writer.WriteString(text.Text);
                break;
            case AgentSessionContinuationSlotContent slot:
                writer.WriteProperty("continuation_item_id");
                writer.WriteString(slot.ContinuationItemId);
                break;
            case AgentSessionToolCallContent call:
                writer.WriteProperty("call_id");
                writer.WriteString(call.CallId);
                writer.WriteProperty("name");
                writer.WriteString(call.Name);
                writer.WriteProperty("arguments_json");
                writer.WriteString(call.ArgumentsJson);
                break;
            case AgentSessionTerminalCallContent terminal:
                writer.WriteProperty("call_id");
                writer.WriteString(terminal.CallId);
                writer.WriteProperty("name");
                writer.WriteString(terminal.Name);
                writer.WriteProperty("arguments_json");
                writer.WriteString(terminal.ArgumentsJson);
                writer.WriteProperty("arguments_sha256");
                writer.WriteString(terminal.ArgumentsSha256);
                break;
            default:
                throw new InvalidOperationException("Unsupported session content.");
        }

        writer.WriteObjectEnd();
    }

    private static void WriteContinuation(
        ref Rfc8785Writer writer,
        AgentSessionContinuation continuation)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("codec_id");
        writer.WriteString(continuation.CodecId);
        writer.WriteProperty("codec_discriminator");
        writer.WriteString(continuation.CodecDiscriminator);
        writer.WriteProperty("items");
        writer.WriteArrayStart();
        for (var index = 0; index < continuation.Items.Length; index++)
        {
            if (index > 0)
            {
                writer.WriteComma();
            }

            WriteContinuationItem(ref writer, continuation.Items[index]);
        }

        writer.WriteArrayEnd();
        writer.WriteObjectEnd();
    }

    private static void WriteContinuationItem(
        ref Rfc8785Writer writer,
        AgentSessionContinuationItem item)
    {
        writer.WriteObjectStart();
        writer.WriteProperty("item_id");
        writer.WriteString(item.ItemId);
        writer.WriteProperty("encoding");
        writer.WriteString(item.Encoding);
        writer.WriteProperty("payload");
        writer.WriteString(item.Payload);
        writer.WriteProperty("payload_sha256");
        writer.WriteString(item.PayloadSha256);
        writer.WriteProperty("message_id");
        writer.WriteString(item.MessageId);
        writer.WriteProperty("content_position");
        writer.WriteNumber(item.ContentPosition);
        writer.WriteProperty("associated_call_id");
        WriteNullableString(ref writer, item.AssociatedCallId);
        writer.WriteObjectEnd();
    }

    private static void WriteNullableString(
        ref Rfc8785Writer writer,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull();
        }
        else
        {
            writer.WriteString(value);
        }
    }
}
