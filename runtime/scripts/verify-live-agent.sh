#!/usr/bin/env bash
# Secret-free deterministic and Native AOT R3 live-agent replacement proof.
# Every product phase runs in a fresh process. Raw requests, SESSION/STATE,
# credentials, random prior facts, and logs remain in temporary roots and are
# deleted.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
RUNTIME_TESTS="${REPO_ROOT}/runtime/tests"
PROJECT="${RUNTIME_TESTS}/LiveAgentVerifierFixture/AgenticPrReview.Runtime.LiveAgentVerifierFixture.csproj"
TEST_PROJECT="${RUNTIME_TESTS}/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
CORPUS="${RUNTIME_TESTS}/fixtures/agent/r3-quality/corpus.json"
FIXTURES="${RUNTIME_TESTS}/fixtures/agent/r3-live-agent"
GOLDEN="${FIXTURES}/replacement-record.json.golden"
NEGATIVE_CASES="${FIXTURES}/negative-cases.tsv"
CANARY_ROUTES="${FIXTURES}/canary-routes.tsv"
ASSEMBLY="AgenticPrReview.Runtime.LiveAgentVerifierFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"
PROVIDER_CANARY="APR111_PROVIDER_SECRET_CANARY"
STATE_KEY_B64="QVBSMTExX1NUQVRFX0tFWV9DQU5BUllfMTIzNDU2Nzg="
GITHUB_CANARY="APR111_GITHUB_CANARY"
ACTIONS_CANARY="APR111_ACTIONS_CANARY"
WORKFLOW_CANARY="APR111_UNRELATED_WORKFLOW_CANARY"
REPOSITORY_CANARY="APR111_REPOSITORY_CANARY"
PATH_CANARY="APR111_PATH_CANARY"
PROMPT_CANARY="APR111_PROMPT_INJECTION_CANARY"
PUBLIC_RESULT_CANARY="APR111_PUBLIC_RESULT_CANARY"

_tempdirs=()

_windows_interop() {
  [[ "$(uname -s)" == MINGW* ]] ||
    [[ "$(uname -s)" == Linux &&
       -r /proc/sys/kernel/osrelease &&
       "$(tr '[:upper:]' '[:lower:]' </proc/sys/kernel/osrelease)" == *microsoft* &&
       "${DOTNET_CMD}" == *.exe ]]
}

_windows_path() {
  if [[ "$(uname -s)" == MINGW* ]]; then
    cygpath -w "$1"
  else
    wslpath -w "$1"
  fi
}

_cleanup() {
  local directory
  local status=0
  local -a remaining=()
  for directory in "${_tempdirs[@]:-}"; do
    if [[ -n "${directory:-}" && -d "${directory}" ]]; then
      if ! rm -rf -- "${directory}"; then
        status=1
        remaining+=("${directory}")
      fi
    fi
  done
  _tempdirs=("${remaining[@]}")
  return "${status}"
}
trap '_cleanup || true' EXIT

_script_error() {
  printf 'APR_R3_LIVE_SCRIPT_ERROR line=%s\n' "$1" >&2
}
trap '_script_error "${LINENO}"' ERR

_fail() {
  printf '%s\n' "$1" >&2
  return 1
}

_require_file() {
  [[ -f "$2" ]] || _fail "APR_R3_LIVE_FIXTURE_MISSING $1"
}

_new_tempdir() {
  local __outvar="$1"
  local __directory
  if _windows_interop && [[ "$(uname -s)" == Linux ]]; then
    __directory="$(mktemp -d /mnt/c/tmp/apr111.XXXXXX)"
  else
    __directory="$(mktemp -d)"
  fi
  _tempdirs+=("${__directory}")
  printf -v "${__outvar}" '%s' "${__directory}"
}

