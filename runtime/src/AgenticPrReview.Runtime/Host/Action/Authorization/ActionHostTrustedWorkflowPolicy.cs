using System.Text;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal sealed record ActionHostTrustedWorkflowEvidence(
    string ConcurrencyGroup,
    string ActionReference,
    string PreflightJobId)
{
    internal ActionHostSameHeadContinuationPolicy SameHeadContinuationPolicy
        { get; init; } =
        ActionHostSameHeadContinuationPolicy.ReuseAcceptedState;

    internal ActionHostPayloadContinuityMode PayloadContinuityMode
        { get; init; } = ActionHostPayloadContinuityMode.ExactExecutable;

    internal string? PayloadSourceCommit { get; init; }

    internal string? PayloadSourceTree { get; init; }
}

internal enum ActionHostPayloadContinuityMode
{
    ExactExecutable = 0,
    ExactSource,
}

internal enum ActionHostSameHeadContinuationPolicy
{
    ReuseAcceptedState = 0,
    ContinueAcrossWorkflowRuns,
}

internal enum ActionHostTrustedWorkflowFailure
{
    None = 0,
    SourceInvalid,
    YamlInvalid,
    TriggerInvalid,
    PermissionInvalid,
    ConcurrencyInvalid,
    JobInvalid,
}

internal static class ActionHostTrustedWorkflowPolicy
{
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    internal static bool TryValidate(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        string actionSourceSha,
        string payloadSha256,
        out ActionHostTrustedWorkflowEvidence? evidence)
    {
        return TryValidate(
            source,
            policy,
            actionSourceSha,
            payloadSha256,
            out evidence,
            out _);
    }

    internal static bool TryValidate(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        string actionSourceSha,
        string payloadSha256,
        out ActionHostTrustedWorkflowEvidence? evidence,
        out ActionHostTrustedWorkflowFailure failure)
    {
        return TryValidateExact(
            source,
            policy,
            actionSourceSha,
            ActionHostTrustedWorkflowContract.Render(
                actionSourceSha,
                payloadSha256),
            out evidence,
            out failure);
    }

    internal static bool TryValidateExact(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        string actionSourceSha,
        string expectedRender,
        out ActionHostTrustedWorkflowEvidence? evidence,
        out ActionHostTrustedWorkflowFailure failure)
    {
        evidence = null;
        failure = ActionHostTrustedWorkflowFailure.SourceInvalid;
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(expectedRender);
        if (source is null ||
            source.Length is <= 0 or > 256 * 1024 ||
            source.Length >= 3 &&
                source[0] == 0xef &&
                source[1] == 0xbb &&
                source[2] == 0xbf)
        {
            return false;
        }

        try
        {
            var text = StrictUtf8.GetString(source);
            var normalizedText = text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
            if (normalizedText.Contains('\r'))
            {
                failure = ActionHostTrustedWorkflowFailure.YamlInvalid;
                return false;
            }

            if (!PolicyYamlParser.TryParse(text, out var root) || root is null)
            {
                failure = ActionHostTrustedWorkflowFailure.YamlInvalid;
                return false;
            }

            if (!StringComparer.Ordinal.Equals(
                    normalizedText,
                    expectedRender))
            {
                failure = ActionHostTrustedWorkflowFailure.JobInvalid;
                return false;
            }

            evidence = new(
                ActionHostAuthorizationPolicy.ConcurrencyGroup,
                policy.ActionReference(actionSourceSha),
                ActionHostAuthorizationPolicy.PreflightJobId);
            failure = ActionHostTrustedWorkflowFailure.None;
            return true;
        }
        catch (Exception exception) when (exception is DecoderFallbackException or
            InvalidOperationException or
            OverflowException)
        {
            return false;
        }
    }

    private sealed class PolicyNode
    {
        internal Dictionary<string, PolicyNode>? Map { get; init; }

        internal List<PolicyNode>? Sequence { get; init; }

        internal string? Scalar { get; init; }

    }

