using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofCapture;

public sealed record CapturePlanSource(
    string SourceId,
    string OperationId,
    string Phase,
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

public sealed record CapturePlanExpectedRole(
    string Role,
    string OperationId,
    string Scope,
    string RunId,
    string RunAttempt,
    string[] ProducerSourceIds);

public sealed record CapturePlanObservedRun(
    string OperationId,
    string Scope,
    string RunId,
    string RunAttempt,
    string Ownership);

public sealed record CapturePlanDocument(
    string Kind,
    string RepositoryId,
    string Repository,
    string[] OperationIds,
    string ExecutionAuthorizationSha256,
    string ProducerJournalDirectory,
    string ProducerJournalSealSha256,
    string ProducerJournalSealFileIdentity,
    string Disposition,
    string PhaseMaterializerSourceSha256,
    string PhaseMaterializerBuildSha256,
    CapturePlanExpectedRole[] ExpectedRoles,
    CapturePlanObservedRun[] ObservedRuns,
    string SourceMapSha256,
    string PackageName,
    CapturePlanSource[] Sources,
    CapturePlanArtifact[] Artifacts);

public static class CapturePlan
{
    public const string CheckedSourceMapSha256 =
        "f99fba6f95199833597957d1e7b79b47d6754bae3cbc37f2b0b45b4d72ed734e";
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
            var producerJournal = ProducerOutcomeJournal.Open(
                root,
                value.ProducerJournalDirectory,
                value.ExecutionAuthorizationSha256);
            var producerSeal = producerJournal.ReadSeal();
            var partial = preCleanup
                ? PhaseFragmentJournal.ReadPartial(
                    root,
                    value.PackageName,
                    value.OperationIds,
                    value.ExecutionAuthorizationSha256,
                    value.PhaseMaterializerSourceSha256,
                    value.PhaseMaterializerBuildSha256,
                    producerJournal)
                : null;
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
                    !Sha256(value.ExecutionAuthorizationSha256) ||
                    !RestrictedEvidenceRoot.IsSinglePathSegment(value.ProducerJournalDirectory) ||
                    value.ProducerJournalSealSha256 != producerSeal.Sha256 ||
                    value.ProducerJournalSealFileIdentity != producerSeal.PhysicalIdentitySha256 ||
                    value.Disposition != producerSeal.Document.Disposition ||
                    !Sha256(value.PhaseMaterializerSourceSha256) ||
                    !Sha256(value.PhaseMaterializerBuildSha256) ||
                    !value.OperationIds.SequenceEqual(
                        producerSeal.Document.OperationIds,
                        StringComparer.Ordinal) ||
                    !value.ExpectedRoles.Select(item => new ProducerJournalExpectedRole(
                            item.Role, item.OperationId, item.Scope, item.RunId, item.RunAttempt,
                            item.ProducerSourceIds))
                        .Select(item => CanonicalEvidence.Encode(item, EvidenceJson.Options))
                        .Select(DigestAndZero)
                        .SequenceEqual(
                            producerSeal.Document.DerivedRoles
                                .Select(item => CanonicalEvidence.Encode(item, EvidenceJson.Options))
                                .Select(DigestAndZero),
                            StringComparer.Ordinal) ||
                    !value.ObservedRuns.Select(item => new ProducerJournalObservedRun(
                            item.OperationId, item.Scope, item.RunId, item.RunAttempt, item.Ownership))
                        .Select(item => CanonicalEvidence.Encode(item, EvidenceJson.Options))
                        .Select(DigestAndZero)
                        .SequenceEqual(
                            producerSeal.Document.ObservedRuns
                                .Select(item => CanonicalEvidence.Encode(item, EvidenceJson.Options))
                                .Select(DigestAndZero),
                            StringComparer.Ordinal) ||
                    value.ExpectedRoles.Length > 4 ||
                    value.ExpectedRoles.Select(item => item.Role).Distinct(StringComparer.Ordinal).Count() !=
                        value.ExpectedRoles.Length ||
                    value.ExpectedRoles.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() !=
                        value.ExpectedRoles.Length ||
                    value.ObservedRuns.Select(item => item.RunId).Distinct(StringComparer.Ordinal).Count() !=
                        value.ObservedRuns.Length ||
                    value.ObservedRuns.Any(item =>
                        !value.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                        !new[] { "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                        item.Ownership is not ("operation-owned" or "ownership-ambiguous") ||
                        !PositiveDecimal(item.RunId) ||
                        !PositiveDecimal(item.RunAttempt)) ||
                    value.ExpectedRoles.Any(item =>
                        !value.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                        !new[] { "normal", "stale" }.Contains(item.Scope, StringComparer.Ordinal) ||
                        !PositiveDecimal(item.RunId) ||
                        item.RunAttempt != "1" ||
                        item.ProducerSourceIds.Length == 0 ||
                        item.ProducerSourceIds.Distinct(StringComparer.Ordinal).Count() !=
                            item.ProducerSourceIds.Length ||
                        item.ProducerSourceIds.Any(sourceId =>
                            !Regex.IsMatch(sourceId, ":page:[1-9][0-9]*$") ||
                            !value.Sources.Any(source => sourceId.StartsWith(
                                $"{source.SourceId}:page:",
                                StringComparison.Ordinal))) ||
                        !value.ObservedRuns.Any(run =>
                            StringComparer.Ordinal.Equals(run.OperationId, item.OperationId) &&
                            StringComparer.Ordinal.Equals(run.Scope, item.Scope) &&
                            StringComparer.Ordinal.Equals(run.RunId, item.RunId) &&
                            StringComparer.Ordinal.Equals(run.RunAttempt, item.RunAttempt) &&
                            run.Ownership == "operation-owned")) ||
                    (value.Disposition == "success-candidate"
                        ? !ExactExpectedRoles(value)
                        : !ValidRecoveryRoles(value)) ||
                    value.SourceMapSha256 != CheckedSourceMapSha256 ||
                    !BoundedText(value.PackageName, EvidenceLimits.MaximumNameBytes) ||
                    !RestrictedEvidenceRoot.IsSinglePathSegment(value.PackageName) ||
                    (postCleanup && value.Sources.Length !=
                        15 + (3 * value.ObservedRuns.Length)) ||
                    value.Artifacts.Length != 0 ||
                    value.Sources.Select(item => item.SourceId).Distinct(StringComparer.Ordinal).Count() != value.Sources.Length ||
                    value.Sources.Any(item =>
                        !value.OperationIds.Contains(item.OperationId, StringComparer.Ordinal) ||
                        !BoundedText(item.Phase, EvidenceLimits.MaximumNameBytes) ||
                        !ValidPhase(item.Phase, item.OperationId, value.OperationIds)) ||
                    (preCleanup && !ExactRetainedSources(value, partial!.Sources)) ||
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
                        !value.ObservedRuns.Any(run =>
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

    private static bool ExactExpectedRoles(CapturePlanDocument value)
    {
        var roles = new[]
        {
            "normal-bootstrap",
            "normal-continuation",
            "stale-protected",
            "stale-follow-on",
        };
        return value.ExpectedRoles.Select(item => item.Role).SequenceEqual(roles, StringComparer.Ordinal) &&
            value.ExpectedRoles.Take(2).All(item =>
                StringComparer.Ordinal.Equals(item.OperationId, value.OperationIds[0]) &&
                StringComparer.Ordinal.Equals(item.Scope, "normal")) &&
            value.ExpectedRoles.Skip(2).All(item =>
                StringComparer.Ordinal.Equals(item.OperationId, value.OperationIds[1]) &&
                StringComparer.Ordinal.Equals(item.Scope, "stale"));
    }

    private static bool ValidRecoveryRoles(CapturePlanDocument value)
    {
        var roles = new[]
        {
            "normal-bootstrap",
            "normal-continuation",
            "stale-protected",
            "stale-follow-on",
        };
        var prior = -1;
        foreach (var role in value.ExpectedRoles)
        {
            var index = Array.IndexOf(roles, role.Role);
            if (index <= prior ||
                role.OperationId != value.OperationIds[index < 2 ? 0 : 1] ||
                role.Scope != (index < 2 ? "normal" : "stale"))
            {
                return false;
            }
            prior = index;
        }
        return true;
    }

    private static bool PositiveDecimal(string value) =>
        value.Length > 0 && value.Length <= 20 &&
        value.All(character => character is >= '0' and <= '9') &&
        (value.Length == 1 || value[0] != '0') &&
        ulong.TryParse(value, out var parsed) && parsed > 0;

    private static string DigestAndZero(byte[] bytes)
    {
        try
        {
            return CanonicalEvidence.Sha256(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static bool Sha256(string value) =>
        value.Length == 64 &&
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidPhase(
        string phase,
        string operationId,
        IReadOnlyList<string> operationIds)
    {
        var allowed = new[]
        {
            "producer-discovery", "baseline-normal", "baseline-stale", "normal-variable-readback",
            "bootstrap-readiness", "bootstrap-pending", "bootstrap-approval",
            "bootstrap-jobs", "bootstrap-concurrency", "continuation-readiness",
            "continuation-pending", "continuation-approval", "continuation-jobs",
            "stale-variable-readback", "stale-readiness", "stale-pending", "stale-approval",
            "stale-jobs", "stale-follow-on-jobs", "stale-concurrency", "terminal-normal",
            "terminal-stale", "post-cleanup-normal", "post-cleanup-stale",
        };
        return allowed.Contains(phase, StringComparer.Ordinal) &&
            (phase == "producer-discovery"
                ? operationId == operationIds[0]
                : operationId ==
                    operationIds[phase.Contains("stale", StringComparison.Ordinal) ? 1 : 0]);
    }

    private static bool ExactSources(CapturePlanDocument value)
    {
        var expected = new List<string>();
        expected.Add("producer-discovery-final");
        var roleRuns = value.ExpectedRoles.ToDictionary(item => item.Role, item => item.RunId,
            StringComparer.Ordinal);
        var runs = value.ObservedRuns.Select(item => item.RunId).ToArray();
        foreach (var phase in new[] { "setup", "execution" })
        {
            foreach (var scope in new[] { "normal", "stale" })
            {
                expected.Add($"authorization-{phase}-comment-{scope}");
                expected.Add($"authorization-{phase}-permission-{scope}");
            }
        }
        foreach (var scope in new[] { "normal", "stale" })
        {
            expected.Add($"baseline-{scope}-environment-protection");
            expected.Add($"baseline-{scope}-environment-branch-policies");
            expected.Add($"baseline-{scope}-environment-secret-inventory");
            expected.Add($"baseline-{scope}-authorization-variable");
            expected.Add($"proof-control-comments-{scope}");
        }
        foreach (var (phase, role) in new[]
        {
            ("bootstrap", "normal-bootstrap"),
            ("continuation", "normal-continuation"),
            ("stale", "stale-protected"),
        })
        {
            if (!roleRuns.TryGetValue(role, out var run)) continue;
            expected.Add($"readiness-{phase}-environment-protection");
            expected.Add($"readiness-{phase}-environment-branch-policies");
            expected.Add($"readiness-{phase}-environment-secret-inventory");
            expected.Add($"readiness-{phase}-authorization-variable");
            expected.Add($"transition-{phase}-pending-run-{run}");
            expected.Add($"transition-{phase}-approvals-run-{run}");
            expected.Add($"transition-{phase}-jobs-run-{run}");
        }
        if (roleRuns.ContainsKey("normal-bootstrap"))
        {
            expected.Add("proof-control-bootstrap-comment");
            expected.Add("proof-control-bootstrap-comment");
            expected.Add("proof-control-bootstrap-permission");
        }
        if (roleRuns.ContainsKey("stale-protected"))
        {
            expected.Add("proof-control-stale-comment");
            expected.Add("proof-control-stale-comment");
            expected.Add("proof-control-stale-comment");
            expected.Add("proof-control-stale-comment");
            expected.Add("proof-control-stale-permission");
            expected.Add("proof-control-stale-permission");
        }
        if (roleRuns.TryGetValue("normal-bootstrap", out var normalOwner) &&
            roleRuns.ContainsKey("normal-continuation"))
        {
            expected.Add($"concurrency-normal-run-{normalOwner}");
        }
        if (roleRuns.TryGetValue("stale-protected", out var staleOwner) &&
            roleRuns.ContainsKey("stale-follow-on"))
        {
            expected.Add($"concurrency-stale-run-{staleOwner}");
        }
        if (roleRuns.TryGetValue("stale-follow-on", out var staleFollowOn))
        {
            expected.Add($"transition-stale-follow-on-jobs-run-{staleFollowOn}");
        }
        foreach (var run in runs)
        {
            expected.Add($"artifacts-run-{run}");
            expected.Add($"run-terminal-{run}");
        }

        foreach (var source in value.Sources)
        {
            if (!TryClassifyExactSource(
                    source,
                    value.Repository,
                    value.OperationIds,
                    runs,
                    roleRuns,
                    out var classification) ||
                !expected.Remove(classification))
            {
                return false;
            }
        }
        return expected.Count == 0;
    }

    private static bool ExactRetainedSources(
        CapturePlanDocument value,
        IReadOnlyList<CaptureManifestSource> retained)
    {
        var planned = value.Sources.Where(source =>
            source.Phase != "producer-discovery" &&
            PhaseFragmentMaterializer.RequiresRetained(source.Phase)).ToArray();
        var groups = retained.GroupBy(source =>
        {
            var match = Regex.Match(source.SourceId, "^(.*):page:([1-9][0-9]*)$");
            return match.Success ? match.Groups[1].Value : string.Empty;
        }, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.OrderBy(
            source => source.Page).ToArray(), StringComparer.Ordinal);
        if (groups.ContainsKey(string.Empty) || groups.Count != planned.Length ||
            planned.Select(source => source.SourceId).Distinct(StringComparer.Ordinal).Count() !=
                planned.Length)
        {
            return false;
        }
        foreach (var source in planned)
        {
            if (!groups.Remove(source.SourceId, out var pages) || pages.Length == 0 ||
                pages.Select((page, index) => (page, index)).Any(item =>
                    item.page.SourceId != $"{source.SourceId}:page:{item.index + 1}" ||
                    item.page.Page != item.index + 1 ||
                    item.page.OperationId != source.OperationId ||
                    item.page.Phase != source.Phase) ||
                pages[0].Route != source.Route ||
                (source.Pagination == "none"
                    ? pages.Length != 1 || pages[0].NextRoute is not null
                    : pages.Select((page, index) => (page, index)).Any(item =>
                        item.page.NextRoute != (item.index == pages.Length - 1
                            ? null
                            : pages[item.index + 1].Route))))
            {
                return false;
            }
        }
        return groups.Count == 0;
    }

    private static bool TryClassifyExactSource(
        CapturePlanSource source,
        string repository,
        string[] operationIds,
        string[] runs,
        IReadOnlyDictionary<string, string> roleRuns,
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
        if (source.SourceId == "producer-discovery-final")
        {
            classification = source.SourceId;
            var endpoint = $"{root}/actions/workflows/r4-trusted-proof.yml/runs";
            return source.Phase == "producer-discovery" &&
                source.EndpointFamily == endpoint &&
                source.Route == $"{endpoint}?per_page=100" &&
                source.Pagination == "complete-cursor";
        }
        var comment = Regex.Match(
            source.SourceId,
            "^authorization-(setup|execution)-comment-(normal|stale)-([1-9][0-9]*)$");
        if (comment.Success)
        {
            classification = $"authorization-{comment.Groups[1].Value}-comment-{comment.Groups[2].Value}";
            return source.Phase == $"baseline-{comment.Groups[2].Value}" &&
                Exact(source, $"{root}/issues/comments/{comment.Groups[3].Value}", "none");
        }
        var permission = Regex.Match(
            source.SourceId,
            "^authorization-(setup|execution)-permission-(normal|stale)-([A-Za-z0-9-]+)$");
        if (permission.Success)
        {
            classification = $"authorization-{permission.Groups[1].Value}-permission-{permission.Groups[2].Value}";
            return source.Phase == $"baseline-{permission.Groups[2].Value}" && Exact(
                source,
                $"{root}/collaborators/{permission.Groups[3].Value}/permission",
                "none");
        }
        var readiness = Regex.Match(
            source.SourceId,
            "^(baseline-(normal|stale)|readiness-(bootstrap|continuation|stale))-(environment-protection|environment-branch-policies|environment-secret-inventory|authorization-variable)$");
        if (readiness.Success)
        {
            var prefix = readiness.Groups[1].Value;
            var kind = readiness.Groups[4].Value;
            classification = $"{prefix}-{kind}";
            var expectedPhase = prefix.StartsWith("baseline-", StringComparison.Ordinal)
                ? prefix
                : $"{readiness.Groups[3].Value}-readiness";
            var endpoint = kind switch
            {
                "environment-protection" => $"{root}/environments/r4-trusted-proof",
                "environment-branch-policies" =>
                    $"{root}/environments/r4-trusted-proof/deployment-branch-policies",
                "environment-secret-inventory" => $"{root}/environments/r4-trusted-proof/secrets",
                _ => $"{root}/actions/variables/R4_TRUSTED_PROOF_AUTHORIZATION",
            };
            return source.Phase == expectedPhase && Exact(
                source,
                endpoint,
                kind is "environment-branch-policies" or "environment-secret-inventory"
                    ? "complete-cursor"
                    : "none");
        }
        var transition = Regex.Match(
            source.SourceId,
            "^transition-(bootstrap|continuation|stale|stale-follow-on)-(pending|approvals|jobs)-run-([1-9][0-9]*)$");
        if (transition.Success)
        {
            var phase = transition.Groups[1].Value;
            var kind = transition.Groups[2].Value;
            var run = transition.Groups[3].Value;
            var role = phase switch
            {
                "bootstrap" => "normal-bootstrap",
                "continuation" => "normal-continuation",
                "stale" => "stale-protected",
                _ => "stale-follow-on",
            };
            if (phase == "stale-follow-on" && kind != "jobs") return false;
            if (!roleRuns.TryGetValue(role, out var expectedRun) ||
                !StringComparer.Ordinal.Equals(run, expectedRun) ||
                source.Phase != $"{phase}-{(kind == "approvals" ? "approval" : kind)}") return false;
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
            var ownerRole = scope == "normal" ? "normal-bootstrap" : "stale-protected";
            var waiterRole = scope == "normal" ? "normal-continuation" : "stale-follow-on";
            if (!roleRuns.TryGetValue(ownerRole, out var expectedRun) ||
                !roleRuns.TryGetValue(waiterRole, out var waiter) ||
                !StringComparer.Ordinal.Equals(run, expectedRun) ||
                source.Phase != (scope == "normal" ? "bootstrap-concurrency" : "stale-concurrency"))
            {
                return false;
            }
            classification = $"concurrency-{scope}-run-{run}";
            return source.Pagination == "none" &&
                Regex.IsMatch(
                    source.Route,
                    $"^{Regex.Escape(root)}/actions/concurrency_groups/agentic-pr-review-r4-[1-9][0-9]*-pr-[1-9][0-9]*\\?ahead_of_run={Regex.Escape(waiter)}$");
        }
        var proofComment = Regex.Match(
            source.SourceId,
            "^proof-control-(bootstrap|continuation|stale)-comment-([1-9][0-9]*)$");
        if (proofComment.Success)
        {
            var phase = proofComment.Groups[1].Value;
            classification = $"proof-control-{phase}-comment";
            return source.Phase == $"{phase}-approval" &&
                Exact(source, $"{root}/issues/comments/{proofComment.Groups[2].Value}", "none");
        }
        var proofCommentInventory = Regex.Match(
            source.SourceId,
            "^proof-control-comments-(normal|stale)-pr-([1-9][0-9]*)$");
        if (proofCommentInventory.Success)
        {
            var scope = proofCommentInventory.Groups[1].Value;
            classification = $"proof-control-comments-{scope}";
            return source.Phase == $"terminal-{scope}" && Exact(
                source,
                $"{root}/issues/{proofCommentInventory.Groups[2].Value}/comments",
                "complete-cursor");
        }
        var proofPermission = Regex.Match(
            source.SourceId,
            "^proof-control-(bootstrap|stale)-permission-([1-9][0-9]*)-([A-Za-z0-9-]+)$");
        if (proofPermission.Success)
        {
            var phase = proofPermission.Groups[1].Value;
            classification = $"proof-control-{phase}-permission";
            return Exact(
                source,
                $"{root}/collaborators/{proofPermission.Groups[3].Value}/permission",
                "none") && source.Phase == $"{phase}-approval";
        }
        var runSource = Regex.Match(source.SourceId, "^(artifacts-run|run-terminal)-([1-9][0-9]*)$");
        if (runSource.Success && runs.Contains(runSource.Groups[2].Value, StringComparer.Ordinal))
        {
            var family = runSource.Groups[1].Value;
            var run = runSource.Groups[2].Value;
            classification = $"{family}-{run}";
            return source.OperationId == operationIds[
                    source.Phase == "terminal-stale" ? 1 : 0] &&
                source.Phase is "terminal-normal" or "terminal-stale" && Exact(
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
        var runs = value.ObservedRuns.Select(item => item.RunId).ToArray();
        var expected = new List<string>
        {
            "producer-discovery-final",
            "control-comments-normal",
            "control-comments-stale",
            "sticky-comments-normal",
            "sticky-comments-stale",
            "variables-normal",
            "variables-stale",
            "secrets-normal",
            "secrets-stale",
            "environment-normal",
            "environment-stale",
            "ref-normal",
            "ref-stale",
            "pr-normal",
            "pr-stale",
        };
        expected.AddRange(runs.Select(run => $"state-delete-{run}"));
        expected.AddRange(runs.Select(run => $"state-empty-{run}"));
        expected.AddRange(runs.Select(run => $"final-run-{run}"));
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
        if (source.SourceId == "producer-discovery-final")
        {
            classification = source.SourceId;
            var endpoint = $"{root}/actions/workflows/r4-trusted-proof.yml/runs";
            return source.Phase == "producer-discovery" &&
                source.OperationId == operationIds[0] &&
                source.EndpointFamily == endpoint &&
                source.Route == $"{endpoint}?per_page=100" &&
                source.Pagination == "complete-cursor";
        }
        var comments = Regex.Match(
            source.SourceId,
            "^post-cleanup-(control|sticky)-comments-(normal|stale)-pr-([1-9][0-9]*)$");
        if (comments.Success)
        {
            classification = $"{comments.Groups[1].Value}-comments-{comments.Groups[2].Value}";
            prNumbers.Add(comments.Groups[3].Value);
            return Exact(
                source,
                $"{root}/issues/{comments.Groups[3].Value}/comments",
                "complete-cursor");
        }
        var artifacts = Regex.Match(
            source.SourceId,
            "^post-cleanup-state-(delete|empty)-run-([1-9][0-9]*)$");
        if (artifacts.Success && runs.Contains(artifacts.Groups[2].Value, StringComparer.Ordinal))
        {
            var run = artifacts.Groups[2].Value;
            classification = $"state-{artifacts.Groups[1].Value}-{run}";
            return Exact(source, $"{root}/actions/runs/{run}/artifacts", "complete-cursor");
        }
        var global = Regex.Match(source.SourceId, "^post-cleanup-(variables|secrets|environment)-(normal|stale)$");
        if (global.Success)
        {
            var kind = global.Groups[1].Value;
            var scope = global.Groups[2].Value;
            classification = $"{kind}-{scope}";
            var endpoint = kind switch
            {
                "variables" => $"{root}/actions/variables",
                "secrets" => $"{root}/environments/r4-trusted-proof/secrets",
                _ => $"{root}/environments/r4-trusted-proof",
            };
            return Exact(source, endpoint, kind == "environment" ? "none" : "complete-cursor");
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
        var runSource = Regex.Match(source.SourceId, "^post-cleanup-final-run-([1-9][0-9]*)$");
        if (runSource.Success && runs.Contains(runSource.Groups[1].Value, StringComparer.Ordinal))
        {
            var run = runSource.Groups[1].Value;
            classification = $"final-run-{run}";
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