_closed_environment() {
  local root="$1"
  mkdir -p -- "${root}/home" "${root}/tmp" "${root}/dotnet-home"
  if _windows_interop; then
    local windows_root
    windows_root="$(_windows_path "${root}")"
    local dotnet_executable
    dotnet_executable="$(readlink -f "$(command -v "${DOTNET_CMD}")")"
    local windows_dotnet_root
    windows_dotnet_root="$(_windows_path "$(dirname "${dotnet_executable}")")"
    local wsl_environment
    wsl_environment="AGENTIC_REVIEW_DEEPSEEK_API_KEY:"
    wsl_environment+="AGENTIC_REVIEW_R3_STATE_KEY_B64:GITHUB_TOKEN:"
    wsl_environment+="ACTIONS_RUNTIME_TOKEN:APR111_UNRELATED_WORKFLOW:"
    wsl_environment+="APR111_REPOSITORY:APR111_PATH:APR111_PROMPT"
    wsl_environment+=":APR111_PUBLIC_RESULT"
    printf '%s\0' \
      "AGENTIC_REVIEW_DEEPSEEK_API_KEY=${PROVIDER_CANARY}" \
      "AGENTIC_REVIEW_R3_STATE_KEY_B64=${STATE_KEY_B64}" \
      "GITHUB_TOKEN=${GITHUB_CANARY}" \
      "ACTIONS_RUNTIME_TOKEN=${ACTIONS_CANARY}" \
      "APR111_UNRELATED_WORKFLOW=${WORKFLOW_CANARY}" \
      "APR111_REPOSITORY=${REPOSITORY_CANARY}" \
      "APR111_PATH=${PATH_CANARY}" \
      "APR111_PROMPT=${PROMPT_CANARY}" \
      "APR111_PUBLIC_RESULT=${PUBLIC_RESULT_CANARY}" \
      "WSLENV=${wsl_environment}" \
      "DOTNET_CLI_HOME=${windows_root}" \
      "DOTNET_ROOT=${windows_dotnet_root}" \
      "HOME=${windows_root}" \
      "HOMEDRIVE=C:" \
      "HOMEPATH=\\tmp" \
      "LANG=C.UTF-8" \
      "LC_ALL=C.UTF-8" \
      "LOGONSERVER=\\\\LOCALHOST" \
      "PATH=C:\\Windows\\System32" \
      "SYSTEMDRIVE=C:" \
      "SystemRoot=C:\\Windows" \
      "TEMP=${windows_root}" \
      "TMPDIR=${windows_root}" \
      "TZ=UTC" \
      "USERDOMAIN=LOCAL" \
      "USERNAME=proof" \
      "USERPROFILE=${windows_root}" \
      "WINDIR=C:\\Windows"
  else
    local dotnet_path
    dotnet_path="$(readlink -f "$(command -v "${DOTNET_CMD}")")"
    printf '%s\0' \
      "AGENTIC_REVIEW_DEEPSEEK_API_KEY=${PROVIDER_CANARY}" \
      "AGENTIC_REVIEW_R3_STATE_KEY_B64=${STATE_KEY_B64}" \
      "GITHUB_TOKEN=${GITHUB_CANARY}" \
      "ACTIONS_RUNTIME_TOKEN=${ACTIONS_CANARY}" \
      "APR111_UNRELATED_WORKFLOW=${WORKFLOW_CANARY}" \
      "APR111_REPOSITORY=${REPOSITORY_CANARY}" \
      "APR111_PATH=${PATH_CANARY}" \
      "APR111_PROMPT=${PROMPT_CANARY}" \
      "APR111_PUBLIC_RESULT=${PUBLIC_RESULT_CANARY}" \
      "DOTNET_CLI_HOME=${root}/dotnet-home" \
      "DOTNET_ROOT=$(dirname "${dotnet_path}")" \
      "HOME=${root}/home" \
      "LANG=C.UTF-8" \
      "LC_ALL=C.UTF-8" \
      "TMPDIR=${root}/tmp" \
      "TZ=UTC"
  fi
}

_run_closed_process() {
  local timeout_seconds="$1"
  local controlled_root="$2"
  shift 2
  local -a environment=()
  while [[ "$1" != "APR_R3_LIVE_COMMAND" ]]; do
    environment+=("$1")
    shift
  done
  shift
  if [[ "$(uname -s)" == MINGW* ]]; then
    node -e '
      const childProcess = require("child_process");
      const marker = process.argv.indexOf("APR_R3_LIVE_COMMAND");
      if (marker < 0 || marker + 1 >= process.argv.length) process.exit(125);
      const environment = {};
      for (const entry of process.argv.slice(3, marker)) {
        const separator = entry.indexOf("=");
        if (separator < 1) process.exit(125);
        environment[entry.slice(0, separator)] = entry.slice(separator + 1);
      }
      const result = childProcess.spawnSync(
        process.argv[marker + 1],
        process.argv.slice(marker + 2),
        {
          env: environment,
          cwd: process.argv[2],
          stdio: "inherit",
          timeout: Number(process.argv[1]) * 1000,
          windowsHide: true,
        });
      process.exit(result.status === null ? 124 : result.status);
    ' "${timeout_seconds}" "${controlled_root}" \
      "${environment[@]}" APR_R3_LIVE_COMMAND "$@"
  else
    (
      cd -- "${controlled_root}"
      timeout "${timeout_seconds}s" env -i "${environment[@]}" "$@"
    )
  fi
}

