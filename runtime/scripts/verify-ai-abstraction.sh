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
PRODUCTION_PROJECT="${REPO_ROOT}/runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj"
SELECTED_SOURCE="${REPO_ROOT}/runtime/src/AgenticPrReview.Runtime/Agent/Chat/MinimalChatClient.cs"
FIXTURES="${REPO_ROOT}/runtime/tests/fixtures/agent/ai-abstraction"
FIRST_INPUT="${FIXTURES}/first-input.json"
RESUME_INPUT="${FIXTURES}/resume-input.json"
EXPECTED_FIRST="${FIXTURES}/expected-first.json.golden"
EXPECTED_RESUME="${FIXTURES}/expected-resume.json.golden"
EXPECTED_COMBINED="${FIXTURES}/expected-combined.json.golden"
SOURCE_MANIFEST="${FIXTURES}/manifests/neutral-source-manifest.tsv"
API_MANIFEST="${FIXTURES}/manifests/neutral-api-member-manifest.tsv"
ASSEMBLY="AgenticPrReview.Runtime.AiAbstractionAotFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"
NEUTRAL_COMMIT="405e0468fc15dc932970b378081ae030028409fe"

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
  wrong-existing-tool-result-id
  swapped-tool-results
  duplicate-call-id
  reordered-records
  continuation-rebound-existing
  continuation-rebound-unknown
  resume-instructions-missing
  resume-request-missing
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
  local label="$1"
  local path="$2"
  if [[ ! -f "${path}" ]]; then
    printf 'APR_AI_FIXTURE_MISSING %s\n' "${label}" >&2
    return 1
  fi
}

_require_inputs() {
  _require_file project "${PROJECT}"
  _require_file production-project "${PRODUCTION_PROJECT}"
  _require_file selected-source "${SELECTED_SOURCE}"
  _require_file first-input "${FIRST_INPUT}"
  _require_file resume-input "${RESUME_INPUT}"
  _require_file expected-first "${EXPECTED_FIRST}"
  _require_file expected-resume "${EXPECTED_RESUME}"
  _require_file expected-combined "${EXPECTED_COMBINED}"
  _require_file neutral-source-manifest "${SOURCE_MANIFEST}"
  _require_file neutral-api-manifest "${API_MANIFEST}"
}

_compare_files() {
  local actual="$1"
  local expected="$2"
  local code="$3"
  local context="$4"
  if ! cmp -s -- "${actual}" "${expected}"; then
    printf '%s %s\n' "${code}" "${context}" >&2
    return 1
  fi
}

_enforce_zero_warnings() {
  local candidate="$1"
  local mode="$2"
  local warnings="$3"
  if [[ "${warnings}" != "0" ]]; then
    printf 'APR_AI_WARNING_NONZERO %s %s %s\n' \
      "${candidate}" "${mode}" "${warnings}" >&2
    return 1
  fi
}

_verifier_self_tests() {
  local root
  _new_tempdir root
  printf 'actual' >"${root}/actual"
  printf 'expected' >"${root}/expected"
  local code
  for code in \
    APR_AI_FIRST_ORACLE_MISMATCH \
    APR_AI_RESUME_ORACLE_MISMATCH \
    APR_AI_FRAMEWORK_AOT_MISMATCH \
    APR_AI_CROSS_CANDIDATE_MISMATCH; do
    local output
    if output="$(_compare_files \
      "${root}/actual" "${root}/expected" "${code}" injected 2>&1)"; then
      printf 'APR_AI_VERIFIER_SELF_TEST_ACCEPTED %s\n' "${code}" >&2
      return 1
    fi
    if [[ "${output}" != "${code} injected" ]]; then
      printf 'APR_AI_VERIFIER_SELF_TEST_CODE %s\n' "${code}" >&2
      return 1
    fi
  done
  local warning_output
  if warning_output="$(_enforce_zero_warnings Candidate mode 1 2>&1)"; then
    printf 'APR_AI_VERIFIER_SELF_TEST_ACCEPTED APR_AI_WARNING_NONZERO\n' >&2
    return 1
  fi
  if [[ "${warning_output}" != "APR_AI_WARNING_NONZERO Candidate mode 1" ]]; then
    printf 'APR_AI_VERIFIER_SELF_TEST_CODE APR_AI_WARNING_NONZERO\n' >&2
    return 1
  fi
  local sentinel="${root}/absolute-private-sentinel"
  local missing_output
  if missing_output="$(_require_file symbolic-missing "${sentinel}" 2>&1)"; then
    printf 'APR_AI_VERIFIER_SELF_TEST_ACCEPTED APR_AI_FIXTURE_MISSING\n' >&2
    return 1
  fi
  if [[ "${missing_output}" != "APR_AI_FIXTURE_MISSING symbolic-missing" ||
        "${missing_output}" == *"${root}"* ]]; then
    printf 'APR_AI_VERIFIER_SELF_TEST_PRIVACY\n' >&2
    return 1
  fi
  printf 'APR_AI_VERIFIER_SELF_TEST_OK\n'
}

