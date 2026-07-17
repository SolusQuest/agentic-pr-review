using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Canonical;

namespace AgenticPrReview.Runtime.Prefix;

/// <summary>
/// Validation, canonicalization, and digest computation for the five closed
/// cache-contract envelopes. Envelope field sets and the digestId algorithm
/// are owned by ## Prefix Contract of the design document; this class owns
/// the language-specific field-domain validation.
/// </summary>
internal static class PrefixEnvelopeValidator
{
    internal enum EnvelopeKind
    {
        Template,
        Policy,
        Tools,
        CacheConfig,
        Adapter,
    }

    internal static PrefixDiagnostic? Validate(
        EnvelopeKind kind,
        JsonElement envelope,
        out ValidatedEnvelope? validated)
    {
        validated = null;

        try
        {
            return ValidateCore(kind, envelope, out validated);
        }
        catch (ObjectDisposedException)
        {
            // An element from an already-disposed JsonDocument must surface as a
            // typed failure, never as an escaping exception.
            validated = null;
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid);
        }
        catch (InvalidOperationException)
        {
            // System.Text.Json refuses to decode incomplete UTF-16 strings.
            validated = null;
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.CanonicalInputRejected);
        }
    }

    private static PrefixDiagnostic? ValidateCore(
        EnvelopeKind kind,
        JsonElement envelope,
        out ValidatedEnvelope? validated)
    {
        validated = null;

        if (envelope.ValueKind != JsonValueKind.Object)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: string.Empty);
        }

        var keyError = CheckExactKeySet(kind, envelope);
        if (keyError is not null)
        {
            return keyError;
        }

        var fieldError = CheckFields(kind, envelope);
        if (fieldError is not null)
        {
            return fieldError;
        }

        ImmutableArray<byte> canonicalBytes;
        try
        {
            canonicalBytes = JsonElementCanonicalizer.Canonicalize(
                envelope,
                PrefixBounds.MaxEnvelopeJsonDepth,
                PrefixBounds.MaxEnvelopeObjectProperties,
                PrefixBounds.MaxEnvelopeArrayItems);
        }
        catch (Rfc8785CanonicalizationException ex)
        {
            return ex.Reason switch
            {
                Rfc8785RejectionReason.DuplicateProperty
                    => PrefixDiagnostic.Create(PrefixDiagnosticCodes.CanonicalInputRejected, path: ex.Path),
                Rfc8785RejectionReason.DepthLimitExceeded
                    or Rfc8785RejectionReason.PropertyCountExceeded
                    or Rfc8785RejectionReason.ArrayLengthExceeded
                    => PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: ex.Path),
                _ => PrefixDiagnostic.Create(PrefixDiagnosticCodes.CanonicalInputRejected, path: ex.Path),
            };
        }

        if (canonicalBytes.Length > PrefixBounds.MaxEnvelopeCanonicalBytes)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeTooLarge);
        }

        var digest = PrefixHashPrimitives.DigestId(TagFor(kind), canonicalBytes.AsSpan());
        validated = new ValidatedEnvelope(envelope, canonicalBytes, digest);
        return null;
    }

    internal static byte[] TagFor(EnvelopeKind kind) => kind switch
    {
        EnvelopeKind.Template => PrefixDomainTags.Template,
        EnvelopeKind.Policy => PrefixDomainTags.Policy,
        EnvelopeKind.Tools => PrefixDomainTags.Tools,
        EnvelopeKind.CacheConfig => PrefixDomainTags.Config,
        EnvelopeKind.Adapter => PrefixDomainTags.Adapter,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static PrefixDiagnostic? CheckExactKeySet(EnvelopeKind kind, JsonElement envelope)
    {
        var allowed = kind switch
        {
            EnvelopeKind.Template => new[] { "definition", "schemaVersion", "templateVersion" },
            EnvelopeKind.Policy => new[] { "constraints", "instructions", "policyVersion", "schemaVersion" },
            EnvelopeKind.Tools => new[] { "definitions", "schemaVersion", "toolsetVersion" },
            EnvelopeKind.CacheConfig => new[] { "cacheConfigVersion", "eligibility", "markerPolicy", "schemaVersion", "statelessMode" },
            EnvelopeKind.Adapter => new[] { "adapterBuildVersion", "capabilityProfileVersion", "schemaVersion" },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var property in envelope.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/" + property.Name);
            }

            if (!allowedSet.Contains(property.Name))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/" + property.Name);
            }
        }

        foreach (var required in allowed)
        {
            if (!seen.Contains(required))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/" + required);
            }
        }

        return null;
    }

    private static PrefixDiagnostic? CheckFields(EnvelopeKind kind, JsonElement envelope)
    {
        var versionError = CheckVersionField(envelope, "schemaVersion");
        if (versionError is not null)
        {
            return versionError;
        }

        switch (kind)
        {
            case EnvelopeKind.Template:
                return CheckVersionField(envelope, "templateVersion");

            case EnvelopeKind.Policy:
            {
                var error = CheckVersionField(envelope, "policyVersion");
                if (error is not null)
                {
                    return error;
                }

                return CheckStringField(envelope, "instructions");
            }

            case EnvelopeKind.Tools:
            {
                var error = CheckVersionField(envelope, "toolsetVersion");
                if (error is not null)
                {
                    return error;
                }

                return CheckToolDefinitions(envelope.GetProperty("definitions"));
            }

            case EnvelopeKind.CacheConfig:
            {
                var error = CheckVersionField(envelope, "cacheConfigVersion");
                if (error is not null)
                {
                    return error;
                }

                error = CheckStringField(envelope, "markerPolicy") ?? CheckStringField(envelope, "eligibility");
                if (error is not null)
                {
                    return error;
                }

                var stateless = envelope.GetProperty("statelessMode");
                if (stateless.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/statelessMode");
                }

                return null;
            }

            case EnvelopeKind.Adapter:
            {
                var error = CheckVersionField(envelope, "capabilityProfileVersion");
                if (error is not null)
                {
                    return error;
                }

                var buildVersion = envelope.GetProperty("adapterBuildVersion");
                if (buildVersion.ValueKind != JsonValueKind.String)
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/adapterBuildVersion");
                }

                if (!PrefixIdentityValidation.IsValidIdentity(buildVersion.GetString()))
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityInvalid, path: "/adapterBuildVersion");
                }

                return null;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private static PrefixDiagnostic? CheckVersionField(JsonElement envelope, string name)
    {
        // Version fields accept any JSON number whose value is a mathematical
        // integer in range (1e0 is legal and canonicalizes to 1), matching the
        // ES Number semantics of the TypeScript validator.
        var value = envelope.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetDouble(out var number)
            || number != Math.Truncate(number)
            || number < 1
            || number > 2_147_483_647)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/" + name);
        }

        return null;
    }

    private static PrefixDiagnostic? CheckStringField(JsonElement envelope, string name)
    {
        if (envelope.GetProperty(name).ValueKind != JsonValueKind.String)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/" + name);
        }

        return null;
    }

    private static PrefixDiagnostic? CheckToolDefinitions(JsonElement definitions)
    {
        if (definitions.ValueKind != JsonValueKind.Array)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/definitions");
        }

        var count = definitions.GetArrayLength();
        if (count > PrefixBounds.MaxToolDefinitions)
        {
            return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: "/definitions");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var tool in definitions.EnumerateArray())
        {
            var path = "/definitions/" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (tool.ValueKind != JsonValueKind.Object)
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path);
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in tool.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path + "/" + property.Name);
                }

                if (property.Name is not ("description" or "inputSchema" or "name" or "policyMetadata"))
                {
                    return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path + "/" + property.Name);
                }
            }

            if (!seen.Contains("name") || !seen.Contains("description") || !seen.Contains("inputSchema"))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path);
            }

            var name = tool.GetProperty("name");
            if (name.ValueKind != JsonValueKind.String)
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path + "/name");
            }

            if (!PrefixIdentityValidation.IsValidIdentity(name.GetString()))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.IdentityInvalid, path: path + "/name");
            }

            if (!names.Add(name.GetString()!))
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path + "/name");
            }

            if (tool.GetProperty("description").ValueKind != JsonValueKind.String)
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path + "/description");
            }

            if (tool.GetProperty("inputSchema").ValueKind != JsonValueKind.Object)
            {
                return PrefixDiagnostic.Create(PrefixDiagnosticCodes.EnvelopeInvalid, path: path + "/inputSchema");
            }

            index++;
        }

        return null;
    }
}
