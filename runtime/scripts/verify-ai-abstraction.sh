#!/usr/bin/env bash
# Deterministic comparison gate for the two R2 AI-abstraction candidates.
#
# The comparison fixture is intentionally isolated from the production runtime.
# Every candidate is restored and compiled in its own directory. Framework and
# linux-x64 Native AOT modes then execute the same first-run, resume, negative,
# state-last, canary, and byte-oracle checks.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/runtime/tests/AiAbstractionAotFixture/AgenticPrReview.Runtime.AiAbstractionAotFixture.csproj"
FIXTURES="${REPO_ROOT}/runtime/tests/fixtures/agent/ai-abstraction"
FIRST_INPUT="${FIXTURES}/first-input.json"
RESUME_INPUT="${FIXTURES}/resume-input.json"
EXPECTED_FIRST="${FIXTURES}/expected-first.json.golden"
EXPECTED_RESUME="${FIXTURES}/expected-resume.json.golden"
EXPECTED_COMBINED="${FIXTURES}/expected-combined.json.golden"
ASSEMBLY="AgenticPrReview.Runtime.AiAbstractionAotFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"

CANDIDATES=(Minimal MicrosoftExtensionsAI)
NEGATIVE_SCENARIOS=(
  unknown-tool
  unknown-argument-field
  duplicate-argument-field
  malformed-argument
  oversized-tool-result
  response-ordering
  continuation-missing
  continuation-altered
  continuation-oversized
  continuation-misplaced
  continuation-wrong-framing
  continuation-wrong-association
  continuation-wrong-role
  wrong-tool-result-id
  wrong-candidate
  wrong-provider
  wrong-model
  wrong-adapter
  wrong-session
  candidate-object-state
  cancellation
  before-evidence-commit
  before-state-commit
)

_tempdirs=()

_cleanup() {
  if [[ ${#_tempdirs[@]} -eq 0 ]]; then
    return 0
  fi
  local dir
  for dir in "${_tempdirs[@]}"; do
    if [[ -n "${dir:-}" && -d "${dir}" ]]; then
      rm -rf -- "${dir}" || true
    fi
  done
}
trap _cleanup EXIT

_new_tempdir() {
  local __outvar="$1"
  local __dir
  __dir="$(mktemp -d)"
  _tempdirs+=("${__dir}")
  printf -v "${__outvar}" '%s' "${__dir}"
}

_require_file() {
  local path="$1"
  if [[ ! -f "${path}" ]]; then
    printf 'APR_AI_FIXTURE_MISSING %s\n' "${path}" >&2
    exit 1
  fi
}

_require_inputs() {
  _require_file "${PROJECT}"
  _require_file "${FIRST_INPUT}"
  _require_file "${RESUME_INPUT}"
  _require_file "${EXPECTED_FIRST}"
  _require_file "${EXPECTED_RESUME}"
  _require_file "${EXPECTED_COMBINED}"
}

_run_selection_negatives() {
  local mode="$1"
  local root="$2"
  local label
  for label in absent unknown multiple; do
    local -a property=()
    case "${label}" in
      absent) ;;
      unknown) property=(-p:AiAbstractionCandidate=Unknown) ;;
      multiple)
        property=(-p:AiAbstractionCandidate=MicrosoftExtensionsAI%3BMinimal)
        ;;
    esac
    local log="${root}/candidate-${label}.log"
    if "${DOTNET_CMD}" msbuild "${PROJECT}" \
      -nologo \
      -t:ValidateAiAbstractionCandidate \
      -p:AiAbstractionMode="${mode}-${label}" \
      "${property[@]}" >"${log}" 2>&1; then
      printf 'APR_AI_CANDIDATE_INVALID_ACCEPTED %s\n' "${label}" >&2
      return 1
    fi
    if ! grep -Fq 'APR_AI_CANDIDATE_INVALID' "${log}"; then
      printf 'APR_AI_CANDIDATE_FAILURE_UNCLASSIFIED %s\n' "${label}" >&2
      return 1
    fi
  done
}

_dotnet_properties() {
  local candidate="$1"
  local mode="$2"
  local root="$3"
  printf '%s\n' \
    "-p:AiAbstractionCandidate=${candidate}" \
    "-p:AiAbstractionMode=${mode}" \
    "-p:AiAbstractionIsolationRoot=${root}/obj/" \
    "-p:MSBuildProjectExtensionsPath=${root}/obj/extensions/" \
    "-p:BaseIntermediateOutputPath=${root}/obj/intermediate/" \
    "-p:BaseOutputPath=${root}/bin/" \
    "-p:RestorePackagesPath=${root}/packages/"
}

