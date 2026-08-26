using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

public sealed record CapturePlanSource(
    string SourceId,
    string EndpointFamily,
    string Route,
    string Pagination);

public sealed record CapturePlanArtifact(
    string ArtifactId,
    string ArtifactName,
    string MetadataSourceId,
    string ProducingRunId,
    string ProducingRunAttempt,
    string DownloadRoute);

public sealed record CapturePlanOperationRun(
    string OperationId,
    string Scope,
    string RunId,
    string RunAttempt);

public sealed record CapturePlanDocument(
    string Kind,
    string RepositoryId,
    string Repository,
    string[] OperationIds,
    CapturePlanOperationRun[] OperationRuns,
    string SourceMapSha256,
    string PackageName,
    CapturePlanSource[] Sources,
    CapturePlanArtifact[] Artifacts);

public static class CapturePlan
{
    public const string CheckedSourceMapSha256 =
        "1518126dd0a11ccc9b3c847906b07aeed85472ab92acd2d9686b838fc48b15dd";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static CapturePlanDocument Read(RestrictedEvidenceRoot root, string relativePath)
    {
        var pinned = root.ReadPinnedFile(relativePath, EvidenceLimits.MaximumDocumentBytes);
        var bytes = pinned.Bytes;
        try
        {
            var value = JsonSerializer.Deserialize<CapturePlanDocument>(bytes, EvidenceJson.Options) ??
                throw new InvalidDataException("capture_plan_invalid");
            var preCleanup = value.Kind == "apr-r4-e3-capture-plan-v1";
            var postCleanup = value.Kind == "apr-r4-e3-post-cleanup-capture-plan-v1";
            var canonical = CanonicalEvidence.Encode(value, EvidenceJson.Options);
            try
            {
                if (!bytes.AsSpan().SequenceEqual(canonical) ||
                    (!preCleanup && !postCleanup) ||
                    !PositiveDecimal(value.RepositoryId) ||
                    value.Repository.Split('/').Length != 2 ||
                    value.Repository.Split('/').Any(part => !BoundedText(part, EvidenceLimits.MaximumNameBytes)) ||
                    value.OperationIds.Length != 2 ||
                    value.OperationIds.Distinct(StringComparer.Ordinal).Count() != 2 ||
                    value.OperationIds.Any(item => !Sha256(item)) ||
                    value.OperationRuns.Length != 4 ||
                    value.OperationRuns.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() != 4 ||
                    value.OperationRuns.Any(item =>
                        !value.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                        !new[] { "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                        !PositiveDecimal(item.RunId) ||
                        item.RunAttempt != "1") ||
                    value.OperationRuns.GroupBy(item => item.OperationId, StringComparer.Ordinal)
                        .Any(group => group.Count() != 2 ||
                            group.Select(item => item.Scope).Distinct(StringComparer.Ordinal).Count() != 1) ||
                    value.OperationRuns.Count(item => item.Scope == "normal") != 2 ||
                    value.OperationRuns.Count(item => item.Scope == "stale") != 2 ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[0].OperationId, value.OperationIds[0]) ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[0].Scope, "normal") ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[1].OperationId, value.OperationIds[0]) ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[1].Scope, "normal") ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[2].OperationId, value.OperationIds[1]) ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[2].Scope, "stale") ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[3].OperationId, value.OperationIds[1]) ||
                    !StringComparer.Ordinal.Equals(value.OperationRuns[3].Scope, "stale") ||
                    value.SourceMapSha256 != CheckedSourceMapSha256 ||
                    !BoundedText(value.PackageName, EvidenceLimits.MaximumNameBytes) ||
                    !RestrictedEvidenceRoot.IsSinglePathSegment(value.PackageName) ||
                    value.Sources.Length != (preCleanup ? 35 : 17) ||
                    (preCleanup ? value.Artifacts.Length == 0 : value.Artifacts.Length != 0) ||
                    value.Artifacts.Length > EvidenceLimits.MaximumRecords ||
                    value.Sources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length ||
                    !(preCleanup ? ExactSources(value) : ExactPostCleanupSources(value)) ||
                    value.Artifacts.Select(item => item.ArtifactId).Distinct(StringComparer.Ordinal).Count() != value.Artifacts.Length ||
                    value.Artifacts.Any(item =>
                        !PositiveDecimal(item.ArtifactId) ||
                        !BoundedText(item.ArtifactName, EvidenceLimits.MaximumNameBytes) ||
                        !value.Sources.Any(source =>
                            StringComparer.Ordinal.Equals(source.SourceId, item.MetadataSourceId) &&
                            StringComparer.Ordinal.Equals(
                                source.EndpointFamily,
                                $"/repos/{value.Repository}/actions/runs/{item.ProducingRunId}/artifacts") &&
                            StringComparer.Ordinal.Equals(
                                source.SourceId,
                                $"artifacts-run-{item.ProducingRunId}") &&
                            source.Pagination == "complete-cursor") ||
                        !PositiveDecimal(item.ProducingRunId) ||
                        !PositiveDecimal(item.ProducingRunAttempt) ||
                        !value.OperationRuns.Any(run =>
                            StringComparer.Ordinal.Equals(run.RunId, item.ProducingRunId) &&
                            StringComparer.Ordinal.Equals(run.RunAttempt, item.ProducingRunAttempt)) ||
                        item.DownloadRoute != $"/repos/{value.Repository}/actions/artifacts/{item.ArtifactId}/zip"))
                {
                    throw new InvalidDataException("capture_plan_invalid");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonical);
            }

            return value;
        }
        catch (JsonException)
        {
            throw new InvalidDataException("capture_plan_invalid");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool PositiveDecimal(string value) =>
        value.Length > 0 && value.Length <= 20 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        ulong.TryParse(value, out var parsed) && parsed > 0;

    private static bool Sha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ExactSources(CapturePlanDocument value)
    {
        var expected = new List<string>();
        var runs = value.OperationRuns.Select(item => item.RunId).ToArray();
        foreach (var phase in new[] { "setup", "execution", "cleanup" })
        {
            expected.Add($"authorization-{phase}-comment");
            expected.Add($"authorization-{phase}-permission");
        }
        expected.Add("environment-protection");
        foreach (var (phase, run) in new[]
        {
            ("bootstrap", runs[0]),
            ("continuation", runs[1]),
            ("stale", runs[2]),
        })
        {
            expected.Add($"transition-{phase}-pending-run-{run}");
            expected.Add($"transition-{phase}-approvals-run-{run}");
            expected.Add($"transition-{phase}-jobs-run-{run}");
        }
        expected.Add($"concurrency-normal-run-{runs[0]}");
        expected.Add($"concurrency-stale-run-{runs[2]}");
        for (var index = 0; index < 6; index++)
        {
            expected.Add("proof-control-comment");
        }
        for (var index = 0; index < 3; index++)
        {
            expected.Add("proof-control-permission");
        }
        foreach (var run in runs)
        {
            expected.Add($"artifacts-run-{run}");
            expected.Add($"run-terminal-{run}");
        }

        foreach (var source in value.Sources)
        {
            if (!TryClassifyExactSource(source, value.Repository, runs, out var classification) ||
                !expected.Remove(classification))
            {
                return false;
            }
        }
        return expected.Count == 0;
    }

    private static bool TryClassifyExactSource(
        CapturePlanSource source,
        string repository,
        string[] runs,
        out string classification)
    {
        classification = string.Empty;
        if (!BoundedText(source.SourceId, EvidenceLimits.MaximumNameBytes) ||
            !BoundedText(source.EndpointFamily, EvidenceLimits.MaximumRelativePathBytes) ||
            !BoundedText(source.Route, EvidenceLimits.MaximumRelativePathBytes))
        {
            return false;
        }

        var root = $"/repos/{repository}";
        var comment = Regex.Match(
            source.SourceId,
            "^authorization-(setup|execution|cleanup)-comment-([1-9][0-9]*)$");
        if (comment.Success)
        {
            classification = $"authorization-{comment.Groups[1].Value}-comment";
            return Exact(source, $"{root}/issues/comments/{comment.Groups[2].Value}", "none");
        }
        var permission = Regex.Match(
            source.SourceId,
            "^authorization-(setup|execution|cleanup)-permission-([A-Za-z0-9-]+)$");
        if (permission.Success)
        {
            classification = $"authorization-{permission.Groups[1].Value}-permission";
            return Exact(
                source,
                $"{root}/collaborators/{permission.Groups[2].Value}/permission",
                "none");
        }
        if (source.SourceId == "environment-protection")
        {
            classification = source.SourceId;
            return Exact(source, $"{root}/environments/r4-trusted-proof", "none");
        }
        var transition = Regex.Match(
            source.SourceId,
            "^transition-(bootstrap|continuation|stale)-(pending|approvals|jobs)-run-([1-9][0-9]*)$");
        if (transition.Success)
        {
            var phase = transition.Groups[1].Value;
            var kind = transition.Groups[2].Value;
            var run = transition.Groups[3].Value;
            var phaseIndex = phase == "bootstrap" ? 0 : phase == "continuation" ? 1 : 2;
            if (!StringComparer.Ordinal.Equals(run, runs[phaseIndex])) return false;
            classification = $"transition-{phase}-{kind}-run-{run}";
            var suffix = kind switch
            {
                "pending" => "pending_deployments",
                "approvals" => "approvals",
                _ => "attempts/1/jobs",
            };
            return Exact(
                source,
                $"{root}/actions/runs/{run}/{suffix}",
                kind == "jobs" ? "complete-cursor" : "none");
        }
        var concurrency = Regex.Match(
            source.SourceId,
            "^concurrency-(normal|stale)-run-([1-9][0-9]*)$");
        if (concurrency.Success)
        {
            var scope = concurrency.Groups[1].Value;
            var run = concurrency.Groups[2].Value;
            var expectedRun = scope == "normal" ? runs[0] : runs[2];
            if (!StringComparer.Ordinal.Equals(run, expectedRun)) return false;
            classification = $"concurrency-{scope}-run-{run}";
            return Exact(
                source,
                $"{root}/actions/runs/{run}/concurrency_group",
                "complete-cursor");
        }
        var proofComment = Regex.Match(source.SourceId, "^proof-control-comment-([1-9][0-9]*)$");
        if (proofComment.Success)
        {
            classification = "proof-control-comment";
            return Exact(source, $"{root}/issues/comments/{proofComment.Groups[1].Value}", "none");
        }
        var proofPermission = Regex.Match(
            source.SourceId,
            "^proof-control-permission-([1-9][0-9]*)-([A-Za-z0-9-]+)$");
        if (proofPermission.Success)
        {
            classification = "proof-control-permission";
            return Exact(
                source,
                $"{root}/collaborators/{proofPermission.Groups[2].Value}/permission",
                "none");
        }
        var runSource = Regex.Match(source.SourceId, "^(artifacts-run|run-terminal)-([1-9][0-9]*)$");
        if (runSource.Success && runs.Contains(runSource.Groups[2].Value, StringComparer.Ordinal))
        {
            var family = runSource.Groups[1].Value;
            var run = runSource.Groups[2].Value;
            classification = $"{family}-{run}";
            return Exact(
                source,
                family == "artifacts-run"
                    ? $"{root}/actions/runs/{run}/artifacts"
                    : $"{root}/actions/runs/{run}",
                family == "artifacts-run" ? "complete-cursor" : "none");
        }
        return false;
    }

    private static bool ExactPostCleanupSources(CapturePlanDocument value)
    {
        var runs = value.OperationRuns.Select(item => item.RunId).ToArray();
        var expected = new List<string>
        {
            "comments-normal",
            "comments-stale",
            "variables",
            "secrets",
            "environment",
            "ref-normal",
            "ref-stale",
            "pr-normal",
            "pr-stale",
        };
        expected.AddRange(runs.Select(run => $"artifacts-{run}"));
        expected.AddRange(runs.Select(run => $"run-{run}"));
        var prNumbers = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in value.Sources)
        {
            if (!TryClassifyPostCleanupSource(
                    source,
                    value.Repository,
                    value.OperationIds,
                    runs,
                    prNumbers,
                    out var classification) ||
                !expected.Remove(classification))
            {
                return false;
            }
        }
        return expected.Count == 0 && prNumbers.Count == 2;
    }

    private static bool TryClassifyPostCleanupSource(
        CapturePlanSource source,
        string repository,
        string[] operationIds,
        string[] runs,
        HashSet<string> prNumbers,
        out string classification)
    {
        classification = string.Empty;
        if (!BoundedText(source.SourceId, EvidenceLimits.MaximumNameBytes) ||
            !BoundedText(source.EndpointFamily, EvidenceLimits.MaximumRelativePathBytes) ||
            !BoundedText(source.Route, EvidenceLimits.MaximumRelativePathBytes))
        {
            return false;
        }
        var root = $"/repos/{repository}";
        var comments = Regex.Match(
            source.SourceId,
            "^post-cleanup-comments-(normal|stale)-pr-([1-9][0-9]*)$");
        if (comments.Success)
        {
            classification = $"comments-{comments.Groups[1].Value}";
            prNumbers.Add(comments.Groups[2].Value);
            return Exact(
                source,
                $"{root}/issues/{comments.Groups[2].Value}/comments",
                "complete-cursor");
        }
        var artifacts = Regex.Match(source.SourceId, "^post-cleanup-artifacts-run-([1-9][0-9]*)$");
        if (artifacts.Success && runs.Contains(artifacts.Groups[1].Value, StringComparer.Ordinal))
        {
            var run = artifacts.Groups[1].Value;
            classification = $"artifacts-{run}";
            return Exact(source, $"{root}/actions/runs/{run}/artifacts", "complete-cursor");
        }
        if (source.SourceId == "post-cleanup-variables")
        {
            classification = "variables";
            return Exact(source, $"{root}/actions/variables", "complete-cursor");
        }
        if (source.SourceId == "post-cleanup-secrets")
        {
            classification = "secrets";
            return Exact(source, $"{root}/actions/secrets", "complete-cursor");
        }
        if (source.SourceId == "post-cleanup-environment")
        {
            classification = "environment";
            return Exact(source, $"{root}/environments/r4-trusted-proof", "none");
        }
        var fixtureRef = Regex.Match(source.SourceId, "^post-cleanup-ref-(normal|stale)$");
        if (fixtureRef.Success)
        {
            var scope = fixtureRef.Groups[1].Value;
            var operation = operationIds[scope == "normal" ? 0 : 1];
            classification = $"ref-{scope}";
            return Exact(
                source,
                $"{root}/git/matching-refs/heads/r4-trusted-proof/{operation}",
                "complete-cursor");
        }
        var pull = Regex.Match(source.SourceId, "^post-cleanup-pr-(normal|stale)-([1-9][0-9]*)$");
        if (pull.Success)
        {
            var scope = pull.Groups[1].Value;
            prNumbers.Add(pull.Groups[2].Value);
            classification = $"pr-{scope}";
            return Exact(source, $"{root}/pulls/{pull.Groups[2].Value}", "none");
        }
        var runSource = Regex.Match(source.SourceId, "^post-cleanup-run-([1-9][0-9]*)$");
        if (runSource.Success && runs.Contains(runSource.Groups[1].Value, StringComparer.Ordinal))
        {
            var run = runSource.Groups[1].Value;
            classification = $"run-{run}";
            return Exact(source, $"{root}/actions/runs/{run}", "none");
        }
        return false;
    }

    private static bool Exact(CapturePlanSource source, string endpoint, string pagination) =>
        StringComparer.Ordinal.Equals(source.EndpointFamily, endpoint) &&
        StringComparer.Ordinal.Equals(source.Route, endpoint) &&
        StringComparer.Ordinal.Equals(source.Pagination, pagination);

    private static bool BoundedText(string? value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Any(character => char.IsControl(character)))
        {
            return false;
        }

        try
        {
            return StrictUtf8.GetByteCount(value) <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
