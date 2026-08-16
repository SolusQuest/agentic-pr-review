namespace AgenticPrReview.Runtime.ActionHostVerifierFixture;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return args.Length switch
            {
                0 => await FrameworkHost.RunAsync().ConfigureAwait(false),
                _ when args[0] == "supervise" =>
                    await FrameworkSupervisor.RunAsync(args[1..])
                        .ConfigureAwait(false),
                _ => 2,
            };
        }
        catch (Exception exception) when (IsNonFatal(exception))
        {
            Console.Error.WriteLine("APR_ACTION_HOST_FRAMEWORK_UNHANDLED " +
                exception.GetType().Name);
            return 1;
        }
    }

    private static bool IsNonFatal(Exception exception) =>
        exception is not OutOfMemoryException and
        not StackOverflowException and
        not AccessViolationException;
}
