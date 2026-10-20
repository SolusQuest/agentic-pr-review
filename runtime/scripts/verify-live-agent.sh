#!/usr/bin/env bash
# Current credential-free R3 components; managed performs actual archive qualification.
# Superseded native SESSION execution is retired. Historical records stay unchanged.
set -euo pipefail

# Builtins only: retired entrypoints stop before paths, helpers or credential reads.
case "${1:-}" in
  aot)
    printf '%s\n' APR_R3_MANAGED_REQUIRED >&2
    exit 2
    ;;
  live)
    if [[ "$#" -ne 2 || "${2:-}" != --dry-run ]]; then
      printf '%s\n' APR_R3_MANAGED_REQUIRED >&2
      exit 2
    fi
    ;;
esac

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
if [[ "${1:-}" == managed && "$#" -eq 1 ]]; then
  exec node "${SCRIPT_DIR}/../tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs"
fi
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
FIXTURES="${REPO_ROOT}/runtime/tests/fixtures/agent/r3-live-agent"
NEGATIVE_CASES="${FIXTURES}/cr1-r8-typed/negative-cases.tsv"
CANARY_ROUTES="${FIXTURES}/cr1-r8-typed/canary-routes.tsv"

_fail() { printf '%s\n' "$1" >&2; return 1; }
_apr_r3_component_root=''
_cleanup_components() {
  [[ -z "${_apr_r3_component_root}" || ! -d "${_apr_r3_component_root}" ]] ||
    rm -rf -- "${_apr_r3_component_root}"
}
trap '_cleanup_components' EXIT
_validate_manifest() {
  local header
  IFS= read -r header <"${NEGATIVE_CASES}"
  [[ "${header}" == $'case\tphase\tstate_expectation\tstable_code' ]] ||
    _fail APR_R3_LIVE_NEGATIVE_MANIFEST_INVALID
  [[ -z "$(cut -f1 "${NEGATIVE_CASES}" | tail -n +2 | sort | uniq -d)" ]] ||
    _fail APR_R3_LIVE_NEGATIVE_MANIFEST_DUPLICATE
  IFS= read -r header <"${CANARY_ROUTES}"
  [[ "${header}" == $'class\tapproved_route' ]] ||
    _fail APR_R3_LIVE_CANARY_MANIFEST_INVALID
  [[ -z "$(cut -f1 "${CANARY_ROUTES}" | tail -n +2 | sort | uniq -d)" ]] ||
    _fail APR_R3_LIVE_CANARY_MANIFEST_DUPLICATE
}


# Generic private receipt operations remain exercised by the component suite.
_prepare_receipt_parents() {
  local root="$1"
  (umask 077; mkdir -p -- "${root}/receipts/negative")
    (umask 077; mkdir -p -- "${root}/receipts/negative-seeds")
  (umask 077; mkdir -p -- "${root}/receipts")
}

_relocate_private_receipt() {
  local root="$1" private_output="$2" receipt="$3"
    mv -- "${private_output}" "${root}/receipts/${receipt}.json"
    [[ ! -e "${root}/receipts/${receipt}.json.typed-observations.json" && ! -L "${root}/receipts/${receipt}.json.typed-observations.json" ]] || _fail APR_R3_LIVE_TYPED_OBSERVER_RELOCATION_CONFLICT
    mv -n -- "${private_output}.typed-observations.json" "${root}/receipts/${receipt}.json.typed-observations.json" || _fail APR_R3_LIVE_TYPED_OBSERVER_MOVE_FAILED
    [[ ! -e "${private_output}.typed-observations.json" && ! -L "${private_output}.typed-observations.json" ]] || _fail APR_R3_LIVE_TYPED_OBSERVER_RELOCATION_CONFLICT
}