    private static class PolicyYamlParser
    {
        internal static bool TryParse(string text, out PolicyNode? root)
        {
            root = null;
            var normalizedText = text.Replace(
                "\r\n",
                "\n",
                StringComparison.Ordinal);
            if (normalizedText.Contains('\r'))
            {
                return false;
            }

            var rawLines = normalizedText.Split('\n');
            var lines = new List<PolicyLine>(rawLines.Length);
            for (var rawIndex = 0; rawIndex < rawLines.Length; rawIndex++)
            {
                var rawLine = rawLines[rawIndex];
                if (rawLine.Length >
                        ActionHostAuthorizationBounds.MaximumYamlLineCharacters ||
                    rawLine.Contains('\t'))
                {
                    return false;
                }

                var indent = rawLine.TakeWhile(static character =>
                    character == ' ').Count();
                var content = StripComment(rawLine[indent..]).TrimEnd();
                if (content.Length == 0)
                {
                    continue;
                }

                if (indent % 2 != 0 || indent > 64)
                {
                    return false;
                }

                string? blockScalar = null;
                if (IsBlockScalarDeclaration(content))
                {
                    var body = new StringBuilder();
                    var bodyIndent = indent + 2;
                    var bodyIndex = rawIndex + 1;
                    for (; bodyIndex < rawLines.Length; bodyIndex++)
                    {
                        var bodyLine = rawLines[bodyIndex];
                        var actualIndent = bodyLine.TakeWhile(
                            static character => character == ' ').Count();
                        if (bodyLine.Length != 0 && actualIndent <= indent)
                        {
                            break;
                        }

                        if (bodyLine.Length != 0 && actualIndent < bodyIndent ||
                            bodyLine.Contains('\t') ||
                            bodyLine.Length > ActionHostAuthorizationBounds
                                .MaximumYamlLineCharacters)
                        {
                            return false;
                        }

                        body.Append(bodyLine.Length == 0
                            ? string.Empty
                            : bodyLine[Math.Min(bodyIndent, bodyLine.Length)..]);
                        body.Append('\n');
                    }

                    if (body.Length == 0)
                    {
                        return false;
                    }

                    blockScalar = body.ToString();
                    rawIndex = bodyIndex - 1;
                }

                lines.Add(new(indent, content, blockScalar));
            }

            if (lines.Count == 0 ||
                lines.Count > ActionHostAuthorizationBounds.MaximumYamlNodes ||
                lines[0].Indent != 0)
            {
                return false;
            }

            var index = 0;
            if (!TryParseBlock(lines, ref index, 0, out root) ||
                root?.Map is null ||
                index != lines.Count)
            {
                root = null;
                return false;
            }

            return true;
        }

        private static bool TryParseBlock(
            IReadOnlyList<PolicyLine> lines,
            ref int index,
            int indent,
            out PolicyNode? node)
        {
            node = null;
            if (index >= lines.Count || lines[index].Indent != indent)
            {
                return false;
            }

            return lines[index].Content.StartsWith("- ", StringComparison.Ordinal)
                ? TryParseSequence(lines, ref index, indent, out node)
                : TryParseMap(lines, ref index, indent, out node);
        }

        private static bool TryParseMap(
            IReadOnlyList<PolicyLine> lines,
            ref int index,
            int indent,
            out PolicyNode? node)
        {
            var map = new Dictionary<string, PolicyNode>(StringComparer.Ordinal);
            node = new() { Map = map };
            while (index < lines.Count && lines[index].Indent == indent &&
                !lines[index].Content.StartsWith("- ", StringComparison.Ordinal))
            {
                var current = lines[index];
                if (!TrySplitMapping(
                        current.Content,
                        out var key,
                        out var value) ||
                    !map.TryAdd(key!, new PolicyNode()))
                {
                    return false;
                }

                index++;
                PolicyNode? child;
                if (current.BlockScalar is not null)
                {
                    child = new PolicyNode { Scalar = current.BlockScalar };
                }
                else if (value is not null)
                {
                    if (!TryScalar(value, out child))
                    {
                        return false;
                    }
                }
                else if (index < lines.Count && lines[index].Indent > indent)
                {
                    if (lines[index].Indent != indent + 2 ||
                        !TryParseBlock(lines, ref index, indent + 2, out child))
                    {
                        return false;
                    }
                }
                else
                {
                    child = new PolicyNode { Scalar = string.Empty };
                }

                if (child is null)
                {
                    return false;
                }

                map[key!] = child;
            }

            return map.Count != 0;
        }