_run_fixture() {
  local controlled_root="$1"
  local verb="$2"
  local phase_root="$3"
  local output="$4"
  local log="$5"
  shift 5
  local -a extra_arguments=("$@")
  local -a environment
  mapfile -d '' -t environment < <(_closed_environment "${controlled_root}")
  local corpus="${CORPUS}"
  local root_argument="${phase_root}"
  local output_argument="${output}"
  local execution_artifact="${EXECUTION_ARTIFACT}"
  local build_pair_manifest="${BUILD_PAIR_MANIFEST}"
  local -a runner=("${RUNNER[@]}")
  if _windows_interop; then
    corpus="$(_windows_path "${corpus}")"
    root_argument="$(_windows_path "${phase_root}")"
    output_argument="$(_windows_path "${output}")"
    execution_artifact="$(_windows_path "${EXECUTION_ARTIFACT}")"
    build_pair_manifest="$(_windows_path "${BUILD_PAIR_MANIFEST}")"
    if [[ "${#runner[@]}" -gt 1 ]]; then
      runner[1]="$(_windows_path "${runner[1]}")"
    fi
    local index
    for ((index = 0; index < ${#extra_arguments[@]}; index++)); do
      case "${extra_arguments[index]}" in
        --negative-manifest|--canary-manifest|--replacement-target|--architecture-assembly)
          ((index += 1))
          extra_arguments[index]="$(_windows_path \
            "${extra_arguments[index]}")"
          ;;
      esac
    done
  fi
  _run_closed_process 45 "${controlled_root}" \
    "${environment[@]}" APR_R3_LIVE_COMMAND \
    "${runner[@]}" "${verb}" \
    --root "${root_argument}" \
    --corpus "${corpus}" \
    --output "${output_argument}" \
    --execution-kind "${EXECUTION_KIND}" \
    --execution-artifact "${execution_artifact}" \
    --build-pair-manifest "${build_pair_manifest}" \
    "${extra_arguments[@]}" >"${log}" 2>&1
}

_extract_hash() {
  local field="$1"
  local path="$2"
  grep -o "\"${field}\":\"[0-9a-f]\{64\}\"" "${path}" |
    head -n 1 |
    cut -d '"' -f 4
}

_assert_phase_marker() {
  local log="$1"
  local marker="$2"
  tr -d '\r' <"${log}" | grep -Fxq "${marker}"
}

_tamper_lineage() {
  local path="$1"
  if command -v node >/dev/null 2>&1; then
    node -e '
      const fs = require("fs");
      const path = process.argv[1];
      const value = JSON.parse(fs.readFileSync(path, "utf8"));
      value.invocation_identity_sha256 = "f".repeat(64);
      fs.writeFileSync(path, JSON.stringify(value));
    ' "${path}"
  elif command -v python3 >/dev/null 2>&1; then
    python3 -c '
import json, sys
path = sys.argv[1]
with open(path, "r", encoding="utf-8") as stream:
    value = json.load(stream)
value["invocation_identity_sha256"] = "f" * 64
with open(path, "w", encoding="utf-8", newline="") as stream:
    json.dump(value, stream, separators=(",", ":"))
' "${path}"
  else
    _fail APR_R3_LIVE_JSON_TAMPER_TOOL_MISSING
  fi
}

_run_negative_case() {
  local root="$1"
  local case_id="$2"
  local verb="$3"
  local requires_prior="$4"
  local tamper_lineage="$5"
  local phase_root="${root}/negative-runs/${case_id}"
  local receipt="${root}/receipts/negative/${case_id}.json"
  local private_receipt="${phase_root}/private/negative-receipt.json"
  local log="${root}/negative-${case_id}.log"
  mkdir -p -- "${phase_root}" "${root}/receipts/negative"
  local -a extra_arguments=()
  if [[ "${requires_prior}" == yes ]]; then
    local seed_output="${phase_root}/private/seed-receipt.json"
    local seed_log="${root}/negative-${case_id}-seed.log"
    _run_fixture "${root}" continuation-seed "${phase_root}" \
      "${seed_output}" "${seed_log}" ||
      _fail "APR_R3_LIVE_NEGATIVE_SEED_FAILED ${case_id}"
    _assert_phase_marker \
      "${seed_log}" "APR_R3_LIVE_PHASE_OK ContinuationSeed" ||
      _fail "APR_R3_LIVE_NEGATIVE_SEED_MARKER_INVALID ${case_id}"
    local expected_lineage expected_history
    expected_lineage="$(_extract_hash lineage_sha256 "${seed_output}")"
    expected_history="$(_extract_hash \
      historical_messages_sha256 "${seed_output}")"
    [[ -n "${expected_lineage}" && -n "${expected_history}" ]] ||
      _fail "APR_R3_LIVE_NEGATIVE_SEED_RECEIPT_INVALID ${case_id}"
    mkdir -p -- "${root}/receipts/negative-seeds"
    mv -- "${seed_output}" \
      "${root}/receipts/negative-seeds/${case_id}.json"
    extra_arguments=(
      --expected-lineage-sha256 "${expected_lineage}"
      --expected-history-sha256 "${expected_history}"
    )
    if [[ "${tamper_lineage}" == yes ]]; then
      _tamper_lineage "${phase_root}/host/accepted-lineage.json"
    fi
  fi

  if ! _run_fixture "${root}" "${verb}" "${phase_root}" \
      "${private_receipt}" "${log}" "${extra_arguments[@]}"; then
    tail -n 20 "${log}" >&2
    [[ ! -f "${private_receipt}" ]] ||
      sed -n '1p' "${private_receipt}" >&2
    _fail "APR_R3_LIVE_NEGATIVE_EXECUTION_FAILED ${case_id}"
  fi
  _assert_phase_marker \
    "${log}" "APR_R3_LIVE_NEGATIVE_OK ${case_id}" ||
    _fail "APR_R3_LIVE_NEGATIVE_MARKER_INVALID ${case_id}"
  mv -- "${private_receipt}" "${receipt}"
}

_run_once() {
  local root="$1"
  mkdir -p -- "${root}/receipts"

  local verb directory receipt marker phase
  local -a phases=(
    'must-find|must-find|must-find|MustFind'
    'must-not-find|must-not-find|must-not-find|MustNotFind'
    'continuation-seed|continuation|seed|ContinuationSeed'
    'continuation-restore|continuation|restore|ContinuationRestore'
  )
  for phase in "${phases[@]}"; do
    IFS='|' read -r verb directory receipt marker <<<"${phase}"
    mkdir -p -- "${root}/${directory}"
    local private_output="${root}/${directory}/private/receipt.json"
    local log="${root}/${receipt}.log"
    local -a extra_arguments=()
    if [[ "${verb}" == continuation-restore ]]; then
      local expected_lineage expected_history
      expected_lineage="$(_extract_hash \
        lineage_sha256 "${root}/receipts/seed.json")"
      expected_history="$(_extract_hash \
        historical_messages_sha256 "${root}/receipts/seed.json")"
      [[ -n "${expected_lineage}" ]] ||
        _fail APR_R3_LIVE_SEED_LINEAGE_MISSING
      [[ -n "${expected_history}" ]] ||
        _fail APR_R3_LIVE_SEED_HISTORY_MISSING
      extra_arguments=(
        --expected-lineage-sha256 "${expected_lineage}"
        --expected-history-sha256 "${expected_history}"
      )
    fi
    if ! _run_fixture "${root}" "${verb}" "${root}/${directory}" \
        "${private_output}" "${log}" "${extra_arguments[@]}"; then
      tail -n 20 "${log}" >&2
      if [[ -f "${root}/${directory}/private/failure.code" ]]; then
        sed -n '1p' "${root}/${directory}/private/failure.code" >&2
      fi
      if [[ -f "${private_output}" ]]; then
        sed -n '1p' "${private_output}" >&2
      fi
      _fail "APR_R3_LIVE_PHASE_EXECUTION_FAILED ${verb}"
    fi
    tr -d '\r' <"${log}" |
      grep -Fxq "APR_R3_LIVE_PHASE_OK ${marker}" ||
      _fail "APR_R3_LIVE_PHASE_MARKER_INVALID ${verb}"
    mv -- "${private_output}" "${root}/receipts/${receipt}.json"
  done

  mkdir -p -- "${root}/canary"
  local canary_output="${root}/canary/private/receipt.json"
  local canary_log="${root}/canary.log"
  _run_fixture "${root}" canary-routing "${root}/canary" \
    "${canary_output}" "${canary_log}" \
    --canary-manifest "${CANARY_ROUTES}" ||
    _fail APR_R3_LIVE_CANARY_EXECUTION_FAILED
  _assert_phase_marker \
    "${canary_log}" "APR_R3_LIVE_PHASE_OK CanaryRouting" ||
    _fail APR_R3_LIVE_CANARY_MARKER_INVALID
  mv -- "${canary_output}" "${root}/receipts/canary.json"

  mkdir -p -- "${root}/architecture/private"
  local architecture_output="${root}/architecture/private/receipt.json"
  local architecture_log="${root}/architecture.log"
  _run_fixture "${root}" architecture "${root}/architecture" \
    "${architecture_output}" "${architecture_log}" \
    --architecture-assembly "${ARCHITECTURE_ASSEMBLY}" ||
    _fail APR_R3_LIVE_ARCHITECTURE_EXECUTION_FAILED
  _assert_phase_marker \
    "${architecture_log}" APR_R3_LIVE_ARCHITECTURE_OK ||
    _fail APR_R3_LIVE_ARCHITECTURE_MARKER_INVALID
  mv -- "${architecture_output}" "${root}/receipts/architecture.json"

  local negative
  local -a negatives=(
    'outer-authorization-denied|negative-outer-authorization-denied|no|no'
    'inner-authorization-denied|negative-inner-authorization-denied|no|no'
    'provider-http-failure|negative-provider-http-failure|no|no'
    'provider-malformed-response|negative-provider-malformed-response|no|no'
    'tool-arguments-invalid|negative-tool-arguments-invalid|no|no'
    'terminal-ungrounded|negative-terminal-ungrounded|no|no'
    'transition-from-head-invalid|negative-transition-from-head-invalid|yes|no'
    'lineage-authority-tampered|negative-lineage-authority-tampered|yes|yes'
    'quality-failed-after-commit|negative-quality-failed-after-commit|no|no'
    'public-result-canary|negative-public-result-canary|no|no'
  )
  for negative in "${negatives[@]}"; do
    local case_id negative_verb requires_prior tamper_lineage
    IFS='|' read -r case_id negative_verb requires_prior tamper_lineage \
      <<<"${negative}"
    _run_negative_case "${root}" "${case_id}" "${negative_verb}" \
      "${requires_prior}" "${tamper_lineage}"
  done

  local replacement_root="${root}/replacement-run"
  local replacement_seed_receipt=
  replacement_seed_receipt="${replacement_root}/private/seed-receipt.json"
  local replacement_seed_log="${root}/negative-replacement-seed.log"
  mkdir -p -- "${replacement_root}"
  if ! _run_fixture "${root}" continuation-seed "${replacement_root}" \
      "${replacement_seed_receipt}" "${replacement_seed_log}"; then
    tail -n 20 "${replacement_seed_log}" >&2
    _fail APR_R3_LIVE_REPLACEMENT_SEED_EXECUTION_FAILED
  fi
  _assert_phase_marker "${replacement_seed_log}" \
    "APR_R3_LIVE_PHASE_OK ContinuationSeed" ||
    _fail APR_R3_LIVE_REPLACEMENT_SEED_MARKER_INVALID
  local replacement_lineage replacement_history
  replacement_lineage="$(_extract_hash \
    lineage_sha256 "${replacement_seed_receipt}")"
  replacement_history="$(_extract_hash \
    historical_messages_sha256 "${replacement_seed_receipt}")"
  [[ -n "${replacement_lineage}" ]] ||
    _fail APR_R3_LIVE_REPLACEMENT_SEED_LINEAGE_MISSING
  [[ -n "${replacement_history}" ]] ||
    _fail APR_R3_LIVE_REPLACEMENT_SEED_HISTORY_MISSING
  rm -f -- "${replacement_seed_receipt}"

  local prior_replacement="${root}/prior-replacement.json"
  local replacement_receipt=
  replacement_receipt="${root}/private/replacement-write-receipt.json"
  local replacement_log="${root}/negative-replacement-write-failed.log"
  printf '%s\n' prior-accepted-evidence >"${prior_replacement}"
  if ! _run_fixture "${root}" negative-replacement-write-failed "${root}" \
      "${replacement_receipt}" "${replacement_log}" \
      --negative-manifest "${NEGATIVE_CASES}" \
      --canary-manifest "${CANARY_ROUTES}" \
      --expected-lineage-sha256 "${replacement_lineage}" \
      --expected-history-sha256 "${replacement_history}" \
      --replacement-target "${prior_replacement}"; then
    tail -n 20 "${replacement_log}" >&2
    [[ ! -f "${root}/private/failure.code" ]] ||
      sed -n '1p' "${root}/private/failure.code" >&2
    [[ ! -f "${replacement_receipt}" ]] ||
      sed -n '1p' "${replacement_receipt}" >&2
    _fail APR_R3_LIVE_REPLACEMENT_NEGATIVE_EXECUTION_FAILED
  fi
  _assert_phase_marker "${replacement_log}" \
    "APR_R3_LIVE_NEGATIVE_OK replacement-write-failed" ||
    _fail APR_R3_LIVE_REPLACEMENT_NEGATIVE_MARKER_INVALID
  grep -Fxq prior-accepted-evidence "${prior_replacement}" ||
    _fail APR_R3_LIVE_REPLACEMENT_PRIOR_CHANGED
  mv -- "${replacement_receipt}" \
    "${root}/receipts/negative/replacement-write-failed.json"

  if ! _run_fixture "${root}" aggregate "${root}" \
      "${root}/replacement.json" "${root}/aggregate.log" \
      --negative-manifest "${NEGATIVE_CASES}" \
      --canary-manifest "${CANARY_ROUTES}"; then
    tail -n 20 "${root}/aggregate.log" >&2
    if [[ -f "${root}/private/failure.code" ]]; then
      sed -n '1p' "${root}/private/failure.code" >&2
    fi
    _fail APR_R3_LIVE_AGGREGATE_FAILED
  fi
  tr -d '\r' <"${root}/aggregate.log" |
    grep -Fxq APR_R3_LIVE_DETERMINISTIC_OK ||
    _fail APR_R3_LIVE_AGGREGATE_MARKER_INVALID
  if ! cmp -s -- "${root}/replacement.json" "${GOLDEN}"; then
    sed -n '1p' "${root}/replacement.json" >&2
    _fail APR_R3_LIVE_GOLDEN_MISMATCH
  fi

  if grep -Raq \
      -e "${PROVIDER_CANARY}" \
      -e APR111_STATE_KEY_CANARY_12345678 \
      -e "${STATE_KEY_B64}" \
      -e "${GITHUB_CANARY}" \
      -e "${ACTIONS_CANARY}" \
      -e "${WORKFLOW_CANARY}" \
      -e "${PUBLIC_RESULT_CANARY}" \
      "${root}"; then
    _fail APR_R3_LIVE_CANARY_PUBLISHED
  fi
  local routed_path
  while IFS= read -r routed_path; do
    case "${routed_path}" in
      "${root}/canary/input/reviewed-input.json"|"${root}/canary/input/snapshot-manifest.json") ;;
      *) _fail "APR_R3_LIVE_CANARY_ROUTE_PUBLISHED ${routed_path}" ;;
    esac
  done < <(grep -RIl \
    -e "${REPOSITORY_CANARY}" \
    -e "${PATH_CANARY}" \
    -e "${PROMPT_CANARY}" \
    "${root}" || true)
}

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

