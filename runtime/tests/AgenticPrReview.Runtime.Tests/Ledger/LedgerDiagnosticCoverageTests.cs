using System.Reflection;
using System.Text.Json;
using AgenticPrReview.Runtime.Ledger;

namespace AgenticPrReview.Runtime.Tests.Ledger;

/// <summary>
/// Enumerates every constant defined in <see cref="LedgerDiagnosticCodes"/> and asserts
/// that the manifest exercises each code through at least one fixture. Catches drift
/// between code definitions and the fixture corpus.
/// </summary>
public sealed class LedgerDiagnosticCoverageTests
{
    [Fact]
    public void EveryDiagnosticCodeIsExercisedByAtLeastOneFixtureOrIsAllowlisted()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "protocol", "fixtures", "v1");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "manifest.json")));

        var covered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.RootElement.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString();
            switch (type)
            {
                case "ledger-restore":
                    var e = entry.GetProperty("expectation");
                    if (e.TryGetProperty("code", out var code)) covered.Add(code.GetString()!);
                    break;
                case "ledger-transition":
                    var te = entry.GetProperty("transitionExpectation");
                    if (te.TryGetProperty("code", out var tcode)) covered.Add(tcode.GetString()!);
                    break;
                case "ledger-build":
                    var be = entry.GetProperty("buildExpectation");
                    if (be.TryGetProperty("code", out var bcode)) covered.Add(bcode.GetString()!);
                    if (be.TryGetProperty("causeCode", out var cc)) covered.Add(cc.GetString()!);
                    break;
            }
        }

        // Codes only reachable via focused unit tests (not through the fixture corpus).
        var allowlist = new HashSet<string>(StringComparer.Ordinal)
        {
            // Reachable only through direct malformed byte payloads, covered by dedicated
            // canonicalizer/scanner tests.
            LedgerDiagnosticCodes.InvalidJson,
            // Reachable only through the schema-result mapper's variant discriminator on a
            // continuation header that carries a forbidden field. Reset/reset-forbidden-field
            // covers the "reset variant" branch; continuation-shape-violation is exercised by
            // LedgerSchemaMapperTests.
            LedgerDiagnosticCodes.ContinuationShapeViolation,
            // recovery-shape-violation: reachable via recovery + forbidden field; covered in
            // LedgerSchemaMapperTests as a focused case rather than a fixture.
            LedgerDiagnosticCodes.RecoveryShapeViolation,
            // record_role_mismatch: reachable only by a record with role="tool"; covered by
            // LedgerSchemaMapperTests.
            LedgerDiagnosticCodes.RecordRoleMismatch,
            // Reachable only through caller-supplied ExpectedTransition values
            // that disagree with the candidate/predecessor; covered by
            // LedgerAppendTests focused unit cases.
            LedgerDiagnosticCodes.StateGenerationMismatch,
            LedgerDiagnosticCodes.ResetReasonMismatch,
            LedgerDiagnosticCodes.RecoveryReasonMismatch,
            LedgerDiagnosticCodes.ResetEpochNotFresh,
            // schema_violation: catch-all reachable in many negative fixtures but not always
            // named directly; several fixtures already map to it.
            // Covered via absolute-path-in-finding.json fixture.
            // Included below to avoid brittle name matching in tests.
        };

        var allCodes = typeof(LedgerDiagnosticCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        var missing = allCodes.Where(code => !covered.Contains(code) && !allowlist.Contains(code)).ToArray();
        Assert.True(missing.Length == 0,
            "Diagnostic codes with no fixture coverage: " + string.Join(", ", missing));
    }
}
