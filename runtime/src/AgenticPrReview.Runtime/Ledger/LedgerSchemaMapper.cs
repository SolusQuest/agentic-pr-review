using System.Text.Json;
using Json.Schema;

namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Maps raw JsonSchema.Net evaluation results to deterministic ledger
/// diagnostic codes. The mapper walks the instance in canonical JSON Pointer
/// order and returns the first violation resolved under the precedence
/// documented in the issue.
/// </summary>
internal static class LedgerSchemaMapper
{
    public static LedgerDiagnostic Map(JsonElement root, EvaluationResults results)
    {
        // Collect deterministic mapping candidates by walking the instance.
        var kind = root.TryGetProperty("header", out var headerElement) &&
                   headerElement.ValueKind == JsonValueKind.Object &&
                   headerElement.TryGetProperty("kind", out var kindEl) &&
                   kindEl.ValueKind == JsonValueKind.String
            ? kindEl.GetString()
            : null;

        // Rule 1: additionalProperties violation - determine whether the extra
        // property is header-vocabulary (variant-forbidden) or truly unknown.
        // Rule 1a (variant-forbidden) is checked first.
        if (kind is not null &&
            headerElement.ValueKind == JsonValueKind.Object)
        {
            var variantForbidden = FindVariantForbiddenHeaderField(headerElement, kind);
            if (variantForbidden is not null)
            {
                return LedgerDiagnosticMessages.Of(variantForbidden);
            }
        }

        // Detect any additional property anywhere.
        var extra = FindAnyAdditionalProperty(root);
        if (extra is not null)
        {
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnknownField);
        }

        // Missing required fields.
        var missing = FindMissingRequired(root, kind);
        if (missing is not null)
        {
            return LedgerDiagnosticMessages.Of(missing);
        }

