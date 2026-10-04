#!/usr/bin/env bash
# Credential-free, bounded R7 capacity proof. Each mode compiles its own exact-source
# evaluation fixture once and executes the same authored inventory in fresh children.
set -euo pipefail
umask 077
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj"
TEST_PROJECT="${REPO_ROOT}/runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
CORPUS="${REPO_ROOT}/runtime/tests/fixtures/agent/r7/capacity/cases.json"
ASSEMBLY="AgenticPrReview.Runtime.ReviewEvaluationFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"
MODE="${1:-all}"
case "${MODE}" in framework|aot|all) ;; *) printf 'APR_R7_CAPACITY_ARGUMENTS\n' >&2; exit 2 ;; esac
[[ $# -le 1 ]] || { printf 'APR_R7_CAPACITY_ARGUMENTS\n' >&2; exit 2; }
cd "${REPO_ROOT}"
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
      -p:PublishAot=true -p:JsonSerializerIsReflectionEnabledByDefault=false \
      -o "${ROOT}/aot" >"${ROOT}/aot.build.log" 2>&1
    runner=("${ROOT}/aot/${ASSEMBLY}")
    [[ -x "${runner[0]}" && "$(head -c 4 "${runner[0]}" | od -An -tx1 | tr -d ' \n')" == 7f454c46 ]]
  fi
  "${runner[@]}" r7-capacity-run --corpus "${CORPUS}" --out "${ROOT}/${mode}.json"
  node --input-type=module - "${ROOT}/${mode}.json" "${SOURCE_SHA}" "${SOURCE_TREE}" <<'NODE'
import { readFileSync } from 'node:fs';
const [path, commit, tree] = process.argv.slice(2);
const report = JSON.parse(readFileSync(path, 'utf8'));
if (report.sourceCommit !== commit || report.sourceTree !== tree || report.sourceClean !== true) process.exit(1);
NODE
  "${runner[@]}" r7-capacity-verify --corpus "${CORPUS}" --report "${ROOT}/${mode}.json"
  if grep -aEq 'APR_R7_PRIVATE_' "${ROOT}/${mode}.json"; then return 1; fi
  local artifact="${ROOT}/${mode}/${ASSEMBLY}"
  if [[ "${mode}" == framework ]]; then artifact+='.dll'; fi
  printf 'r7_capacity_gate mode=%s source=%s tree=%s artifact=%s cases=15 host_cases=8 result=verified\n' \
    "${mode}" "${SOURCE_SHA}" "${SOURCE_TREE}" "$(sha256sum "${artifact}" | cut -d' ' -f1)"
  if [[ "${mode}" == framework ]]; then
    "${DOTNET_CMD}" test "${TEST_PROJECT}" -c Release --nologo \
      --filter 'FullyQualifiedName~R7CapacityVerifierTests|FullyQualifiedName~R7CapacityStateTests|FullyQualifiedName~TrustedConfiguredCallsReachRealProviderAndBothSessionConsumers' \
      --logger "trx;LogFileName=${ROOT}/focused.trx" >"${ROOT}/focused.log" 2>&1
    "${runner[@]}" verify-cases --trx "${ROOT}/focused.trx" \
      --require 'R7CapacityVerifierTests,R7CapacityStateTests,TrustedConfiguredCallsReachRealProviderAndBothSessionConsumers'
    printf 'r7_capacity_gate trusted_config_and_focused_tests=verified\n'
  fi
  # Optional sanitized evidence export for a task audit; all private roots still get deleted.
  if [[ -n "${R7_CAPACITY_REPORT_DIR:-}" ]]; then
    mkdir -p -- "${R7_CAPACITY_REPORT_DIR}"
    cp -- "${ROOT}/${mode}.json" "${R7_CAPACITY_REPORT_DIR}/${mode}.json"
  fi
}
if [[ "${MODE}" != aot ]]; then _run framework; fi
if [[ "${MODE}" != framework ]]; then _run aot; fi
if [[ "${MODE}" == all ]]; then
  "${DOTNET_CMD}" "${ROOT}/framework/${ASSEMBLY}.dll" r7-capacity-parity --corpus "${CORPUS}" "${ROOT}/framework.json" "${ROOT}/aot.json"
fi
[[ "$(git rev-parse HEAD)" == "${SOURCE_SHA}" && "$(git rev-parse 'HEAD^{tree}')" == "${SOURCE_TREE}" &&
   -z "$(git status --porcelain --untracked-files=normal)" ]]
_cleanup
trap - EXIT
printf 'APR_R7_CAPACITY_GATE_PASSED cleanup=verified\n'