_prepare_common() {
  _require_file project "${PROJECT}"
  _require_file test-project "${TEST_PROJECT}"
  _require_file corpus "${CORPUS}"
  _require_file golden "${GOLDEN}"
  _require_file negative-cases "${NEGATIVE_CASES}"
  _require_file canary-routes "${CANARY_ROUTES}"
  _validate_manifest

}

_hash_file() {
  sha256sum "$1" | cut -d ' ' -f 1
}

_write_build_pair() {
  local build_root="$1"
  local execution_sha architecture_sha pair_sha
  execution_sha="$(_hash_file "${EXECUTION_ARTIFACT}")"
  architecture_sha="$(_hash_file "${ARCHITECTURE_ASSEMBLY}")"
  pair_sha="$({
    printf '%s\n' \
      apr-r3-live-agent-build-pair-v1 \
      "${EXECUTION_KIND}" \
      "${execution_sha}" \
      "${architecture_sha}"
  } | sha256sum | cut -d ' ' -f 1)"
  BUILD_PAIR_MANIFEST="${build_root}/build-pair.json"
  printf '{"kind":"apr-r3-live-agent-build-pair-v1","execution_kind":"%s","execution_artifact_sha256":"%s","architecture_assembly_sha256":"%s","build_pair_sha256":"%s"}\n' \
    "${EXECUTION_KIND}" \
    "${execution_sha}" \
    "${architecture_sha}" \
    "${pair_sha}" >"${BUILD_PAIR_MANIFEST}"
}