_emit_frozen_manifests() {
  printf 'APR_AI_MANIFEST neutral_source_sha256=%s\n' \
    "$(sha256sum "${SOURCE_MANIFEST}" | awk '{print $1}')"
  printf 'APR_AI_MANIFEST neutral_api_sha256=%s\n' \
    "$(sha256sum "${API_MANIFEST}" | awk '{print $1}')"
  local header
  IFS= read -r header <"${SOURCE_MANIFEST}"
  local candidate path expected_object expected_bytes scope
  while IFS=$'\t' read -r \
    candidate path expected_object expected_bytes scope; do
    if [[ -z "${candidate}" ]]; then
      continue
    fi
    local actual_object
    local actual_bytes
    if ! actual_object="$(git rev-parse "${NEUTRAL_COMMIT}:${path}" 2>/dev/null)" ||
       ! actual_bytes="$(git cat-file -s "${NEUTRAL_COMMIT}:${path}" 2>/dev/null)"; then
      printf 'APR_AI_NEUTRAL_EVIDENCE_MISSING %s\n' "${candidate}" >&2
      return 1
    fi
    if [[ "${actual_object}" != "${expected_object}" ||
          "${actual_bytes}" != "${expected_bytes}" ]]; then
      printf 'APR_AI_NEUTRAL_SOURCE_MISMATCH %s\n' "${candidate}" >&2
      return 1
    fi
  done < <(tail -n +2 "${SOURCE_MANIFEST}")
}

_run_fixture_checked() {
  local code="$1"
  local candidate="$2"
  local mode="$3"
  local log="$4"
  shift 4
  if ! "$@" >"${log}" 2>&1; then
    printf '%s %s %s\n' "${code}" "${candidate}" "${mode}" >&2
    return 1
  fi
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
      return 1
    fi
    if ! "${DOTNET_CMD}" build "${PROJECT}" \
      -c Release \
      --no-restore \
      "${properties[@]}" >>"${log}" 2>&1; then
      printf 'APR_AI_BUILD_FAIL %s %s\n' "${candidate}" "${mode}" >&2
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
      return 1
    fi
    CANDIDATE_RUNNER=("${root}/publish/${ASSEMBLY}")
  fi

  local assets="${root}/obj/extensions/project.assets.json"
  _require_file candidate-assets "${assets}"
  local graph="${root}/package-graph.txt"
  if ! python3 -c \
    'import json,sys; data=json.load(open(sys.argv[1], encoding="utf-8")); print("\n".join(sorted(key for key,value in data["libraries"].items() if value.get("type") == "package")))' \
    "${assets}" >"${graph}" 2>"${root}/assets-parse.log"; then
    printf 'APR_AI_ASSETS_PARSE_FAIL %s %s\n' "${candidate}" "${mode}" >&2
    return 1
  fi
  local ai_packages
  ai_packages="$(awk -F/ \
    'tolower($1) ~ /^microsoft\.extensions\.ai/ { print tolower($0) }' \
    "${graph}")"
  if [[ "${candidate}" == "Minimal" && -n "${ai_packages}" ]]; then
    printf 'APR_AI_DEPENDENCY_LEAK Minimal %s\n' "${mode}" >&2
    return 1
  fi
  if [[ "${candidate}" == "MicrosoftExtensionsAI" &&
        "${ai_packages}" != "microsoft.extensions.ai.abstractions/10.8.1" ]]; then
    printf 'APR_AI_DEPENDENCY_SET MicrosoftExtensionsAI %s\n' "${mode}" >&2
    return 1
  fi
  if grep -Eiq 'semantic.?kernel|agent.?framework' "${graph}"; then
    printf 'APR_AI_DEPENDENCY_FORBIDDEN %s %s\n' "${candidate}" "${mode}" >&2
    return 1
  fi
  local expected_graph="${FIXTURES}/manifests/${candidate}-${mode}-packages.txt"
  _require_file expected-package-graph "${expected_graph}"
  _compare_files "${graph}" "${expected_graph}" \
    APR_AI_DEPENDENCY_GRAPH_MISMATCH "${candidate}:${mode}"
  while IFS= read -r dependency; do
    if [[ -n "${dependency}" ]]; then
      printf 'APR_AI_MANIFEST dependency candidate=%s mode=%s package=%s\n' \
        "${candidate}" "${mode}" "${dependency}"
    fi
  done <"${graph}"
  printf 'APR_AI_METRIC package_graph_sha256 candidate=%s mode=%s sha256=%s\n' \
    "${candidate}" "${mode}" "$(sha256sum "${graph}" | awk '{print $1}')"

  local warnings
  warnings="$(grep -Eic ': warning [A-Z]+[0-9]+:' "${log}" || true)"
  printf 'APR_AI_METRIC warnings candidate=%s mode=%s count=%s\n' \
    "${candidate}" "${mode}" "${warnings}"
  _enforce_zero_warnings "${candidate}" "${mode}" "${warnings}"
  if [[ "${mode}" == "aot" ]]; then
    local executable_bytes
    local publish_bytes
    _require_file publish-executable "${root}/publish/${ASSEMBLY}"
    executable_bytes="$(stat -c '%s' "${root}/publish/${ASSEMBLY}")"
    publish_bytes="$(find "${root}/publish" -type f -printf '%s\n' |
      awk '{ total += $1 } END { print total + 0 }')"
    printf 'APR_AI_METRIC bytes candidate=%s executable=%s publish=%s\n' \
      "${candidate}" "${executable_bytes}" "${publish_bytes}"
    local publish_manifest="${root}/publish-manifest.txt"
    find "${root}/publish" -type f -printf '%P\t%s\n' |
      LC_ALL=C sort >"${publish_manifest}"
    while IFS=$'\t' read -r file bytes; do
      printf 'APR_AI_MANIFEST publish candidate=%s file=%s bytes=%s\n' \
        "${candidate}" "${file}" "${bytes}"
    done <"${publish_manifest}"
  fi
}

