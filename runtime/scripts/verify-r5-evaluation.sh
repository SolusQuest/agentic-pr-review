#!/usr/bin/env bash
# Deterministic R5 evaluation gate for framework-dependent .NET and linux-x64
# Native AOT. Executes the named R5 deterministic corpus in both modes against
# an outside-in declared coverage inventory, proves framework/AOT semantic
# parity, and fails closed on coverage, leakage or cleanup violations.
#
# Subcommands:
#   framework   Framework-dependent mode: build once, run all scenarios + the
#               focused Host/session test set (TRX-verified).
#   aot         Native AOT mode: publish linux-x64 once, run all scenarios.
#   all         framework -> aot -> cross-mode parity -> cleanup assertion.
#
# Provider-secret-free by construction: no scenario invokes live-local
# --execute; the generated plan is an ephemeral dry-run input only.
#
# Logs are public-safe: only stable r5_eval_gate topology lines and verifier
# verdict JSON are printed. All private state lives under a marked temp root
# outside the repository and must be absent for the gate to pass.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj"
TEST_PROJECT="${REPO_ROOT}/runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
FIXTURES="${REPO_ROOT}/runtime/tests/fixtures/agent/r5"
ASSEMBLY="AgenticPrReview.Runtime.ReviewEvaluationFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"

CORPUS_CANARIES="APR242_TERMINAL_CANARY,APR242_CANDIDATE_CANARY,APR242_NOTES_CANARY,APR242_POLICY_CANARY,APR242_SOURCE_CANARY"
LIVE_CANARY="APR251_PRIVATE_CONTENT_CANARY"
FOCUSED_CLASSES="R5IncrementalReviewTests,R5SessionGrowthTests,R5CapacityResetTests,R5ResetHandoffTests,R5VerifierCoverageTests"

_roots=()

_cleanup() {
  if [[ ${#_roots[@]} -eq 0 ]]; then
    return 0
  fi
  local root
  for root in "${_roots[@]}"; do
    if [[ -n "${root:-}" && -d "${root}" ]]; then
      rm -rf -- "${root}" || true
      if [[ -d "${root}" ]]; then
        printf 'APR_R5_EVAL_CLEANUP_RETAINED %s\n' "${root}" >&2
      fi
    fi
  done
}
trap _cleanup EXIT INT TERM

_script_error() {
  printf 'APR_R5_EVAL_SCRIPT_ERROR line=%s\n' "$1" >&2
}
trap '_script_error "${LINENO}"' ERR

_fail() {
  printf '%s\n' "$1" >&2
  exit 1
}

_require_file() {
  local label="$1"
  local path="$2"
  [[ -f "${path}" ]] || _fail "APR_R5_EVAL_FIXTURE_MISSING ${label}"
}

_require_inputs() {
  _require_file project "${PROJECT}"
  _require_file test-project "${TEST_PROJECT}"
  _require_file quality-corpus "${FIXTURES}/quality/bundle/manifest.json"
  _require_file replay-corpus "${FIXTURES}/replay/manifest.json"
  _require_file incremental-corpus "${FIXTURES}/incremental/manifest.json"
  _require_file growth-corpus "${FIXTURES}/growth/manifest.json"
}

_new_root() {
  local __outvar="$1"
  local __dir
  __dir="$(mktemp -d)"
  touch "${__dir}/.r5-v1-temp-root"
  _roots+=("${__dir}")
  printf -v "${__outvar}" '%s' "${__dir}"
}

# $1 mode label, $2... runner argv. Runs one scenario, verifies its report
# against the declared inventory and records the verdict receipt.
_scenario() {
  local mode="$1" name="$2"; shift 2
  local report="${EVIDENCE}/${mode}/${name}.report.json"
  local verdict="${EVIDENCE}/${mode}/${name}.verdict.json"
  if ! "${RUNNER[@]}" "$@" >"${report}" 2>"${report}.err"; then
    cat "${report}.err" >&2 || true
    _fail "APR_R5_EVAL_SCENARIO_FAILED ${mode} ${name}"
  fi
  local extra=()
  case "${name}" in
    reset-owner|live-self-test) ;;
    quality|live-plan) extra+=(--corpus "${FIXTURES}/quality/bundle") ;;
    *) extra+=(--corpus "${FIXTURES}/${name}") ;;
  esac
  case "${name}" in
    live-self-test) extra+=(--forbid "${LIVE_CANARY}") ;;
    *)              extra+=(--forbid "${CORPUS_CANARIES}") ;;
  esac
  if ! "${RUNNER[@]}" verify-cases --scenario "${name}" "${extra[@]}" --report "${report}" >"${verdict}"; then
    cat "${verdict}" >&2 || true
    _fail "APR_R5_EVAL_COVERAGE_REJECTED ${mode} ${name}"
  fi
  printf 'r5_eval_gate mode=%s scenario=%s result=verified\n' "${mode}" "${name}"
}

