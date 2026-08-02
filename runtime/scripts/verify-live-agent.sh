#!/usr/bin/env bash
# Secret-free deterministic R3 live-agent replacement proof. Every product
# phase runs in a fresh process. Raw requests, SESSION/STATE, credentials,
# random prior facts, and logs remain in temporary roots and are deleted.

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
    printf '%s\0' \
      "AGENTIC_REVIEW_DEEPSEEK_API_KEY=${PROVIDER_CANARY}" \
      "AGENTIC_REVIEW_R3_STATE_KEY_B64=${STATE_KEY_B64}" \
      "GITHUB_TOKEN=${GITHUB_CANARY}" \
      "ACTIONS_RUNTIME_TOKEN=${ACTIONS_CANARY}" \
      "APR111_UNRELATED_WORKFLOW=${WORKFLOW_CANARY}" \
      "APR111_REPOSITORY=${REPOSITORY_CANARY}" \
      "APR111_PATH=${PATH_CANARY}" \
      "APR111_PROMPT=${PROMPT_CANARY}" \
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
  local -a environment
  mapfile -d '' -t environment < <(_closed_environment "${controlled_root}")
  local corpus="${CORPUS}"
  local root_argument="${phase_root}"
  local output_argument="${output}"
  local runner="${RUNNER_DLL}"
  if _windows_interop; then
    corpus="$(_windows_path "${corpus}")"
    root_argument="$(_windows_path "${phase_root}")"
    output_argument="$(_windows_path "${output}")"
    runner="$(_windows_path "${RUNNER_DLL}")"
  fi
  _run_closed_process 45 "${controlled_root}" \
    "${environment[@]}" APR_R3_LIVE_COMMAND \
    "${DOTNET_EXE}" "${runner}" "${verb}" \
    --root "${root_argument}" \
    --corpus "${corpus}" \
    --output "${output_argument}" >"${log}" 2>&1
}

_extract_hash() {
  local field="$1"
  local path="$2"
  grep -o "\"${field}\":\"[0-9a-f]\{64\}\"" "${path}" |
    head -n 1 |
    cut -d '"' -f 4
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
    if ! _run_fixture "${root}" "${verb}" "${root}/${directory}" \
        "${private_output}" "${log}"; then
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

  if ! _run_fixture "${root}" aggregate "${root}" \
      "${root}/replacement.json" "${root}/aggregate.log"; then
    tail -n 20 "${root}/aggregate.log" >&2
    if [[ -f "${root}/private/failure.code" ]]; then
      sed -n '1p' "${root}/private/failure.code" >&2
    fi
    _fail APR_R3_LIVE_AGGREGATE_FAILED
  fi
  tr -d '\r' <"${root}/aggregate.log" |
    grep -Fxq APR_R3_LIVE_DETERMINISTIC_OK ||
    _fail APR_R3_LIVE_AGGREGATE_MARKER_INVALID
  cmp -s -- "${root}/replacement.json" "${GOLDEN}" ||
    _fail APR_R3_LIVE_GOLDEN_MISMATCH

  if grep -Raq \
      -e "${PROVIDER_CANARY}" \
      -e APR111_STATE_KEY_CANARY_12345678 \
      -e "${STATE_KEY_B64}" \
      -e "${GITHUB_CANARY}" \
      -e "${ACTIONS_CANARY}" \
      -e "${WORKFLOW_CANARY}" \
      -e "${REPOSITORY_CANARY}" \
      -e "${PATH_CANARY}" \
      -e "${PROMPT_CANARY}" \
      "${root}"; then
    _fail APR_R3_LIVE_CANARY_PUBLISHED
  fi
}

_validate_manifest() {
  local header
  IFS= read -r header <"${NEGATIVE_CASES}"
  [[ "${header}" == $'case\tphase\tstate_expectation\tstable_code' ]] ||
    _fail APR_R3_LIVE_NEGATIVE_MANIFEST_INVALID
  [[ -z "$(cut -f1 "${NEGATIVE_CASES}" | tail -n +2 | sort | uniq -d)" ]] ||
    _fail APR_R3_LIVE_NEGATIVE_MANIFEST_DUPLICATE
}

run_deterministic() {
  _require_file project "${PROJECT}"
  _require_file test-project "${TEST_PROJECT}"
  _require_file corpus "${CORPUS}"
  _require_file golden "${GOLDEN}"
  _require_file negative-cases "${NEGATIVE_CASES}"
  _validate_manifest

  local build_root
  _new_tempdir build_root
  local build_project="${PROJECT}"
  local build_output="${build_root}/build"
  local test_project="${TEST_PROJECT}"
  if _windows_interop; then
    build_project="$(_windows_path "${PROJECT}")"
    build_output="$(_windows_path "${build_root}/build")"
    test_project="$(_windows_path "${TEST_PROJECT}")"
  fi
  if ! "${DOTNET_CMD}" build "${build_project}" \
      --configuration Release --nologo -o "${build_output}" \
      >"${build_root}/build.log" 2>&1; then
    tail -n 40 "${build_root}/build.log" >&2
    _fail APR_R3_LIVE_BUILD_FAILED
  fi
  RUNNER_DLL="${build_root}/build/${ASSEMBLY}.dll"
  DOTNET_EXE="$(readlink -f "$(command -v "${DOTNET_CMD}")")"

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
  printf '%s\n' APR_R3_LIVE_DETERMINISTIC_OK
}

subcommand="${1:-}"
case "${subcommand}" in
  deterministic) run_deterministic ;;
  -h|--help|help)
    printf '%s\n' 'Usage: verify-live-agent.sh deterministic'
    ;;
  *)
    printf 'APR_R3_LIVE_COMMAND_INVALID %s\n' "${subcommand:-missing}" >&2
    exit 2
    ;;
esac
