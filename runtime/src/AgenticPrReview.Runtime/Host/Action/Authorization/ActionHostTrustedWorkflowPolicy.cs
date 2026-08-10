using System.Text;

namespace AgenticPrReview.Runtime.ActionHost.Authorization;

internal sealed record ActionHostTrustedWorkflowEvidence(
    string ConcurrencyGroup,
    string ActionReference,
    string PreflightJobId);

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
        out ActionHostTrustedWorkflowEvidence? evidence)
    {
        return TryValidate(
            source,
            policy,
            actionSourceSha,
            out evidence,
            out _);
    }

    internal static bool TryValidate(
        byte[]? source,
        ActionHostAuthorizationPolicy policy,
        string actionSourceSha,
        out ActionHostTrustedWorkflowEvidence? evidence,
        out ActionHostTrustedWorkflowFailure failure)
    {
        evidence = null;
        failure = ActionHostTrustedWorkflowFailure.SourceInvalid;
        ArgumentNullException.ThrowIfNull(policy);
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
            if (!PolicyYamlParser.TryParse(text, out var root) || root is null)
            {
                failure = ActionHostTrustedWorkflowFailure.YamlInvalid;
                return false;
            }

            if (!ValidateTriggers(root))
            {
                failure = ActionHostTrustedWorkflowFailure.TriggerInvalid;
                return false;
            }

            if (!IsEmptyMap(root.Value("permissions")))
            {
                failure = ActionHostTrustedWorkflowFailure.PermissionInvalid;
                return false;
            }

            if (!ValidateConcurrency(root.Value("concurrency")))
            {
                failure = ActionHostTrustedWorkflowFailure.ConcurrencyInvalid;
                return false;
            }

            if (!ValidateJobs(
                    root.Value("jobs"),
                    policy.ActionReference(actionSourceSha)))
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

    private static bool ValidateTriggers(PolicyNode root)
    {
        var triggers = root.Value("on");
        if (triggers?.Map is null ||
            !triggers.HasOnly("workflow_run", "workflow_dispatch"))
        {
            return false;
        }

        var workflowRun = triggers.Value("workflow_run");
        var workflows = workflowRun?.Value("workflows")?.Sequence;
        var types = workflowRun?.Value("types")?.Sequence;
        if (workflowRun?.Map is null ||
            !workflowRun.HasOnly("workflows", "types") ||
            workflows is not { Count: 1 } ||
            types is not { Count: 1 } ||
            !IsScalar(workflows[0], ActionHostAuthorizationPolicy
                .TriggerWorkflowName) ||
            !IsScalar(types[0], "completed"))
        {
            return false;
        }

        var dispatch = triggers.Value("workflow_dispatch");
        var inputs = dispatch?.Value("inputs");
        var pullRequest = inputs?.Value("pr-number");
        return dispatch?.Map is not null &&
            dispatch.HasOnly("inputs") &&
            inputs?.Map is { Count: 1 } &&
            pullRequest?.Map is not null &&
            IsScalar(pullRequest.Value("required"), "true") &&
            IsScalar(pullRequest.Value("type"), "number") &&
            pullRequest.Value("default") is null &&
            pullRequest.Map.Keys.All(static key =>
                key is "description" or "required" or "type");
    }

    private static bool ValidateConcurrency(PolicyNode? concurrency) =>
        concurrency?.Map is not null &&
        concurrency.HasOnly("group", "cancel-in-progress") &&
        IsScalar(
            concurrency.Value("group"),
            ActionHostAuthorizationPolicy.ConcurrencyGroup) &&
        IsScalar(concurrency.Value("cancel-in-progress"), "false");

    private static bool ValidateJobs(
        PolicyNode? jobs,
        string expectedActionReference)
    {
        if (jobs?.Map is null ||
            jobs.Value(ActionHostAuthorizationPolicy.PreflightJobId) is not
                { } preflight ||
            jobs.Value(ActionHostAuthorizationPolicy.WorkflowRunJobId) is not
                { } workflowRun ||
            jobs.Value(
                ActionHostAuthorizationPolicy.WorkflowDispatchJobId) is not
                { } workflowDispatch ||
            !ValidatePreflight(preflight) ||
            !ValidatePrivilegedJob(
                workflowRun,
                ActionHostAuthorizationPolicy.WorkflowRunGuard,
                expectedActionReference) ||
            !ValidatePrivilegedJob(
                workflowDispatch,
                ActionHostAuthorizationPolicy.WorkflowDispatchGuard,
                expectedActionReference))
        {
            return false;
        }

        foreach (var (jobId, job) in jobs.Map)
        {
            if (job.Map is null || job.Value("concurrency") is not null)
            {
                return false;
            }

            var isPrivileged = jobId is
                ActionHostAuthorizationPolicy.WorkflowRunJobId or
                ActionHostAuthorizationPolicy.WorkflowDispatchJobId;
            if (!isPrivileged &&
                !StringComparer.Ordinal.Equals(
                    jobId,
                    ActionHostAuthorizationPolicy.PreflightJobId) &&
                !IsAbsentOrEmptyMap(job.Value("permissions")))
            {
                return false;
            }

            var actionReferences = new List<string>();
            CollectActionReferences(job, actionReferences);
            var productReferences = actionReferences.Where(static value =>
                value.StartsWith(
                    ActionHostAuthorizationPolicy.ActionPath,
                    StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith(
                    "./.github/actions/agentic-pr-review",
                    StringComparison.OrdinalIgnoreCase)).ToArray();
            if (actionReferences.Contains(
                    "<block-scalar>",
                    StringComparer.Ordinal))
            {
                return false;
            }

            if (isPrivileged)
            {
                if (productReferences.Length != 1 ||
                    !StringComparer.Ordinal.Equals(
                        productReferences[0],
                        expectedActionReference))
                {
                    return false;
                }
            }
            else if (productReferences.Length != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool ValidatePreflight(PolicyNode job)
    {
        var outputs = job.Value("outputs");
        return job.Map is not null &&
            IsEmptyMap(job.Value("permissions")) &&
            job.Value("concurrency") is null &&
            outputs?.Map is { Count: 1 } &&
            IsScalar(
                outputs.Value("authorized"),
                ActionHostAuthorizationPolicy.PreflightOutput);
    }

    private static bool ValidatePrivilegedJob(
        PolicyNode job,
        string expectedGuard,
        string expectedActionReference)
    {
        var permissions = job.Value("permissions");
        if (job.Map is null ||
            !IsScalar(
                job.Value("needs"),
                ActionHostAuthorizationPolicy.PreflightJobId) ||
            !IsScalar(job.Value("if"), expectedGuard) ||
            permissions?.Map is null ||
            !permissions.HasOnly(
                "actions",
                "contents",
                "pull-requests") ||
            !IsScalar(permissions.Value("actions"), "write") ||
            !IsScalar(permissions.Value("contents"), "read") ||
            !IsScalar(permissions.Value("pull-requests"), "write") ||
            job.Value("concurrency") is not null)
        {
            return false;
        }

        var steps = job.Value("steps");
        return steps?.Sequence is { } sequence &&
            sequence.Count(step =>
                step.Map is not null &&
                IsScalar(step.Value("uses"), expectedActionReference)) == 1;
    }

    private static void CollectActionReferences(
        PolicyNode node,
        ICollection<string> references)
    {
        if (node.Map is not null)
        {
            foreach (var (key, child) in node.Map)
            {
                if (StringComparer.Ordinal.Equals(key, "uses") &&
                    child.Scalar is not null)
                {
                    references.Add(child.Scalar);
                }

                CollectActionReferences(child, references);
            }
        }

        if (node.Sequence is not null)
        {
            foreach (var child in node.Sequence)
            {
                CollectActionReferences(child, references);
            }
        }
    }

    private static bool IsScalar(PolicyNode? node, string expected) =>
        node?.Scalar is { } scalar &&
        StringComparer.Ordinal.Equals(scalar, expected);

    private static bool IsEmptyMap(PolicyNode? node) =>
        node?.Scalar == "{}";

    private static bool IsAbsentOrEmptyMap(PolicyNode? node) =>
        node is null || IsEmptyMap(node);

    private sealed class PolicyNode
    {
        internal Dictionary<string, PolicyNode>? Map { get; init; }

        internal List<PolicyNode>? Sequence { get; init; }

        internal string? Scalar { get; init; }

        internal PolicyNode? Value(string key) =>
            Map is not null && Map.TryGetValue(key, out var value)
                ? value
                : null;

        internal bool HasOnly(params string[] keys) =>
            Map is not null &&
            Map.Count == keys.Length &&
            keys.All(Map.ContainsKey);
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
            var blockIndent = -1;
            foreach (var rawLine in rawLines)
            {
                if (rawLine.Length >
                        ActionHostAuthorizationBounds.MaximumYamlLineCharacters ||
                    rawLine.Contains('\t'))
                {
                    return false;
                }

                var indent = rawLine.TakeWhile(static character =>
                    character == ' ').Count();
                if (blockIndent >= 0 && indent > blockIndent)
                {
                    continue;
                }

                blockIndent = -1;
                var content = StripComment(rawLine[indent..]).TrimEnd();
                if (content.Length == 0)
                {
                    continue;
                }

                if (indent % 2 != 0 || indent > 64)
                {
                    return false;
                }

                if (IsBlockScalarDeclaration(content))
                {
                    blockIndent = indent;
                }

                lines.Add(new(indent, content));
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
                if (!TrySplitMapping(
                        lines[index].Content,
                        out var key,
                        out var value) ||
                    !map.TryAdd(key!, new PolicyNode()))
                {
                    return false;
                }

                index++;
                PolicyNode? child;
                if (value is not null)
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
                var content = lines[index].Content[2..].Trim();
                if (content.Length == 0)
                {
                    return false;
                }

                index++;
                if (TrySplitMapping(content, out var key, out var value))
                {
                    var map = new Dictionary<string, PolicyNode>(
                        StringComparer.Ordinal);
                    if (!TryScalarOrNested(
                            lines,
                            ref index,
                            indent,
                            value,
                            out var first) ||
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
                        if (!TrySplitMapping(
                                lines[index].Content,
                                out var continuationKey,
                                out var continuationValue) ||
                            map.ContainsKey(continuationKey!))
                        {
                            return false;
                        }

                        index++;
                        if (!TryScalarOrNested(
                                lines,
                                ref index,
                                indent + 2,
                                continuationValue,
                                out var continuation))
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

        private readonly record struct PolicyLine(int Indent, string Content);
    }
}
