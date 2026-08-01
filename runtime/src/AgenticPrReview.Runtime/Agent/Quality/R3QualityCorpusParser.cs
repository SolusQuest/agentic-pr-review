using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Agent.Quality;

internal static class R3QualityCorpusParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryParse(
        ReadOnlySpan<byte> utf8,
        out R3QualityCorpus? corpus,
        out R3QualityParseFailure? failure)
    {
        corpus = null;
        failure = null;
        var fixtureSha256 = AgentCanonical.HashDomain(
            R3QualityFormat.InvalidFixtureHashDomain,
            utf8);
        if (utf8.Length is < 1 or > R3QualityFormat.MaximumCorpusBytes)
        {
            failure = Invalid(fixtureSha256, "corpus_size_invalid");
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                utf8.ToArray(),
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32,
                });
            var root = document.RootElement;
            if (!HasProperties(root, "kind", "cases") ||
                !StringComparer.Ordinal.Equals(
                    RequiredString(root, "kind"),
                    R3QualityFormat.CorpusKind))
            {
                failure = Invalid(fixtureSha256, "corpus_schema_invalid");
                return false;
            }

            var casesElement = root.GetProperty("cases");
            if (casesElement.ValueKind != JsonValueKind.Array ||
                casesElement.GetArrayLength() != R3QualityFormat.CaseCount)
            {
                failure = Invalid(fixtureSha256, "corpus_cases_invalid");
                return false;
            }

            var cases = ImmutableArray.CreateBuilder<R3QualityCase>(
                R3QualityFormat.CaseCount);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var kinds = new HashSet<R3QualityCaseKind>();
            foreach (var element in casesElement.EnumerateArray())
            {
                var parsed = ParseCase(element);
                if (!ids.Add(parsed.Id) || !kinds.Add(parsed.Kind))
                {
                    failure = Invalid(fixtureSha256, "corpus_cases_invalid");
                    return false;
                }

                cases.Add(parsed);
            }

            if (kinds.Count != R3QualityFormat.CaseCount)
            {
                failure = Invalid(fixtureSha256, "corpus_cases_invalid");
                return false;
            }

            corpus = new R3QualityCorpus(cases.ToImmutable());
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            DecoderFallbackException or
            FormatException or
            InvalidOperationException or
            JsonException or
            KeyNotFoundException or
            NotSupportedException or
            OverflowException or
            Rfc8785CanonicalizationException)
        {
            failure = Invalid(fixtureSha256, "corpus_schema_invalid");
            return false;
        }
    }

    private static R3QualityCase ParseCase(JsonElement element)
    {
        if (!HasProperties(
                element,
                "id",
                "kind",
                "reviewed_identity",
                "initial_context",
                "process_one_context",
                "files",
                "change",
                "expectation"))
        {
            throw new FormatException("Invalid quality case shape.");
        }

        var id = RequiredString(element, "id");
        if (!AgentValueDomains.IsIdentifier(id))
        {
            throw new FormatException("Invalid quality case id.");
        }

        var kind = RequiredString(element, "kind") switch
        {
            "must_find" => R3QualityCaseKind.MustFind,
            "must_not_find" => R3QualityCaseKind.MustNotFind,
            "continuation" => R3QualityCaseKind.Continuation,
            _ => throw new FormatException("Invalid quality case kind."),
        };
        var identity = ParseIdentity(element.GetProperty("reviewed_identity"));
        var initialContext = RequiredString(element, "initial_context");
        if (!AgentValueDomains.IsUtf8(
                initialContext,
                minimumBytes: 1,
                AgentLimits.ContentBytes))
        {
            throw new FormatException("Invalid initial context.");
        }

        var processOneContext = NullableString(
            element.GetProperty("process_one_context"));
        if (processOneContext is not null &&
            !AgentValueDomains.IsUtf8(
                processOneContext,
                minimumBytes: 1,
                AgentLimits.ContentBytes))
        {
            throw new FormatException("Invalid process-one context.");
        }

        var files = ParseFiles(element.GetProperty("files"));
        var change = ParseChange(
            element.GetProperty("change"),
            identity,
            files);
        var expectation = ParseExpectation(
            element.GetProperty("expectation"),
            kind,
            identity,
            initialContext,
            processOneContext,
            files,
            change.ChangedFile,
            change.DiffSource);
        var canonical = JsonElementCanonicalizer.Canonicalize(
            element,
            maxDepth: 32,
            maxProperties: 256,
            maxArrayItems: 1_024,
            maxBytes: R3QualityFormat.MaximumCorpusBytes,
            out var capExceeded);
        if (capExceeded)
        {
            throw new FormatException("Quality case exceeds canonical bound.");
        }

        return new R3QualityCase(
            id,
            AgentCanonical.HashDomain(
                R3QualityFormat.CaseHashDomain,
                canonical.AsSpan()),
            kind,
            identity,
            initialContext,
            processOneContext,
            files,
            change.ChangedFile,
            change.DiffSource,
            expectation);
    }

    private static ReviewedIdentity ParseIdentity(JsonElement element)
    {
        if (!HasProperties(
                element,
                "repository_id",
                "review_target",
                "base_sha",
                "head_sha"))
        {
            throw new FormatException("Invalid reviewed identity shape.");
        }

        var identity = new ReviewedIdentity(
            RequiredString(element, "repository_id"),
            element.GetProperty("review_target").GetInt64(),
            RequiredString(element, "base_sha"),
            RequiredString(element, "head_sha"));
        return identity.IsValid()
            ? identity
            : throw new FormatException("Invalid reviewed identity.");
    }

    private static ImmutableArray<R3QualityRepositoryFile> ParseFiles(
        JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() is < 1 or > R3QualityFormat.MaximumFilesPerCase)
        {
            throw new FormatException("Invalid repository files.");
        }

        var builder = ImmutableArray.CreateBuilder<R3QualityRepositoryFile>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (var file in element.EnumerateArray())
        {
            if (!HasProperties(file, "path", "content"))
            {
                throw new FormatException("Invalid repository file shape.");
            }

            var path = RequiredString(file, "path");
            var content = RequiredString(file, "content");
            var contentBytes = StrictUtf8.GetByteCount(content);
            totalBytes = checked(totalBytes + contentBytes);
            if (!RepositoryPath.IsValid(path) ||
                !paths.Add(path) ||
                contentBytes > R3QualityFormat.MaximumFileBytes ||
                totalBytes > R3QualityFormat.MaximumCorpusBytes ||
                content.Contains('\0') ||
                content.Contains('\r'))
            {
                throw new FormatException("Invalid repository file.");
            }

            builder.Add(new R3QualityRepositoryFile(path, content));
        }

        return builder.ToImmutable();
    }

    private static ParsedChange ParseChange(
        JsonElement element,
        ReviewedIdentity identity,
        ImmutableArray<R3QualityRepositoryFile> files)
    {
        if (!HasProperties(element, "changed_file", "diff_source"))
        {
            throw new FormatException("Invalid change shape.");
        }

        var changedElement = element.GetProperty("changed_file");
        if (!HasProperties(
                changedElement,
                "path",
                "previous_path",
                "status",
                "additions",
                "deletions",
                "changes",
                "patch_status",
                "source_truncated"))
        {
            throw new FormatException("Invalid changed-file shape.");
        }

        var path = RequiredString(changedElement, "path");
        var previousPath = NullableString(changedElement.GetProperty("previous_path"));
        var status = RequiredString(changedElement, "status");
        var additions = changedElement.GetProperty("additions").GetInt32();
        var deletions = changedElement.GetProperty("deletions").GetInt32();
        var changes = changedElement.GetProperty("changes").GetInt32();
        var patchStatus = RequiredString(changedElement, "patch_status");
        var sourceTruncated = changedElement
            .GetProperty("source_truncated")
            .GetBoolean();

        var sourceElement = element.GetProperty("diff_source");
        if (!HasProperties(
                sourceElement,
                "path",
                "previous_path",
                "status",
                "source_truncated",
                "hunks") ||
            !StringComparer.Ordinal.Equals(
                path,
                RequiredString(sourceElement, "path")) ||
            !StringComparer.Ordinal.Equals(
                previousPath,
                NullableString(sourceElement.GetProperty("previous_path"))) ||
            !StringComparer.Ordinal.Equals(
                status,
                RequiredString(sourceElement, "status")) ||
            sourceTruncated != sourceElement
                .GetProperty("source_truncated")
                .GetBoolean())
        {
            throw new FormatException("Incoherent diff source root.");
        }

        var source = new ReviewedDiffSource(
            identity,
            path,
            previousPath,
            status,
            sourceTruncated,
            ParseHunks(sourceElement.GetProperty("hunks")));
        var changed = new ReviewedChangedFile(
            path,
            previousPath,
            status,
            additions,
            deletions,
            changes,
            patchStatus,
            source.PatchSha256,
            sourceTruncated);
        var tracked = files
            .Select(file => file.Path)
            .ToImmutableHashSet(StringComparer.Ordinal);
        if (!ReviewedChangedFileValidation.IsShapeValid(changed) ||
            !ReviewedChangedFileValidation.MembershipIsValid(changed, tracked) ||
            patchStatus != "available" ||
            source.RepresentedAdditions != additions ||
            source.RepresentedDeletions != deletions ||
            changes != checked(additions + deletions) ||
            !HeadContentMatches(files, source))
        {
            throw new FormatException("Incoherent changed file or diff source.");
        }

        return new ParsedChange(changed, source);
    }

    private static ImmutableArray<ReviewedDiffHunk> ParseHunks(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() is < 1 or > AgentLimits.DiffHunksPerFile)
        {
            throw new FormatException("Invalid diff hunks.");
        }

        var hunks = ImmutableArray.CreateBuilder<ReviewedDiffHunk>();
        foreach (var hunk in element.EnumerateArray())
        {
            if (!HasProperties(
                    hunk,
                    "old_start",
                    "old_count",
                    "new_start",
                    "new_count",
                    "lines"))
            {
                throw new FormatException("Invalid diff hunk shape.");
            }

            var lineElements = hunk.GetProperty("lines");
            if (lineElements.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Invalid diff lines.");
            }

            var lines = ImmutableArray.CreateBuilder<ReviewedDiffLine>();
            foreach (var line in lineElements.EnumerateArray())
            {
                if (!HasProperties(line, "kind", "old_line", "new_line", "text"))
                {
                    throw new FormatException("Invalid diff line shape.");
                }

                lines.Add(new ReviewedDiffLine(
                    RequiredString(line, "kind"),
                    NullableInt32(line.GetProperty("old_line")),
                    NullableInt32(line.GetProperty("new_line")),
                    RequiredString(line, "text")));
            }

            hunks.Add(new ReviewedDiffHunk(
                hunk.GetProperty("old_start").GetInt32(),
                hunk.GetProperty("old_count").GetInt32(),
                hunk.GetProperty("new_start").GetInt32(),
                hunk.GetProperty("new_count").GetInt32(),
                lines.ToImmutable()));
        }

        return hunks.ToImmutable();
    }

    private static R3QualityExpectation ParseExpectation(
        JsonElement element,
        R3QualityCaseKind kind,
        ReviewedIdentity identity,
        string initialContext,
        string? processOneContext,
        ImmutableArray<R3QualityRepositoryFile> files,
        ReviewedChangedFile changed,
        ReviewedDiffSource source) =>
        kind switch
        {
            R3QualityCaseKind.MustFind => ParseMustFind(
                element,
                identity,
                initialContext,
                source),
            R3QualityCaseKind.MustNotFind => ParseMustNotFind(
                element,
                identity,
                source),
            R3QualityCaseKind.Continuation => ParseContinuation(
                element,
                initialContext,
                processOneContext,
                files,
                changed,
                source),
            _ => throw new FormatException("Invalid expectation kind."),
        };

    private static R3QualityMustFindExpectation ParseMustFind(
        JsonElement element,
        ReviewedIdentity identity,
        string initialContext,
        ReviewedDiffSource source)
    {
        if (!HasProperties(
                element,
                "required_tool",
                "required_start_hunk",
                "required_hunk_count",
                "required_observation_id",
                "evidence",
                "expected_severity",
                "target_marker",
                "expected_finding_token"))
        {
            throw new FormatException("Invalid must-find expectation shape.");
        }

        var required = ParseRequiredObservation(element, identity, source);
        var evidenceElement = element.GetProperty("evidence");
        if (!HasProperties(evidenceElement, "path", "start_line", "end_line"))
        {
            throw new FormatException("Invalid expected evidence shape.");
        }

        var evidence = new AgentEvidence(
            required.ObservationId,
            RequiredString(evidenceElement, "path"),
            evidenceElement.GetProperty("start_line").GetInt32(),
            evidenceElement.GetProperty("end_line").GetInt32());
        var severity = RequiredString(element, "expected_severity");
        var targetMarker = RequiredString(element, "target_marker");
        var findingToken = RequiredString(element, "expected_finding_token");
        if (severity is not ("critical" or "high" or "medium" or "low") ||
            !AgentValueDomains.IsIdentifier(targetMarker) ||
            !AgentValueDomains.IsIdentifier(findingToken) ||
            StringComparer.Ordinal.Equals(targetMarker, findingToken) ||
            Contains(initialContext, targetMarker) ||
            !RequiredResultGrounds(required.Result, evidence) ||
            Count(required.Result.AsSpan(), StrictUtf8.GetBytes(targetMarker)) != 1)
        {
            throw new FormatException("Invalid must-find expectation.");
        }

        return new R3QualityMustFindExpectation(
            required.ObservationId,
            required.Arguments,
            required.Result,
            evidence,
            severity,
            targetMarker,
            findingToken);
    }

    private static R3QualityMustNotFindExpectation ParseMustNotFind(
        JsonElement element,
        ReviewedIdentity identity,
        ReviewedDiffSource source)
    {
        if (!HasProperties(
                element,
                "required_tool",
                "required_start_hunk",
                "required_hunk_count",
                "required_observation_id",
                "path",
                "prohibited_lines"))
        {
            throw new FormatException("Invalid must-not-find expectation shape.");
        }

        var required = ParseRequiredObservation(element, identity, source);
        var path = RequiredString(element, "path");
        var linesElement = element.GetProperty("prohibited_lines");
        if (linesElement.ValueKind != JsonValueKind.Array ||
            linesElement.GetArrayLength() is < 1 or > AgentLimits.ReadFileLines)
        {
            throw new FormatException("Invalid prohibited lines.");
        }

        var lines = linesElement
            .EnumerateArray()
            .Select(item => item.GetInt32())
            .ToImmutableArray();
        if (!RepositoryPath.IsValid(path) ||
            lines.Any(line => line < 1) ||
            lines.Distinct().Count() != lines.Length ||
            !lines.SequenceEqual(lines.Order()) ||
            lines.Any(line => !ResultContainsLine(required.Result, path, line)))
        {
            throw new FormatException("Invalid prohibited location.");
        }

        return new R3QualityMustNotFindExpectation(
            required.ObservationId,
            required.Arguments,
            required.Result,
            path,
            lines);
    }

    private static R3QualityContinuationExpectation ParseContinuation(
        JsonElement element,
        string initialContext,
        string? processOneContext,
        ImmutableArray<R3QualityRepositoryFile> files,
        ReviewedChangedFile changed,
        ReviewedDiffSource source)
    {
        if (!HasProperties(element, "prior_only_marker", "fresh_input_names"))
        {
            throw new FormatException("Invalid continuation expectation shape.");
        }

        var marker = RequiredString(element, "prior_only_marker");
        var namesElement = element.GetProperty("fresh_input_names");
        if (!AgentValueDomains.IsIdentifier(marker) ||
            processOneContext is null ||
            !Contains(processOneContext, marker) ||
            Contains(initialContext, marker) ||
            namesElement.ValueKind != JsonValueKind.Array ||
            namesElement.GetArrayLength() is < 1 or
                > R3QualityFormat.MaximumFreshInputs)
        {
            throw new FormatException("Invalid continuation marker or manifest.");
        }

        var names = namesElement
            .EnumerateArray()
            .Select(item => item.GetString() ?? throw new FormatException())
            .ToImmutableArray();
        if (names.Any(name => !RepositoryPath.IsValid(name)) ||
            names.Distinct(StringComparer.Ordinal).Count() != names.Length ||
            files.Any(file => Contains(file.Content, marker)) ||
            Contains(Encoding.UTF8.GetString(source.CanonicalBytes.AsSpan()), marker) ||
            Contains(changed.Path, marker))
        {
            throw new FormatException("Continuation fact leaks into fresh corpus data.");
        }

        return new R3QualityContinuationExpectation(marker, names);
    }

    private static RequiredObservation ParseRequiredObservation(
        JsonElement element,
        ReviewedIdentity identity,
        ReviewedDiffSource source)
    {
        if (!StringComparer.Ordinal.Equals(
                RequiredString(element, "required_tool"),
                AgentToolRegistry.ReadDiffName))
        {
            throw new FormatException("Invalid required tool.");
        }

        var start = element.GetProperty("required_start_hunk").GetInt32();
        var count = element.GetProperty("required_hunk_count").GetInt32();
        if (start < 1 || count is < 1 or > AgentLimits.ReadDiffHunks)
        {
            throw new FormatException("Invalid required read_diff page.");
        }

        var selected = source.Hunks
            .Skip(start - 1)
            .Take(count)
            .ToImmutableArray();
        if (selected.Length == 0)
        {
            throw new FormatException("Required read_diff page is empty.");
        }

        var end = start + selected.Length - 1;
        var truncated = end < source.Hunks.Length;
        var withoutObservation = new ReadDiffResult(
            "ok",
            identity,
            source.Path,
            source.PatchSha256,
            source.SourceTruncated,
            start,
            count,
            start,
            end,
            selected,
            truncated,
            truncated ? end + 1 : null,
            ObservationId: null);
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadDiffObservationDomain,
            ReadDiffResultWriter.Write(
                withoutObservation,
                includeObservationId: false));
        if (!StringComparer.Ordinal.Equals(
                observationId,
                RequiredString(element, "required_observation_id")))
        {
            throw new FormatException("Pinned observation id is incoherent.");
        }

        var result = ReadDiffResultWriter.Write(
            withoutObservation with { ObservationId = observationId });
        return new RequiredObservation(
            observationId,
            ImmutableArray.CreateRange(AgentToolArguments.WriteReadDiff(
                source.Path,
                start,
                count,
                includeStart: true,
                includeCount: true)),
            ImmutableArray.CreateRange(result));
    }

    private static bool RequiredResultGrounds(
        ImmutableArray<byte> canonicalResult,
        AgentEvidence evidence)
    {
        using var document = JsonDocument.Parse(canonicalResult.ToArray());
        if (!StringComparer.Ordinal.Equals(
                document.RootElement.GetProperty("path").GetString(),
                evidence.Path))
        {
            return false;
        }

        var lines = ReturnedNewLines(document.RootElement).ToHashSet();
        for (var line = evidence.StartLine; line <= evidence.EndLine; line++)
        {
            if (!lines.Contains(line))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ResultContainsLine(
        ImmutableArray<byte> canonicalResult,
        string path,
        int line)
    {
        using var document = JsonDocument.Parse(canonicalResult.ToArray());
        return StringComparer.Ordinal.Equals(
                document.RootElement.GetProperty("path").GetString(),
                path) &&
            ReturnedNewLines(document.RootElement).Contains(line);
    }

    private static IEnumerable<int> ReturnedNewLines(JsonElement root) =>
        root.GetProperty("hunks")
            .EnumerateArray()
            .SelectMany(hunk => hunk.GetProperty("lines").EnumerateArray())
            .Where(line => line.GetProperty("kind").GetString() is "context" or "addition")
            .Select(line => line.GetProperty("new_line").GetInt32());

    private static bool HeadContentMatches(
        ImmutableArray<R3QualityRepositoryFile> files,
        ReviewedDiffSource source)
    {
        var file = files.SingleOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Path, source.Path));
        if (file is null)
        {
            return false;
        }

        var lines = file.Content.Split('\n');
        if (file.Content.EndsWith('\n'))
        {
            lines = lines[..^1];
        }

        foreach (var line in source.Hunks
                     .SelectMany(hunk => hunk.Lines)
                     .Where(line => line.Kind is "context" or "addition"))
        {
            if (line.NewLine is null ||
                line.NewLine.Value > lines.Length ||
                !StringComparer.Ordinal.Equals(
                    lines[line.NewLine.Value - 1],
                    line.Text))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasProperties(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != names.Length)
        {
            return false;
        }

        for (var index = 0; index < names.Length; index++)
        {
            if (!properties[index].NameEquals(names[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? throw new FormatException("String is null.")
            : throw new FormatException("Property is not a string.");
    }

    private static string? NullableString(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => throw new FormatException("Property is not nullable string."),
        };

    private static int? NullableInt32(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();

    private static bool Contains(string text, string marker) =>
        text.Contains(marker, StringComparison.Ordinal);

    private static int Count(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        var count = 0;
        while (true)
        {
            var index = haystack.IndexOf(needle);
            if (index < 0)
            {
                return count;
            }

            count++;
            haystack = haystack[(index + needle.Length)..];
        }
    }

    private static R3QualityParseFailure Invalid(
        string fixtureSha256,
        string sourceCode) =>
        new("corpus", fixtureSha256, sourceCode);

    private sealed record ParsedChange(
        ReviewedChangedFile ChangedFile,
        ReviewedDiffSource DiffSource);

    private sealed record RequiredObservation(
        string ObservationId,
        ImmutableArray<byte> Arguments,
        ImmutableArray<byte> Result);
}
