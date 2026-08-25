namespace AgenticPrReview.Runtime.ActionHostTrustedProofEvidenceContracts;

public static class EvidenceLimits
{
    public const int MaximumNameBytes = 256;
    public const int MaximumCorrelationBytes = 256;
    public const int MaximumRelativePathBytes = 1_024;
    public const int MaximumEncryptedObjectBytes = 2 * 1024 * 1024;
    public const int MaximumArchiveBytes = 4 * 1024 * 1024;
    public const int MaximumDocumentBytes = 256 * 1024;
    public const int RecordsPerPage = 100;
    public const int MaximumPages = 3;
    public const int MaximumRecords = 256;
    public const int MaximumCredentialBytes = 4 * 1024;
    public const int MaximumCompressionRatio = 100;
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LogicalOperationTimeout = TimeSpan.FromSeconds(120);
}