_check_production_dependency_graph() {
  local mode_root="$1"
  local root="${mode_root}/production-dependencies"
  mkdir -p -- "${root}"
  local log="${root}/restore.log"
  if ! "${DOTNET_CMD}" restore "${PRODUCTION_PROJECT}" \
    "-p:MSBuildProjectExtensionsPath=${root}/obj/" \
    "-p:BaseIntermediateOutputPath=${root}/obj/intermediate/" \
    "-p:RestorePackagesPath=${root}/packages/" >"${log}" 2>&1; then
    printf 'APR_AI_PRODUCTION_RESTORE_FAIL\n' >&2
    return 1
  fi
  local assets="${root}/obj/project.assets.json"
  _require_file production-assets "${assets}"
  local graph="${root}/package-graph.txt"
  if ! python3 -c \
    'import json,sys; data=json.load(open(sys.argv[1], encoding="utf-8")); print("\n".join(sorted(key for key,value in data["libraries"].items() if value.get("type") == "package")))' \
    "${assets}" >"${graph}" 2>"${root}/assets-parse.log"; then
    printf 'APR_AI_PRODUCTION_ASSETS_PARSE_FAIL\n' >&2
    return 1
  fi
  if awk -F/ 'tolower($1) ~ /^microsoft\.extensions\.ai/ { found=1 } END { exit !found }' \
    "${graph}"; then
    printf 'APR_AI_PRODUCTION_DEPENDENCY_LEAK\n' >&2
    return 1
  fi
  local expected_graph="${FIXTURES}/manifests/production-packages.txt"
  _require_file expected-production-package-graph "${expected_graph}"
  _compare_files "${graph}" "${expected_graph}" \
    APR_AI_PRODUCTION_DEPENDENCY_GRAPH_MISMATCH production
  while IFS= read -r dependency; do
    if [[ -n "${dependency}" ]]; then
      printf 'APR_AI_MANIFEST production_dependency package=%s\n' \
        "${dependency}"
    fi
  done <"${graph}"
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

  _run_fixture_checked APR_AI_FIRST_RUN_FAIL \
    "${candidate}" "${mode}" "${work}/first.log" \
    "${CANDIDATE_RUNNER[@]}" first \
    --fixture "${FIRST_INPUT}" \
    --state "${work}/state.json" \
    --evidence "${work}/first.json" \
    --process-id "${work}/first.pid" \
    "${canary_args[@]}"
  _run_fixture_checked APR_AI_RESUME_RUN_FAIL \
    "${candidate}" "${mode}" "${work}/resume.log" \
    "${CANDIDATE_RUNNER[@]}" resume \
    --fixture "${RESUME_INPUT}" \
    --state "${work}/state.json" \
    --first-evidence "${work}/first.json" \
    --evidence "${work}/resume.json" \
    --combined "${work}/combined.json" \
    --process-id "${work}/resume.pid" \
    "${canary_args[@]}"

  _compare_files "${work}/first.json" "${EXPECTED_FIRST}" \
    APR_AI_FIRST_ORACLE_MISMATCH "${candidate}:${mode}"
  _compare_files "${work}/resume.json" "${EXPECTED_RESUME}" \
    APR_AI_RESUME_ORACLE_MISMATCH "${candidate}:${mode}"
  _compare_files "${work}/combined.json" "${EXPECTED_COMBINED}" \
    APR_AI_COMBINED_ORACLE_MISMATCH "${candidate}:${mode}"
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
    _run_fixture_checked APR_AI_NEGATIVE_RUN_FAIL \
      "${candidate}:${scenario}" "${mode}" "${work}/run.log" \
      "${CANDIDATE_RUNNER[@]}" negative \
      --scenario "${scenario}" \
      --first-fixture "${FIRST_INPUT}" \
      --second-fixture "${RESUME_INPUT}" \
      --state "${work}/state.json" \
      --evidence "${work}/evidence.json" \
      "${canary_args[@]}"
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
  _emit_frozen_manifests
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
      _compare_files "${first_combined}" "${CANDIDATE_COMBINED}" \
        APR_AI_CROSS_CANDIDATE_MISMATCH "${mode}"
    fi
    printf 'APR_AI_CANDIDATE_OK %s %s\n' "${candidate}" "${mode}"
  done
  MODE_COMBINED="${first_combined}"
  printf 'APR_AI_MODE_OK %s\n' "${mode}"
}