_build_candidate() {
  local candidate="$1"
  local mode="$2"
  local root="$3"
  local -a properties
  mapfile -t properties < <(_dotnet_properties "${candidate}" "${mode}" "${root}")
  local log="${root}/build.log"

  if [[ "${mode}" == "framework" ]]; then
    if ! "${DOTNET_CMD}" restore "${PROJECT}" \
      "${properties[@]}" >"${log}" 2>&1; then
      printf 'APR_AI_RESTORE_FAIL %s %s\n' "${candidate}" "${mode}" >&2
      tail -n 80 "${log}" >&2
      return 1
    fi
    if ! "${DOTNET_CMD}" build "${PROJECT}" \
      -c Release \
      --no-restore \
      "${properties[@]}" >>"${log}" 2>&1; then
      printf 'APR_AI_BUILD_FAIL %s %s\n' "${candidate}" "${mode}" >&2
      tail -n 80 "${log}" >&2
      return 1
    fi
    CANDIDATE_RUNNER=(
      "${DOTNET_CMD}"
      "${root}/bin/Release/net10.0/${ASSEMBLY}.dll"
    )
  else
    if ! "${DOTNET_CMD}" restore "${PROJECT}" \
      -r linux-x64 \
      -p:SelfContained=true \
      -p:PublishAot=true \
      "${properties[@]}" >"${log}" 2>&1; then
      printf 'APR_AI_RESTORE_FAIL %s %s\n' "${candidate}" "${mode}" >&2
      tail -n 80 "${log}" >&2
      return 1
    fi
    if ! "${DOTNET_CMD}" publish "${PROJECT}" \
      -c Release \
      -r linux-x64 \
      --self-contained true \
      --no-restore \
      -p:PublishAot=true \
      -o "${root}/publish" \
      "${properties[@]}" >>"${log}" 2>&1; then
      printf 'APR_AI_AOT_PUBLISH_FAIL %s\n' "${candidate}" >&2
      tail -n 80 "${log}" >&2
      return 1
    fi
    CANDIDATE_RUNNER=("${root}/publish/${ASSEMBLY}")
  fi

  local assets="${root}/obj/extensions/project.assets.json"
  _require_file "${assets}"
  if [[ "${candidate}" == "Minimal" ]]; then
    if grep -Fq '"Microsoft.Extensions.AI.Abstractions/' "${assets}"; then
      printf 'APR_AI_DEPENDENCY_LEAK Minimal %s\n' "${mode}" >&2
      return 1
    fi
  elif ! grep -Fq '"Microsoft.Extensions.AI.Abstractions/10.8.1"' "${assets}"; then
    printf 'APR_AI_DEPENDENCY_MISSING MicrosoftExtensionsAI %s\n' "${mode}" >&2
    return 1
  fi

  local warnings
  warnings="$(grep -Eic ': warning [A-Z]+[0-9]+:' "${log}" || true)"
  printf 'APR_AI_METRIC warnings candidate=%s mode=%s count=%s\n' \
    "${candidate}" "${mode}" "${warnings}"
  if [[ "${mode}" == "aot" ]]; then
    local executable_bytes
    local publish_bytes
    executable_bytes="$(stat -c '%s' "${root}/publish/${ASSEMBLY}")"
    publish_bytes="$(find "${root}/publish" -type f -printf '%s\n' |
      awk '{ total += $1 } END { print total + 0 }')"
    printf 'APR_AI_METRIC bytes candidate=%s executable=%s publish=%s\n' \
      "${candidate}" "${executable_bytes}" "${publish_bytes}"
  fi
}

_run_positive() {
  local candidate="$1"
  local mode="$2"
  local root="$3"
  local work="${root}/positive"
  local -a canary_args=()
  local -a grep_args=()
  local canary
  for canary in "${ACTIVE_CANARIES[@]}"; do
    canary_args+=(--canary "${canary}")
    grep_args+=(-e "${canary}")
  done
  mkdir -p -- "${work}"

  "${CANDIDATE_RUNNER[@]}" first \
    --fixture "${FIRST_INPUT}" \
    --state "${work}/state.json" \
    --evidence "${work}/first.json" \
    --process-id "${work}/first.pid" \
    "${canary_args[@]}" >"${work}/first.log" 2>&1
  "${CANDIDATE_RUNNER[@]}" resume \
    --fixture "${RESUME_INPUT}" \
    --state "${work}/state.json" \
    --first-evidence "${work}/first.json" \
    --evidence "${work}/resume.json" \
    --combined "${work}/combined.json" \
    --process-id "${work}/resume.pid" \
    "${canary_args[@]}" >"${work}/resume.log" 2>&1

  cmp "${work}/first.json" "${EXPECTED_FIRST}"
  cmp "${work}/resume.json" "${EXPECTED_RESUME}"
  cmp "${work}/combined.json" "${EXPECTED_COMBINED}"
  if cmp -s "${work}/first.pid" "${work}/resume.pid"; then
    printf 'APR_AI_PROCESS_REUSE %s %s\n' "${candidate}" "${mode}" >&2
    return 1
  fi
  if grep -R -F "${grep_args[@]}" "${work}" >/dev/null; then
    printf 'APR_AI_CANARY_LEAK %s %s\n' "${candidate}" "${mode}" >&2
    return 1
  fi
  CANDIDATE_COMBINED="${work}/combined.json"
}