_run_repeated_proof() {
  local success_marker="$1"
  local build_root="$2"
  local test_project="${TEST_PROJECT}"
  if _windows_interop; then
    test_project="$(_windows_path "${TEST_PROJECT}")"
  fi

  local first second
  _new_tempdir first
  _new_tempdir second
  _run_once "${first}"
  _run_once "${second}"
  cmp -s -- "${first}/replacement.json" "${second}/replacement.json" ||
    _fail APR_R3_LIVE_PUBLIC_RECORD_UNSTABLE

  local first_fact second_fact first_request second_request
  local first_terminal second_terminal
  first_fact="$(_extract_hash prior_fact_sha256 "${first}/receipts/seed.json")"
  second_fact="$(_extract_hash prior_fact_sha256 "${second}/receipts/seed.json")"
  first_request="$(_extract_hash first_request_sha256 "${first}/receipts/restore.json")"
  second_request="$(_extract_hash first_request_sha256 "${second}/receipts/restore.json")"
  first_terminal="$(_extract_hash terminal_sha256 "${first}/receipts/restore.json")"
  second_terminal="$(_extract_hash terminal_sha256 "${second}/receipts/restore.json")"
  [[ -n "${first_fact}" && -n "${second_fact}" &&
     "${first_fact}" != "${second_fact}" &&
     -n "${first_request}" && -n "${second_request}" &&
     "${first_request}" != "${second_request}" &&
     -n "${first_terminal}" && -n "${second_terminal}" &&
     "${first_terminal}" != "${second_terminal}" ]] ||
    _fail APR_R3_LIVE_PRIVATE_FRESHNESS_FAILED

  "${DOTNET_CMD}" test "${test_project}" --configuration Release --nologo \
    --filter 'FullyQualifiedName~LiveAgentVerifier' \
    >"${build_root}/focused-tests.log" 2>&1 ||
    _fail APR_R3_LIVE_FOCUSED_TESTS_FAILED
  _cleanup || _fail APR_R3_LIVE_CLEANUP_FAILED
  printf '%s\n' "${success_marker}"
}

