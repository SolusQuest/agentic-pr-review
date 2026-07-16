using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Json.Schema;

namespace AgenticPrReview.Runtime.Ledger;

internal static class LedgerSchemaMapper
{
    public static ImmutableArray<LedgerDiagnostic> Map(JsonDocument instance, EvaluationResults results)
    {
        var rawSchema = LoadRawSchema();
        var root = instance.RootElement;
        var kind = GetString(root, "header", "kind");
        var variants = ResolveHeaderVariants(rawSchema, kind);
        var matchingHeaderErrorPointers = variants.MatchingVariantName is not null
            ? EvaluateMatchingHeaderErrors(rawSchema, root.GetProperty("header"), variants.MatchingVariantName)
            : null;
        variants = variants with { MatchingHeaderErrorPointers = matchingHeaderErrorPointers };

        var candidates = ImmutableArray.CreateBuilder<DiagnosticCandidate>();
        CollectCandidates(results, candidates, rawSchema, root, variants);

        var selected = candidates.ToImmutable()
            .GroupBy(c => c.Pointer)
            .Select(g => g.OrderBy(c => c.Priority).First())
            .OrderBy(c => c.Pointer, StringComparer.Ordinal)
            .ThenBy(c => c.Priority)
            .ToImmutableArray();

        return selected.Select(c => new LedgerDiagnostic
        {
            Code = c.Code,
            Message = $"{c.Code}:{c.Pointer}"
        }).ToImmutableArray();
    }

    private static void CollectCandidates(
        EvaluationResults result,
        ImmutableArray<DiagnosticCandidate>.Builder candidates,
        JsonDocument rawSchema,
        JsonElement root,
        HeaderVariantInfo variants)
    {
        if (result.IsValid)
        {
            return;
        }

        var schemaPointer = result.SchemaLocation.ToString();
        var instancePointer = result.InstanceLocation.ToString();

        if (ShouldSuppress(schemaPointer, instancePointer, root, variants, rawSchema))
        {
            // If this node is under a non-matching oneOf branch, all descendants are noise.
            return;
        }

        var hasErrors = result.Errors is not null && result.Errors.Count > 0;
        var hasInvalidChildren = result.Details is not null && result.Details.Any(d => !d.IsValid);

        if (hasErrors)
        {
            var candidate = Classify(result, rawSchema, root, variants);
            if (candidate is not null)
            {
                candidates.Add(candidate.Value);
            }
        }

        if (result.Details is not null)
        {
            foreach (var child in result.Details)
            {
                CollectCandidates(child, candidates, rawSchema, root, variants);
            }
        }
    }

    private static DiagnosticCandidate? Classify(
        EvaluationResults result,
        JsonDocument rawSchema,
        JsonElement root,
        HeaderVariantInfo variants)
    {
        var keyword = result.Errors is not null && result.Errors.Count > 0
            ? result.Errors.Keys.FirstOrDefault()
            : null;

        var instancePointer = result.InstanceLocation.ToString();
        var schemaPointer = result.SchemaLocation.ToString();

        // Resolve pointer to actual offending location for additionalProperties / required.
        if (keyword == "additionalProperties")
        {
            var propertyName = TryExtractPropertyName(result);
            if (propertyName is not null)
            {
                instancePointer = $"{instancePointer}/{LedgerSafePath.EscapeJsonPointerSegment(propertyName)}";
            }
        }

        var sanitizedPointer = LedgerSafePath.SanitizeInstancePointer(instancePointer);

        if (instancePointer == "")
        {
            // Root-level failure.
            if (keyword == "additionalProperties")
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.UnknownField, 4, sanitizedPointer);
            }

            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        if (instancePointer == "/header" || instancePointer.StartsWith("/header/", StringComparison.Ordinal))
        {
            return ClassifyHeader(result, rawSchema, variants, instancePointer, sanitizedPointer, keyword);
        }

        if (instancePointer == "/records" || instancePointer.StartsWith("/records/", StringComparison.Ordinal))
        {
            return ClassifyRecord(result, rawSchema, root, variants, instancePointer, sanitizedPointer, keyword);
        }