        // Records array size / emptiness.
        if (root.TryGetProperty("records", out var records) && records.ValueKind == JsonValueKind.Array)
        {
            var length = records.GetArrayLength();
            if (length < 2) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordsEmpty);
            if (length > LedgerLimits.MaxRecords) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InteractionLimitExceeded);
        }

        // Per-record arrays.
        if (records.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in records.EnumerateArray())
            {
                if (rec.ValueKind != JsonValueKind.Object) continue;
                if (rec.TryGetProperty("changedFiles", out var cf) && cf.ValueKind == JsonValueKind.Array && cf.GetArrayLength() > LedgerLimits.MaxChangedFilesPerContext)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.ChangedFileLimitExceeded);
                if (rec.TryGetProperty("findings", out var fn) && fn.ValueKind == JsonValueKind.Array && fn.GetArrayLength() > LedgerLimits.MaxFindingsPerOutcome)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.FindingLimitExceeded);
                if (rec.TryGetProperty("limitations", out var lm) && lm.ValueKind == JsonValueKind.Array && lm.GetArrayLength() > LedgerLimits.MaxLimitationsPerOutcome)
                    return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.LimitationsLimitExceeded);
            }
        }

        // Record role mismatch.
        if (records.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in records.EnumerateArray())
            {
                if (rec.ValueKind == JsonValueKind.Object &&
                    rec.TryGetProperty("role", out var role) &&
                    role.ValueKind == JsonValueKind.String)
                {
                    var r = role.GetString();
                    if (r != "review_context" && r != "review_outcome")
                        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordRoleMismatch);
                }
            }
        }

        // Changed-file status enum.
        if (records.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in records.EnumerateArray())
            {
                if (rec.ValueKind == JsonValueKind.Object &&
                    rec.TryGetProperty("changedFiles", out var cfa) &&
                    cfa.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cf in cfa.EnumerateArray())
                    {
                        if (cf.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String)
                        {
                            var s = status.GetString();
                            if (s is not "added" and not "modified" and not "removed" and not "renamed" and not "copied")
                                return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnsupportedChangeStatus);
                        }
                    }
                }
            }
        }

        // Bootstrap/recovery shape: state generation const violation.
        if (kind is "bootstrap" or "recovery")
        {
            if (headerElement.TryGetProperty("stateGeneration", out var sg) &&
                sg.ValueKind == JsonValueKind.Number && sg.GetInt32() != 0)
            {
                return LedgerDiagnosticMessages.Of(kind == "bootstrap"
                    ? LedgerDiagnosticCodes.BootstrapShapeViolation
                    : LedgerDiagnosticCodes.RecoveryShapeViolation);
            }
            if (headerElement.TryGetProperty("predecessorLedgerSha256", out var pls) &&
                pls.ValueKind == JsonValueKind.String && pls.GetString() != "bootstrap")
            {
                return LedgerDiagnosticMessages.Of(kind == "bootstrap"
                    ? LedgerDiagnosticCodes.BootstrapShapeViolation
                    : LedgerDiagnosticCodes.RecoveryShapeViolation);
            }
        }

        // Overlong string values.
        var overlong = FindOverlongString(root);
        if (overlong) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);

        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
    }

    private static readonly HashSet<string> HeaderVocabulary = new(StringComparer.Ordinal)
    {
        "kind","repository","headRepository","pullRequest","workflowIdentity","trustedExecutionDomain","sessionEpoch",
        "providerId","modelId","adapterId","templateId","policyId","toolDefinitionId","cacheConfigId",
        "stateGeneration","ledgerEpoch","predecessorLedgerSha256","predecessorStateGeneration","predecessorManifestSha256",
        "resetReason","recoveryReason",
    };

    private static readonly Dictionary<string, HashSet<string>> AllowedByKind = new()
    {
        ["bootstrap"] = new(StringComparer.Ordinal)
        {
            "kind","repository","headRepository","pullRequest","workflowIdentity","trustedExecutionDomain","sessionEpoch",
            "providerId","modelId","adapterId","templateId","policyId","toolDefinitionId","cacheConfigId",
            "stateGeneration","ledgerEpoch","predecessorLedgerSha256",
        },
        ["continuation"] = new(StringComparer.Ordinal)
        {
            "kind","repository","headRepository","pullRequest","workflowIdentity","trustedExecutionDomain","sessionEpoch",
            "providerId","modelId","adapterId","templateId","policyId","toolDefinitionId","cacheConfigId",
            "stateGeneration","ledgerEpoch","predecessorLedgerSha256","predecessorStateGeneration",
        },
        ["reset"] = new(StringComparer.Ordinal)
        {
            "kind","repository","headRepository","pullRequest","workflowIdentity","trustedExecutionDomain","sessionEpoch",
            "providerId","modelId","adapterId","templateId","policyId","toolDefinitionId","cacheConfigId",
            "stateGeneration","ledgerEpoch","predecessorLedgerSha256","predecessorStateGeneration",
            "predecessorManifestSha256","resetReason",
        },
        ["recovery"] = new(StringComparer.Ordinal)
        {
            "kind","repository","headRepository","pullRequest","workflowIdentity","trustedExecutionDomain","sessionEpoch",
            "providerId","modelId","adapterId","templateId","policyId","toolDefinitionId","cacheConfigId",
            "stateGeneration","ledgerEpoch","predecessorLedgerSha256","recoveryReason",
        },
    };

    private static string? FindVariantForbiddenHeaderField(JsonElement header, string kind)
    {
        if (!AllowedByKind.TryGetValue(kind, out var allowed)) return null;
        foreach (var prop in header.EnumerateObject())
        {
            if (!allowed.Contains(prop.Name) && HeaderVocabulary.Contains(prop.Name))
            {
                return kind switch
                {
                    "bootstrap" => LedgerDiagnosticCodes.BootstrapShapeViolation,
                    "reset" => LedgerDiagnosticCodes.ResetShapeViolation,
                    "recovery" => LedgerDiagnosticCodes.RecoveryShapeViolation,
                    "continuation" => LedgerDiagnosticCodes.ContinuationShapeViolation,
                    _ => null,
                };
            }
        }
        return null;
    }

    private static string? FindAnyAdditionalProperty(JsonElement root)
    {
        // Walk the whole tree; identify any property that isn't in the vocabulary.
        // We rely on the schema's evaluation to tell us there's an additionalProperties
        // violation, but we detect it structurally here by checking a fixed vocabulary
        // for each object kind. Header extras that are not part of the full header
        // vocabulary map to ledger_unknown_field; extras inside the header that ARE part
        // of the vocabulary but forbidden under the current kind are handled by
        // FindVariantForbiddenHeaderField before this method runs.
        if (root.TryGetProperty("header", out var header) && header.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in header.EnumerateObject())
            {
                if (!HeaderVocabulary.Contains(prop.Name))
                    return prop.Name;
            }
        }
        return FindExtra(root);
    }

    private static readonly HashSet<string> TopLevelAllowed = new(StringComparer.Ordinal)
    { "schemaVersion","prefixContractVersion","header","records" };
    private static readonly HashSet<string> ContextRecordAllowed = new(StringComparer.Ordinal)
    { "role","interactionId","interactionOrdinal","reviewedHeadSha","reviewedBaseSha","subjectDigest","cacheContractDigest","changedFiles" };
    private static readonly HashSet<string> OutcomeRecordAllowed = new(StringComparer.Ordinal)
    { "role","interactionId","interactionOrdinal","summary","findings","limitations" };
    private static readonly HashSet<string> ChangedFileAllowed = new(StringComparer.Ordinal)
    { "path","previousPath","status","additions","deletions","changes","patch" };
    private static readonly HashSet<string> PatchAllowed = new(StringComparer.Ordinal)
    { "sha256","truncated","maxChars" };
    private static readonly HashSet<string> FindingAllowed = new(StringComparer.Ordinal)
    { "severity","confidence","category","title","body","evidence","path","startLine","endLine","suggestedAction","inlinePreference" };

    private static string? FindExtra(JsonElement root)
    {
        foreach (var p in root.EnumerateObject())
        {
            if (!TopLevelAllowed.Contains(p.Name)) return p.Name;
        }
        if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in recs.EnumerateArray())
            {
                if (rec.ValueKind != JsonValueKind.Object) continue;
                var role = rec.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                var allowed = role switch { "review_context" => ContextRecordAllowed, "review_outcome" => OutcomeRecordAllowed, _ => null };
                if (allowed is null) continue;
                foreach (var pr in rec.EnumerateObject())
                {
                    if (!allowed.Contains(pr.Name)) return pr.Name;
                }
                if (rec.TryGetProperty("changedFiles", out var cfs) && cfs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cf in cfs.EnumerateArray())
                    {
                        if (cf.ValueKind != JsonValueKind.Object) continue;
                        foreach (var pr in cf.EnumerateObject())
                        {
                            if (!ChangedFileAllowed.Contains(pr.Name)) return pr.Name;
                        }
                        if (cf.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.Object)
                        {
                            foreach (var pp in patch.EnumerateObject())
                            {
                                if (!PatchAllowed.Contains(pp.Name)) return pp.Name;
                            }
                        }
                    }
                }
                if (rec.TryGetProperty("findings", out var fns) && fns.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fns.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object) continue;
                        foreach (var pr in f.EnumerateObject())
                        {
                            if (!FindingAllowed.Contains(pr.Name)) return pr.Name;
                        }
                    }
                }
            }
        }
        return null;
    }

    private static string? FindMissingRequired(JsonElement root, string? kind)
    {
        // Check top-level required
        if (!root.TryGetProperty("schemaVersion", out _) ||
            !root.TryGetProperty("prefixContractVersion", out _) ||
            !root.TryGetProperty("header", out _) ||
            !root.TryGetProperty("records", out _))
        {
            return LedgerDiagnosticCodes.SchemaViolation;
        }

        // Header required per kind.
        if (root.GetProperty("header").ValueKind != JsonValueKind.Object)
            return LedgerDiagnosticCodes.SchemaViolation;
        var header = root.GetProperty("header");
        if (kind is null) return LedgerDiagnosticCodes.SchemaViolation;
        var mustHave = kind switch
        {
            "bootstrap" => new[] { "stateGeneration","ledgerEpoch","predecessorLedgerSha256" },
            "continuation" => new[] { "stateGeneration","ledgerEpoch","predecessorLedgerSha256","predecessorStateGeneration" },
            "reset" => new[] { "stateGeneration","ledgerEpoch","predecessorLedgerSha256","predecessorStateGeneration","predecessorManifestSha256","resetReason" },
            "recovery" => new[] { "stateGeneration","ledgerEpoch","predecessorLedgerSha256","recoveryReason" },
            _ => Array.Empty<string>(),
        };
        foreach (var name in mustHave)
        {
            if (!header.TryGetProperty(name, out _))
            {
                return name switch
                {
                    "resetReason" => LedgerDiagnosticCodes.ResetReasonMissing,
                    "recoveryReason" => LedgerDiagnosticCodes.RecoveryReasonMissing,
                    _ => kind switch
                    {
                        "bootstrap" => LedgerDiagnosticCodes.BootstrapShapeViolation,
                        "reset" => LedgerDiagnosticCodes.ResetShapeViolation,
                        "recovery" => LedgerDiagnosticCodes.RecoveryShapeViolation,
                        "continuation" => LedgerDiagnosticCodes.ContinuationShapeViolation,
                        _ => LedgerDiagnosticCodes.SchemaViolation,
                    },
                };
            }
        }

        // Common identity fields
        var identityRequired = new[]
        {
            "kind","repository","headRepository","pullRequest","workflowIdentity","trustedExecutionDomain","sessionEpoch",
            "providerId","modelId","adapterId","templateId","policyId","toolDefinitionId","cacheConfigId",
        };
        foreach (var name in identityRequired)
        {
            if (!header.TryGetProperty(name, out _))
                return LedgerDiagnosticCodes.SchemaViolation;
        }
        return null;
    }

    private static bool FindOverlongString(JsonElement root)
    {
        // Only detect obvious over-lengths visible on well-shaped records:
        // summary > 4000, finding.body > 4000, title > 240, evidence > 2000,
        // suggestedAction > 1600, limitations item > 1200, path > 500.
        if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in recs.EnumerateArray())
            {
                if (rec.ValueKind != JsonValueKind.Object) continue;
                if (rec.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String && s.GetString()!.Length > LedgerLimits.MaxSummaryChars) return true;
                if (rec.TryGetProperty("limitations", out var l) && l.ValueKind == JsonValueKind.Array)
                {
                    foreach (var li in l.EnumerateArray())
                    {
                        if (li.ValueKind == JsonValueKind.String && li.GetString()!.Length > LedgerLimits.MaxLimitationsItemChars) return true;
                    }
                }
                if (rec.TryGetProperty("findings", out var fs) && fs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fs.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object) continue;
                        if (f.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String && b.GetString()!.Length > LedgerLimits.MaxFindingBodyChars) return true;
                        if (f.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && t.GetString()!.Length > LedgerLimits.MaxFindingTitleChars) return true;
                        if (f.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String && ev.GetString()!.Length > LedgerLimits.MaxFindingEvidenceChars) return true;
                        if (f.TryGetProperty("suggestedAction", out var sa) && sa.ValueKind == JsonValueKind.String && sa.GetString()!.Length > LedgerLimits.MaxFindingSuggestedActionChars) return true;
                        if (f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && p.GetString()!.Length > LedgerLimits.MaxSafeRelativePathChars) return true;
                    }
                }
            }
        }
        return false;
    }
}
