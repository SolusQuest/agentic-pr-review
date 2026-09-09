using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

internal static class ReplayAdmission
{
    internal static ReplayAdmissionResult Load(string root, CancellationToken cancellationToken = default)
    {
        try
        {
            var captured = ReplayDirectory.Capture(root, cancellationToken);
            var manifest = captured.Manifest;
            var corpus = AgentCanonical.HashDomain("apr.r5.replay.corpus", ReplayJson.Write(manifest));
            var roles = manifest.Files.ToDictionary(file => file.Path, file => file.Role, StringComparer.Ordinal);
            var used = new HashSet<string>(StringComparer.Ordinal);
            ImmutableArray<byte> Member(string path, string role)
            {
                if (!ReplayLimits.Path(path) || !roles.TryGetValue(path, out var actual) || actual != role)
                    throw new ReplayRejected(ReplayAdmissionCode.InvalidReference);
                used.Add(path);
                return captured.Files[path];
            }
            var runs = ImmutableArray.CreateBuilder<AdmittedReplayRun>();
            foreach (var input in manifest.Runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var repository = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.Ordinal);
                foreach (var entry in input.Repository)
                {
                    var bytes = Member(entry.File, "repository");
                    _ = SourceLines(bytes.AsSpan());
                    repository.Add(entry.Path, bytes);
                }
                var diffs = ReplayJson.Read(Member(input.Diff, "diff").AsSpan(), ReplayJsonContext.Default.ReplayDiffDocument);
                var sources = AdmitDiffs(diffs, input.ReviewedIdentity.Runtime, repository.ToImmutable());
                var policy = Member(input.Policy, "policy");
                var context = ReplayLimits.Utf8.GetString(Member(input.Context, "context").AsSpan());
                if (policy.IsEmpty || !ReplayLimits.Text(context, AgentLimits.ContentBytes))
                    throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
                var script = ReplayJson.Read(Member(input.Script, "script").AsSpan(), ReplayJsonContext.Default.ReplayScript);
                if (!ValidScript(script)) throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
                var assertions = ReplayJson.Read(Member(input.Assertions, "assertions").AsSpan(), ReplayJsonContext.Default.ReplayAssertions);
                if (assertions is null || !Enum.TryParse<EvaluationCode>(assertions.ExpectedCode, out var expectedCode) ||
                    !Enum.IsDefined(expectedCode) || expectedCode.ToString() != assertions.ExpectedCode)
                    throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
                var expected = EvaluationCase.Admit(new(input.CaseId, corpus, input.ReviewedIdentity,
                    assertions.Defects, assertions.RequiredObservations, assertions.ProhibitedFindings));
                if (expected is null) throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
                runs.Add(new(input, manifest.Configuration, repository.ToImmutable(), sources, policy, context, script!, expected, expectedCode));
            }
            if (used.Count != captured.Files.Count) throw new ReplayRejected(ReplayAdmissionCode.InvalidReference);
            cancellationToken.ThrowIfCancellationRequested();
            return new(ReplayAdmissionCode.Admitted, new(corpus, manifest.Files, runs.ToImmutable()));
        }
        catch (ReplayRejected error) { return new(error.Code, null); }
        catch (OperationCanceledException) { return new(ReplayAdmissionCode.Cancelled, null); }
        catch (Exception error) when (error is DecoderFallbackException or EncoderFallbackException or ArgumentException or OverflowException or NotSupportedException)
        { return new(ReplayAdmissionCode.InvalidContent, null); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { return new(ReplayAdmissionCode.IoFailure, null); }
    }