run_deterministic() {
  _prepare_common
  local build_root
  _new_tempdir build_root
  local build_project="${PROJECT}"
  local build_output="${build_root}/build"
  if _windows_interop; then
    build_project="$(_windows_path "${PROJECT}")"
    build_output="$(_windows_path "${build_root}/build")"
  fi
  if ! "${DOTNET_CMD}" build "${build_project}" \
      --configuration Release --nologo -o "${build_output}" \
      -p:PublishAot=false \
      >"${build_root}/build.log" 2>&1; then
    tail -n 40 "${build_root}/build.log" >&2
    _fail APR_R3_LIVE_BUILD_FAILED
  fi
  RUNNER_DLL="${build_root}/build/${ASSEMBLY}.dll"
  DOTNET_EXE="$(readlink -f "$(command -v "${DOTNET_CMD}")")"
  RUNNER=("${DOTNET_EXE}" "${RUNNER_DLL}")
  EXECUTION_KIND=framework
  EXECUTION_ARTIFACT="${RUNNER_DLL}"
  ARCHITECTURE_ASSEMBLY="${RUNNER_DLL}"
  _write_build_pair "${build_root}"
  _run_repeated_proof APR_R3_LIVE_DETERMINISTIC_OK "${build_root}"
}

_audit_aot_warnings() {
  local log="$1"
  local expected
  expected="CSC : warning IL3058: Referenced assembly 'JsonSchema.Net' is not built with "'`true`'" and may not be compatible with AOT."
  local -a warnings=()
  mapfile -t warnings < <(tr -d '\r' <"${log}" |
    grep -E '(^|: )warning [A-Z]+[0-9]+:' || true)
  [[ "${#warnings[@]}" -eq 2 ]] ||
    _fail APR_R3_LIVE_AOT_WARNING_ALLOWLIST_MISSING
  local warning runtime_count=0 fixture_count=0
  for warning in "${warnings[@]}"; do
    [[ "${warning}" == *"${expected}"* ]] ||
      _fail "APR_R3_LIVE_AOT_WARNING_UNEXPECTED ${warning}"
    case "${warning}" in
      *AgenticPrReview.Runtime.LiveAgentVerifierFixture.csproj*)
        ((fixture_count += 1))
        ;;
      *AgenticPrReview.Runtime.csproj*)
        ((runtime_count += 1))
        ;;
      *) _fail "APR_R3_LIVE_AOT_WARNING_ORIGIN_INVALID ${warning}" ;;
    esac
  done
  [[ "${runtime_count}" -eq 1 && "${fixture_count}" -eq 1 ]] ||
    _fail APR_R3_LIVE_AOT_WARNING_ORIGIN_INVALID
  printf 'APR_R3_LIVE_AOT_WARNING_ALLOWLIST count=%s\n' "${#warnings[@]}"
}