        return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
    }

    private static DiagnosticCandidate? ClassifyHeader(
        EvaluationResults result,
        JsonDocument rawSchema,
        HeaderVariantInfo variants,
        string instancePointer,
        string sanitizedPointer,
        string? keyword)
    {
        var kind = variants.Kind;
        var isRecognizedKind = IsRecognizedKind(kind);
        var propertyName = TryExtractPropertyName(result);

        if (keyword == "additionalProperties" && propertyName is not null)
        {
            if (isRecognizedKind && IsUnderMatchingHeaderBranch(result.SchemaLocation.ToString(), variants))
            {
                if (variants.AllVariantProperties.Contains(propertyName))
                {
                    return new DiagnosticCandidate(ShapeViolationCode(kind), 2, sanitizedPointer);
                }

                return new DiagnosticCandidate(LedgerDiagnosticCodes.UnknownField, 4, sanitizedPointer);
            }

            return new DiagnosticCandidate(LedgerDiagnosticCodes.UnknownField, 4, sanitizedPointer);
        }

        if (keyword == "required")
        {
            var missing = TryExtractPropertyName(result);
            if (isRecognizedKind)
            {
                if (kind == "reset" && missing == "resetReason")
                {
                    return new DiagnosticCandidate(LedgerDiagnosticCodes.ResetReasonMissing, 1, sanitizedPointer);
                }

                if (kind == "recovery_root" && missing == "recoveryReason")
                {
                    return new DiagnosticCandidate(LedgerDiagnosticCodes.RecoveryRootReasonMissing, 1, sanitizedPointer);
                }
            }

            if (isRecognizedKind && IsUnderMatchingHeaderBranch(result.SchemaLocation.ToString(), variants))
            {
                return new DiagnosticCandidate(ShapeViolationCode(kind), 2, sanitizedPointer);
            }

            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        if (keyword is "const" or "enum" or "type" or "minimum" or "maximum" or "minLength" or "pattern")
        {
            if (isRecognizedKind && IsUnderMatchingHeaderBranch(result.SchemaLocation.ToString(), variants))
            {
                return new DiagnosticCandidate(ShapeViolationCode(kind), 2, sanitizedPointer);
            }

            if (instancePointer == "/header/kind")
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
            }

            if (isRecognizedKind)
            {
                return new DiagnosticCandidate(ShapeViolationCode(kind), 2, sanitizedPointer);
            }

            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        if (isRecognizedKind && IsUnderMatchingHeaderBranch(result.SchemaLocation.ToString(), variants))
        {
            return new DiagnosticCandidate(ShapeViolationCode(kind), 2, sanitizedPointer);
        }

        return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
    }

    private static DiagnosticCandidate? ClassifyRecord(
        EvaluationResults result,
        JsonDocument rawSchema,
        JsonElement root,
        HeaderVariantInfo variants,
        string instancePointer,
        string sanitizedPointer,
        string? keyword)
    {
        if (instancePointer == "/records")
        {
            if (keyword == "minItems")
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.RecordsEmpty, 3, sanitizedPointer);
            }

            if (keyword == "maxItems")
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.InteractionLimitExceeded, 3, sanitizedPointer);
            }

            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        // /records/N/...  extract index.
        var index = TryParseRecordIndex(instancePointer);
        if (index is null)
        {
            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        if (!root.TryGetProperty("records", out var recordsArray) ||
            recordsArray.ValueKind != JsonValueKind.Array ||
            index.Value >= recordsArray.GetArrayLength())
        {
            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        var recordElement = recordsArray[index.Value];
        var role = GetString(recordElement, "role");
        var matchingRecordBranch = role switch
        {
            "review_context" => 0,
            "review_outcome" => 1,
            _ => (int?)null
        };

        if (instancePointer == $"/records/{index.Value}")
        {
            if (keyword == "additionalProperties")
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
            }

            if (keyword == "required" || keyword == "oneOf")
            {
                if (role is null && matchingRecordBranch is null)
                {
                    // oneOf container failure when role unrecognized is suppressed above;
                    // the role enum failure is the diagnostic.
                    return new DiagnosticCandidate(LedgerDiagnosticCodes.RecordRoleMismatch, 5, sanitizedPointer);
                }

                return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
            }

            if (keyword == "enum" && result.SchemaLocation.ToString().EndsWith("/role/enum", StringComparison.Ordinal))
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.RecordRoleMismatch, 5, sanitizedPointer);
            }

            return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
        }

        if (instancePointer == $"/records/{index.Value}/role")
        {
            return new DiagnosticCandidate(LedgerDiagnosticCodes.RecordRoleMismatch, 5, sanitizedPointer);
        }

        if (instancePointer.StartsWith($"/records/{index.Value}/changedFiles", StringComparison.Ordinal))
        {
            if (keyword == "maxItems" && instancePointer == $"/records/{index.Value}/changedFiles")
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.ChangedFileLimitExceeded, 6, sanitizedPointer);
            }

            if (keyword == "enum" && instancePointer.Contains("/status", StringComparison.Ordinal))
            {
                return new DiagnosticCandidate(LedgerDiagnosticCodes.UnsupportedChangeStatus, 9, sanitizedPointer);
            }
        }

        if (instancePointer == $"/records/{index.Value}/findings" && keyword == "maxItems")
        {
            return new DiagnosticCandidate(LedgerDiagnosticCodes.FindingLimitExceeded, 7, sanitizedPointer);
        }

        if (instancePointer == $"/records/{index.Value}/limitations" && keyword == "maxItems")
        {
            return new DiagnosticCandidate(LedgerDiagnosticCodes.LimitationsLimitExceeded, 8, sanitizedPointer);
        }

        if (keyword == "maxLength")
        {
            return new DiagnosticCandidate(LedgerDiagnosticCodes.OverlongValue, 10, sanitizedPointer);
        }

        return new DiagnosticCandidate(LedgerDiagnosticCodes.SchemaViolation, 11, sanitizedPointer);
    }

    private static bool ShouldSuppress(
        string schemaPointer,
        string instancePointer,
        JsonElement root,
        HeaderVariantInfo variants,
        JsonDocument rawSchema)
    {
        var fragment = GetSchemaFragment(schemaPointer);
        var kind = variants.Kind;

        // Header oneOf collapse.
        if (kind is not null && IsRecognizedKind(kind))
        {
            const string headerOneOfPrefix = "#/$defs/header/oneOf";
            if (fragment.StartsWith(headerOneOfPrefix, StringComparison.Ordinal))
            {
                if (fragment == headerOneOfPrefix)
                {
                    return true;
                }

                var branchPart = fragment.Substring(headerOneOfPrefix.Length + 1);
                var slashIndex = branchPart.IndexOf('/');
                if (slashIndex > 0)
                {
                    branchPart = branchPart.Substring(0, slashIndex);
                }

                if (int.TryParse(branchPart, out var branchIndex))
                {
                    return branchIndex != variants.MatchingHeaderBranch;
                }
            }
        }

        // Suppress errors from non-matching header variant definitions.
        foreach (var variantName in variants.AllVariantNames)
        {
            var prefix = $"#/$defs/{variantName}";
            var isUnderVariant = fragment == prefix || fragment.StartsWith(prefix + "/", StringComparison.Ordinal);
            if (isUnderVariant && variantName != variants.MatchingVariantName)
            {
                return true;
            }
        }

        // Suppress header errors that cannot be reproduced by evaluating only the matching variant.
        // This removes noise from shared $defs used by non-matching branches.
        if (instancePointer.StartsWith("/header", StringComparison.Ordinal) &&
            variants.MatchingHeaderErrorPointers is not null &&
            !variants.MatchingHeaderErrorPointers.Contains(instancePointer))
        {
            return true;
        }

        // Record oneOf collapse.
        var recordIndex = TryParseRecordIndex(instancePointer);
        if (recordIndex.HasValue &&
            root.TryGetProperty("records", out var recordsArray) &&
            recordsArray.ValueKind == JsonValueKind.Array &&
            recordIndex.Value < recordsArray.GetArrayLength())
        {
            var role = GetString(recordsArray[recordIndex.Value], "role");
            if (role is "review_context" or "review_outcome")
            {
                var matchingRecordBranch = role == "review_context" ? 0 : 1;
                const string recordOneOfPrefix = "#/$defs/record/oneOf";
                if (fragment.StartsWith(recordOneOfPrefix, StringComparison.Ordinal))
                {
                    if (fragment == recordOneOfPrefix)
                    {
                        return true;
                    }

                    var branchPart = fragment.Substring(recordOneOfPrefix.Length + 1);
                    var slashIndex = branchPart.IndexOf('/');
                    if (slashIndex > 0)
                    {
                        branchPart = branchPart.Substring(0, slashIndex);
                    }

                    if (int.TryParse(branchPart, out var branchIndex))
                    {
                        return branchIndex != matchingRecordBranch;
                    }
                }

                var matchingRecordVariant = role == "review_context" ? "reviewContextRecord" : "reviewOutcomeRecord";
                foreach (var recordVariantName in new[] { "reviewContextRecord", "reviewOutcomeRecord" })
                {
                    var prefix = $"#/$defs/{recordVariantName}";
                    var isUnderVariant = fragment == prefix || fragment.StartsWith(prefix + "/", StringComparison.Ordinal);
                    if (isUnderVariant && recordVariantName != matchingRecordVariant)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string GetSchemaFragment(string schemaPointer)
    {
        var hashIndex = schemaPointer.LastIndexOf('#');
        if (hashIndex < 0)
        {
            return schemaPointer;
        }

        return schemaPointer.Substring(hashIndex);
    }

    private static string ShapeViolationCode(string? kind)
    {
        return kind switch
        {
            "bootstrap" => LedgerDiagnosticCodes.BootstrapShapeViolation,
            "continuation" => LedgerDiagnosticCodes.ContinuationShapeViolation,
            "reset" => LedgerDiagnosticCodes.ResetShapeViolation,
            "recovery_root" => LedgerDiagnosticCodes.RecoveryRootShapeViolation,
            _ => LedgerDiagnosticCodes.SchemaViolation
        };
    }

    private static bool IsRecognizedKind(string? kind)
    {
        return kind is "bootstrap" or "continuation" or "reset" or "recovery_root";
    }

    private static bool IsUnderMatchingHeaderBranch(string schemaPointer, HeaderVariantInfo variants)
    {
        var fragment = GetSchemaFragment(schemaPointer);

        if (variants.MatchingHeaderBranch.HasValue)
        {
            var oneOfPrefix = $"#/$defs/header/oneOf/{variants.MatchingHeaderBranch.Value}";
            if (fragment.StartsWith(oneOfPrefix, StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (variants.MatchingVariantName is not null)
        {
            var variantPrefix = $"#/$defs/{variants.MatchingVariantName}";
            if (fragment == variantPrefix || fragment.StartsWith(variantPrefix + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static HeaderVariantInfo ResolveHeaderVariants(JsonDocument rawSchema, string? kind)
    {
        var defNamesBuilder = ImmutableHashSet.CreateBuilder<string>();
        var propertyNamesBuilder = ImmutableHashSet.CreateBuilder<string>();
        string? matchingVariantName = null;
        int? matchingHeaderBranch = null;

        if (rawSchema.RootElement.TryGetProperty("$defs", out var defs) &&
            defs.TryGetProperty("header", out var headerDef) &&
            headerDef.TryGetProperty("oneOf", out var oneOf))
        {
            var index = 0;
            foreach (var branch in oneOf.EnumerateArray())
            {
                var variantName = ResolveRefTarget(branch);
                if (variantName is not null && defs.TryGetProperty(variantName, out var variantDef))
                {
                    defNamesBuilder.Add(variantName);
                    CollectPropertyNames(variantDef, propertyNamesBuilder);
                    if (kind is not null && TryGetKindConst(variantDef) == kind)
                    {
                        matchingVariantName = variantName;
                        matchingHeaderBranch = index;
                    }
                }

                index++;
            }
        }

        return new HeaderVariantInfo(
            kind,
            matchingVariantName,
            matchingHeaderBranch,
            defNamesBuilder.ToImmutable(),
            propertyNamesBuilder.ToImmutable(),
            null);
    }

    private static ImmutableHashSet<string> EvaluateMatchingHeaderErrors(JsonDocument rawSchema, JsonElement headerElement, string variantName)
    {
        var defs = rawSchema.RootElement.GetProperty("$defs").GetRawText();
        var schemaJson = $$"""
            {"$defs":{{defs}},"$ref":"#/$defs/{{variantName}}"}
            """;
        var schema = JsonSchema.FromText(schemaJson);
        var results = schema.Evaluate(headerElement, new EvaluationOptions { OutputFormat = OutputFormat.List });
        var builder = ImmutableHashSet.CreateBuilder<string>();
        CollectInstancePointers(results, builder, "/header");
        return builder.ToImmutable();
    }

    private static void CollectInstancePointers(EvaluationResults result, ImmutableHashSet<string>.Builder builder, string prefix)
    {
        if (result.IsValid)
        {
            return;
        }

        var pointer = result.InstanceLocation.ToString();
        builder.Add(string.IsNullOrEmpty(pointer) ? prefix : prefix + pointer);
        if (result.Details is null)
        {
            return;
        }

        foreach (var child in result.Details)
        {
            CollectInstancePointers(child, builder, prefix);
        }
    }

    private static string? ResolveRefTarget(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty("$ref", out var refElement) &&
            refElement.ValueKind == JsonValueKind.String)
        {
            var reference = refElement.GetString();
            if (reference is not null && reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            {
                return reference.Substring("#/$defs/".Length);
            }
        }

        return null;
    }

    private static string? TryGetKindConst(JsonElement variantDef)
    {
        if (variantDef.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (variantDef.TryGetProperty("properties", out var props) &&
            props.TryGetProperty("kind", out var kindSchema) &&
            kindSchema.TryGetProperty("const", out var constElement) &&
            constElement.ValueKind == JsonValueKind.String)
        {
            return constElement.GetString();
        }

        if (variantDef.TryGetProperty("allOf", out var allOf))
        {
            foreach (var sub in allOf.EnumerateArray())
            {
                var kind = TryGetKindConst(sub);
                if (kind is not null)
                {
                    return kind;
                }
            }
        }

        return null;
    }

    private static void CollectPropertyNames(JsonElement schemaNode, ImmutableHashSet<string>.Builder builder)
    {
        if (schemaNode.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (schemaNode.TryGetProperty("properties", out var props))
        {
            foreach (var prop in props.EnumerateObject())
            {
                builder.Add(prop.Name);
            }
        }

        if (schemaNode.TryGetProperty("allOf", out var allOf))
        {
            foreach (var sub in allOf.EnumerateArray())
            {
                CollectPropertyNames(sub, builder);
            }
        }

        if (schemaNode.TryGetProperty("oneOf", out var oneOf))
        {
            foreach (var sub in oneOf.EnumerateArray())
            {
                CollectPropertyNames(sub, builder);
            }
        }

        if (schemaNode.TryGetProperty("anyOf", out var anyOf))
        {
            foreach (var sub in anyOf.EnumerateArray())
            {
                CollectPropertyNames(sub, builder);
            }
        }
    }

    private static int? TryParseRecordIndex(string instancePointer)
    {
        if (!instancePointer.StartsWith("/records/", StringComparison.Ordinal))
        {
            return null;
        }

        var rest = instancePointer.Substring("/records/".Length);
        var slashIndex = rest.IndexOf('/');
        if (slashIndex >= 0)
        {
            rest = rest.Substring(0, slashIndex);
        }

        if (int.TryParse(rest, out var index))
        {
            return index;
        }

        return null;
    }

    private static string? TryExtractPropertyName(EvaluationResults result)
    {
        if (result.Errors is null)
        {
            return null;
        }

        foreach (var error in result.Errors.Values)
        {
            var open = error.IndexOf('[');
            var close = error.IndexOf(']');
            if (open >= 0 && close > open)
            {
                return error.Substring(open + 1, close - open - 1).Trim('\'', '\"', ' ');
            }
        }

        return null;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out var next))
            {
                return null;
            }

            current = next;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static JsonDocument LoadRawSchema()
    {
        var assembly = typeof(LedgerSchemaMapper).Assembly;
        using var stream = assembly.GetManifestResourceStream("AgenticPrReview.Protocol.provider-session-ledger.v1.json")
            ?? throw new InvalidOperationException("Missing embedded ledger schema resource.");
        return JsonDocument.Parse(stream);
    }

    private readonly record struct HeaderVariantInfo(
        string? Kind,
        string? MatchingVariantName,
        int? MatchingHeaderBranch,
        ImmutableHashSet<string> AllVariantNames,
        ImmutableHashSet<string> AllVariantProperties,
        ImmutableHashSet<string>? MatchingHeaderErrorPointers);

    private readonly record struct DiagnosticCandidate(string Code, int Priority, string Pointer);
}
