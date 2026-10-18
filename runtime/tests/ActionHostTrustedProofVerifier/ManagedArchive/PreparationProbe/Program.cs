using System.Text.Json;
using AgenticPrReview.Runtime.Solus;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Serialization;
using AgenticPrReview.Runtime.Application.Solus;

// Supporting construction diagnostics. These never run Agent/state/publication
// work and never qualify an archive or replace the actual generated Action.
static string Failure(Exception error) => error switch {
    InvalidOperationException => "invalid-operation",
    NotSupportedException => "not-supported",
    TypeInitializationException => "type-initialization",
    ArgumentException => "argument",
    _ => "other",
};
var count = 0;
var failure = "none";
try
{
    foreach (var name in new[] { "list_files", "list_changed_files", "read_diff", "read_file", "search_text" })
    {
        var descriptor = SolusToolSchemas.Descriptor(name);
        if (string.IsNullOrWhiteSpace(descriptor.InputSchema.NormalizedJson) ||
            string.IsNullOrWhiteSpace(descriptor.ResultSchema.NormalizedJson)) throw new InvalidOperationException();
        count++;
    }
}
catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failure = Failure(error); }
Console.WriteLine($"APR_D5_PREPARATION_PROBE descriptors={count} reflection={(JsonSerializer.IsReflectionEnabledByDefault ? "enabled" : "disabled")} failure={failure} archive_qualified=false");
if (args.Length == 1)
{
    var contextLive = false;
    var phase = "launch";
    var hostStatus = "unavailable";
    failure = "none";
    try
    {
        if (!ActionHostJsonCodec.TryReadLaunch(await File.ReadAllBytesAsync(args[0]), out var launch, out _) || launch is null)
            throw new ArgumentException();
        var host = new ActionHostComposition();
        var review = new SolusFreshReviewComposition(host);
        phase = "host-preparation";
        var result = await host.RunPreparedFreshAsync(launch, prepared => {
            phase = "prepared-callback";
            contextLive = prepared.FreshContext?.IsLive == true;
            try { _ = review.PrepareStateExecution(prepared); phase = "description-complete"; }
            catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failure = Failure(error); }
            return Task.FromResult(new ProbeResult());
        }, CancellationToken.None);
        hostStatus = result.Failure?.Status.ToString() ?? "callback";
    }
    catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException)) { failure = Failure(error); }
    Console.WriteLine($"APR_D5_PREPARATION_HOST context_live={(contextLive ? "true" : "false")} phase={phase} failure={failure} host_status={hostStatus} archive_qualified=false");
}
internal sealed record ProbeResult;
