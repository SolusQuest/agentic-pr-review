using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Session;

internal static class AgentStableRequestMaterializer
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryMaterialize(
        AgentSessionTrustedRequest trusted,
        string? priorSessionSha256,
        out AgentSessionMaterializedStableRequest? materialized)
    {
        materialized = null;
        if (trusted is null ||
            trusted.TrustedPolicyBytes is null ||
            trusted.TrustedPolicyBytes.Length > AgentLimits.RequestBytes ||
            !AgentValueDomains.IsUtf8(trusted.RepositoryId, 1, 128) ||
            trusted.ReviewTarget < 1 ||
            !AgentValueDomains.IsUtf8(trusted.WorkflowIdentity, 1, 256) ||
            !AgentValueDomains.IsUtf8(trusted.BuildId, 1, 256) ||
            !AgentValueDomains.IsUtf8(trusted.ProviderId, 1, 128) ||
            !AgentValueDomains.IsUtf8(trusted.ModelId, 1, 128) ||
            !AgentValueDomains.IsUtf8(trusted.AdapterId, 1, 128) ||
            (priorSessionSha256 is not null &&
                !AgentSessionValidation.IsLowerHex(
                    priorSessionSha256,
                    64)) ||
            !TryMaterializeControlMessages(
                trusted.TrustedPolicyBytes,
                out var controlMessages))
        {
            return false;
        }

        var plan = new StableAgentPlan(
            trusted.RepositoryId,
            trusted.ReviewTarget,
            trusted.WorkflowIdentity,
            AgentCanonical.HashRaw(trusted.TrustedPolicyBytes),
            AgentCanonical.ToolsetSha256(AgentToolRegistry.Definitions),
            AgentCanonical.LimitsSha256(),
            trusted.BuildId,
            trusted.ProviderId,
            trusted.ModelId,
            trusted.AdapterId,
            priorSessionSha256);
        materialized = new AgentSessionMaterializedStableRequest(
            plan,
            controlMessages!);
        return true;
    }

    private static bool TryMaterializeControlMessages(
        byte[] trustedPolicyBytes,
        out ProjectChatMessage[]? messages)
    {
        messages = null;
        string policyText;
        try
        {
            policyText = StrictUtf8.GetString(trustedPolicyBytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        if (!AgentValueDomains.IsUtf8(
                policyText,
                1,
                AgentLimits.ContentBytes))
        {
            return false;
        }

        messages =
        [
            new ProjectChatMessage(
                "system",
                [new ProjectTextContent(policyText)]),
        ];
        return true;
    }
}

internal static class AgentSessionValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryValidateEnvelopeRoot(
        AgentSessionEnvelopeRootDto root,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.CurrentMalformed;
        if (root.Namespace is null ||
            root.Discriminator is null ||
            root.SessionId is null ||
            root.RepositoryId is null ||
            root.WorkflowIdentity is null ||
            root.ProviderId is null ||
            root.ModelId is null ||
            root.AdapterId is null ||
            root.PolicySha256 is null ||
            root.BuildId is null ||
            root.ToolsetSha256 is null ||
            root.LimitsSha256 is null ||
            root.ProducerBaseSha is null ||
            root.ProducerHeadSha is null ||
            root.CompletedRuns is null ||
            !StringComparer.Ordinal.Equals(
                root.Namespace,
                AgentSessionFormat.Namespace) ||
            !StringComparer.Ordinal.Equals(
                root.Discriminator,
                AgentSessionFormat.Discriminator) ||
            !AgentValueDomains.IsIdentifier(root.SessionId) ||
            !AgentValueDomains.IsUtf8(root.RepositoryId, 1, 128) ||
            root.ReviewTarget < 1 ||
            !AgentValueDomains.IsUtf8(root.WorkflowIdentity, 1, 256) ||
            !AgentValueDomains.IsUtf8(root.ProviderId, 1, 128) ||
            !AgentValueDomains.IsUtf8(root.ModelId, 1, 128) ||
            !AgentValueDomains.IsUtf8(root.AdapterId, 1, 128) ||
            !IsLowerHex(root.PolicySha256, 64) ||
            !AgentValueDomains.IsUtf8(root.BuildId, 1, 256) ||
            !IsLowerHex(root.ToolsetSha256, 64) ||
            !IsLowerHex(root.LimitsSha256, 64) ||
            !IsLowerHex(root.ProducerBaseSha, 40) ||
            !IsLowerHex(root.ProducerHeadSha, 40) ||
            root.Generation < 0 ||
            root.CompletedRuns.Length is < 1 or >
                AgentSessionFormat.MaximumCompletedRuns ||
            root.Generation != root.CompletedRuns.Length - 1L ||
            (root.Generation == 0 &&
                (root.PredecessorStateSha256 is not null ||
                    root.PriorSessionSha256 is not null)) ||
            (root.Generation > 0 &&
                (!IsLowerHex(root.PredecessorStateSha256, 64) ||
                    !IsLowerHex(root.PriorSessionSha256, 64))))
        {
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    internal static bool TryValidateRoot(
        AgentSessionDocument document,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.CurrentMalformed;
        if (!StringComparer.Ordinal.Equals(
                document.Namespace,
                AgentSessionFormat.Namespace) ||
            !StringComparer.Ordinal.Equals(
                document.Discriminator,
                AgentSessionFormat.Discriminator) ||
            !AgentValueDomains.IsIdentifier(document.SessionId) ||
            !AgentValueDomains.IsUtf8(document.RepositoryId, 1, 128) ||
            document.ReviewTarget < 1 ||
            !AgentValueDomains.IsUtf8(document.WorkflowIdentity, 1, 256) ||
            !AgentValueDomains.IsUtf8(document.ProviderId, 1, 128) ||
            !AgentValueDomains.IsUtf8(document.ModelId, 1, 128) ||
            !AgentValueDomains.IsUtf8(document.AdapterId, 1, 128) ||
            !IsLowerHex(document.PolicySha256, 64) ||
            !AgentValueDomains.IsUtf8(document.BuildId, 1, 256) ||
            !IsLowerHex(document.ToolsetSha256, 64) ||
            !IsLowerHex(document.LimitsSha256, 64) ||
            !IsLowerHex(document.ProducerBaseSha, 40) ||
            !IsLowerHex(document.ProducerHeadSha, 40) ||
            document.Generation < 0 ||
            document.CompletedRuns.Length is < 1 or >
                AgentSessionFormat.MaximumCompletedRuns ||
            document.Generation != document.CompletedRuns.Length - 1L ||
            (document.Generation == 0 &&
                (document.PredecessorStateSha256 is not null ||
                    document.PriorSessionSha256 is not null)) ||
            (document.Generation > 0 &&
                (!IsLowerHex(document.PredecessorStateSha256, 64) ||
                    !IsLowerHex(document.PriorSessionSha256, 64))))
        {
            return false;
        }

        var runIds = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < document.CompletedRuns.Length; index++)
        {
            var run = document.CompletedRuns[index];
            if (!AgentValueDomains.IsIdentifier(run.RunId) ||
                !runIds.Add(run.RunId) ||
                run.RunOrdinal != index ||
                !run.ReviewedIdentity.IsValid() ||
                !StringComparer.Ordinal.Equals(
                    run.ReviewedIdentity.RepositoryId,
                    document.RepositoryId) ||
                run.ReviewedIdentity.ReviewTarget != document.ReviewTarget ||
                !IsLowerHex(run.StablePlanSha256, 64))
            {
                return false;
            }
        }

        var latest = document.CompletedRuns[^1];
        if (!StringComparer.Ordinal.Equals(
                latest.ReviewedIdentity.BaseSha,
                document.ProducerBaseSha) ||
            !StringComparer.Ordinal.Equals(
                latest.ReviewedIdentity.HeadSha,
                document.ProducerHeadSha))
        {
            return false;
        }

        var firstPlan = PlanFromRoot(document, priorSessionSha256: null);
        if (!StringComparer.Ordinal.Equals(
                document.CompletedRuns[0].StablePlanSha256,
                AgentCanonical.StablePlanSha256(firstPlan)))
        {
            failureCode = AgentSessionCodes.RecordInvalid;
            return false;
        }

        var latestPlan = PlanFromRoot(
            document,
            document.PriorSessionSha256);
        if (!StringComparer.Ordinal.Equals(
                latest.StablePlanSha256,
                AgentCanonical.StablePlanSha256(latestPlan)))
        {
            failureCode = AgentSessionCodes.RecordInvalid;
            return false;
        }

        failureCode = string.Empty;
        return true;
    }

    internal static bool TryValidateRecords(
        AgentSessionDocument document,
        IAgentContinuationCodec continuationCodec,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.RecordInvalid;
        if (continuationCodec is null ||
            !AgentValueDomains.IsIdentifier(continuationCodec.CodecId) ||
            !AgentValueDomains.IsIdentifier(
                continuationCodec.CodecDiscriminator))
        {
            failureCode = AgentSessionCodes.ContinuationInvalid;
            return false;
        }

        var identifiers = document.CompletedRuns
            .Select(run => run.RunId)
            .ToHashSet(StringComparer.Ordinal);
        long continuationBytes = 0;
        long sessionRecords = 0;
        foreach (var run in document.CompletedRuns)
        {
            try
            {
                sessionRecords = checked(
                    sessionRecords +
                    run.Records.Length +
                    run.Continuation.Items.Length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (sessionRecords > AgentLimits.SessionRecords)
            {
                return false;
            }

            if (!TryValidateRun(
                    run,
                    continuationCodec,
                    identifiers,
                    ref continuationBytes,
                    out failureCode))
            {
                return false;
            }
        }

        failureCode = string.Empty;
        return true;
    }

    internal static StableAgentPlan PlanFromRoot(
        AgentSessionDocument document,
        string? priorSessionSha256) =>
        new(
            document.RepositoryId,
            document.ReviewTarget,
            document.WorkflowIdentity,
            document.PolicySha256,
            document.ToolsetSha256,
            document.LimitsSha256,
            document.BuildId,
            document.ProviderId,
            document.ModelId,
            document.AdapterId,
            priorSessionSha256);

    internal static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryValidateRun(
        AgentSessionCompletedRun run,
        IAgentContinuationCodec continuationCodec,
        HashSet<string> identifiers,
        ref long continuationBytes,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.RecordInvalid;
        if (run.Records.Length is < 3 or > AgentLimits.SessionRecords)
        {
            return false;
        }

        for (var index = 0; index < run.Records.Length; index++)
        {
            var record = run.Records[index];
            if (record.Sequence != index ||
                !AgentValueDomains.IsIdentifier(record.Id) ||
                !identifiers.Add(record.Id) ||
                AgentSessionCodec.WriteRecordBytes(record).Length is 0 or >
                    AgentLimits.SessionRecordBytes)
            {
                return false;
            }

            if (!HasExactClassification(record))
            {
                failureCode = AgentSessionCodes.ClassificationInvalid;
                return false;
            }
        }

        if (run.Records[0] is not AgentSessionReviewContextRecord context ||
            context.ReviewedIdentity != run.ReviewedIdentity ||
            !AgentValueDomains.IsUtf8(
                context.Text,
                1,
                AgentLimits.ContentBytes))
        {
            return false;
        }

        var observations = new List<AgentObservation>();
        var assistantMessages =
            new Dictionary<string, AgentSessionAssistantMessageRecord>(
                StringComparer.Ordinal);
        var recordIndex = 1;
        var messageOrdinal = 0;
        var terminalSeen = false;
        long toolResultBytes = 0;
        while (recordIndex < run.Records.Length)
        {
            if (run.Records[recordIndex] is not
                AgentSessionAssistantMessageRecord message ||
                message.MessageOrdinal != messageOrdinal ||
                !TryValidateMessageContents(
                    message,
                    identifiers,
                    out var normalCalls,
                    out var terminalCall,
                    out failureCode))
            {
                return false;
            }

            assistantMessages.Add(message.Id, message);
            messageOrdinal++;
            recordIndex++;
            if (terminalCall is not null)
            {
                if (terminalSeen ||
                    recordIndex >= run.Records.Length ||
                    run.Records[recordIndex] is not
                        AgentSessionReviewOutcomeRecord outcome ||
                    recordIndex != run.Records.Length - 1 ||
                    !TryValidateTerminal(
                        run.ReviewedIdentity,
                        message,
                        terminalCall,
                        outcome,
                        observations))
                {
                    failureCode = AgentSessionCodes.AssociationInvalid;
                    return false;
                }

                terminalSeen = true;
                recordIndex++;
                continue;
            }

            if (normalCalls.Length is < 1 or >
                AgentLimits.ToolCallsPerResponse)
            {
                return false;
            }

            foreach (var call in normalCalls)
            {
                if (recordIndex >= run.Records.Length ||
                    run.Records[recordIndex] is not
                        AgentSessionToolResultRecord result ||
                    !StringComparer.Ordinal.Equals(
                        result.SourceMessageId,
                        message.Id) ||
                    !StringComparer.Ordinal.Equals(
                        result.CallId,
                        call.CallId) ||
                    !StringComparer.Ordinal.Equals(
                        result.Name,
                        call.Name) ||
                    !TryUtf8Length(
                        result.ResultJson,
                        1,
                        AgentLimits.ToolResultBytes,
                        out var resultBytes) ||
                    !TryReadObservation(
                        call,
                        result,
                        run.ReviewedIdentity,
                        resultBytes!,
                        out var observation))
                {
                    failureCode = AgentSessionCodes.AssociationInvalid;
                    return false;
                }

                try
                {
                    toolResultBytes = checked(
                        toolResultBytes + resultBytes!.Length);
                }
                catch (OverflowException)
                {
                    return false;
                }

                if (toolResultBytes > AgentLimits.ToolResultsTotalBytes)
                {
                    return false;
                }

                observations.Add(observation!);
                recordIndex++;
            }
        }

        if (!terminalSeen ||
            !TryValidateContinuation(
                run,
                assistantMessages,
                continuationCodec,
                identifiers,
                ref continuationBytes,
                out failureCode))
        {
            return false;
        }

        return true;
    }

    private static bool TryValidateMessageContents(
        AgentSessionAssistantMessageRecord message,
        HashSet<string> identifiers,
        out ImmutableArray<AgentSessionToolCallContent> normalCalls,
        out AgentSessionTerminalCallContent? terminalCall,
        out string failureCode)
    {
        normalCalls = [];
        terminalCall = null;
        failureCode = AgentSessionCodes.RecordInvalid;
        if (message.Contents.Length is < 1 or > AgentLimits.PartsPerMessage)
        {
            return false;
        }

        var calls =
            ImmutableArray.CreateBuilder<AgentSessionToolCallContent>();
        for (var index = 0; index < message.Contents.Length; index++)
        {
            var content = message.Contents[index];
            if (content.ContentPosition != index)
            {
                return false;
            }

            switch (content)
            {
                case AgentSessionTextContent text
                    when AgentValueDomains.IsUtf8(
                        text.Text,
                        1,
                        AgentLimits.ContentBytes):
                case AgentSessionContinuationSlotContent slot
                    when AgentValueDomains.IsIdentifier(
                        slot.ContinuationItemId):
                    break;
                case AgentSessionToolCallContent call
                    when AgentValueDomains.IsIdentifier(call.CallId) &&
                        identifiers.Add(call.CallId) &&
                        TryValidateNormalCall(call):
                    calls.Add(call);
                    break;
                case AgentSessionTerminalCallContent terminal
                    when AgentValueDomains.IsIdentifier(terminal.CallId) &&
                        identifiers.Add(terminal.CallId) &&
                        StringComparer.Ordinal.Equals(
                            terminal.Name,
                            AgentToolRegistry.FinishReviewName) &&
                        TryUtf8Length(
                            terminal.ArgumentsJson,
                            1,
                            AgentLimits.TerminalBytes,
                            out _) &&
                        AgentToolArguments.TryFinishReview(
                            terminal.ArgumentsJson,
                            out var arguments) &&
                        StringComparer.Ordinal.Equals(
                            terminal.ArgumentsSha256,
                            AgentCanonical.HashDomain(
                                AgentCanonical.TerminalDomain,
                                arguments!.CanonicalBytes)):
                    if (terminalCall is not null)
                    {
                        return false;
                    }

                    terminalCall = terminal;
                    break;
                default:
                    return false;
            }
        }

        if (terminalCall is not null)
        {
            if (calls.Count != 0)
            {
                return false;
            }
        }
        else if (calls.Count is < 1 or > AgentLimits.ToolCallsPerResponse)
        {
            return false;
        }

        normalCalls = calls.ToImmutable();
        return true;
    }

    private static bool TryValidateNormalCall(
        AgentSessionToolCallContent call)
    {
        if (!TryUtf8Length(
                call.ArgumentsJson,
                1,
                AgentLimits.ToolArgumentsBytes,
                out _))
        {
            return false;
        }

        byte[] argumentsBytes;
        try
        {
            argumentsBytes = StrictUtf8.GetBytes(call.ArgumentsJson);
        }
        catch (EncoderFallbackException)
        {
            return false;
        }

        return call.Name switch
        {
            AgentToolRegistry.ReadFileName =>
                AgentToolArguments.TryReadFile(
                    call.ArgumentsJson,
                    out var read) &&
                argumentsBytes.AsSpan().SequenceEqual(
                    read!.CanonicalBytes),
            AgentToolRegistry.SearchTextName =>
                AgentToolArguments.TrySearchTextCanonical(
                    call.ArgumentsJson,
                    out var search) &&
                argumentsBytes.AsSpan().SequenceEqual(
                    search!.CanonicalBytes),
            _ => false,
        };
    }

    private static bool TryValidateTerminal(
        ReviewedIdentity reviewedIdentity,
        AgentSessionAssistantMessageRecord message,
        AgentSessionTerminalCallContent terminal,
        AgentSessionReviewOutcomeRecord outcome,
        IReadOnlyList<AgentObservation> observations)
    {
        if (!StringComparer.Ordinal.Equals(
                outcome.TerminalMessageId,
                message.Id) ||
            !StringComparer.Ordinal.Equals(
                outcome.TerminalCallId,
                terminal.CallId) ||
            !StringComparer.Ordinal.Equals(
                outcome.TerminalSha256,
                terminal.ArgumentsSha256) ||
            !AgentToolArguments.TryFinishReview(
                terminal.ArgumentsJson,
                out var arguments) ||
            !TerminalReviewValidator.TryValidate(
                arguments!,
                reviewedIdentity,
                observations,
                out var review) ||
            review is null ||
            !StringComparer.Ordinal.Equals(
                review.TerminalSha256,
                terminal.ArgumentsSha256) ||
            !StringComparer.Ordinal.Equals(
                review.TerminalSha256,
                outcome.TerminalSha256) ||
            !StringComparer.Ordinal.Equals(
                review.Summary,
                outcome.Summary))
        {
            return false;
        }

        using var document = JsonDocument.Parse(review.CanonicalBytes);
        var findingsJson = document.RootElement
            .GetProperty("findings")
            .GetRawText();
        return StringComparer.Ordinal.Equals(
            findingsJson,
            outcome.FindingsJson);
    }

    private static bool TryReadObservation(
        AgentSessionToolCallContent call,
        AgentSessionToolResultRecord stored,
        ReviewedIdentity expectedIdentity,
        byte[] canonical,
        out AgentObservation? observation)
    {
        observation = null;
        try
        {
            switch (call.Name)
            {
                case AgentToolRegistry.ReadFileName:
                    if (!AgentToolArguments.TryReadFile(
                            call.ArgumentsJson,
                            out var readArguments))
                    {
                        return false;
                    }

                    var readDto = JsonSerializer.Deserialize(
                        canonical,
                        AgentSessionJsonContext.Default
                            .AgentSessionReadFileResultDto);
                    if (readDto?.ReviewedIdentity is null ||
                        !TryIdentity(
                            readDto.ReviewedIdentity,
                            out var readIdentity) ||
                        readDto.Status is null ||
                        readDto.Path is null ||
                        readDto.RawSha256 is null ||
                        readDto.Lines is null ||
                        readDto.ObservationId is null ||
                        readDto.Lines.Any(line =>
                            line is null || line.Text is null))
                    {
                        return false;
                    }

                    var readLines = readDto.Lines.Select(line =>
                        new ReadFileLine(line.Line, line.Text!))
                        .ToImmutableArray();
                    var readResult = new ReadFileResult(
                        readDto.Status,
                        readIdentity!,
                        readDto.Path,
                        readDto.RawSha256,
                        readDto.RequestedStartLine,
                        readDto.RequestedLineCount,
                        readDto.ReturnedStartLine,
                        readDto.ReturnedEndLine,
                        readLines,
                        readDto.Truncated,
                        readDto.TruncationReason,
                        readDto.ObservationId);
                    var readReturned = readLines.Length == 0
                        ? EmptyReturnedLines()
                        : EmptyReturnedLines().Add(
                            readResult.Path,
                            readLines.Select(line => line.Line)
                                .ToImmutableHashSet());
                    if (readResult.ReviewedIdentity != expectedIdentity ||
                        readResult.Status is not ("ok" or "start_after_eof") ||
                        !StringComparer.Ordinal.Equals(
                            readResult.Path,
                            readArguments!.Path) ||
                        readResult.RequestedStartLine !=
                            readArguments.StartLine ||
                        readResult.RequestedLineCount !=
                            readArguments.LineCount ||
                        !HasValidReadResultSemantics(
                            readArguments,
                            readResult) ||
                        !IsLowerHex(readResult.RawSha256, 64) ||
                        !IsLowerHex(readResult.ObservationId, 64) ||
                        !canonical.AsSpan().SequenceEqual(
                            ReadFileResultWriter.Write(readResult)) ||
                        !StringComparer.Ordinal.Equals(
                            readResult.ObservationId,
                            AgentCanonical.HashDomain(
                                AgentCanonical.ReadObservationDomain,
                                ReadFileResultWriter.Write(
                                    readResult with
                                    {
                                        ObservationId = null,
                                    },
                                    includeObservationId: false))) ||
                        !StringComparer.Ordinal.Equals(
                            stored.ObservationId,
                            readResult.ObservationId))
                    {
                        return false;
                    }

                    observation = new AgentObservation(
                        readResult.ObservationId!,
                        expectedIdentity,
                        readReturned);
                    return true;

                case AgentToolRegistry.SearchTextName:
                    if (!AgentToolArguments.TrySearchTextCanonical(
                            call.ArgumentsJson,
                            out var searchArguments))
                    {
                        return false;
                    }

                    var searchDto = JsonSerializer.Deserialize(
                        canonical,
                        AgentSessionJsonContext.Default
                            .AgentSessionSearchTextResultDto);
                    if (searchDto?.ReviewedIdentity is null ||
                        !TryIdentity(
                            searchDto.ReviewedIdentity,
                            out var searchIdentity) ||
                        searchDto.Status is null ||
                        searchDto.QuerySha256 is null ||
                        searchDto.Matches is null ||
                        searchDto.ObservationId is null ||
                        searchDto.Matches.Any(match =>
                            match is null ||
                            match.Path is null ||
                            match.RawSha256 is null ||
                            match.Text is null))
                    {
                        return false;
                    }

                    var matches = searchDto.Matches.Select(match =>
                        new SearchMatch(
                            match.Path!,
                            match.RawSha256!,
                            match.Line,
                            match.Text!))
                        .ToImmutableArray();
                    var searchResult = new SearchTextResult(
                        searchDto.Status,
                        searchIdentity!,
                        searchDto.QuerySha256,
                        searchDto.Path,
                        searchDto.FilesScanned,
                        searchDto.RawBytesScanned,
                        searchDto.SkippedInvalidUtf8,
                        searchDto.SkippedBinary,
                        searchDto.SkippedLoneCr,
                        searchDto.SkippedOversized,
                        matches,
                        searchDto.Truncated,
                        searchDto.TruncationReason,
                        searchDto.ObservationId);
                    var searchReturned = matches
                        .GroupBy(match => match.Path, StringComparer.Ordinal)
                        .ToImmutableDictionary(
                            group => group.Key,
                            group => group.Select(match => match.Line)
                                .ToImmutableHashSet(),
                            StringComparer.Ordinal);
                    if (searchResult.ReviewedIdentity != expectedIdentity ||
                        !StringComparer.Ordinal.Equals(
                            searchResult.Status,
                            "ok") ||
                        !StringComparer.Ordinal.Equals(
                            searchResult.QuerySha256,
                            AgentCanonical.QuerySha256(
                                searchArguments!.Query)) ||
                        !StringComparer.Ordinal.Equals(
                            searchResult.Path,
                            searchArguments.Path) ||
                        searchResult.FilesScanned < 0 ||
                        searchResult.RawBytesScanned < 0 ||
                        searchResult.SkippedInvalidUtf8 < 0 ||
                        searchResult.SkippedBinary < 0 ||
                        searchResult.SkippedLoneCr < 0 ||
                        searchResult.SkippedOversized < 0 ||
                        !HasValidSearchResultSemantics(
                            searchArguments,
                            searchResult) ||
                        !IsLowerHex(searchResult.ObservationId, 64) ||
                        !canonical.AsSpan().SequenceEqual(
                            SearchTextResultWriter.Write(searchResult)) ||
                        !StringComparer.Ordinal.Equals(
                            searchResult.ObservationId,
                            AgentCanonical.HashDomain(
                                AgentCanonical.SearchObservationDomain,
                                SearchTextResultWriter.Write(
                                    searchResult with
                                    {
                                        ObservationId = null,
                                    },
                                    includeObservationId: false))) ||
                        !StringComparer.Ordinal.Equals(
                            stored.ObservationId,
                            searchResult.ObservationId))
                    {
                        return false;
                    }

                    observation = new AgentObservation(
                        searchResult.ObservationId!,
                        expectedIdentity,
                        searchReturned);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception exception) when (
            exception is JsonException or
            NotSupportedException or
            Rfc8785CanonicalizationException)
        {
            return false;
        }
    }

    private static bool HasValidReadResultSemantics(
        ReadFileArguments arguments,
        ReadFileResult result)
    {
        if (result.Lines.Length > arguments.LineCount ||
            result.Lines.Length > AgentLimits.ReadFileLines)
        {
            return false;
        }

        if (StringComparer.Ordinal.Equals(
                result.Status,
                "start_after_eof"))
        {
            return result.ReturnedStartLine is null &&
                result.ReturnedEndLine is null &&
                result.Lines.Length == 0 &&
                !result.Truncated &&
                result.TruncationReason is null;
        }

        if (!StringComparer.Ordinal.Equals(result.Status, "ok"))
        {
            return false;
        }

        if (result.Lines.Length == 0)
        {
            return result.ReturnedStartLine is null &&
                result.ReturnedEndLine is null &&
                result.Truncated &&
                StringComparer.Ordinal.Equals(
                    result.TruncationReason,
                    "result_bytes");
        }

        if (result.ReturnedStartLine != arguments.StartLine)
        {
            return false;
        }

        int expectedEnd;
        try
        {
            expectedEnd = checked(
                arguments.StartLine + result.Lines.Length - 1);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (result.ReturnedEndLine != expectedEnd)
        {
            return false;
        }

        for (var index = 0; index < result.Lines.Length; index++)
        {
            int expectedLine;
            try
            {
                expectedLine = checked(arguments.StartLine + index);
            }
            catch (OverflowException)
            {
                return false;
            }

            var line = result.Lines[index];
            if (line.Line != expectedLine ||
                !AgentValueDomains.IsUtf8(
                    line.Text,
                    0,
                    AgentLimits.ContentBytes) ||
                line.Text.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                return false;
            }
        }

        if (!result.Truncated)
        {
            return result.TruncationReason is null;
        }

        return result.TruncationReason switch
        {
            "line_count" =>
                result.Lines.Length == arguments.LineCount,
            "result_bytes" =>
                result.Lines.Length < arguments.LineCount,
            _ => false,
        };
    }

    private static bool HasValidSearchResultSemantics(
        SearchTextArguments arguments,
        SearchTextResult result)
    {
        if (!StringComparer.Ordinal.Equals(result.Status, "ok") ||
            result.FilesScanned is < 0 or > AgentLimits.SearchFiles ||
            result.RawBytesScanned is < 0 or > AgentLimits.SearchRawBytes ||
            result.SkippedInvalidUtf8 < 0 ||
            result.SkippedBinary < 0 ||
            result.SkippedLoneCr < 0 ||
            result.SkippedOversized < 0 ||
            result.Matches.Length > AgentLimits.SearchMatches)
        {
            return false;
        }

        long skipped;
        try
        {
            skipped = checked(
                (long)result.SkippedInvalidUtf8 +
                result.SkippedBinary +
                result.SkippedLoneCr +
                result.SkippedOversized);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (skipped > result.FilesScanned)
        {
            return false;
        }

        long rawByteCapacity;
        try
        {
            rawByteCapacity = checked(
                (long)(result.FilesScanned - result.SkippedOversized) *
                AgentLimits.SearchFileBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (result.RawBytesScanned > rawByteCapacity ||
            (result.FilesScanned == 0 &&
                (result.RawBytesScanned != 0 ||
                    skipped != 0 ||
                    result.Matches.Length != 0 ||
                    result.Truncated ||
                    result.TruncationReason is not null)) ||
            (result.Matches.Length != 0 &&
                (result.FilesScanned == 0 ||
                    result.RawBytesScanned == 0)))
        {
            return false;
        }

        if (arguments.Path is not null &&
            (result.FilesScanned != 1 ||
                result.RawBytesScanned > AgentLimits.SearchFileBytes ||
                skipped != 0))
        {
            return false;
        }

        var matchedPaths = new HashSet<string>(StringComparer.Ordinal);
        var rawHashes = new Dictionary<string, string>(
            StringComparer.Ordinal);
        string? priorPath = null;
        var priorLine = 0;
        foreach (var match in result.Matches)
        {
            if (!RepositoryPath.IsValid(match.Path) ||
                !IsLowerHex(match.RawSha256, 64) ||
                match.Line < 1 ||
                !AgentValueDomains.IsUtf8(
                    match.Text,
                    0,
                    AgentLimits.ContentBytes) ||
                match.Text.IndexOfAny(['\0', '\r', '\n']) >= 0 ||
                !match.Text.Contains(
                    arguments.Query,
                    StringComparison.Ordinal) ||
                (arguments.Path is not null &&
                    !StringComparer.Ordinal.Equals(
                        match.Path,
                        arguments.Path)) ||
                (rawHashes.TryGetValue(match.Path, out var rawHash) &&
                    !StringComparer.Ordinal.Equals(
                        rawHash,
                        match.RawSha256)))
            {
                return false;
            }

            if (priorPath is not null)
            {
                var pathOrder = StringComparer.Ordinal.Compare(
                    priorPath,
                    match.Path);
                if (pathOrder > 0 ||
                    (pathOrder == 0 && match.Line <= priorLine))
                {
                    return false;
                }
            }

            matchedPaths.Add(match.Path);
            rawHashes[match.Path] = match.RawSha256;
            priorPath = match.Path;
            priorLine = match.Line;
        }

        if (matchedPaths.Count + skipped > result.FilesScanned)
        {
            return false;
        }

        if (!result.Truncated)
        {
            return result.TruncationReason is null;
        }

        return result.TruncationReason switch
        {
            "files_scanned" =>
                arguments.Path is null &&
                result.FilesScanned == AgentLimits.SearchFiles,
            "bytes_scanned" =>
                arguments.Path is null &&
                result.RawBytesScanned >
                    AgentLimits.SearchRawBytes -
                    AgentLimits.SearchFileBytes,
            "matches" =>
                result.Matches.Length == AgentLimits.SearchMatches,
            "result_bytes" =>
                result.Matches.Length < AgentLimits.SearchMatches,
            _ => false,
        };
    }

    private static bool TryValidateContinuation(
        AgentSessionCompletedRun run,
        IReadOnlyDictionary<string, AgentSessionAssistantMessageRecord>
            assistantMessages,
        IAgentContinuationCodec codec,
        HashSet<string> identifiers,
        ref long aggregateBytes,
        out string failureCode)
    {
        failureCode = AgentSessionCodes.ContinuationInvalid;
        var continuation = run.Continuation;
        if (!StringComparer.Ordinal.Equals(
                continuation.CodecId,
                codec.CodecId) ||
            !StringComparer.Ordinal.Equals(
                continuation.CodecDiscriminator,
                codec.CodecDiscriminator) ||
            continuation.Items.Length > AgentLimits.PartsTotal)
        {
            return false;
        }

        var slots = new HashSet<(string MessageId, int Position)>();
        foreach (var item in continuation.Items)
        {
            if (!AgentValueDomains.IsIdentifier(item.ItemId) ||
                !identifiers.Add(item.ItemId) ||
                item.Encoding is not ("utf8" or "base64") ||
                item.PayloadBytes.Length > AgentLimits.ContinuationItemBytes ||
                !IsLowerHex(item.PayloadSha256, 64) ||
                !StringComparer.Ordinal.Equals(
                    item.PayloadSha256,
                    AgentSessionCodec.ContinuationPayloadSha256(
                        continuation.CodecId,
                        continuation.CodecDiscriminator,
                        item.ItemId,
                        item.Encoding,
                        item.PayloadBytes)) ||
                !AgentValueDomains.IsIdentifier(item.MessageId) ||
                item.ContentPosition is < 0 or >= AgentLimits.PartsPerMessage ||
                (item.AssociatedCallId is not null &&
                    !AgentValueDomains.IsIdentifier(item.AssociatedCallId)) ||
                !slots.Add((item.MessageId, item.ContentPosition)) ||
                !assistantMessages.TryGetValue(
                    item.MessageId,
                    out var message) ||
                item.ContentPosition >= message.Contents.Length ||
                message.Contents[item.ContentPosition] is not
                    AgentSessionContinuationSlotContent slot ||
                !StringComparer.Ordinal.Equals(
                    slot.ContinuationItemId,
                    item.ItemId) ||
                (item.AssociatedCallId is not null &&
                    !message.Contents.Any(content =>
                        content is AgentSessionToolCallContent call &&
                            StringComparer.Ordinal.Equals(
                                call.CallId,
                                item.AssociatedCallId) ||
                        content is AgentSessionTerminalCallContent terminal &&
                            StringComparer.Ordinal.Equals(
                                terminal.CallId,
                                item.AssociatedCallId))) ||
                !AgentContinuationCodecBoundary.TryDecode(
                    codec,
                    item.Encoding,
                    item.PayloadBytes,
                    out var value) ||
                value is null ||
                !AgentValueDomains.IsUtf8(
                    value.Readable,
                    0,
                    AgentLimits.ContentBytes) ||
                !AgentValueDomains.IsUtf8(
                    value.Opaque,
                    0,
                    AgentLimits.ContentBytes) ||
                !AgentValueDomains.IsUtf8(
                    value.Framing,
                    1,
                    AgentLimits.ContentBytes))
            {
                return false;
            }

            try
            {
                aggregateBytes = checked(
                    aggregateBytes + item.PayloadBytes.Length);
            }
            catch (OverflowException)
            {
                return false;
            }

            if (aggregateBytes > AgentLimits.ContinuationTotalBytes)
            {
                return false;
            }
        }

        var allSlots = assistantMessages.Values
            .SelectMany(message => message.Contents.Select(content =>
                (message.Id, Content: content)))
            .Where(entry =>
                entry.Content is AgentSessionContinuationSlotContent)
            .Select(entry =>
                (entry.Id, entry.Content.ContentPosition))
            .ToHashSet();
        return allSlots.SetEquals(slots);
    }

    private static bool HasExactClassification(AgentSessionRecord record) =>
        record switch
        {
            AgentSessionReviewContextRecord =>
                StringComparer.Ordinal.Equals(record.Role, "user") &&
                StringComparer.Ordinal.Equals(record.Framing, "text") &&
                StringComparer.Ordinal.Equals(
                    record.Classification,
                    "untrusted_review_data"),
            AgentSessionAssistantMessageRecord =>
                StringComparer.Ordinal.Equals(record.Role, "assistant") &&
                StringComparer.Ordinal.Equals(
                    record.Framing,
                    "provider_message") &&
                StringComparer.Ordinal.Equals(
                    record.Classification,
                    "provider_data"),
            AgentSessionToolResultRecord =>
                StringComparer.Ordinal.Equals(record.Role, "tool") &&
                StringComparer.Ordinal.Equals(
                    record.Framing,
                    "tool_result") &&
                StringComparer.Ordinal.Equals(
                    record.Classification,
                    "untrusted_tool_data"),
            AgentSessionReviewOutcomeRecord =>
                StringComparer.Ordinal.Equals(record.Role, "assistant") &&
                StringComparer.Ordinal.Equals(
                    record.Framing,
                    "validated_terminal") &&
                StringComparer.Ordinal.Equals(
                    record.Classification,
                    "validated_terminal_data"),
            _ => false,
        };

    private static bool TryUtf8Length(
        string value,
        int minimum,
        int maximum,
        out byte[]? bytes)
    {
        bytes = null;
        try
        {
            bytes = StrictUtf8.GetBytes(value);
            return bytes.Length >= minimum && bytes.Length <= maximum;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool TryIdentity(
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
        return identity.IsValid();
    }

    private static ImmutableDictionary<string, ImmutableHashSet<int>>
        EmptyReturnedLines() =>
        ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
            .WithComparers(StringComparer.Ordinal);
}
