using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

internal static class ReplayLimits
{
    internal const string Format = "apr.r5.synthetic-replay.v1";
    internal const string ManifestName = "manifest.json";
    internal const int ManifestBytes = 64 * 1024;
    internal const int FileBytes = 64 * 1024;
    internal const int TotalBytes = 512 * 1024;
    internal const int Files = 64;
    internal const int Runs = 16;
    internal const int PathBytes = 256;
    internal const int PathSegments = 8;
    internal const int Depth = 12;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static bool Path(string? path)
    {
        if (path is null || !RepositoryPath.IsValid(path) || Utf8.GetByteCount(path) > PathBytes) return false;
        var segments = path.Split('/');
        return segments.Length <= PathSegments && segments.All(segment =>
        {
            var stem = segment.Split('.')[0].ToUpperInvariant();
            return stem is not ("CON" or "PRN" or "AUX" or "NUL") &&
                !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
        });
    }

    internal static bool Role(string? role) => role is "repository" or "diff" or "policy" or "context" or "script" or "assertions";
    internal static bool Text(string? text, int maximum, bool empty = false) =>
        text is not null && AgentValueDomains.IsUtf8(text, empty ? 0 : 1, maximum);
}

internal sealed record ReplayFile(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string Role,
    [property: JsonRequired] int Length,
    [property: JsonRequired] string Sha256);

internal sealed record ReplayConfiguration(
    [property: JsonRequired] string WorkflowIdentity,
    [property: JsonRequired] string ProviderId,
    [property: JsonRequired] string ModelId,
    [property: JsonRequired] string AdapterId);

internal sealed record ReplayRepositoryEntry(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string File);

internal sealed record ReplayRun(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string CaseId,
    [property: JsonRequired] string Transition,
    [property: JsonRequired] string? PreviousRunId,
    [property: JsonRequired] EvaluationReviewedIdentity ReviewedIdentity,
    [property: JsonRequired] ImmutableArray<ReplayRepositoryEntry> Repository,
    [property: JsonRequired] string Diff,
    [property: JsonRequired] string Policy,
    [property: JsonRequired] string Context,
    [property: JsonRequired] string Script,
    [property: JsonRequired] string Assertions);

internal sealed record ReplayManifest(
    [property: JsonRequired] string Format,
    [property: JsonRequired] string SourceKind,
    [property: JsonRequired] ReplayConfiguration Configuration,
    [property: JsonRequired] ImmutableArray<ReplayFile> Files,
    [property: JsonRequired] ImmutableArray<ReplayRun> Runs);

internal sealed record ReplayDiffLine(
    [property: JsonRequired] string Kind,
    [property: JsonRequired] int? OldLine,
    [property: JsonRequired] int? NewLine,
    [property: JsonRequired] string Text);

internal sealed record ReplayDiffHunk(
    [property: JsonRequired] int OldStart,
    [property: JsonRequired] int OldCount,
    [property: JsonRequired] int NewStart,
    [property: JsonRequired] int NewCount,
    [property: JsonRequired] ImmutableArray<ReplayDiffLine> Lines);

internal sealed record ReplayDiff(
    [property: JsonRequired] string Path,
    [property: JsonRequired] string? PreviousPath,
    [property: JsonRequired] string Status,
    [property: JsonRequired] bool SourceTruncated,
    [property: JsonRequired] ImmutableArray<ReplayDiffHunk> Hunks);

internal sealed record ReplayDiffDocument([property: JsonRequired] ImmutableArray<ReplayDiff> Changes);

// Authored provider inputs may intentionally cause an Agent rejection. They are never tool observations or SESSION data.
internal sealed record ReplayToolCall(
    [property: JsonRequired] string Id,
    [property: JsonRequired] string Name,
    [property: JsonRequired] string ArgumentsJson);

internal sealed record ReplayScriptTurn(
    [property: JsonRequired] ImmutableArray<ReplayToolCall> ToolCalls,
    [property: JsonRequired] string ReasoningContent);

internal sealed record ReplayScript([property: JsonRequired] ImmutableArray<ReplayScriptTurn> Turns)
{
    public override string ToString() => "replay_script";
}

internal sealed record ReplayAssertions(
    [property: JsonRequired] string ExpectedCode,
    [property: JsonRequired] ImmutableArray<ExpectedDefect> Defects,
    [property: JsonRequired] ImmutableArray<RequiredObservation> RequiredObservations,
    [property: JsonRequired] ImmutableArray<ProhibitedFinding> ProhibitedFindings);

internal enum ReplayAdmissionCode { Admitted, InvalidManifest, UnsafeEntry, ContentMismatch, InvalidContent, InvalidReference, Cancelled, IoFailure }

internal sealed record ReplayAdmissionResult(ReplayAdmissionCode Code, AdmittedReplayFixture? Fixture)
{
    public override string ToString() => Code.ToString();
}