        private static bool TryParseSequence(
            IReadOnlyList<PolicyLine> lines,
            ref int index,
            int indent,
            out PolicyNode? node)
        {
            var sequence = new List<PolicyNode>();
            node = new() { Sequence = sequence };
            while (index < lines.Count &&
                lines[index].Indent == indent &&
                lines[index].Content.StartsWith("- ", StringComparison.Ordinal))
            {
                var current = lines[index];
                var content = current.Content[2..].Trim();
                if (content.Length == 0)
                {
                    return false;
                }

                index++;
                if (TrySplitMapping(content, out var key, out var value))
                {
                    var map = new Dictionary<string, PolicyNode>(
                        StringComparer.Ordinal);
                    if (!(current.BlockScalar is not null
                            ? AssignScalar(current.BlockScalar, out var first)
                            : TryScalarOrNested(
                                lines,
                                ref index,
                                indent,
                                value,
                                out first)) ||
                        !map.TryAdd(key!, first!))
                    {
                        return false;
                    }

                    while (index < lines.Count &&
                        lines[index].Indent == indent + 2 &&
                        !lines[index].Content.StartsWith(
                            "- ",
                            StringComparison.Ordinal))
                    {
                        var continuationLine = lines[index];
                        if (!TrySplitMapping(
                                continuationLine.Content,
                                out var continuationKey,
                                out var continuationValue) ||
                            map.ContainsKey(continuationKey!))
                        {
                            return false;
                        }

                        index++;
                        if (!(continuationLine.BlockScalar is not null
                                ? AssignScalar(
                                    continuationLine.BlockScalar,
                                    out var continuation)
                                : TryScalarOrNested(
                                    lines,
                                    ref index,
                                    indent + 2,
                                    continuationValue,
                                    out continuation)))
                        {
                            return false;
                        }

                        map.Add(continuationKey!, continuation!);
                    }

                    sequence.Add(new PolicyNode { Map = map });
                }
                else
                {
                    if (!TryScalar(content, out var scalar) ||
                        index < lines.Count && lines[index].Indent > indent)
                    {
                        return false;
                    }

                    sequence.Add(scalar!);
                }
            }

            return sequence.Count != 0;
        }

        private static bool TryScalarOrNested(
            IReadOnlyList<PolicyLine> lines,
            ref int index,
            int parentIndent,
            string? value,
            out PolicyNode? node)
        {
            node = null;
            if (value is not null)
            {
                return TryScalar(value, out node);
            }

            if (index >= lines.Count || lines[index].Indent != parentIndent + 2)
            {
                node = new PolicyNode { Scalar = string.Empty };
                return true;
            }

            return TryParseBlock(lines, ref index, parentIndent + 2, out node);
        }

        private static bool TrySplitMapping(
            string content,
            out string? key,
            out string? value)
        {
            key = null;
            value = null;
            var separator = FindUnquotedColon(content);
            if (separator <= 0)
            {
                return false;
            }

            var candidateKey = content[..separator].Trim();
            if (!IsPlainKey(candidateKey) || candidateKey == "<<")
            {
                return false;
            }

            var candidateValue = content[(separator + 1)..].Trim();
            key = candidateKey;
            value = candidateValue.Length == 0 ? null : candidateValue;
            return true;
        }

        private static bool TryScalar(string value, out PolicyNode? node)
        {
            node = null;
            var normalized = NormalizeScalar(value);
            if (normalized is null ||
                normalized.StartsWith('*') ||
                normalized.StartsWith('&') ||
                normalized.StartsWith('!') ||
                normalized.StartsWith('[') ||
                normalized.StartsWith('{') && normalized != "{}")
            {
                return false;
            }

            node = new PolicyNode { Scalar = normalized };
            return true;
        }

        private static bool AssignScalar(
            string value,
            out PolicyNode? node)
        {
            node = new PolicyNode { Scalar = value };
            return true;
        }

        private static string? NormalizeScalar(string value)
        {
            value = value.Trim();
            if (value is "|" or "|-" or "|+" or ">" or ">-" or ">+")
            {
                return "<block-scalar>";
            }

            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            {
                return value[1..^1].Replace("''", "'", StringComparison.Ordinal);
            }

            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                var inner = value[1..^1];
                return inner.Contains('\\') ? null : inner;
            }

            return value;
        }

        private static bool IsPlainKey(string key) =>
            key.Length is > 0 and <= 255 &&
            key.All(static character =>
                char.IsLetterOrDigit(character) ||
                character is '-' or '_' or '.');

        private static int FindUnquotedColon(string value)
        {
            var single = false;
            var doubleQuoted = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\'' && !doubleQuoted)
                {
                    single = !single;
                }
                else if (character == '"' && !single)
                {
                    doubleQuoted = !doubleQuoted;
                }
                else if (character == ':' && !single && !doubleQuoted)
                {
                    return index;
                }
            }

            return -1;
        }

        private static string StripComment(string value)
        {
            var single = false;
            var doubleQuoted = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character == '\'' && !doubleQuoted)
                {
                    single = !single;
                }
                else if (character == '"' && !single)
                {
                    doubleQuoted = !doubleQuoted;
                }
                else if (character == '#' && !single && !doubleQuoted &&
                    (index == 0 || char.IsWhiteSpace(value[index - 1])))
                {
                    return value[..index];
                }
            }

            return value;
        }

        private static bool IsBlockScalarDeclaration(string content)
        {
            var separator = FindUnquotedColon(content);
            if (separator < 0)
            {
                return false;
            }

            var value = content[(separator + 1)..].Trim();
            return value is "|" or "|-" or "|+" or ">" or ">-" or ">+";
        }

        private readonly record struct PolicyLine(
            int Indent,
            string Content,
            string? BlockScalar);
    }
}