run_framework() {
  _run_mode framework
}

run_aot() {
  _run_mode aot
}

run_all() {
  _verifier_self_tests
  run_framework
  local framework_combined="${MODE_COMBINED}"
  run_aot
  _compare_files "${framework_combined}" "${MODE_COMBINED}" \
    APR_AI_FRAMEWORK_AOT_MISMATCH all
}

_run_selected_mode() {
  local mode="$1"
  _require_inputs
  if ! grep -Fq \
    '..\..\src\AgenticPrReview.Runtime\Agent\Chat\MinimalChatClient.cs' \
    "${PROJECT}"; then
    printf 'APR_AI_SELECTED_SOURCE_NOT_LINKED\n' >&2
    return 1
  fi
  local mode_root
  _new_tempdir mode_root
  if [[ "${mode}" == "framework" ]]; then
    _check_production_dependency_graph "${mode_root}"
  fi
  local candidate="Minimal"
  local root="${mode_root}/${candidate}"
  mkdir -p -- "${root}"
  ACTIVE_CANARIES=(
    "apr-canary-github-selected-${mode}-$$-${RANDOM}"
    "apr-canary-actions-selected-${mode}-$$-${RANDOM}"
    "apr-canary-provider-selected-${mode}-$$-${RANDOM}"
    "apr-canary-state-crypto-selected-${mode}-$$-${RANDOM}"
    "apr-canary-runner-selected-${mode}-$$-${RANDOM}"
    "apr-canary-registry-selected-${mode}-$$-${RANDOM}"
    "apr-canary-cloud-selected-${mode}-$$-${RANDOM}"
    "apr-canary-signing-selected-${mode}-$$-${RANDOM}"
    "apr-canary-unrelated-workflow-selected-${mode}-$$-${RANDOM}"
  )
  _build_candidate "${candidate}" "${mode}" "${root}"
  _run_positive "${candidate}" "selected-${mode}" "${root}"
  _run_negatives "${candidate}" "selected-${mode}" "${root}"
  printf 'APR_AI_SELECTED_PRODUCTION_OK %s\n' "${mode}"
}

run_selected_production() {
  _verifier_self_tests
  _run_selected_mode framework
  local framework_combined="${CANDIDATE_COMBINED}"
  _run_selected_mode aot
  _compare_files "${framework_combined}" "${CANDIDATE_COMBINED}" \
    APR_AI_FRAMEWORK_AOT_MISMATCH selected-production
}

subcommand="${1:-all}"
case "${subcommand}" in
  framework) run_framework ;;
  aot) run_aot ;;
  all) run_all ;;
  selected-production) run_selected_production ;;
  -h|--help|help)
    cat <<'USAGE'
Usage: verify-ai-abstraction.sh [framework|aot|all|selected-production]

  framework            Compare both candidates on the .NET runtime.
  aot                  Compare both candidates as linux-x64 Native AOT binaries.
  all                  Run framework and AOT comparison gates (default).
  selected-production  Execute the selected shared-source production seam.
USAGE
    ;;
  *)
    printf 'APR_AI_COMMAND_INVALID %s\n' "${subcommand}" >&2
    exit 2
    ;;
esac