run_aot() {
  _prepare_common
  [[ "$(uname -s)" == Linux ]] ||
    _fail APR_R3_LIVE_AOT_REQUIRES_LINUX
  _windows_interop && _fail APR_R3_LIVE_AOT_REQUIRES_NATIVE_DOTNET

  local build_root
  _new_tempdir build_root
  local publish_output="${build_root}/publish"
  local intermediate_output="${build_root}/aot-intermediate"
  if ! "${DOTNET_CMD}" publish "${PROJECT}" \
      --configuration Release --nologo \
      --runtime linux-x64 --self-contained true \
      --artifacts-path "${build_root}/artifacts" \
      -o "${publish_output}" \
      -p:PublishAot=true \
      -p:JsonSerializerIsReflectionEnabledByDefault=false \
      -p:TreatWarningsAsErrors=true \
      -p:WarningsNotAsErrors=IL3058 \
      -p:LiveAgentVerifierAotIntermediateDirectory="${intermediate_output}" \
      >"${build_root}/publish.log" 2>&1; then
    tail -n 80 "${build_root}/publish.log" >&2
    _fail APR_R3_LIVE_AOT_PUBLISH_FAILED
  fi
  _audit_aot_warnings "${build_root}/publish.log"

  EXECUTION_KIND=native-aot
  EXECUTION_ARTIFACT="${publish_output}/${ASSEMBLY}"
  ARCHITECTURE_ASSEMBLY="${intermediate_output}/${ASSEMBLY}.dll"
  _require_file aot-executable "${EXECUTION_ARTIFACT}"
  [[ -x "${EXECUTION_ARTIFACT}" ]] ||
    _fail APR_R3_LIVE_AOT_EXECUTABLE_INVALID
  _require_file aot-intermediate "${ARCHITECTURE_ASSEMBLY}"
  RUNNER=("${EXECUTION_ARTIFACT}")
  _write_build_pair "${build_root}"
  _run_repeated_proof APR_R3_LIVE_AOT_OK "${build_root}"
}

subcommand="${1:-}"
case "${subcommand}" in
  deterministic) run_deterministic ;;
  aot) run_aot ;;
  -h|--help|help)
    printf '%s\n' 'Usage: verify-live-agent.sh [deterministic|aot]'
    ;;
  *)
    printf 'APR_R3_LIVE_COMMAND_INVALID %s\n' "${subcommand:-missing}" >&2
    exit 2
    ;;
esac