_build_framework() {
  local log="${BUILD}/build-framework.log"
  "${DOTNET_CMD}" build "${PROJECT}" -c Release --nologo \
      -o "${BUILD}/framework" >"${log}" 2>&1 ||
    _fail "APR_R5_EVAL_FRAMEWORK_BUILD_FAILED"
  RUNNER=(
    "$(readlink -f "$(command -v "${DOTNET_CMD}")")"
    "$(if [[ "$(uname -s)" == MINGW* ]]; then cygpath -w "${BUILD}/framework/${ASSEMBLY}.dll"; else printf '%s' "${BUILD}/framework/${ASSEMBLY}.dll"; fi)"
  )
  ARTIFACT_SHA="$(sha256sum "${BUILD}/framework/${ASSEMBLY}.dll" | cut -d' ' -f1)"
}

_build_aot() {
  local log="${BUILD}/build-aot.log"
  "${DOTNET_CMD}" publish "${PROJECT}" -c Release -r linux-x64 \
      --self-contained true -p:PublishAot=true \
      -p:JsonSerializerIsReflectionEnabledByDefault=false \
      -o "${BUILD}/aot" >"${log}" 2>&1 ||
    _fail "APR_R5_EVAL_AOT_PUBLISH_FAILED"
  RUNNER=("${BUILD}/aot/${ASSEMBLY}")
  [[ -x "${RUNNER[0]}" ]] || _fail "APR_R5_EVAL_AOT_BINARY_MISSING"
  ARTIFACT_SHA="$(sha256sum "${RUNNER[0]}" | cut -d' ' -f1)"
}

_run_scenarios() {
  local mode="$1"
  mkdir -p -- "${EVIDENCE}/${mode}"
  _scenario "${mode}" quality       quality --corpus "${FIXTURES}/quality"
  _scenario "${mode}" replay        replay --bundle "${FIXTURES}/replay"
  _scenario "${mode}" incremental   replay --bundle "${FIXTURES}/incremental"
  _scenario "${mode}" growth        replay --bundle "${FIXTURES}/growth"
  _scenario "${mode}" reset-owner   reset --fixture self-test
  _scenario "${mode}" live-self-test live-local --dry-run --fixture self-test
  local plan="${EVIDENCE}/${mode}/live.plan.json"
  "${RUNNER[@]}" r5-plan --corpus "${FIXTURES}/quality/bundle" --out "${plan}" \
      >"${EVIDENCE}/${mode}/live.plan.out" ||
    _fail "APR_R5_EVAL_PLAN_FAILED ${mode}"
  _scenario "${mode}" live-plan live-local --dry-run --plan "${plan}"
  printf 'r5_eval_gate mode=%s artifact_sha256=%s\n' "${mode}" "${ARTIFACT_SHA}"
}