_run_negatives() {
  local candidate="$1"
  local mode="$2"
  local root="$3"
  local -a canary_args=()
  local -a grep_args=()
  local canary
  for canary in "${ACTIVE_CANARIES[@]}"; do
    canary_args+=(--canary "${canary}")
    grep_args+=(-e "${canary}")
  done
  local scenario
  for scenario in "${NEGATIVE_SCENARIOS[@]}"; do
    local work="${root}/negative/${scenario}"
    mkdir -p -- "${work}"
    "${CANDIDATE_RUNNER[@]}" negative \
      --scenario "${scenario}" \
      --first-fixture "${FIRST_INPUT}" \
      --resume-fixture "${RESUME_INPUT}" \
      --state "${work}/state.json" \
      --evidence "${work}/evidence.json" \
      "${canary_args[@]}" >"${work}/run.log" 2>&1
    if [[ -e "${work}/state.json" ]]; then
      printf 'APR_AI_STATE_PARTIAL %s %s %s\n' \
        "${candidate}" "${mode}" "${scenario}" >&2
      return 1
    fi
    if ! grep -Fxq "APR_AI_NEGATIVE_OK ${scenario}" "${work}/run.log"; then
      printf 'APR_AI_NEGATIVE_REPORT %s %s %s\n' \
        "${candidate}" "${mode}" "${scenario}" >&2
      return 1
    fi
    if grep -R -F "${grep_args[@]}" "${work}" >/dev/null; then
      printf 'APR_AI_CANARY_LEAK %s %s %s\n' \
        "${candidate}" "${mode}" "${scenario}" >&2
      return 1
    fi
  done
}

_run_mode() {
  local mode="$1"
  _require_inputs
  local mode_root
  _new_tempdir mode_root
  _run_selection_negatives "${mode}" "${mode_root}"
  local first_combined=""
  local candidate
  for candidate in "${CANDIDATES[@]}"; do
    local root="${mode_root}/${candidate}"
    mkdir -p -- "${root}"
    ACTIVE_CANARIES=(
      "apr-canary-github-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-actions-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-provider-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-state-crypto-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-runner-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-registry-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-cloud-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-signing-${mode}-${candidate}-$$-${RANDOM}"
      "apr-canary-unrelated-workflow-${mode}-${candidate}-$$-${RANDOM}"
    )
    _build_candidate "${candidate}" "${mode}" "${root}"
    _run_positive "${candidate}" "${mode}" "${root}"
    _run_negatives "${candidate}" "${mode}" "${root}"
    if [[ -z "${first_combined}" ]]; then
      first_combined="${CANDIDATE_COMBINED}"
    else
      cmp "${first_combined}" "${CANDIDATE_COMBINED}"
    fi
    printf 'APR_AI_CANDIDATE_OK %s %s\n' "${candidate}" "${mode}"
  done
  printf 'APR_AI_MODE_OK %s\n' "${mode}"
}

run_framework() {
  _run_mode framework
}

run_aot() {
  _run_mode aot
}

run_all() {
  run_framework
  run_aot
}

subcommand="${1:-all}"
case "${subcommand}" in
  framework) run_framework ;;
  aot) run_aot ;;
  all) run_all ;;
  selected-production)
    printf 'APR_AI_PRODUCTION_NOT_MATERIALIZED\n' >&2
    exit 3
    ;;
  -h|--help|help)
    cat <<'USAGE'
Usage: verify-ai-abstraction.sh [framework|aot|all|selected-production]

  framework            Compare both candidates on the .NET runtime.
  aot                  Compare both candidates as linux-x64 Native AOT binaries.
  all                  Run framework and AOT comparison gates (default).
  selected-production  Reserved until the evidence-backed selection is materialized.
USAGE
    ;;
  *)
    printf 'APR_AI_COMMAND_INVALID %s\n' "${subcommand}" >&2
    exit 2
    ;;
esac
