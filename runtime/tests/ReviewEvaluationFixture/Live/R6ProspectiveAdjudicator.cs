using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;

internal sealed record R6ProspectiveReviewResult(
    string Status, string Cleanup, int HumanCases, int AiCases,
    ImmutableArray<R6ProspectiveCaseReceipt> Receipts);

// The invocation selects origin. The editable sidecar contains no origin field.
internal sealed class R6ProspectiveAdjudicator(TextReader input, TextWriter prompts, string origin)
{
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromHours(1);

    internal async Task<R6ProspectiveReviewResult> ReviewAsync(
        IReadOnlyList<LiveAdjudicationCase> cases, LivePlanRubric rubric,
        CancellationToken token)
    {
        if (!R6ProspectiveRubric.IsV1(rubric) && !R6ProspectiveRubric.IsV2(rubric))
            throw new InvalidOperationException("prospective_rubric_invalid");
        var v2 = R6ProspectiveRubric.IsV2(rubric);
        string? root = null;
        var status = cases.Count == 0 ? "not_evaluable" : "adjudicated";
        var cleanup = "none";
        var receipts = ImmutableArray.CreateBuilder<R6ProspectiveCaseReceipt>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(Timeout);
        try
        {
            if (origin is not (R6ProspectiveAssessment.AiOrigin or R6ProspectiveAssessment.HumanOrigin))
                throw new InvalidOperationException("prospective_origin_invalid");
            root = ReplayProcess.CreatePrivateRoot();
            await prompts.WriteLineAsync("r6_review_private_directory " + root);
            foreach (var item in cases.OrderBy(c => c.Index))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (item.Index is < 0 or >= 5)
                    throw new InvalidOperationException("prospective_case_index_invalid");
                var stem = v2 ? "case-" + item.Index : "review";
                var packetPath = Path.Join(root, stem + ".json");
                var annotationPath = Path.Join(root, v2 ? stem + "-annotation.json" :
                    "annotation.json");
                await WritePacketAsync(packetPath, item, rubric, deadline.Token);
                var initial = new R6ProspectiveAnnotation(rubric.Id,
                    rubric.Sha256, item.Run.Expected.Input.CorpusSha256,
                    item.Run.Expected.Sha256, item.Subject.ConfigurationSha256,
                    item.Subject.ExecutionSha256,
                    item.Subject.Findings.Select((_, ordinal) => new R6ProspectiveFindingAnnotation(
                        ordinal, R6ProspectiveAssessment.Unresolved, null, null, null, [],
                        v2 ? [] : null)).ToImmutableArray());
                await File.WriteAllBytesAsync(annotationPath,
                    R6ProspectiveAssessment.WriteAnnotation(initial), deadline.Token);
                var accepted = false;
                var skipped = false;
                for (var attempt = 0; attempt < (v2 ? 2 : 1); attempt++)
                {
                    await prompts.WriteLineAsync("r6_review_case " + item.Index +
                        " inspect " + Path.GetFileName(packetPath) + "; edit " +
                        Path.GetFileName(annotationPath) +
                        (v2 ? "; compare causes across findings and prior accepted case files; reuse one group id for the same cause; mark unresolved if uncertain" : "") +
                        "; enter accept, skip or stop");
                    await prompts.FlushAsync(deadline.Token);
                    var command = await ReadCommandAsync(deadline.Token);
                    if (command is null or "stop")
                    { status = v2 ? "review_incomplete" : "pending"; break; }
                    if (command == "skip")
                    { status = v2 ? "review_incomplete" : "pending"; skipped = true; break; }
                    if (command != "accept")
                    { status = v2 ? "review_incomplete" : "input_invalid"; break; }
                    var info = new FileInfo(annotationPath);
                    if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        status = v2 ? "failed" : "input_invalid";
                        break;
                    }
                    R6ProspectiveAnnotation? annotation = null;
                    if (info.Exists && info.Length is >= 1 and <= EvaluationLimits.InputBytes)
                    {
                        var bytes = new byte[EvaluationLimits.InputBytes + 1];
                        int length;
                        using (var stream = new FileStream(annotationPath, FileMode.Open,
                            FileAccess.Read, FileShare.Read))
                            length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false,
                                deadline.Token);
                        annotation = R6ProspectiveAssessment.ReadAnnotation(
                            bytes.AsSpan(0, length));
                    }
                    if (v2 && !R6ProspectiveAssessment.ValidV2EditableEnvelope(
                            item.Index, item.Run.Expected, item.Subject, annotation, rubric))
                    {
                        if (attempt == 1) status = "review_incomplete";
                        continue;
                    }
                    var receipt = R6ProspectiveAssessment.Assess(item.Index,
                        item.Run.Expected, item.Subject, annotation, origin, rubric);
                    if (receipt is null)
                    {
                        status = v2 ? "review_incomplete" : "input_invalid";
                        break;
                    }
                    receipts.Add(receipt);
                    accepted = true;
                    break;
                }
                if (!accepted)
                {
                    if (skipped) continue;
                    break;
                }
                if (!v2)
                {
                    File.Delete(packetPath);
                    File.Delete(annotationPath);
                }
            }
            if (status == "adjudicated" && receipts.Count != cases.Count)
                status = v2 ? "review_incomplete" : "pending";
        }
        catch (OperationCanceledException)
        { status = v2 ? "review_incomplete" : "cancelled"; }
        catch { status = "failed"; }
        finally
        {
            if (root is not null) cleanup = ReplayProcess.Cleanup(root) ? "cleaned" : "cleanup_failed";
        }
        if (cleanup == "cleanup_failed") status = "cleanup_failed";
        return new(status, cleanup,
            origin == R6ProspectiveAssessment.HumanOrigin ? receipts.Count : 0,
            origin == R6ProspectiveAssessment.AiOrigin ? receipts.Count : 0,
            receipts.ToImmutable());
    }

    private async Task<string?> ReadCommandAsync(CancellationToken token)
    {
        var value = new StringBuilder();
        var one = new char[1];
        while (value.Length <= 32)
        {
            if (await Task.Run(async () => await input.ReadAsync(one.AsMemory(), token),
                CancellationToken.None).WaitAsync(token) == 0) return null;
            if (one[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(one[0]);
        }
        return "invalid";
    }

    private static async Task WritePacketAsync(string path, LiveAdjudicationCase item,
        LivePlanRubric rubric, CancellationToken token)
    {
        await LiveAdjudicator.WritePacketAsync(path, item, token);
        using var original = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in original.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteString("rubric_id", rubric.Id);
            writer.WriteString("rubric_sha256", rubric.Sha256);
            writer.WriteStartArray("returned_observations");
            foreach (var observed in item.Subject.GroundedObservations)
            {
                writer.WriteStartObject();
                writer.WriteString("tool", observed.Tool);
                writer.WriteString("observation_id", observed.Observation.ObservationId);
                writer.WriteStartObject("returned_lines");
                foreach (var pair in observed.Observation.ReturnedLines.OrderBy(p => p.Key,
                    StringComparer.Ordinal))
                {
                    writer.WriteStartArray(pair.Key);
                    foreach (var line in pair.Value.Order()) writer.WriteNumberValue(line);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        if (output.Length > LiveAdjudicator.PacketBytes) throw new IOException();
        await File.WriteAllBytesAsync(path, output.ToArray(), token);
    }
}
