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
