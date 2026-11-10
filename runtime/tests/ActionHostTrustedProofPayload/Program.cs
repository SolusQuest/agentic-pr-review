#if !APR_TEST_ORIGINAL_RUNTIME_PREVIEW
namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.Length switch
            {
                0 => await TrustedProofPayloadHost.RunAsync()
                    .ConfigureAwait(false),
                _ when args[0] == "barrier" =>
                    await TrustedProofControlCli.RunAsync(args[1..])
                        .ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            Console.Error.WriteLine(
                "APR_R4_E2P_UNHANDLED " + exception.GetType().Name);
            return 1;
        }
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
#else
namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

// No default Host route. Only the fixed parent seal consumer can launch a phase;
// the embedded expected context is identity data, never a serialized custody seal.
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["test-only-reference-metadata"])
                return OriginalRuntimePreviewReferenceProbe.Inspect();
            if (args is ["test-only-original-loader-probe"])
            {
                using var probe = OriginalRuntimePreviewLoader.Open();
                probe.Validate();
                if (!probe.RuntimeLoaded) return 2;
                Console.WriteLine("APR_TEST_ORIGINAL_LOADER_PROBE {\"assembly_loaded\":true,\"original_assembly_sha256\":\"" +
                    OriginalRuntimeReferenceBuildIdentity.AssemblySHA256 + "\",\"qualification\":false,\"provider_calls\":0}");
                return 0;
            }
            if (args is not ["test-only-original-bootstrap"] and
                not ["test-only-original-continuation"]) return 2;
            using var original = OriginalRuntimePreviewLoader.Open();
            return await OriginalRuntimePreviewHost.RunAsync(original,
                args[0] == "test-only-original-bootstrap").ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            Console.Error.WriteLine("APR_TEST_ORIGINAL_REFERENCE_REJECTED");
            return 2;
        }
    }
}
#endif
