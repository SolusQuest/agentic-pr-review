namespace AgenticPrReview.Runtime.Host.State.OpaqueStore;

// Transport sizing allowances, not activation of the corresponding semantic
// records. See README.md; C3 owns larger SESSION/transaction admission.
internal static class OpaqueStoreCapacity
{
    internal const int InnerEnvelopeBytes = 32 * 1024 * 1024;
    internal const int GenerationBytes = InnerEnvelopeBytes + 256 * 1024 + 16 * 1024;
    internal const int PhysicalCopyBytes = GenerationBytes + 16 * 1024;
    internal const int ControlFramingBytes = 132;
    internal const int ControlWrapperBytes = 16 * 1024 + ControlFramingBytes;
    internal const int AcceptanceRecoveryBytes =
        64 * 1024 + ControlWrapperBytes + PhysicalCopyBytes + ControlWrapperBytes + 1024;
    internal const int RecoveryRecordBytes = AcceptanceRecoveryBytes + 64 * 1024;
    internal const int TargetEnvelopeBytes = RecoveryRecordBytes + ControlWrapperBytes;
    internal const int AnchorBytes = TargetEnvelopeBytes + 2048;
    internal const int MaximumObjectBytes = AnchorBytes + ControlWrapperBytes;
    internal const int MaximumBase64Bytes = 4 * ((MaximumObjectBytes + 2) / 3);
    internal const int EnvelopeMetadataBytes = 290;
    internal const int MaximumTransportEnvelopeBytes = MaximumBase64Bytes + EnvelopeMetadataBytes;
    internal const int ZipFramingBytes = 158;
    internal const int MaximumArchiveBytes = MaximumTransportEnvelopeBytes +
        (MaximumTransportEnvelopeBytes + 4095) / 4096 +
        (MaximumTransportEnvelopeBytes + 16383) / 16384 +
        (MaximumTransportEnvelopeBytes + 33554431) / 33554432 + 13 + ZipFramingBytes;
}
