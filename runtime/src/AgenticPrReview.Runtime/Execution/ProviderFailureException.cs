namespace AgenticPrReview.Runtime;

public sealed class ProviderFailureException : Exception
{
    public ProviderFailureException()
        : this("APR_PROVIDER_TRANSPORT", 30)
    {
    }

    public ProviderFailureException(string code, int exitCode)
        : base("Provider invocation failed.")
    {
        Code = code;
        ExitCode = exitCode;
    }

    public string Code { get; }

    public int ExitCode { get; }
}
