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

        // Refined precedence: unknown-field is highest priority. Only if no
        // truly-unknown property exists do we consider header-vocabulary
        // variant-forbidden fields.
        var extra = FindAnyAdditionalProperty(root);
        if (extra is not null)
        {
            return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.UnknownField);
        }
        if (kind is not null &&
            headerElement.ValueKind == JsonValueKind.Object)
        {
            var variantForbidden = FindVariantForbiddenHeaderField(headerElement, kind);
            if (variantForbidden is not null)
            {
                return LedgerDiagnosticMessages.Of(variantForbidden);
            }
        }

        // Missing required fields.
        var missing = FindMissingRequired(root, kind);
        if (missing is not null)
        {
            return LedgerDiagnosticMessages.Of(missing);
        }

        var records = default(JsonElement);
        var recordsPresent = root.TryGetProperty("records", out records) && records.ValueKind == JsonValueKind.Array;

        // Precedence step 3: oneOf variant discriminator (header.kind).
        // Bootstrap/recovery must have stateGeneration == 0 and
        // predecessorLedgerSha256 == "bootstrap"; violation is a shape-violation.
        if (kind is "bootstrap" or "recovery")
        {
            if (headerElement.TryGetProperty("stateGeneration", out var sg) &&
                sg.ValueKind == JsonValueKind.Number && sg.TryGetInt32(out var sgVal) && sgVal != 0)
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

        // Precedence step 4: const / enum violations. Record role and changed-file
        // status are enum violations that map to specific ledger diagnostic codes.
        if (recordsPresent)
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

        // Precedence step 5: array minItems / maxItems.
        if (recordsPresent)
        {
            var length = records.GetArrayLength();
            if (length < 2) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.RecordsEmpty);
            if (length > LedgerLimits.MaxRecords) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.InteractionLimitExceeded);

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

        // Precedence step 6: string maxLength.
        var overlong = FindOverlongString(root);
        if (overlong) return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.OverlongValue);

        // Precedence step 7: pattern / numeric-range / catch-all.
        return LedgerDiagnosticMessages.Of(LedgerDiagnosticCodes.SchemaViolation);
    }

    // Free-form identity fields governed by string maxLength: 256 in the
    // ledger schema. Kept in sync with LedgerBuilder / LedgerSemanticChecks.
    private static readonly string[] IdentityStringNames =
    {
        "workflowIdentity","trustedExecutionDomain","sessionEpoch","providerId","modelId",
    };

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

    // Union of legal fields across both record roles, used when the role
    // literal is unknown and we want to prioritize an unknown-field violation
    // over role-mismatch.
    private static readonly HashSet<string> RecordUnionAllowed = new(StringComparer.Ordinal)
    {
        "role","interactionId","interactionOrdinal",
        "reviewedHeadSha","reviewedBaseSha","subjectDigest","cacheContractDigest","changedFiles",
        "summary","findings","limitations",
    };

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
                // When the role does not identify a known record shape we still
                // check every field against the union of legal context+outcome
                // fields; an unknown-field violation there is higher precedence
                // than the record-role-mismatch that will otherwise fire below.
                var effective = allowed ?? RecordUnionAllowed;
                foreach (var pr in rec.EnumerateObject())
                {
                    if (!effective.Contains(pr.Name)) return pr.Name;
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
        // Detect over-lengths on all string-maxLength fields governed by the
        // ledger schema. Identity strings (workflowIdentity, trustedExecutionDomain,
        // sessionEpoch, providerId, modelId) have maxLength 256; repository /
        // headRepository have maxLength 200; free-form record fields have
        // their own caps; safeRelativePath has maxLength 500 on changed-file
        // paths (path and previousPath) and finding.path. All map to
        // ledger_overlong_value so parser / projection / candidate paths
        // classify identically.
        if (root.TryGetProperty("header", out var hdr) && hdr.ValueKind == JsonValueKind.Object)
        {
            foreach (var idName in IdentityStringNames)
            {
                if (hdr.TryGetProperty(idName, out var v) && v.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(v.GetString()!) > 256)
                    return true;
            }
            if (hdr.TryGetProperty("repository", out var repo) && repo.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(repo.GetString()!) > 200)
                return true;
            if (hdr.TryGetProperty("headRepository", out var hRepo) && hRepo.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(hRepo.GetString()!) > 200)
                return true;
        }
        if (root.TryGetProperty("records", out var recs) && recs.ValueKind == JsonValueKind.Array)
        {
            foreach (var rec in recs.EnumerateArray())
            {
                if (rec.ValueKind != JsonValueKind.Object) continue;
                if (rec.TryGetProperty("changedFiles", out var cfArr) && cfArr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var cf in cfArr.EnumerateArray())
                    {
                        if (cf.ValueKind != JsonValueKind.Object) continue;
                        if (cf.TryGetProperty("path", out var pth) && pth.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(pth.GetString()!) > LedgerLimits.MaxSafeRelativePathChars) return true;
                        if (cf.TryGetProperty("previousPath", out var pp) && pp.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(pp.GetString()!) > LedgerLimits.MaxSafeRelativePathChars) return true;
                    }
                }
                if (rec.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(s.GetString()!) > LedgerLimits.MaxSummaryChars) return true;
                if (rec.TryGetProperty("limitations", out var l) && l.ValueKind == JsonValueKind.Array)
                {
                    foreach (var li in l.EnumerateArray())
                    {
                        if (li.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(li.GetString()!) > LedgerLimits.MaxLimitationsItemChars) return true;
                    }
                }
                if (rec.TryGetProperty("findings", out var fs) && fs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var f in fs.EnumerateArray())
                    {
                        if (f.ValueKind != JsonValueKind.Object) continue;
                        if (f.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(b.GetString()!) > LedgerLimits.MaxFindingBodyChars) return true;
                        if (f.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(t.GetString()!) > LedgerLimits.MaxFindingTitleChars) return true;
                        if (f.TryGetProperty("evidence", out var ev) && ev.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(ev.GetString()!) > LedgerLimits.MaxFindingEvidenceChars) return true;
                        if (f.TryGetProperty("suggestedAction", out var sa) && sa.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(sa.GetString()!) > LedgerLimits.MaxFindingSuggestedActionChars) return true;
                        if (f.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String && LedgerLimits.SchemaStringLength(p.GetString()!) > LedgerLimits.MaxSafeRelativePathChars) return true;
                    }
                }
            }
        }
        return false;
    }
}
