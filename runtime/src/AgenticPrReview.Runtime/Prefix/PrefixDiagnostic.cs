namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Prefix-contract diagnostic. Message is a fixed template per code with an
/// optional safe path; it never embeds caller content.
/// </summary>
public sealed class PrefixDiagnostic
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? CauseCode { get; init; }

    internal static PrefixDiagnostic Create(string code, string? causeCode = null, string? path = null)
    {
        var message = path is null ? code : code + ":" + path;
        return new PrefixDiagnostic { Code = code, Message = message, CauseCode = causeCode };
    }
}
