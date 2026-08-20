using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
        Assert.False(File.Exists(Path.Join(root, "src", "types.ts")));
        Assert.False(File.Exists(Path.Join(root, "src", "utils.ts")));

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
        AssertDescriptionOnlySchemaChange(root,
            "2c6f94dd39941794ff3f59f6663ce0c31113e6c1",
            "protocol/schemas/review-input.v1.json", "repoRelativePath");
        AssertDescriptionOnlySchemaChange(root,
            "2c6f94dd39941794ff3f59f6663ce0c31113e6c1",
            "protocol/schemas/review-result.v1.json", "safeRelativePath");
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
        Assert.Equal("removed", w15.GetProperty("disposition").GetString());
        Assert.Equal(new[] { "src/types.ts", "src/utils.ts" },
            w15.GetProperty("removed_paths").EnumerateArray()
                .Select(value => value.GetString()).ToArray());
        Assert.False(File.Exists(Path.Join(root, "src", "types.ts")));
        Assert.False(File.Exists(Path.Join(root, "src", "utils.ts")));
        var baseSha = inventory.RootElement.GetProperty("base_sha").GetString()!;
        Assert.Equal(baseSha, w15.GetProperty("historical_base_sha").GetString());
        var baseFiles = inventory.RootElement.GetProperty("files")
            .EnumerateArray().ToDictionary(
                value => value.GetProperty("path").GetString()!,
                value => value.GetProperty("sha256").GetString()!,
                StringComparer.Ordinal);
        foreach (var removed in w15.GetProperty("removed_path_inventory")
                     .EnumerateArray())
        {
            var path = removed.GetProperty("path").GetString()!;
            var digest = removed.GetProperty("sha256").GetString()!;
            Assert.Equal(baseFiles[path], digest);
            Assert.Equal(digest, BaseBlobDigest(root, baseSha, path));
        }

        var historicalConsumers = w15.GetProperty("historical_consumers")
            .EnumerateArray().ToArray();
        Assert.Equal(new[]
        {
            "W7|src/ledger-csharp.test.ts|8a9e830718b91c4db76dbccf2e56f4af83d016e34f37a6fe25e0249b922e3b0a|ReviewTarget|",
            "W7|src/ledger-csharp.ts|60c12013ca7e40de45bbbbefce603a6d687b8d3dccf61cc6187e3961157282dc|LiveProvider,Phase,ReviewMode,ReviewTarget,StructuredReviewEnvelopeV1|",
            "W8|src/comments.test.ts|72dd8d97e2d3bfeb7f8cdc9a10ade2b4783a6196e919dc475675df7906917400|StructuredReviewEnvelopeV1|",
            "W8|src/comments.ts|ca44ceede088c1d5fcc689a11113e9f70d8b27bb046ff4b46d90863c59e2af48|LineageAction,LineageReason,Phase,ReviewTarget,RuntimeBackend,RuntimeLineageTotals,RuntimeUsage,StructuredFindingV1,StructuredReviewEnvelopeV1|sha256,truncateText",
            "W9|src/inline-comments.test.ts|cfd95b65b3b6d193846299376b2d7d8dbac6e5a8c02f0fe909f5f24c61c42ac8|InlineCommentsPolicy,StructuredFindingV1,StructuredReviewEnvelopeV1|",
            "W9|src/inline-comments.ts|815492896d1801e6dcbf3a052ff48a58469c4b34bfa0b577a181f64f35207659|ChangedFile,InlineCommentsMetadata,InlineCommentsPolicy,StructuredFindingV1,StructuredReviewEnvelopeV1|sha256,truncateText",
            "W9|src/target.test.ts|890c1dfed0ef95fb2fa23c6f4debd22e65aaf75deef9dfc298138c34c07a2ba0||sha256",
            "W9|src/target.ts|5940223675c3e6aa87dd6abdfc6b8467d2be378d1262a344b840cbfa8780df5a|ChangedFile,PullRequestDiffSnapshotDeltaV1,PullRequestDiffSnapshotEntryV1,PullRequestDiffSnapshotV1,ReviewTarget,RuntimeProvider,TargetMode|normalizeRepoRelativePath,sha256",
            "W10|src/protocol/build-review-input.test.ts|a6b210dcefcb61082ba04579e3775b1ac3b539948509f5dbbcc0e5ca65f32df3|ChangedFile,LoadedBlock,RestoredState,ReviewTarget|sha256",
            "W10|src/protocol/build-review-input.ts|6f2b43f270b978f9d025a5f119e8f8ad70565430082d5d5eed5889716042ca17|InlineCommentConfidence,InlineCommentSeverity,LoadedBlock,Phase,RestoredState,ReviewTarget,RuntimeProvider,ToolMode|sha256",
            "W10|src/protocol/map-review-result.test.ts|9515091147b09a9ae43ad177b23a1b56ed557dfae8712e50fe8c368be59d744a|StructuredFindingV1|",
            "W10|src/protocol/review-input.test.ts|9aecf1e1d279eb3774edb76328251a6a074abca03d125fc2c25069b9f14a9006||sha256",
            "W10|src/structured.test.ts|ffa1d0b6335e42a5f9bd863ab134679485dea3db959b62cecc0da00448a22926|ReviewTarget,RuntimeLineageTotals,RuntimeUsage|",
            "W10|src/structured.ts|79e7e7bba6c6ae61fe70b9c520e8b8e15ecffe6edcd757652e6bebe9e764f320|InlineCommentsMetadata,Phase,ReviewTarget,ReviewedRange,RuntimeLineageTotals,RuntimeProvider,RuntimeUsage,StructuredFindingV1,StructuredReviewEnvelopeV1,ToolMode|normalizeRepoRelativePath,sha256",
        }, historicalConsumers.Select(value => string.Join('|',
            value.GetProperty("leaf_id").GetString(),
            value.GetProperty("path").GetString(),
            value.GetProperty("sha256").GetString(),
            JsonStrings(value, "types"),
            JsonStrings(value, "utils"))).ToArray());
        foreach (var consumer in historicalConsumers)
        {
            var path = consumer.GetProperty("path").GetString()!;
            var digest = consumer.GetProperty("sha256").GetString()!;
            Assert.False(File.Exists(Path.Join(root,
                path.Replace('/', Path.DirectorySeparatorChar))));
            Assert.Equal(baseFiles[path], digest);
            Assert.Equal(digest, BaseBlobDigest(root, baseSha, path));
        }

        var dispositionGroups = w15.GetProperty("symbol_dispositions")
            .EnumerateArray().ToArray();
        Assert.Equal(new[]
        {
            "removed-selectors|reviewed_obsolete|LiveProvider,RuntimeBackend,ReviewMode,EffectiveDiffSource||ActionHostComposition|runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostComposition.cs|The single trusted C# composition replaces TypeScript provider backend and review-route selector dispatch.|workflow-run",
            "protocol-control-fields|superseded|RuntimeProvider,TargetMode,Phase,ToolMode|RuntimeReview,RuntimeOptions,RuntimeApplication,RuntimeJsonContext|ActionHostComposition|runtime/src/AgenticPrReview.Runtime/Protocol/Models/ReviewInput.cs,runtime/src/AgenticPrReview.Runtime/Application/RuntimeApplication.cs,runtime/src/AgenticPrReview.Runtime/Protocol/RuntimeJson.cs,runtime/tests/AgenticPrReview.Runtime.Tests/Protocol/ProtocolFixtureTests.cs|The direct runtime retains phase provider and tool-mode fields while ActionHost uses one provider route and independent trusted policy.|workflow-run",
            "review-content-envelope|superseded|ModelReviewFindingV1,ModelReviewContentV1,StructuredFindingV1,ReviewedRange,StructuredReviewEnvelopeV1|RuntimeFinding,RuntimeJsonContext,RuntimeApplication|TerminalReviewValidator,AgentLoop,R4PublicationIdentityV1|protocol/schemas/review-result.v1.json,runtime/src/AgenticPrReview.Runtime/Protocol/RuntimeJson.cs,runtime/src/AgenticPrReview.Runtime/Agent/Tools/TerminalReviewValidator.cs,runtime/src/AgenticPrReview.Runtime/Agent/Loop/AgentLoop.cs,runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4PublicationIdentityV1.cs|Direct-runtime finding fields remain live; grounded Agent evidence and Host identity replace TypeScript envelope mechanics.|public-result",
            "inline-policy-metadata|superseded|InlineCommentSeverity,InlineCommentConfidence,InlineCommentsPolicy,InlineCommentsMetadata|RuntimeInlineCommentsPolicy,RuntimeApplication|ActionHostTrustedPolicy,InlineCandidateMapper,ActionHostCoordinator|protocol/schemas/review-input.v1.json,runtime/src/AgenticPrReview.Runtime/Protocol/Models/ReviewInput.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Policy/ActionHostTrustedPolicyMaterializer.cs,runtime/src/AgenticPrReview.Runtime/Host/Publishing/Inline/InlineCandidateMapper.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/ActionHostCoordinator.cs|Direct runtime retains inline inputs while ActionHost uses trusted fixed policy targeting and mutation accounting.|inline",
            "trusted-context-blocks|superseded|LoadedBlock|RuntimeContextDocument,RuntimeApplication|ActionHostTrustedPolicy|protocol/schemas/review-input.v1.json,runtime/src/AgenticPrReview.Runtime/Protocol/Models/ReviewInput.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Policy/ActionHostTrustedPolicyMaterializer.cs|Direct runtime retains context documents while H3 independently bounds trusted ActionHost policy blobs.|workflow-run",
            "target-changed-files-snapshots|superseded|ChangedFile,PullRequestDiffSnapshotV1,PullRequestDiffSnapshotEntryV1,PullRequestDiffSnapshotChangedEntryV1,PullRequestDiffSnapshotRemovedEntryV1,PullRequestDiffSnapshotDeltaV1,ReviewTarget|RuntimeChangedFile,RuntimeApplication|ReviewedChangedFileSet,BoundedReviewedSnapshotBuilder,ReviewedExactDiffBuilder,ReviewedTreePath|protocol/schemas/review-input.v1.json,runtime/src/AgenticPrReview.Runtime/Protocol/Models/ReviewInput.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/ChangedFiles/ReviewedChangedFileSet.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/BoundedReviewedSnapshotBuilder.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/Diff/ReviewedExactDiffBuilder.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/ReviewedTreeSnapshot.cs|Direct runtime retains changed-file fields while ActionHost exact snapshots replace the old target and delta model.|dispatch-continuation",
            "state-usage-lineage|superseded|RestoredState,RuntimeUsage,RuntimeUsageTotals,RuntimeLineageTotals,LineageReason,LineageAction|SchemaContracts,ProtocolFixtureTests|LiveAgentFreshProcessLineage,LineageService,AuthorizedAcceptedStateComposer|protocol/schemas/review-result.v1.json,protocol/schemas/review-trace.v1.json,runtime/src/AgenticPrReview.Runtime/Protocol/SchemaContracts.cs,runtime/tests/AgenticPrReview.Runtime.Tests/Protocol/ProtocolFixtureTests.cs,runtime/src/AgenticPrReview.Runtime/Application/LiveAgentFreshProcessLineage.cs,runtime/src/AgenticPrReview.Runtime/Host/State/Lineage/LineageService.cs,runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AuthorizedAcceptedStateComposer.cs|Direct runtime retains current-run usage schemas while encrypted accepted state replaces TypeScript lineage mechanics.|dispatch-continuation",
            "generic-configuration-helpers|reviewed_obsolete|required,parseInteger,parseOptionalInteger,parsePositiveInteger,parseOptionalPositiveInteger,parseBoolean,oneOf,clamp||RuntimeApplication,SchemaContracts|runtime/src/AgenticPrReview.Runtime/Application/RuntimeApplication.cs,runtime/src/AgenticPrReview.Runtime/Protocol/SchemaContracts.cs|Closed schema and C# parsing reject invalid values; generic parsing and silent clamp are not retained APIs.|workflow-run",
            "safe-path-handling|superseded|normalizeRepoRelativePath|SchemaContracts|ReviewedTreePath|protocol/schemas/review-input.v1.json,protocol/schemas/review-result.v1.json,runtime/src/AgenticPrReview.Runtime/Protocol/SchemaContracts.cs,runtime/tests/AgenticPrReview.Runtime.Tests/Protocol/ProtocolFixtureTests.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/ReviewedTreeSnapshot.cs|Schemas retain path syntax; H5 rejects unsafe paths instead of normalizing input. SemanticValidation only checks path-line relations.|inline",
            "hashing|retained_replacement|sha256||R4PublicationIdentityV1,ReviewedTreeIdentityWriter,LineageService|runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4PublicationIdentityV1.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/ReviewedTreeSnapshot.cs,runtime/src/AgenticPrReview.Runtime/Host/State/Lineage/LineageService.cs|Feature-specific framed byte identities replace a generic shared hashing helper.|dispatch-continuation",
            "state-key-sanitization|reviewed_obsolete|sanitizeStateKey||AuthorizedAcceptedStateComposer,LineageService|runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AuthorizedAcceptedStateComposer.cs,runtime/src/AgenticPrReview.Runtime/Host/State/Lineage/LineageService.cs|Scope-bound state identity rejects mismatches instead of sanitizing caller text into a key.|dispatch-continuation",
            "bounds-truncation|superseded|assertWithinLimit,truncateText||TerminalReviewValidator,ReviewedContentLimits,R4StickyRenderer|runtime/src/AgenticPrReview.Runtime/Agent/Tools/TerminalReviewValidator.cs,runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/ReviewedContentLimits.cs,runtime/src/AgenticPrReview.Runtime/Host/Publishing/Rendering/R4StickyRenderer.cs|Scalar UTF-8 and complete-block fail-closed rendering replace UTF-16 slicing.|public-result",
            "generic-filesystem-json-helpers|reviewed_obsolete|resolveWorkspacePath,readTextFile,ensureDir,writeTextFile,writeJsonFile,readJsonFile,walkFiles,relativePosix||BoundedReviewedSnapshotBuilder,AuthorizedAcceptedStateComposer|runtime/src/AgenticPrReview.Runtime/Host/Action/Snapshot/BoundedReviewedSnapshotBuilder.cs,runtime/src/AgenticPrReview.Runtime/Host/State/Restore/AuthorizedAcceptedStateComposer.cs|No generic utility barrel survives; feature-local C# code owns bounded I/O.|workflow-run",
        }, dispositionGroups.Select(value => string.Join('|',
            value.GetProperty("id").GetString(),
            value.GetProperty("typescript_alias_disposition").GetString(),
            JsonStrings(value, "symbols"),
            JsonStrings(value, "retained_direct_runtime_owners"),
            JsonStrings(value, "actionhost_owners"),
            JsonStrings(value, "evidence_paths"),
            value.GetProperty("deliberate_semantic_difference").GetString(),
            value.GetProperty("framework_scenario_id").GetString()))
            .ToArray());
        var expectedSymbols = new[]
        {
            "RuntimeProvider", "LiveProvider", "RuntimeBackend", "TargetMode",
            "ReviewMode", "Phase", "EffectiveDiffSource", "ToolMode",
            "InlineCommentSeverity", "InlineCommentConfidence",
            "ModelReviewFindingV1", "ModelReviewContentV1", "StructuredFindingV1",
            "ReviewedRange", "StructuredReviewEnvelopeV1", "InlineCommentsPolicy",
            "InlineCommentsMetadata", "LoadedBlock", "ChangedFile",
            "PullRequestDiffSnapshotV1", "PullRequestDiffSnapshotEntryV1",
            "PullRequestDiffSnapshotChangedEntryV1",
            "PullRequestDiffSnapshotRemovedEntryV1", "PullRequestDiffSnapshotDeltaV1",
            "ReviewTarget", "RestoredState", "RuntimeUsage", "RuntimeUsageTotals",
            "RuntimeLineageTotals", "LineageReason", "LineageAction", "required",
            "parseInteger", "parseOptionalInteger", "parsePositiveInteger",
            "parseOptionalPositiveInteger", "parseBoolean", "oneOf", "clamp",
            "sha256", "normalizeRepoRelativePath", "sanitizeStateKey",
            "assertWithinLimit", "truncateText", "resolveWorkspacePath",
            "readTextFile", "ensureDir", "writeTextFile", "writeJsonFile",
            "readJsonFile", "walkFiles", "relativePosix",
        };
        var actualSymbols = dispositionGroups.SelectMany(value =>
                value.GetProperty("symbols").EnumerateArray()
                    .Select(symbol => symbol.GetString()!))
            .ToArray();
        Assert.Equal(expectedSymbols.Length, actualSymbols.Length);
        Assert.Equal(expectedSymbols.Order(StringComparer.Ordinal),
            actualSymbols.Order(StringComparer.Ordinal));
        Assert.Equal(actualSymbols.Length,
            actualSymbols.Distinct(StringComparer.Ordinal).Count());
        var scenarios = w15.GetProperty("framework_scenario_ids")
            .EnumerateArray().Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(dispositionGroups, group =>
        {
            Assert.False(string.IsNullOrWhiteSpace(group.GetProperty(
                "deliberate_semantic_difference").GetString()));
            var scenario = group.GetProperty("framework_scenario_id").GetString()!;
            Assert.Contains(scenario, scenarios);
            Assert.All(group.GetProperty("evidence_paths").EnumerateArray(), evidence =>
            {
                var path = evidence.GetString()!;
                Assert.True(File.Exists(Path.Join(root,
                    path.Replace('/', Path.DirectorySeparatorChar))),
                    $"Missing W15 evidence: {path}");
            });
        });
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
        var residualAllowlist = File.ReadAllText(Path.Join(root, "src",
            "residual-reference-allowlist.ts"));
        Assert.DoesNotContain("RR-003", residualAllowlist, StringComparison.Ordinal);
        Assert.DoesNotContain("RR-008", residualAllowlist, StringComparison.Ordinal);

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

    private static string JsonStrings(JsonElement value, string property) =>
        value.TryGetProperty(property, out var array)
            ? string.Join(',', array.EnumerateArray()
                .Select(item => item.GetString()))
            : string.Empty;

    private static string BaseBlobDigest(
        string repository,
        string revision,
        string path)
        => Convert.ToHexString(SHA256.HashData(
                BaseBlobBytes(repository, revision, path)))
            .ToLowerInvariant();

    private static void AssertDescriptionOnlySchemaChange(
        string repository,
        string revision,
        string path,
        string definition)
    {
        var prior = JsonNode.Parse(BaseBlobBytes(repository, revision, path))!;
        var current = JsonNode.Parse(File.ReadAllBytes(Path.Join(repository,
            path.Replace('/', Path.DirectorySeparatorChar))))!;
        var priorDescription = prior["definitions"]![definition]!["description"]!
            .GetValue<string>();
        var currentDescription = current["definitions"]![definition]!["description"]!
            .GetValue<string>();
        Assert.NotEqual(priorDescription, currentDescription);
        Assert.Contains("SchemaContracts", currentDescription,
            StringComparison.Ordinal);
        prior["definitions"]![definition]!["description"] = currentDescription;
        Assert.True(JsonNode.DeepEquals(prior, current));
    }

    private static byte[] BaseBlobBytes(
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
        return bytes.ToArray();
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