_project_component_failures() {
  node --input-type=module - "$1" <<'APR_R3_TEST_DIAGNOSTIC'
import { readFileSync, lstatSync } from 'node:fs';
// BEGIN APR_R3_TRUSTED_LIVE_TEST_PROJECTION
const trustedLiveDeclaredMethods = new Set([
  "AdmittedSessionFailuresRetainTheirExactClassification",
  "CanaryCompletionClearsAllPhaseDerivedEvidence",
  "CanaryFailureSuppressesAllReceiptFields",
  "CanonicalProductResultBytesRemainDiagnosticFree",
  "CleanupAndContinuationFailuresRemainClassifiedThroughAdmission",
  "CompletionRecordIsBoundedAndDoesNotContainSecrets",
  "EveryChildStageProducesOnlyClosedPrivateFailureEvidence",
  "ExceptionsMapDirectlyToClosedPrivateCategories",
  "FailedPhaseIsSeparatedFromPassingPhaseEvidence",
  "FailedReceiptNeverPromotesACompletedQualityOutcome",
  "FailureClassificationIsExhaustiveAndStable",
  "FileLeakFromAFailedChildDominatesTheProviderFailure",
  "FreshProcessProofContractDoesNotGrantExecutionCapabilities",
  "LauncherCancellationTerminatesTheProcessTree",
  "LauncherRejectsEmbeddedArgumentAndOversizedOutputLeaks",
  "LauncherSerializesFreshProcessesAndTimesOut",
  "LauncherTimeoutTerminatesTheProcessTree",
  "LauncherUsesPublicArgumentsAndAnExactChildEnvironment",
  "MaterializerFailureAndPrivateMarkerRetainClosedCategories",
  "MaterializerUsesCaseSpecificEvidenceAndContinuationPolicies",
  "MissingOrCanaryReceiptFailsClosed",
  "PrivateFailureCodecRejectsUnknownDuplicateAndRawValues",
  "PrivateFailureMarkerUsesCreateNewSemantics",
  "ProductCodeInventoryExactlyCoversCurrentSourceFamilies",
  "QualityFailureProjectionSurvivesTransportProofWrapper",
  "ReceiptRejectsOverlongAndInconsistentCombinations",
  "ReceiptRejectsUnownedOrSensitiveLookingDiagnostics",
  "RejectedPassingReceiptBecomesNonReflectiveInvalidEvidence",
  "SupervisorConsumesOnlyTheAdmittedSeedReceiptBeforeRestore",
  "SupervisorEmitsOneSanitizedFailureRecord",
  "SupervisorSigtermTerminatesChildAndCleansSensitiveRoot",
  "TransportProofWrapperAdmitsMappedQualityFailure",
  "UnknownSessionCodeBecomesNonReflectiveInvalidReceiptEvidence"
]);
function projectTrustedLiveTestFailures(bytes) {
  const unknown = ['APR_R3_TRUSTED_LIVE_TEST_FAILURES_UNKNOWN'];
  if (!Buffer.isBuffer(bytes) || bytes.length === 0 || bytes.length > 1048576) return unknown;
  let text;
  try { text = new TextDecoder('utf-8', { fatal: true }).decode(bytes); }
  catch { return unknown; }
  if (/[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/u.test(text)) return unknown;
  const failed = new Set();
  for (const line of text.split(/\r?\n/u)) {
    if (line.length > 8192) return unknown;
    if (!line.includes('[FAIL]')) continue;
    const match = /^\[xUnit\.net [0-9:.]+\]\s+AgenticPrReview\.Runtime\.Tests\.Agent\.Core\.TrustedLiveAgentTests\.([A-Za-z][A-Za-z0-9_]{0,100})(?:\([^\r\n]*\))?\s+\[FAIL\]$/u.exec(line);
    if (!match || !trustedLiveDeclaredMethods.has(match[1])) return unknown;
    failed.add(match[1]);
  }
  if (failed.size === 0 || failed.size > 32) return unknown;
  return [...failed].sort().map(name => 'APR_R3_TRUSTED_LIVE_CASE_FAILED ' + name);
}
// END APR_R3_TRUSTED_LIVE_TEST_PROJECTION
try {
  const info = lstatSync(process.argv[2]);
  if (!info.isFile() || info.size > 1048576) throw new Error();
  for (const line of projectTrustedLiveTestFailures(readFileSync(process.argv[2]))) console.log(line);
} catch { console.log('APR_R3_TRUSTED_LIVE_TEST_FAILURES_UNKNOWN'); }
APR_R3_TEST_DIAGNOSTIC
}

run_current_components() {
  [[ "$(uname -s)" == Linux ]] || _fail APR_R3_CURRENT_COMPONENTS_REQUIRES_LINUX
  source "${SCRIPT_DIR}/require-linux-toolchain.sh"
  apr_require_linux_toolchain dotnet node || _fail APR_R3_CURRENT_COMPONENT_TOOLCHAIN_INVALID
  _validate_manifest
  node "${REPO_ROOT}/scripts/check-r3-live-proof.mjs"
  local apr_r3_root apr_r3_node apr_r3_dotnet apr_r3_dotnet_root apr_r3_packages
  apr_r3_root="$(mktemp -d)"
  _apr_r3_component_root="${apr_r3_root}"
  mkdir -p "${apr_r3_root}/home" "${apr_r3_root}/tmp"
  apr_r3_node="$(command -v node)"
  apr_r3_dotnet="$(command -v dotnet)"
  apr_r3_dotnet="$("${apr_r3_node}" -e 'process.stdout.write(require("node:fs").realpathSync(process.argv[1]))' "${apr_r3_dotnet}")"
  apr_r3_dotnet_root="$(dirname "${apr_r3_dotnet}")"
  apr_r3_packages="${NUGET_PACKAGES:-${HOME:?}/.nuget/packages}"
  # The existing native runner requires a complete nonempty TRX result. These
  # actual tests cover current argv, durable engine, quality, protocol, launcher,
  # receipt and capability responsibilities; they cannot qualify SDK execution.
  local apr_r3_filter
  apr_r3_filter='FullyQualifiedName~LiveAgentFreshProcessTests|FullyQualifiedName~LiveAgentSecretSourceTests|FullyQualifiedName~R3LiveAgentApplicationTests|FullyQualifiedName~CurrentWideQualityProjectionTests|FullyQualifiedName~AgentCapabilityArchitectureTests|FullyQualifiedName~LiveAgentVerifierContractTests|FullyQualifiedName~LiveAgentVerifierRetiredEntryTests|FullyQualifiedName~TrustedLiveAgentTests'
  if ! (
    cd "${REPO_ROOT}"
    env -i PATH="${PATH}" HOME="${apr_r3_root}/home" \
      TMPDIR="${apr_r3_root}/tmp" DOTNET_ROOT="${apr_r3_dotnet_root}" \
      DOTNET_CLI_HOME="${apr_r3_root}/home" DOTNET_CLI_TELEMETRY_OPTOUT=1 \
      NUGET_PACKAGES="${apr_r3_packages}" \
      "${apr_r3_node}" scripts/run-native-tests.mjs runtime \
      --configuration Release --artifacts-path "${apr_r3_root}/artifacts" \
      -p:UseSharedCompilation=false --filter "${apr_r3_filter}"
  ) >"${apr_r3_root}/components.log" 2>&1; then
    _project_component_failures "${apr_r3_root}/components.log"
    _fail APR_R3_CURRENT_COMPONENTS_FAILED
    return 1
  fi
  grep -E '^Runner counts: total=[0-9]+ executed=[0-9]+ passed=[0-9]+ failed=0 not-executed=[0-9]+$' \
    "${apr_r3_root}/components.log" || _fail APR_R3_CURRENT_COMPONENT_COUNTS_MISSING
  _cleanup_components || _fail APR_R3_CURRENT_COMPONENT_CLEANUP_FAILED
  printf '%s\n' APR_R3_CURRENT_COMPONENTS_OK
}

case "${1:-}" in
  deterministic)
    [[ "$#" -eq 1 ]] || _fail APR_R3_CURRENT_ARGUMENTS_INVALID
    run_current_components
    ;;
  live)
    run_current_components
    ;;
  -h|--help|help)
    printf '%s\n' 'Usage: verify-live-agent.sh [deterministic|managed|live --dry-run]'
    printf '%s\n' 'Native aot/live execution and live preparation are retired; use managed qualification.'
    ;;
  *)
    _fail APR_R3_CURRENT_ARGUMENTS_INVALID
    ;;
esac
