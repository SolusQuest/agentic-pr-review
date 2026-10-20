namespace AgenticPrReview.Runtime.AgentLoopAotFixture;

internal static class Program
{
    private static int Retired()
    {
        Console.WriteLine("APR_NATIVE_GENERIC_EXECUTION_RETIRED");
        return 2;
    }

    internal static async Task<int> Main(string[] args)
    {
        try
        {
            if (!ProofArguments.TryParse(args, out var command))
            {
                Console.Error.WriteLine(ProofCodes.ArgumentsInvalid);
                return 2;
            }

            var environment = ProofEnvironment.Validate(command!);
            if (!environment.Succeeded)
            {
                Console.WriteLine(environment.Code);
                return environment.ExitCode;
            }

            return command!.Verb switch
            {
                "execute" => await ProofOrchestrator.RunAsync(command),
                "negative" => Retired(),
                "r7-retry" => Retired(),
                _ => 2,
            };
        }
        catch
        {
            Console.Error.WriteLine(ProofCodes.Unexpected);
            return 1;
        }
    }
}
