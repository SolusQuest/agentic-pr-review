#!/usr/bin/env bash
# Managed mode qualifies installed Solus review capacity. Explicit framework/aot/all
# execute structural Host and generic report-reader checks after SESSION retirement.
set -euo pipefail
umask 077
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj"
TEST_PROJECT="${REPO_ROOT}/runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
CORPUS="${REPO_ROOT}/runtime/tests/fixtures/agent/r7/capacity/cases.json"
ASSEMBLY="AgenticPrReview.Runtime.ReviewEvaluationFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"
MODE="${1:-managed}"
case "${MODE}" in managed|framework|aot|all) ;; *) printf 'APR_R7_CAPACITY_ARGUMENTS\n' >&2; exit 2 ;; esac
[[ $# -le 1 ]] || { printf 'APR_R7_CAPACITY_ARGUMENTS\n' >&2; exit 2; }
cd "${REPO_ROOT}"
if [[ "${MODE}" == managed ]]; then
  exec node runtime/tests/SolusCapacityFixture/verify.mjs
fi
[[ -z "$(git status --porcelain --untracked-files=normal)" ]] || { printf 'APR_R7_CAPACITY_SOURCE_DIRTY\n' >&2; exit 1; }
SOURCE_SHA="$(git rev-parse HEAD)"
SOURCE_TREE="$(git rev-parse 'HEAD^{tree}')"
ROOT="$(mktemp -d /tmp/apr-r7-capacity-gate-XXXXXXXX)"
touch "${ROOT}/.r7-capacity-root"
_cleanup() {
  [[ -f "${ROOT}/.r7-capacity-root" && "${ROOT}" == /tmp/apr-r7-capacity-gate-* ]] || return 1
  rm -rf -- "${ROOT}"
  [[ ! -e "${ROOT}" ]]
}
trap _cleanup EXIT
trap 'exit 130' INT TERM
trap 'printf "APR_R7_CAPACITY_GATE_FAILED line=%s\n" "${LINENO}" >&2' ERR
_run() {
  local mode="$1"
  local -a runner
  if [[ "${mode}" == framework ]]; then
    "${DOTNET_CMD}" build "${PROJECT}" -c Release -o "${ROOT}/framework" >"${ROOT}/framework.build.log" 2>&1
    runner=("${DOTNET_CMD}" "${ROOT}/framework/${ASSEMBLY}.dll")
  else
    [[ "$(uname -s)" == Linux && "$(uname -m)" == x86_64 ]]
    "${DOTNET_CMD}" publish "${PROJECT}" -c Release -r linux-x64 --self-contained true \
      -p:PublishAot=true -p:IsAotCompatible=true -p:VerifyReferenceAotCompatibility=true -p:JsonSerializerIsReflectionEnabledByDefault=false \
      -o "${ROOT}/aot" >"${ROOT}/aot.build.log" 2>&1
    runner=("${ROOT}/aot/${ASSEMBLY}")
    [[ -x "${runner[0]}" && "$(head -c 4 "${runner[0]}" | od -An -tx1 | tr -d ' \n')" == 7f454c46 ]]
  fi
  # Structural Host authority, claims, failure preservation and reset only.
  # Installed Solus V3 remains the separate current context/capacity gate.
  "${runner[@]}" r7-capacity-host
  local artifact="${ROOT}/${mode}/${ASSEMBLY}"
  if [[ "${mode}" == framework ]]; then artifact+='.dll'; fi
  local artifact_sha
  artifact_sha="$(sha256sum "${artifact}" | cut -d' ' -f1)"
  printf 'r7_capacity_gate mode=%s source=%s tree=%s artifact=%s cases=15 host_cases=8 result=verified\n' \
    "${mode}" "${SOURCE_SHA}" "${SOURCE_TREE}" "${artifact_sha}"
  if [[ "${mode}" == framework ]]; then
    "${DOTNET_CMD}" test "${TEST_PROJECT}" -c Release --nologo \
      --filter 'FullyQualifiedName~R7CapacityVerifierTests|FullyQualifiedName~R7CapacityUserOracleTests' \
      --logger "trx;LogFileName=${ROOT}/focused.trx" >"${ROOT}/focused.log" 2>&1
    "${runner[@]}" verify-cases --trx "${ROOT}/focused.trx" \
      --require 'R7CapacityVerifierTests,R7CapacityUserOracleTests'
    printf 'r7_capacity_gate trusted_config_and_focused_tests=verified\n'
  fi

}
if [[ "${MODE}" != aot ]]; then _run framework; fi
if [[ "${MODE}" != framework ]]; then _run aot; fi
[[ "$(git rev-parse HEAD)" == "${SOURCE_SHA}" && "$(git rev-parse 'HEAD^{tree}')" == "${SOURCE_TREE}" &&
   -z "$(git status --porcelain --untracked-files=normal)" ]]
_cleanup
trap - EXIT
printf 'APR_R7_CAPACITY_GATE_PASSED cleanup=verified\n'
