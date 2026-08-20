using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class ActionHostFrameworkVerifierArchitectureTests
{
    [Fact]
    public void ProductionCompositionWiresTheRealPostAcceptanceInlineHook()
    {
        var dependencies = ActionHostCompositionDependencies.Production();

        Assert.IsType<PostAcceptanceInlinePublisherHook>(
            dependencies.InlineHook);
        Assert.IsType<BoundedGitHubPublisherTransportFactory>(
            dependencies.PublisherFactory);
        Assert.IsType<AcceptedStateProductionDependencies>(
            dependencies.StateDependencies);
    }

    [Fact]
    public void ProofFactoriesAreNarrowInternalConstructorSeams()
    {
        var provider = typeof(ActionHostDeepSeekProviderRunnerFactory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        var github = typeof(ActionHostGitHubAuthorizationTransportFactory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        var publisher = typeof(BoundedGitHubPublisherTransportFactory)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);
        var state = typeof(AcceptedStateProductionDependencies)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.Contains(provider, constructor =>
            constructor.GetParameters().Length == 1);
        Assert.Contains(github, constructor =>
            constructor.GetParameters().Length == 1);
        Assert.Contains(publisher, constructor =>
            constructor.GetParameters().Length == 1);
        Assert.Contains(state, constructor =>
            constructor.GetParameters().Length == 1);
    }

    [Fact]
    public void FrameworkProofAddsNoPublicSelectorOrActionInput()
    {
        var root = FindRepositoryRoot();
        var action = File.ReadAllText(Path.Join(root,
            ".github", "actions", "agentic-pr-review", "action.yml"));
        var contracts = File.ReadAllText(Path.Join(root,
            "src", "action-wrapper", "launcher", "contracts.ts"));

        Assert.DoesNotContain("proof-mode", action,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verifier-mode", action,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("proof_mode", contracts,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("verifier_mode", contracts,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReplacementAndInventoryArtifactsAreClosedAndPinned()
    {
        var root = FindRepositoryRoot();
        var fixture = Path.Join(root, "runtime", "tests", "fixtures",
            "action-host", "framework");
        using var replacement = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(fixture, "replacement-record.json")));
        using var inventory = JsonDocument.Parse(File.ReadAllBytes(
            Path.Join(fixture, "e1-base-inventory.json")));

        var packages = replacement.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Select(value => value.GetProperty("leaf_id").GetString())
            .ToArray();
        Assert.Equal(new[]
        {
            "W3", "W4", "W5", "W6", "W7", "W8", "W9", "W10",
            "W11", "W12", "W14", "W15",
        }, packages);
        Assert.DoesNotContain("W13", packages);
        var w4 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W4");
        Assert.Equal("removed",
            w4.GetProperty("disposition").GetString());
        Assert.Equal(new[] { "src/live-provider/" },
            w4.GetProperty("removed_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "live-provider")));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Core/LiveAgentVerifierRetirementArchitectureTests.cs",
            w4.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Policy/ActionHostTrustedPolicyArchitectureTests.cs",
            w4.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        var w8 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W8");
        Assert.Equal("removed", w8.GetProperty("disposition").GetString());
        Assert.Equal(new[] { "src/comments.ts", "src/comments.test.ts" },
            w8.GetProperty("removed_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.False(File.Exists(Path.Join(root, "src", "comments.ts")));
        Assert.False(File.Exists(Path.Join(root, "src", "comments.test.ts")));
        Assert.Equal(new[]
        {
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/",
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/",
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/",
            "runtime/src/AgenticPrReview.Runtime/Host/Action/",
        }, w8.GetProperty("retained_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            nameof(R4StickyRenderer),
            nameof(R4StickyMarker),
            nameof(R4PublicationIdentityV1),
            nameof(StickyCommentPublisher),
            nameof(PublicationRecoveryService),
            nameof(ActionHostCoordinator),
            nameof(ActionHostComposition),
        }, w8.GetProperty("csharp_owners").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            $"runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4StickyRenderer.cs#{nameof(R4StickyRenderer)}",
            $"runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4StickyMarker.cs#{nameof(R4StickyMarker)}",
            $"runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4PublicationIdentityV1.cs#{nameof(R4PublicationIdentityV1)}",
            $"runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#{nameof(StickyCommentPublisher)}",
            $"runtime/src/AgenticPrReview.Runtime/Host/Publishing/Recovery/PublicationRecoveryService.cs#{nameof(PublicationRecoveryService)}",
            $"runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostCoordinator.cs#{nameof(ActionHostCoordinator)}",
            $"runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostComposition.cs#{nameof(ActionHostComposition)}",
        }, w8.GetProperty("owner_members").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "P2 / #158 merged",
            "P6 / #162 merged",
            "W7 / #169 merged",
            "E1 / #178 framework evidence green",
        }, w8.GetProperty("deletion_prerequisites").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "complete ordered projection with whole-block public truncation and an explicit omission notice",
            "grounded findings reject empty evidence before identity or rendering",
            "bounded-complete discovery fails closed on page item and completeness overflow",
        }, w8.GetProperty("retained_assertion_groups").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            $"rendering:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.NonEmptyReviewFreezesExactMarkdownAndBodyDigest)}",
            $"rendering:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.ProductionTruncationKeepsCompleteProjectionAndWholeBlocks)}",
            $"marker:P1:{nameof(R4StickyMarkerTests)}.{nameof(R4StickyMarkerTests.OrdinaryAndHistoricalCommentsAreNotR4Targets)}",
            $"fingerprint:P1:{nameof(R4PublicationIdentityTests)}.{nameof(R4PublicationIdentityTests.FindingFingerprintMatchesIndependentGoldenAndEvidenceOrderMatters)}",
            $"fingerprint:P1:{nameof(R4PublicationIdentityTests)}.{nameof(R4PublicationIdentityTests.UnicodeNormalizationDoesNotParticipateInFindingIdentity)}",
            $"pathless_rejection:P1:{nameof(R4PublicationIdentityTests)}.{nameof(R4PublicationIdentityTests.MalformedInternalFindingValuesFailClosed)}",
            $"target_discovery:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.HistoricalAndForeignScopeCommentsAreNotAdopted)}",
            $"target_discovery:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.DiscoveryAcceptsExactlyFiftyPagesAndFiveThousandRecords)}",
            $"target_discovery:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.PageItemAndCompletenessCapPlusOneFailClosed)}",
            $"target_discovery:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.CrossPageLastEvidenceIsRequiredForCompleteDiscovery)}",
            $"duplicate_handling:P1:{nameof(R4PublicationIdentityTests)}.{nameof(R4PublicationIdentityTests.DuplicateFingerprintsFailClosedWithoutAlternateIdentity)}",
            $"duplicate_handling:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.MultipleOrMalformedR4TargetsFailBeforeWrite)}",
            $"create_update:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.ZeroCreatesAndExactResponseGetRelistProduceReceipt)}",
            $"create_update:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.OneUpdatesEvenWhenExistingBodyIsAlreadyExact)}",
            $"empty_result:P1:{nameof(R4StickyMarkerTests)}.{nameof(R4StickyMarkerTests.EmptyReviewFreezesExactBodyDigestMarkerAndPlacement)}",
            $"escaping:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.EveryFreeTextRenderContextKeepsTheFullCanaryCorpusInert)}",
            $"escaping:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.EvidencePathsStayInertInRepeatedListPositions)}",
            $"bounds:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.ExactFiftyThousandScalarBoundaryIsAccepted)}",
            $"bounds:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.Utf8BudgetCountsFourByteScalarsIndependently)}",
            $"bounds:P1:{nameof(R4StickyRendererTests)}.{nameof(R4StickyRendererTests.TwentyFindingsAreAcceptedAndTwentyOneFailClosed)}",
            $"bounds:P2:{nameof(StickyCommentSerializerTests)}.{nameof(StickyCommentSerializerTests.SerializedRequestAcceptsExactCapAndRejectsCapPlusOne)}",
            $"response_validation_readback:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.ZeroCreatesAndExactResponseGetRelistProduceReceipt)}",
            $"response_validation_readback:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.ReadOnlyExactDiscoveryReturnsObservedReceiptWithoutMutation)}",
            $"response_validation_readback:P2:{nameof(StickyPublicationContractsTests)}.{nameof(StickyPublicationContractsTests.PersistedP1OrReceiptCanAuthorizeReadOnlyDiscovery)}",
            $"outcome_unknown:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.LostCreateResponseReconcilesWithoutSecondWrite)}",
            $"outcome_unknown:P2:{nameof(StickyCommentPublisherTests)}.{nameof(StickyCommentPublisherTests.UnresolvedMutationNeverRetriesAndHasNoReceipt)}",
            $"outcome_unknown:P5:{nameof(PublicationRecoveryClassifierTests)}.{nameof(PublicationRecoveryClassifierTests.AcceptedOutcomeUnknownConvergesOnExactMarkerAfterRestart)}",
            $"outcome_unknown:P5:{nameof(PublicationRecoveryClassifierTests)}.{nameof(PublicationRecoveryClassifierTests.DurableStickyReceiptPinsCommentIdAcrossProcessRestart)}",
            $"outcome_unknown:P5:{nameof(PublicationRecoveryServiceTests)}.{nameof(PublicationRecoveryServiceTests.ExactStoredPayloadDiscoveryCompletesAcceptanceWithoutMutation)}",
            $"outcome_unknown:P6:{nameof(ActionHostCompositionTests)}.{nameof(ActionHostCompositionTests.P2FailureClassesConvergeThroughDurableRecovery)}",
            $"outcome_unknown:P6:{nameof(ActionHostCompositionTests)}.{nameof(ActionHostCompositionTests.RecoversAcceptanceCrashWithoutProviderKeyOrDuplicateSticky)}",
        }, w8.GetProperty("named_replacement_vectors").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        var w3 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W3");
        Assert.Equal(new[]
        {
            ".github/workflows/ci.yml",
            "src/residual-reference-allowlist.ts",
            "docs/20_architecture/agent-runtime-rebaseline.md",
            "docs/20_architecture/r1-legacy-removal-handoff.md",
            "docs/20_architecture/r3-single-shot-removal-handoff.md",
            "docs/20_architecture/r4-actionhost-wrapper-plan.md",
            "docs/50_ai/agent-context.md",
        }, w3.GetProperty("referenced_tests_and_docs").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        var w5 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W5");
        Assert.Equal("removed", w5.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/state-v2/",
            "protocol/schemas/state-manifest.v2.json",
            "protocol/fixtures/state-manifest-v2/",
            "protocol/fixtures/state-manifest-v2-compat/",
            "scripts/regenerate-state-v2-fixtures.mjs",
            "scripts/regenerate-state-v2-compat-fixtures.mjs",
        }, w5.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "state-v2")));
        Assert.False(File.Exists(Path.Join(root, "protocol", "schemas",
            "state-manifest.v2.json")));
        Assert.Equal(new[]
        {
            "S5 / #155 merged",
            "S6 / #156 merged",
            "P5 / #161 merged",
            "P6 / #162 merged",
            "E1 / #178 framework evidence green",
            "W3 / #165 merged",
            "W6 / #168 merged",
            "W7 / #169 merged",
            "W12 / #174 merged",
        }, w5.GetProperty("deletion_prerequisites").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        var w5Groups = w5.GetProperty("legacy_test_groups").EnumerateArray()
            .ToArray();
        Assert.Equal(25, w5Groups.Length);
        Assert.Equal(25, w5Groups.Select(value => value.GetProperty("id").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(new[]
        {
            "aggregation.test.ts::bounded diagnostic aggregation:retained",
            "builder-input-domain.test.ts::candidate input rejection:retained",
            "builder-string-safety.test.ts::bounded safe strings and paths:retained",
            "classifier-precedence.test.ts::selected-current failure precedence:retained",
            "classifier-wire-format.test.ts::tampered bundle rejection:retained",
            "compat-fixtures.test.ts::compatibility outcome corpus:reviewed_obsolete",
            "compatibility.test.ts::ancestry and state-key compatibility:reviewed_obsolete",
            "constants-mirror.test.ts::StateV2 schema parity:reviewed_obsolete",
            "core.test.ts::StateV2 parser serializer and classifier representation:reviewed_obsolete",
            "cross-field.test.ts::provenance generation and transition binding:retained",
            "deep-path-oracle.test.ts::M4 sidecar traversal oracle:reviewed_obsolete",
            "diagnostic-bounds.test.ts::bounded failure diagnostics:retained",
            "diagnostic-privacy.test.ts::private diagnostic suppression:retained",
            "empty-name-unknown-field.test.ts::closed names and unknown fields:reviewed_obsolete",
            "fixtures.test.ts::byte-identical StateV2 fixture bundles:reviewed_obsolete",
            "import-boundary.test.ts::StateV2 dependency and directory contract:reviewed_obsolete",
            "import-boundary.test.ts::canonical-json recursive AST filesystem boundary:transferred",
            "public-surface.test.ts::StateV2 barrel exports:reviewed_obsolete",
            "resolver-runtime-consequence.test.ts::M4 resolver runtime consequences:reviewed_obsolete",
            "rfc3339.test.ts::accepted-state timestamp grammar:reviewed_obsolete",
            "schema-conformance.test.ts::closed schema and reference validation:reviewed_obsolete",
            "shared-vectors.test.ts::shared M4 vector projection:reviewed_obsolete",
            "shared-vocabulary.test.ts::StateV2 vocabulary parity:reviewed_obsolete",
            "short-circuit-and-exhaustive.test.ts::failure precedence and exhaustive branches:retained",
            "strict-json.test.ts::strict JSON byte and duplicate-property rejection:reviewed_obsolete",
        }, w5Groups.Select(value => $"{value.GetProperty("id").GetString()}:{value.GetProperty("disposition").GetString()}")
            .Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("7a34b1ea484f6e478680338f1ee1c9988bf45f78139071ac3ce4f06fdef5e800",
            w5.GetProperty("mapping_digest").GetString());
        var canonicalBoundary = w5Groups.Single(value => value.GetProperty("id")
            .GetString() == "import-boundary.test.ts::canonical-json recursive AST filesystem boundary");
        Assert.Equal("transferred", canonicalBoundary.GetProperty("disposition").GetString());
        Assert.Equal("W14", canonicalBoundary.GetProperty("owner").GetString());
        Assert.Equal("src/canonical-json/import-boundary.test.ts",
            canonicalBoundary.GetProperty("target_path").GetString());
        Assert.Equal(12, w5.GetProperty("fixture_dispositions").GetArrayLength());
        Assert.Equal(new[]
        {
            "compat-base-change:S5",
            "compat-cache-contract-change:reviewed_obsolete",
            "compat-continuation:S5",
            "compat-contract-version-mismatch:reviewed_obsolete",
            "compat-nondescendant-head:S5",
            "compat-state-key-mismatch:S5",
            "compat-unknown-ancestry:S5",
            "compat-unsafe-provenance:S5",
            "positive-bootstrap:S5",
            "positive-continuation:S5",
            "positive-recovery-root:S6",
            "positive-reset:S5",
        }, w5.GetProperty("fixture_dispositions").EnumerateArray().Select(value =>
            $"{value.GetProperty("id").GetString()}:{(value.TryGetProperty("semantic_owner", out var owner) ? owner.GetString() : value.GetProperty("semantic_disposition").GetString())}")
            .Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(new[]
        {
            "runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Session/AgentSessionArchitectureTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/State/RestrictedStateArchitectureTests.cs",
        }, w5.GetProperty("updated_reference_dispositions").EnumerateArray()
            .Select(value => value.GetProperty("path").GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
            w5.GetProperty("w5_residual_scan").GetProperty("immutable_provenance_paths")
                .EnumerateArray().Select(value => value.GetString()));
        var w6 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W6");
        Assert.Equal("removed", w6.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/state-acceptance/",
            "protocol/schemas/candidate-registration.v1.json",
            "protocol/schemas/accepted-state-marker.v1.json",
            "protocol/schemas/state-selector.v1.json",
            "protocol/schemas/state-publication-receipt.v1.json",
        }, w6.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "state-acceptance")));
        Assert.Contains(
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyPublicationReceipt",
            w6.GetProperty("owner_members").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyPublicationContractsTests.cs",
            w6.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        var w6Cases = w6.GetProperty("legacy_test_cases")
            .EnumerateArray().ToArray();
        var w6CaseIds = w6Cases.Select(value => value.GetProperty("id")
            .GetString()).ToArray();
        Assert.Equal(47, w6CaseIds.Length);
        Assert.Equal(47, w6CaseIds.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("contract.test.ts::contract-version-legacy-v1", w6CaseIds);
        Assert.Contains("contract.test.ts::contract-version-unknown-version", w6CaseIds);
        Assert.Contains("contract.test.ts::kernel-lock", w6CaseIds);
        Assert.Contains("github-state-store.test.ts::counter-transaction", w6CaseIds);
        Assert.All(w6Cases, value =>
        {
            var retained = value.GetProperty("disposition").GetString() == "retained";
            Assert.True(retained || value.GetProperty("disposition").GetString() ==
                "reviewed_obsolete");
            Assert.Equal(retained, value.TryGetProperty("evidence_path", out _));
            Assert.Equal(retained, value.TryGetProperty("owner", out _));
            Assert.Equal(!retained, value.TryGetProperty("reason", out _));
        });
        var w6Helpers = w6.GetProperty("legacy_helper_cases")
            .EnumerateArray().ToArray();
        Assert.Equal(new[] { "lock-child.mjs::unix-socket-lock",
                "store-child.mjs::reference-store-child" },
            w6Helpers.Select(value => value.GetProperty("id").GetString())
                .Order(StringComparer.Ordinal).ToArray());
        var receipt = w6.GetProperty("receipt_disposition");
        Assert.Equal("protocol/schemas/state-publication-receipt.v1.json",
            receipt.GetProperty("legacy_schema").GetString());
        Assert.Equal(new[] { "P2", "P5", "P6", "S5", "S6" },
            receipt.GetProperty("owners").EnumerateArray()
                .Select(value => value.GetString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Sticky/StickyCommentPublisher.cs#StickyPublicationReceipt",
            receipt.GetProperty("owner_members").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Recovery/PublicationRecoveryServiceTests.cs",
            receipt.GetProperty("evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        var residual = w6.GetProperty("w6_residual_scan");
        var forbiddenTokens = residual.GetProperty("forbidden_tokens")
            .EnumerateArray().Select(value => value.GetString()).ToArray();
        Assert.Equal(22, forbiddenTokens.Length);
        Assert.Contains("StateAcceptanceStore", forbiddenTokens);
        Assert.Contains("ReferenceStateStore", forbiddenTokens);
        Assert.Contains("OctokitGitDataClient", forbiddenTokens);
        Assert.Contains("acceptLocalCandidate", forbiddenTokens);
        Assert.Contains("candidate-registration.v1.json", forbiddenTokens);
        Assert.Contains("state-publication-receipt.v1.json", forbiddenTokens);
        Assert.Contains("@actions/cache", forbiddenTokens);
        Assert.Contains("actions/cache", forbiddenTokens);
        Assert.Equal(new[]
        {
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Sticky/StickyCommentPublisherTests.cs",
        }, residual.GetProperty("w8_marker_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Contains("runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
            residual.GetProperty("immutable_provenance_paths").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Equal(new[] { "scripts/check-r3-live-proof.mjs" },
            residual.GetProperty("retained_unrelated_policy_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { ".github/actions/agentic-pr-review/dist/index.js" },
            residual.GetProperty("bundled_dependency_artifact_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        var w9 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W9");
        Assert.Equal("removed", w9.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/inline-comments.ts",
            "src/inline-comments.test.ts",
            "src/target.ts",
            "src/target.test.ts",
        }, w9.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "runtime/src/AgenticPrReview.Runtime/Host/Action/Authorization/",
            "runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/",
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/Inline/",
            "runtime/src/AgenticPrReview.Runtime/Host/Publishing/GitHub/Inline/",
            "runtime/src/AgenticPrReview.Runtime/Host/Action/",
        }, w9.GetProperty("retained_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "ActionHostAuthorizer",
            "ActionHostGitHubAuthorizationJson",
            "BoundedReviewedSnapshotBuilder",
            "ReviewedChangedFileReader",
            "InlineCandidateMapper",
            "InlineCommentPublisher",
            "PostAcceptanceInlinePublisherHook",
            "ActionHostComposition",
        }, w9.GetProperty("csharp_owners").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[] { "inline", "inline-warning", "stale-head" },
            w9.GetProperty("framework_scenario_ids").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "P3 merged",
            "P4 merged",
            "P6 merged",
            "E1 inline branch green",
        }, w9.GetProperty("deletion_prerequisites").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "same-repository open pull-request admission and frozen identity",
            "exact reviewed diff construction unavailable classifications and right-side coordinates",
            "trusted severity deterministic ordering fingerprint inline key and fixed cap five",
            "complete enumeration exact dedup one batch relist readback and retry idempotency",
            "exact closed 422-only fallback bounded individual writes and both head barriers",
            "sticky acceptance before inline authorization and post-acceptance warning-only completion",
        }, w9.GetProperty("retained_assertion_groups").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "raw GitHub patch hashing and TypeScript incremental snapshot deltas",
            "file-SHA-only patch-unavailable comparison and synthetic target resolution",
            "caller-derived state key confidence gate configurable cap and post-list refill",
            "M4 marker state identity and original multi-line range presentation",
            "generic fallback after 5xx or outcome unknown and historical 3000-entry bounds",
            "TypeScript metadata reason DTO target interfaces helpers and compatibility exports",
        }, w9.GetProperty("superseded_assertion_groups").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Authorization/ActionHostAuthorizationFactTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/GitHub/ActionHostGitHubAuthorizationTransportTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Snapshot/BoundedReviewedSnapshotBuilderTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Snapshot/ChangedFiles/ReviewedChangedFileReaderTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Inline/InlineCandidateMapperTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/Inline/InlineCandidateReplacementVectorTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Inline/InlineCommentCodecTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Publishing/GitHub/Inline/InlineCommentPublisherTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostCompositionTests.cs",
            "runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostFrameworkVerifierArchitectureTests.cs",
            "docs/20_architecture/r4-actionhost-wrapper-plan.md",
        }, w9.GetProperty("retained_evidence_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
        }, w9.GetProperty("inventory_evidence_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        foreach (var removed in w9.GetProperty("removed_paths")
                     .EnumerateArray().Select(value => value.GetString()!))
        {
            Assert.False(File.Exists(Path.Join(root,
                removed.Replace('/', Path.DirectorySeparatorChar))));
        }
        foreach (var retained in w9.GetProperty("retained_evidence_paths")
                     .EnumerateArray().Select(value => value.GetString()!))
        {
            Assert.True(File.Exists(Path.Join(root,
                retained.Replace('/', Path.DirectorySeparatorChar))),
                $"Missing W9 retained evidence: {retained}");
        }
        var w10 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W10");
        Assert.Equal("removed", w10.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/protocol/build-review-input.test.ts",
            "src/protocol/build-review-input.ts",
            "src/protocol/fixtures.test.ts",
            "src/protocol/map-review-result.test.ts",
            "src/protocol/map-review-result.ts",
            "src/protocol/review-input.test.ts",
            "src/protocol/review-input.ts",
            "src/protocol/review-result.test.ts",
            "src/protocol/review-result.ts",
            "src/protocol/review-trace.test.ts",
            "src/protocol/review-trace.ts",
            "src/structured.test.ts",
            "src/structured.ts",
        }, w10.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        foreach (var removed in w10.GetProperty("removed_paths")
                     .EnumerateArray().Select(value => value.GetString()!))
        {
            Assert.False(File.Exists(Path.Join(root,
                removed.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Contains(removed, inventory.RootElement.GetProperty("files")
                .EnumerateArray().Select(value =>
                    value.GetProperty("path").GetString()));
        }
        Assert.True(File.Exists(Path.Join(root, "src", "types.ts")));
        Assert.True(File.Exists(Path.Join(root, "src", "utils.ts")));

        Assert.Equal(new[]
        {
            "RuntimeApplication",
            "RuntimeJson",
            "AgentLoop",
            "TerminalReviewValidator",
            "R4PublicationIdentityV1",
            "ActionHostCoordinator",
        }, w10.GetProperty("csharp_owners").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.Equal(new[]
        {
            "dispatch-continuation", "inline", "public-result",
        }, w10.GetProperty("framework_scenario_ids").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        foreach (var retained in w10.GetProperty("retained_evidence_paths")
                     .EnumerateArray().Select(value => value.GetString()!))
        {
            Assert.True(File.Exists(Path.Join(root,
                retained.Replace('/', Path.DirectorySeparatorChar))),
                $"Missing W10 retained evidence: {retained}");
        }
        foreach (var member in w10.GetProperty("owner_members")
                     .EnumerateArray().Select(value => value.GetString()!))
        {
            var separator = member.IndexOf('#', StringComparison.Ordinal);
            Assert.True(separator > 0, $"Invalid W10 owner member: {member}");
            var path = member[..separator];
            var requiredText = member[(separator + 1)..];
            var source = File.ReadAllText(Path.Join(root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            Assert.Contains(requiredText, source, StringComparison.Ordinal);
        }

        var dispositions = w10.GetProperty("legacy_assertion_dispositions")
            .EnumerateArray().ToArray();
        Assert.Equal(new[]
        {
            "input-assembly-credentials-and-host-shape",
            "patch-hash-path-location-and-string-bounds",
            "result-host-fields-grounding-and-fingerprint",
            "trace-privacy-hashes-and-usage-lineage",
            "fixture-manifest-and-noncircular-hash-chain",
            "result-content-and-sidechannel-mapping",
            "structured-extraction-host-assembly-filter-and-cap",
            "typescript-ajv-diagnostic-wording",
            "typescript-dtos-validators-exports-and-old-fingerprint",
        }, dispositions.Select(value => value.GetProperty("id").GetString())
            .ToArray());
        Assert.Equal(new[] { "retained", "superseded", "obsolete" },
            dispositions.Select(value => value.GetProperty("disposition")
                    .GetString()).Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal).OrderBy(value => value switch
                {
                    "retained" => 0,
                    "superseded" => 1,
                    _ => 2,
                }).ToArray());
        Assert.All(dispositions, value =>
        {
            Assert.False(string.IsNullOrWhiteSpace(value
                .GetProperty("current_owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(value
                .GetProperty("semantic_difference").GetString()));
            var evidence = value.GetProperty("evidence_path").GetString()!;
            Assert.True(File.Exists(Path.Join(root,
                evidence.Replace('/', Path.DirectorySeparatorChar))),
                $"Missing W10 disposition evidence: {evidence}");
        });

        var corpus = w10.GetProperty("retained_corpus");
        var schemaPaths = corpus.GetProperty("schema_paths")
            .EnumerateArray().Select(value => value.GetString()!).ToArray();
        Assert.Equal(new[]
        {
            "protocol/schemas/review-input.v1.json",
            "protocol/schemas/review-result.v1.json",
            "protocol/schemas/review-trace.v1.json",
        }, schemaPaths);
        var fixtureRoot = corpus.GetProperty("fixture_root").GetString()!;
        Assert.Equal("protocol/fixtures/v1/", fixtureRoot);
        var fixtureDirectory = Path.Join(root,
            fixtureRoot.Replace('/', Path.DirectorySeparatorChar));
        var corpusPaths = schemaPaths.Concat(Directory.GetFiles(
                fixtureDirectory, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path)
                .Replace('\\', '/')))
            .Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(corpus.GetProperty("file_count").GetInt32(),
            corpusPaths.Length);
        var corpusFraming = new StringBuilder();
        foreach (var path in corpusPaths)
        {
            var digest = Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(Path.Join(root,
                        path.Replace('/', Path.DirectorySeparatorChar)))))
                .ToLowerInvariant();
            corpusFraming.Append(path).Append('\0').Append(digest).Append('\n');
        }
        Assert.Equal(corpus.GetProperty("aggregate_sha256").GetString(),
            Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes(corpusFraming.ToString())))
                .ToLowerInvariant());
        Assert.Equal("ordinal path + NUL + lowercase file sha256 + LF",
            corpus.GetProperty("framing").GetString());
        Assert.True(File.Exists(Path.Join(fixtureDirectory,
            "provider-session-ledger", "valid-bootstrap.json")));
        Assert.Equal(new[]
        {
            "runtime/tests/fixtures/action-host/framework/e1-base-inventory.json",
        }, w10.GetProperty("inventory_evidence_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        var w11 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W11");
        Assert.Equal("removed", w11.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/prefix-contract/",
            "scripts/regenerate-prefix-contract-fixtures.mjs",
        }, w11.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "prefix-contract")));
        Assert.False(File.Exists(Path.Join(root, "scripts",
            "regenerate-prefix-contract-fixtures.mjs")));

        var w12 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W12");
        Assert.Equal("removed",
            w12.GetProperty("disposition").GetString());
        Assert.Equal(new[]
        {
            "src/provider-metadata/",
            "protocol/schemas/provider-run-metadata.v1.json",
            "protocol/fixtures/provider-run-metadata/",
        }, w12.GetProperty("removed_paths").EnumerateArray()
            .Select(value => value.GetString()).ToArray());
        Assert.False(Directory.Exists(Path.Join(root, "src", "provider-metadata")));
        Assert.False(File.Exists(Path.Join(root, "protocol", "schemas",
            "provider-run-metadata.v1.json")));
        Assert.False(Directory.Exists(Path.Join(root, "protocol", "fixtures",
            "provider-run-metadata")));
        Assert.Contains(
            "runtime/tests/AgenticPrReview.Runtime.Tests/Execution/DeepSeek/DeepSeekChatBackendTests.cs",
            w12.GetProperty("retained_evidence_paths").EnumerateArray()
                .Select(value => value.GetString()));
        Assert.Contains(
            "W5 opaque sidecar bytes descriptors hashes and fixtures were removed by W5 after S5/S6/P5/P6/E1 disposition",
            w12.GetProperty("retained_owner_groups").EnumerateArray()
                .Select(value => value.GetString()));

        var w10HandoffEntry = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W10");
        var structuredHandoffs = w10HandoffEntry.GetProperty(
            "inherited_w8_replacement_handoffs").EnumerateArray().ToArray();
        Assert.Equal(5, structuredHandoffs.Length);
        Assert.All(structuredHandoffs, handoff =>
        {
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "prior_typescript_assertion").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "later_owner").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "replacement").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "deliberate_difference").GetString()));
        });
        var w15 = replacement.RootElement.GetProperty("entries")
            .EnumerateArray().Single(value =>
                value.GetProperty("leaf_id").GetString() == "W15");
        var rootHandoffs = w15.GetProperty(
            "retired_w8_consumer_handoffs").EnumerateArray().ToArray();
        Assert.Equal(2, rootHandoffs.Length);
        Assert.All(rootHandoffs, handoff =>
        {
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "prior_typescript_consumer").GetString()));
            Assert.Equal("W15", handoff.GetProperty("later_owner").GetString());
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "disposition").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(handoff.GetProperty(
                "deliberate_difference").GetString()));
        });

        const string prefixCorpus = "protocol/fixtures/prefix-contract/";
        var expectedCorpus = inventory.RootElement.GetProperty("files")
            .EnumerateArray()
            .Where(value => value.GetProperty("path").GetString()!
                .StartsWith(prefixCorpus, StringComparison.Ordinal))
            .ToDictionary(
                value => value.GetProperty("path").GetString()!,
                value => value.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        var corpusRoot = Path.Join(root, "protocol", "fixtures", "prefix-contract");
        var currentCorpus = Directory.GetFiles(corpusRoot, "*",
                SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(expectedCorpus.Keys.Order(StringComparer.Ordinal),
            currentCorpus.Order(StringComparer.Ordinal));
        foreach (var (path, digest) in expectedCorpus)
        {
            Assert.Equal(digest, Convert.ToHexString(SHA256.HashData(
                File.ReadAllBytes(Path.Join(root,
                    path.Replace('/', Path.DirectorySeparatorChar)))))
                .ToLowerInvariant());
        }
        Assert.Equal("e698fb1df6daf49f393e87fac4f00e3a2ec2c716",
            inventory.RootElement.GetProperty("base_sha").GetString());
        Assert.Equal("apr.action-host.replacement-record.v2",
            replacement.RootElement.GetProperty("schema").GetString());
        Assert.Equal(349, inventory.RootElement.GetProperty("files")
            .GetArrayLength());

        var framing = new StringBuilder();
        foreach (var file in inventory.RootElement.GetProperty("files")
                     .EnumerateArray())
        {
            var path = file.GetProperty("path").GetString()!;
            var digest = file.GetProperty("sha256").GetString()!;
            Assert.Equal(digest, BaseBlobDigest(root,
                inventory.RootElement.GetProperty("base_sha").GetString()!,
                path));
            framing.Append(path).Append('\0').Append(digest).Append('\n');
        }

        Assert.Equal(
            inventory.RootElement.GetProperty("aggregate_sha256").GetString(),
            Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(framing.ToString())))
                .ToLowerInvariant());
    }

    [Fact]
    public void RuntimeCiRunsTheCheckedFrameworkProofTwiceWithoutCredentials()
    {
        var root = FindRepositoryRoot();
        var workflow = File.ReadAllText(Path.Join(root,
            ".github", "workflows", "runtime-ci.yml"));

        Assert.True(Count(workflow,
            "bash runtime/scripts/verify-action-host.sh framework") >= 2);
        Assert.True(Count(workflow, "persist-credentials: false") >= 2);
        Assert.DoesNotContain("secrets.", workflow,
            StringComparison.Ordinal);
    }

    private static int Count(string value, string searched)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(searched, offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += searched.Length;
        }

        return count;
    }

    private static string BaseBlobDigest(
        string repository,
        string revision,
        string path)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.ArgumentList.Add("show");
        info.ArgumentList.Add(revision + ":" + path);
        using var process = Process.Start(info);
        Assert.NotNull(process);
        using var bytes = new MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(bytes);
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()))
            .ToLowerInvariant();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Join(directory.FullName, "package.json")) &&
                Directory.Exists(Path.Join(directory.FullName, "runtime")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("repository_root_not_found");
    }
}
