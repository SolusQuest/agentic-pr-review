using System.Buffers;
using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Interaction-id derivation per ## Prefix Contract and the shared
/// interactionId scope clarification. The host derives the id; this helper is
/// the runtime-side producer/verifier of the same pure function.
/// </summary>
public static class InteractionIdDeriver
{
    public static InteractionIdOutcome Derive(
        PredecessorLedgerReference predecessor,
        string consumedInputSha256,
        string currentHeadSha,
        long interactionOrdinal)
    {
        string predecessorComponent;
        switch (predecessor)
        {
            case PredecessorLedgerReference.Bootstrap:
                predecessorComponent = "bootstrap";
                break;
            case PredecessorLedgerReference.LedgerHash ledgerHash:
                if (!PrefixIdentityValidation.IsValidDigest(ledgerHash.Sha256Hex))
                {
                    return Fail(PrefixDiagnosticCodes.DigestInvalid, "/predecessor");
                }

                predecessorComponent = ledgerHash.Sha256Hex;
                break;
            default:
                return Fail(PrefixDiagnosticCodes.IdentityInvalid, "/predecessor");
        }

        if (!PrefixIdentityValidation.IsValidDigest(consumedInputSha256))
        {
            return Fail(PrefixDiagnosticCodes.DigestInvalid, "/consumedInputSha256");
        }

        if (!PrefixIdentityValidation.IsValidGitSha(currentHeadSha))
        {
            return Fail(PrefixDiagnosticCodes.GitShaInvalid, "/currentHeadSha");
        }

        if (!PrefixIdentityValidation.IsValidOrdinal(interactionOrdinal))
        {
            return Fail(PrefixDiagnosticCodes.OrdinalInvalid, "/interactionOrdinal");
        }

        var preimage = new ArrayBufferWriter<byte>(256);
        preimage.Write(PrefixDomainTags.Interaction);
        PrefixHashPrimitives.WriteIdentity(preimage, predecessorComponent);
        PrefixHashPrimitives.WriteIdentity(preimage, consumedInputSha256);
        PrefixHashPrimitives.WriteIdentity(preimage, currentHeadSha);
        PrefixHashPrimitives.WriteIdentity(preimage, interactionOrdinal);

        return new InteractionIdOutcome(
            PrefixHashPrimitives.Sha256Hex(preimage.WrittenSpan),
            ImmutableArray<PrefixDiagnostic>.Empty);
    }

    private static InteractionIdOutcome Fail(string code, string path) =>
        new(null, ImmutableArray.Create(PrefixDiagnostic.Create(code, path: path)));
}
