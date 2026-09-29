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
        IReadOnlyList<LiveAdjudicationCase> cases, CancellationToken token)
    {
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
                var packetPath = Path.Combine(root, "review.json");
                var annotationPath = Path.Combine(root, "annotation.json");
                await WritePacketAsync(packetPath, item, deadline.Token);
                var initial = new R6ProspectiveAnnotation(R6ProspectiveRubric.Id,
                    R6ProspectiveRubric.Sha256, item.Run.Expected.Input.CorpusSha256,
                    item.Run.Expected.Sha256, item.Subject.ConfigurationSha256,
                    item.Subject.ExecutionSha256,
                    item.Subject.Findings.Select((_, ordinal) => new R6ProspectiveFindingAnnotation(
                        ordinal, R6ProspectiveAssessment.Unresolved, null, null, null, [])).ToImmutableArray());
                await File.WriteAllBytesAsync(annotationPath,
                    R6ProspectiveAssessment.WriteAnnotation(initial), deadline.Token);
                await prompts.WriteLineAsync("r6_review_case " + item.Index +
                    " inspect review.json; edit annotation.json; enter accept, skip or stop");
                await prompts.FlushAsync(deadline.Token);
                var command = await ReadCommandAsync(deadline.Token);
                if (command is null or "stop") { status = "pending"; break; }
                if (command == "skip") { status = "pending"; continue; }
                if (command != "accept") { status = "input_invalid"; break; }
                var info = new FileInfo(annotationPath);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                    info.Length is < 1 or > EvaluationLimits.InputBytes)
                { status = "input_invalid"; break; }
                var bytes = new byte[EvaluationLimits.InputBytes + 1];
                int length;
                using (var stream = new FileStream(annotationPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    length = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token);
                var annotation = R6ProspectiveAssessment.ReadAnnotation(bytes.AsSpan(0, length));
                var receipt = R6ProspectiveAssessment.Assess(item.Index, item.Run.Expected,
                    item.Subject, annotation, origin);
                if (receipt is null) { status = "input_invalid"; break; }
                receipts.Add(receipt);
                File.Delete(packetPath);
                File.Delete(annotationPath);
            }
            if (status == "adjudicated" && receipts.Count != cases.Count) status = "pending";
        }
        catch (OperationCanceledException) { status = "cancelled"; }
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
        CancellationToken token)
    {
        await LiveAdjudicator.WritePacketAsync(path, item, token);
        using var original = JsonDocument.Parse(await File.ReadAllBytesAsync(path, token));
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            foreach (var property in original.RootElement.EnumerateObject())
                property.WriteTo(writer);
            writer.WriteString("rubric_id", R6ProspectiveRubric.Id);
            writer.WriteString("rubric_sha256", R6ProspectiveRubric.Sha256);
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