    internal static bool ValidManifest(ReplayManifest manifest)
    {
        if (manifest.Format != ReplayLimits.Format || manifest.SourceKind != "authored-synthetic" ||
            manifest.Configuration is not { } configuration ||
            !ReplayLimits.Text(configuration.WorkflowIdentity, 256) || !ReplayLimits.Text(configuration.ProviderId, 128) ||
            !ReplayLimits.Text(configuration.ModelId, 128) || !ReplayLimits.Text(configuration.AdapterId, 128) ||
            manifest.Files.IsDefault || manifest.Files.Length is < 1 or > ReplayLimits.Files ||
            manifest.Runs.IsDefault || manifest.Runs.Length is < 1 or > ReplayLimits.Runs) return false;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long aggregate = 0;
        string? previousPath = null;
        foreach (var file in manifest.Files)
        {
            if (file is null || !ReplayLimits.Path(file.Path) || file.Path.Equals(ReplayLimits.ManifestName, StringComparison.OrdinalIgnoreCase) ||
                !ReplayLimits.Role(file.Role) || file.Length is < 0 or > ReplayLimits.FileBytes || !EvaluationLimits.Hash(file.Sha256) ||
                !paths.Add(file.Path) || previousPath is not null && StringComparer.Ordinal.Compare(previousPath, file.Path) >= 0) return false;
            aggregate += file.Length;
            previousPath = file.Path;
            for (var index = file.Path.IndexOf('/'); index >= 0; index = file.Path.IndexOf('/', index + 1))
            {
                var directory = file.Path[..index];
                if (directories.TryGetValue(directory, out var spelling) && spelling != directory) return false;
                directories[directory] = directory;
            }
        }
        if (aggregate > ReplayLimits.TotalBytes || directories.Keys.Any(paths.Contains) ||
            directories.ContainsKey(ReplayLimits.ManifestName)) return false;
        var runIds = new HashSet<string>(StringComparer.Ordinal);
        var caseIds = new HashSet<string>(StringComparer.Ordinal);
        ReplayRun? previous = null;
        foreach (var run in manifest.Runs)
        {
            if (run is null || !EvaluationLimits.Id(run.Id) || !EvaluationLimits.Id(run.CaseId) ||
                !runIds.Add(run.Id) || !caseIds.Add(run.CaseId) || run.ReviewedIdentity is not { Valid: true } ||
                run.Repository.IsDefault || run.Repository.Length > ReplayLimits.Files) return false;
            if (previous is null)
            {
                if (run.Transition != "initial" || run.PreviousRunId is not null) return false;
            }
            else if (run.PreviousRunId != previous.Id || run.ReviewedIdentity.RepositoryId != previous.ReviewedIdentity.RepositoryId ||
                run.ReviewedIdentity.ReviewTarget != previous.ReviewedIdentity.ReviewTarget ||
                !(run.Transition == "same_head" && run.ReviewedIdentity == previous.ReviewedIdentity ||
                    run.Transition == "verified_ahead" && run.ReviewedIdentity.HeadSha != previous.ReviewedIdentity.HeadSha)) return false;
            var tracked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in run.Repository)
                if (entry is null || !ReplayLimits.Path(entry.Path) || !ReplayLimits.Path(entry.File) || !tracked.Add(entry.Path)) return false;
            if (!new[] { run.Diff, run.Policy, run.Context, run.Script, run.Assertions }.All(ReplayLimits.Path)) return false;
            previous = run;
        }
        return true;
    }

    private static bool ValidScript(ReplayScript? script) => script is not null && !script.Turns.IsDefault &&
        script.Turns.Length is >= 1 and <= AgentLimits.ModelCalls && script.Turns.All(turn => turn is not null &&
            !turn.ToolCalls.IsDefault && turn.ToolCalls.Length <= AgentLimits.ToolCallsPerResponse &&
            ReplayLimits.Text(turn.ReasoningContent, AgentLimits.ContentBytes, true) &&
            turn.ToolCalls.All(call => call is not null && EvaluationLimits.Id(call.Id) && EvaluationLimits.Id(call.Name) &&
                ReplayLimits.Text(call.ArgumentsJson, AgentLimits.ToolArgumentsBytes, true)));

    private static ImmutableArray<ReviewedDiffSource> AdmitDiffs(ReplayDiffDocument? document, ReviewedIdentity identity,
        ImmutableDictionary<string, ImmutableArray<byte>> repository)
    {
        if (document is null || document.Changes.IsDefault || document.Changes.Length > AgentLimits.ChangedFiles)
            throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
        var sources = ImmutableArray.CreateBuilder<ReviewedDiffSource>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in document.Changes)
        {
            if (change is null || !ReplayLimits.Path(change.Path) || !paths.Add(change.Path) ||
                change.PreviousPath is not null && !ReplayLimits.Path(change.PreviousPath) ||
                change.Hunks.IsDefault || change.Hunks.Length > AgentLimits.DiffHunksPerFile)
                throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
            var hunks = change.Hunks.Select(hunk =>
            {
                if (hunk is null || hunk.Lines.IsDefault || hunk.Lines.Length > AgentLimits.DiffLinesPerHunk || hunk.Lines.Any(line => line is null))
                    throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
                return new ReviewedDiffHunk(hunk.OldStart, hunk.OldCount, hunk.NewStart, hunk.NewCount,
                    hunk.Lines.Select(line => new ReviewedDiffLine(line.Kind, line.OldLine, line.NewLine, line.Text)));
            });
            var source = new ReviewedDiffSource(identity, change.Path, change.PreviousPath, change.Status, change.SourceTruncated, hunks);
            var hasHead = repository.TryGetValue(source.Path, out var bytes);
            var represented = source.Hunks.SelectMany(hunk => hunk.Lines).Where(line => line.Kind is "context" or "addition").ToArray();
            if (source.Status == "removed")
            {
                if (hasHead || represented.Length != 0) throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
            }
            else
            {
                if (!hasHead) throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
                var lines = SourceLines(bytes.AsSpan());
                if (represented.Any(line => line.NewLine is not { } position || position > lines.Length || lines[position - 1] != line.Text))
                    throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
            }
            sources.Add(source);
        }
        return sources.ToImmutable();
    }

    private static string[] SourceLines(ReadOnlySpan<byte> bytes)
    {
        var text = ReplayLimits.Utf8.GetString(bytes);
        if (text.StartsWith('\uFEFF')) text = text[1..];
        if (text.Contains('\0')) throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
        for (var index = 0; index < text.Length; index++)
            if (text[index] == '\r' && (index + 1 >= text.Length || text[index + 1] != '\n'))
                throw new ReplayRejected(ReplayAdmissionCode.InvalidContent);
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }
}