_run_focused_tests() {
  local trx="${EVIDENCE}/focused.trx"
  local trx_arg="${trx}"
  if [[ "$(uname -s)" == MINGW* ]]; then
    trx_arg="$(cygpath -w "${trx}")"
  fi
  "${DOTNET_CMD}" test "${TEST_PROJECT}" -c Release --nologo \
      --filter "FullyQualifiedName~R5IncrementalReviewTests|FullyQualifiedName~R5SessionGrowthTests|FullyQualifiedName~R5CapacityResetTests|FullyQualifiedName~R5ResetHandoffTests|FullyQualifiedName~R5VerifierCoverageTests" \
      --logger "trx;LogFileName=${trx_arg}" >"${BUILD}/focused.log" 2>&1 ||
    _fail "APR_R5_EVAL_FOCUSED_TESTS_FAILED"
  "${RUNNER[@]}" verify-cases --trx "${trx}" --require "${FOCUSED_CLASSES}" ||
    _fail "APR_R5_EVAL_FOCUSED_COVERAGE_REJECTED"
  printf 'r5_eval_gate mode=framework focused_tests=verified\n'
}

run_framework() {
  _build_framework
  _run_scenarios framework
  _run_focused_tests
}

run_aot() {
  _build_aot
  _run_scenarios aot
}

run_parity() {
  local name
  for name in quality replay incremental growth reset-owner live-self-test live-plan; do
    "${RUNNER[@]}" verify-cases --parity \
        "${EVIDENCE}/framework/${name}.verdict.json" \
        "${EVIDENCE}/aot/${name}.verdict.json" ||
      _fail "APR_R5_EVAL_PARITY_REJECTED ${name}"
    printf 'r5_eval_gate parity=%s result=verified\n' "${name}"
  done
}

_assert_cleanup() {
  # Evidence root: the constrained verifier helper must delete it (marked
  # private root under system temp). Build root: fail-closed removal plus
  # absence assertion -- the loaded runner is never deleted out from under
  # itself because evidence is verified first.
  local runner_bin
  if [[ -f "${BUILD}/framework/${ASSEMBLY}.dll" ]]; then
    runner_bin=("$(readlink -f "$(command -v "${DOTNET_CMD}")")" "${BUILD}/framework/${ASSEMBLY}.dll")
  else
    runner_bin=("${BUILD}/aot/${ASSEMBLY}")
  fi
  "${runner_bin[@]}" verify-cases --cleanup "${EVIDENCE}" ||
    _fail "APR_R5_EVAL_CLEANUP_REJECTED ${EVIDENCE}"
  rm -rf -- "${BUILD}"
  [[ ! -d "${BUILD}" ]] || _fail "APR_R5_EVAL_CLEANUP_REJECTED ${BUILD}"
  [[ ! -d "${EVIDENCE}" ]] || _fail "APR_R5_EVAL_CLEANUP_REJECTED ${EVIDENCE}"
  printf 'r5_eval_gate cleanup=verified\n'
}

run_all() {
  run_framework
  run_aot
  # Parity verification needs a runner; reuse the AOT binary.
  RUNNER=("${BUILD}/aot/${ASSEMBLY}")
  run_parity
  _assert_cleanup
}

subcommand="${1:-all}"
case "${subcommand}" in
  framework|aot)
    _require_inputs
    _new_root BUILD
    _new_root EVIDENCE
    "run_${subcommand}"
    _assert_cleanup
    ;;
  all)
    _require_inputs
    _new_root BUILD
    _new_root EVIDENCE
    run_all
    ;;
  -h|--help|help)
    cat <<'USAGE'
Usage: verify-r5-evaluation.sh [framework|aot|all]

  framework   Build once; run all R5 scenarios + focused Host/session tests.
  aot         Publish linux-x64 Native AOT once; run all R5 scenarios.
  all         framework -> aot -> cross-mode parity -> cleanup (default).
USAGE
    ;;
  *)
    printf 'error: unknown subcommand: %s
' "${subcommand}" >&2
    exit 2
    ;;
esac
