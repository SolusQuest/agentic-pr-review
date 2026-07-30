#!/usr/bin/env bash
# Deterministic R2 Agent-loop proof for framework-dependent .NET and linux-x64
# Native AOT. Every proof phase executes in a fresh process with an explicit
# closed environment. Logs and restricted artifacts remain private to temporary
# directories; only stable outcome codes are printed.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/runtime/tests/AgentLoopAotFixture/AgenticPrReview.Runtime.AgentLoopAotFixture.csproj"
TEST_PROJECT="${REPO_ROOT}/runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
FIXTURES="${REPO_ROOT}/runtime/tests/fixtures/agent/agent-loop"
REPOSITORY_FIXTURE="${FIXTURES}/repository"
EXPECTED_BOOTSTRAP="${FIXTURES}/expected/bootstrap.json.golden"
EXPECTED_CONTINUE="${FIXTURES}/expected/continue.json.golden"
NEGATIVE_CASES="${FIXTURES}/negative-cases.txt"
LIMIT_CASES="${FIXTURES}/limit-cases.tsv"
FRAMEWORK_ENVIRONMENT="${FIXTURES}/environment/framework.txt"
FRAMEWORK_WINDOWS_ENVIRONMENT="${FIXTURES}/environment/framework-windows.txt"
AOT_ENVIRONMENT="${FIXTURES}/environment/aot.txt"
ASSEMBLY="AgenticPrReview.Runtime.AgentLoopAotFixture"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"

_tempdirs=()

_cleanup() {
  if [[ ${#_tempdirs[@]} -eq 0 ]]; then
    return 0
  fi
  local directory
  for directory in "${_tempdirs[@]}"; do
    if [[ -n "${directory:-}" && -d "${directory}" ]]; then
      rm -rf -- "${directory}" || true
    fi
  done
}
trap _cleanup EXIT

_new_tempdir() {
  local __outvar="$1"
  local __directory
  __directory="$(mktemp -d)"
  _tempdirs+=("${__directory}")
  printf -v "${__outvar}" '%s' "${__directory}"
}

_fail() {
  printf '%s\n' "$1" >&2
  return 1
}

_require_file() {
  local label="$1"
  local path="$2"
  [[ -f "${path}" ]] || _fail "APR_AGENT_FIXTURE_MISSING ${label}"
}

_require_inputs() {
  _require_file project "${PROJECT}"
  _require_file test-project "${TEST_PROJECT}"
  _require_file repository-fixture "${REPOSITORY_FIXTURE}/reviewed/fact.txt"
  _require_file expected-bootstrap "${EXPECTED_BOOTSTRAP}"
  _require_file expected-continue "${EXPECTED_CONTINUE}"
  _require_file negative-cases "${NEGATIVE_CASES}"
  _require_file limit-cases "${LIMIT_CASES}"
  _require_file framework-environment "${FRAMEWORK_ENVIRONMENT}"
  _require_file framework-windows-environment "${FRAMEWORK_WINDOWS_ENVIRONMENT}"
  _require_file aot-environment "${AOT_ENVIRONMENT}"
}

_copy_repository() {
  local root="$1"
  mkdir -p -- "${root}/repository"
  cp -R -- "${REPOSITORY_FIXTURE}/." "${root}/repository/"
}

_closed_environment() {
  local mode="$1"
  local root="$2"
  mkdir -p -- "${root}/home" "${root}/tmp" "${root}/dotnet-home"
  if [[ "${mode}" == "framework" && "$(uname -s)" == MINGW* ]]; then
    local windows_root
    windows_root="$(cygpath -w "${root}")"
    local windows_dotnet_root
    windows_dotnet_root="$(cygpath -w "$(dirname "$(readlink -f "$(command -v "${DOTNET_CMD}")")")")"
    printf '%s\0' \
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
  elif [[ "${mode}" == "framework" ]]; then
    local dotnet_path
    dotnet_path="$(readlink -f "$(command -v "${DOTNET_CMD}")")"
    printf '%s\0' \
      "DOTNET_CLI_HOME=${root}/dotnet-home" \
      "DOTNET_ROOT=$(dirname "${dotnet_path}")" \
      "HOME=${root}/home" \
      "LANG=C.UTF-8" \
      "LC_ALL=C.UTF-8" \
      "TMPDIR=${root}/tmp" \
      "TZ=UTC"
  else
    printf '%s\0' \
      "HOME=${root}/home" \
      "LANG=C.UTF-8" \
      "LC_ALL=C.UTF-8" \
      "TMPDIR=${root}/tmp" \
      "TZ=UTC"
  fi
}

_environment_manifest() {
  local mode="$1"
  if [[ "${mode}" == "framework" && "$(uname -s)" == MINGW* ]]; then
    printf '%s' "${FRAMEWORK_WINDOWS_ENVIRONMENT}"
  elif [[ "${mode}" == "framework" ]]; then
    printf '%s' "${FRAMEWORK_ENVIRONMENT}"
  else
    printf '%s' "${AOT_ENVIRONMENT}"
  fi
}

_run_closed_process() {
  local timeout_seconds="$1"
  shift
  local -a environment=()
  while [[ "$1" != "APR_AGENT_COMMAND" ]]; do
    environment+=("$1")
    shift
  done
  shift
  if [[ "$(uname -s)" == MINGW* ]]; then
    node -e '
      const childProcess = require("child_process");
      const marker = process.argv.indexOf("APR_AGENT_COMMAND");
      if (marker < 0 || marker + 1 >= process.argv.length) process.exit(125);
      const environment = {};
      for (const entry of process.argv.slice(2, marker)) {
        const separator = entry.indexOf("=");
        if (separator < 1) process.exit(125);
        environment[entry.slice(0, separator)] = entry.slice(separator + 1);
      }
      const result = childProcess.spawnSync(
        process.argv[marker + 1],
        process.argv.slice(marker + 2),
        {
          env: environment,
          stdio: "inherit",
          timeout: Number(process.argv[1]) * 1000,
          windowsHide: true,
        });
      process.exit(result.status === null ? 124 : result.status);
    ' "${timeout_seconds}" "${environment[@]}" APR_AGENT_COMMAND "$@"
  else
    timeout "${timeout_seconds}s" env -i "${environment[@]}" "$@"
  fi
}

_run_fixture() {
  local mode="$1"
  local root="$2"
  local log="$3"
  shift 3
  local manifest
  manifest="$(_environment_manifest "${mode}")"
  local -a environment
  mapfile -d '' -t environment < <(_closed_environment "${mode}" "${root}")
  local -a arguments=(
    "$@"
    --mode "${mode}"
    --root "${root}"
    --environment-manifest "${manifest}"
  )
  if [[ "${mode}" == "framework" && "$(uname -s)" == MINGW* ]]; then
    local index
    for index in "${!arguments[@]}"; do
      if [[ "${arguments[index]}" == /* ]]; then
        arguments[index]="$(cygpath -w "${arguments[index]}")"
      fi
    done
  fi
  if ! _run_closed_process 45 "${environment[@]}" APR_AGENT_COMMAND \
      "${RUNNER[@]}" "${arguments[@]}" >"${log}" 2>&1; then
    return 1
  fi
}

_run_positive() {
  local mode="$1"
  local mode_root="$2"
  local root="${mode_root}/positive"
  mkdir -p -- "${root}"
  _copy_repository "${root}"
  if ! _run_fixture "${mode}" "${root}" "${root}/bootstrap.log" \
      bootstrap --output "${root}/bootstrap.json"; then
    _fail "APR_AGENT_BOOTSTRAP_FAILED ${mode}"
  fi
  if ! _run_fixture "${mode}" "${root}" "${root}/continue.log" \
      continue --output "${root}/continue.json"; then
    _fail "APR_AGENT_CONTINUE_FAILED ${mode}"
  fi
  cmp -s -- "${root}/bootstrap.json" "${EXPECTED_BOOTSTRAP}" ||
    _fail "APR_AGENT_BOOTSTRAP_GOLDEN_MISMATCH ${mode}"
  cmp -s -- "${root}/continue.json" "${EXPECTED_CONTINUE}" ||
    _fail "APR_AGENT_CONTINUE_GOLDEN_MISMATCH ${mode}"
  cmp -s -- "${root}/bootstrap.startup" "${root}/continue.startup" &&
    _fail "APR_AGENT_PROCESS_REUSE ${mode}"
  grep -Fxq APR_AGENT_BOOTSTRAP_OK "${root}/bootstrap.log" ||
    _fail "APR_AGENT_BOOTSTRAP_REPORT_INVALID ${mode}"
  grep -Fxq APR_AGENT_CONTINUE_OK "${root}/continue.log" ||
    _fail "APR_AGENT_CONTINUE_REPORT_INVALID ${mode}"
  printf 'APR_AGENT_POSITIVE_OK %s\n' "${mode}"
}

_requires_seed_state() {
  case "$1" in
    state-*|head-*) return 0 ;;
    *) return 1 ;;
  esac
}

_run_negatives() {
  local mode="$1"
  local mode_root="$2"
  local scenario
  while IFS= read -r scenario; do
    [[ -n "${scenario}" ]] || continue
    local root="${mode_root}/negative/${scenario}"
    mkdir -p -- "${root}"
    _copy_repository "${root}"
    local lineage_before="absent"
    if _requires_seed_state "${scenario}"; then
      if ! _run_fixture "${mode}" "${root}" "${root}/seed.log" \
          bootstrap --output "${root}/seed.json"; then
        _fail "APR_AGENT_NEGATIVE_SEED_FAILED ${mode} ${scenario}"
      fi
      lineage_before="$(sha256sum "${root}/host-lineage.json" | awk '{print $1}')"
    fi
    if ! _run_fixture "${mode}" "${root}" "${root}/negative.log" \
        negative --case "${scenario}" --output "${root}/negative.txt"; then
      _fail "APR_AGENT_NEGATIVE_FAILED ${mode} ${scenario}"
    fi
    grep -Fxq "APR_AGENT_NEGATIVE_OK ${scenario}" "${root}/negative.log" ||
      _fail "APR_AGENT_NEGATIVE_REPORT_INVALID ${mode} ${scenario}"
    cmp -s -- "${root}/negative.log" "${root}/negative.txt" ||
      _fail "APR_AGENT_NEGATIVE_OUTPUT_MISMATCH ${mode} ${scenario}"
    if _requires_seed_state "${scenario}"; then
      local lineage_after
      lineage_after="$(sha256sum "${root}/host-lineage.json" | awk '{print $1}')"
      [[ "${lineage_before}" == "${lineage_after}" ]] ||
        _fail "APR_AGENT_REJECTED_LINEAGE_ADVANCED ${mode} ${scenario}"
    elif [[ -e "${root}/host-lineage.json" ||
            -d "${root}/restricted-state" ]]; then
      _fail "APR_AGENT_REJECTED_STATE_CREATED ${mode} ${scenario}"
    fi
  done <"${NEGATIVE_CASES}"
  printf 'APR_AGENT_NEGATIVES_OK %s\n' "${mode}"
}

_run_environment_negatives() {
  local mode="$1"
  local mode_root="$2"
  local manifest
  manifest="$(_environment_manifest "${mode}")"
  local root="${mode_root}/environment"
  mkdir -p -- "${root}"
  local -a base
  mapfile -d '' -t base < <(_closed_environment "${mode}" "${root}")
  local log="${root}/unallowlisted.log"
  if _run_closed_process 10 "${base[@]}" "APR_UNRELATED=present" \
      APR_AGENT_COMMAND "${RUNNER[@]}" bootstrap --mode "${mode}" --root "${root}" \
      --output "${root}/unallowlisted.json" \
      --environment-manifest "${manifest}" >"${log}" 2>&1; then
    _fail "APR_AGENT_ENVIRONMENT_UNALLOWLISTED_ACCEPTED ${mode}"
  fi
  grep -Fxq APR_AGENT_ENVIRONMENT_INVALID "${log}" ||
    _fail "APR_AGENT_ENVIRONMENT_UNALLOWLISTED_CODE ${mode}"

  local canary_class
  for canary_class in \
    github actions provider state-encryption unrelated-workflow runner \
    package-registry cloud signing; do
    local canary_root="${root}/${canary_class}"
    mkdir -p -- "${canary_root}"
    local -a contaminated
    mapfile -d '' -t contaminated < <(
      _closed_environment "${mode}" "${canary_root}" |
        while IFS= read -r -d '' entry; do
          if [[ "${entry}" == HOME=* ]]; then
            printf '%s\0' "HOME=apr-canary-${canary_class}-environment"
          else
            printf '%s\0' "${entry}"
          fi
        done
    )
    log="${canary_root}/run.log"
    if _run_closed_process 10 "${contaminated[@]}" APR_AGENT_COMMAND \
        "${RUNNER[@]}" bootstrap --mode "${mode}" --root "${canary_root}" \
        --output "${canary_root}/proof.json" \
        --environment-manifest "${manifest}" >"${log}" 2>&1; then
      _fail "APR_AGENT_ENVIRONMENT_CANARY_ACCEPTED ${mode} ${canary_class}"
    fi
    grep -Fxq APR_AGENT_ENVIRONMENT_CANARY "${log}" ||
      _fail "APR_AGENT_ENVIRONMENT_CANARY_CODE ${mode} ${canary_class}"
    [[ ! -e "${canary_root}/host-lineage.json" &&
       ! -d "${canary_root}/restricted-state" ]] ||
      _fail "APR_AGENT_ENVIRONMENT_CANARY_STATE ${mode} ${canary_class}"
  done
  printf 'APR_AGENT_ENVIRONMENT_OK %s\n' "${mode}"
}

_verify_case_manifests() {
  if [[ -n "$(sort "${NEGATIVE_CASES}" | uniq -d)" ]]; then
    _fail APR_AGENT_NEGATIVE_CASE_DUPLICATE
  fi
  local header
  IFS= read -r header <"${LIMIT_CASES}"
  [[ "${header}" == $'outcome\tcase' ]] ||
    _fail APR_AGENT_LIMIT_MANIFEST_INVALID
  local outcome cases scenario
  while IFS=$'\t' read -r outcome cases; do
    [[ -n "${outcome}" && -n "${cases}" ]] ||
      _fail APR_AGENT_LIMIT_MANIFEST_INVALID
    IFS=',' read -ra split_cases <<<"${cases}"
    for scenario in "${split_cases[@]}"; do
      grep -Fxq "${scenario}" "${NEGATIVE_CASES}" ||
        _fail "APR_AGENT_LIMIT_CASE_MISSING ${outcome}"
    done
  done < <(tail -n +2 "${LIMIT_CASES}")
}

_build_framework() {
  local root="$1"
  local log="${root}/build.log"
  if ! "${DOTNET_CMD}" build "${PROJECT}" -c Release --nologo \
      -o "${root}/build" >"${log}" 2>&1; then
    _fail APR_AGENT_FRAMEWORK_BUILD_FAILED
  fi
  RUNNER=(
    "$(readlink -f "$(command -v "${DOTNET_CMD}")")"
    "$(
      if [[ "$(uname -s)" == MINGW* ]]; then
        cygpath -w "${root}/build/${ASSEMBLY}.dll"
      else
        printf '%s' "${root}/build/${ASSEMBLY}.dll"
      fi
    )"
  )
}

_build_aot() {
  local root="$1"
  local log="${root}/publish.log"
  if ! "${DOTNET_CMD}" publish "${PROJECT}" -c Release -r linux-x64 \
      --self-contained true -p:PublishAot=true \
      -p:JsonSerializerIsReflectionEnabledByDefault=false \
      -o "${root}/publish" >"${log}" 2>&1; then
    _fail APR_AGENT_AOT_PUBLISH_FAILED
  fi
  RUNNER=("${root}/publish/${ASSEMBLY}")
  [[ -x "${RUNNER[0]}" ]] || _fail APR_AGENT_AOT_BINARY_MISSING
}

_run_mode() {
  local mode="$1"
  _require_inputs
  _verify_case_manifests
  local mode_root
  _new_tempdir mode_root
  if [[ "${mode}" == "framework" ]]; then
    _build_framework "${mode_root}"
  else
    _build_aot "${mode_root}"
  fi
  _run_positive "${mode}" "${mode_root}"
  _run_negatives "${mode}" "${mode_root}"
  _run_environment_negatives "${mode}" "${mode_root}"
  printf 'APR_AGENT_MODE_OK %s\n' "${mode}"
}

_assert_replacement_inventory() {
  local path
  for path in \
    runtime/tests/AiAbstractionAotFixture \
    runtime/tests/fixtures/agent/ai-abstraction \
    runtime/tools/AiApiManifest \
    runtime/scripts/verify-ai-abstraction.sh; do
    [[ ! -e "${REPO_ROOT}/${path}" ]] ||
      _fail "APR_AGENT_COMPARISON_PATH_REMAINS ${path}"
  done
  grep -Riq "Microsoft.Extensions.AI" \
    "${REPO_ROOT}/runtime/tests/AgenticPrReview.Runtime.Tests" &&
    _fail APR_AGENT_COMPARISON_DEPENDENCY_REMAINS
  [[ ! -e "${REPO_ROOT}/action.yml" &&
     ! -e "${REPO_ROOT}/dist/index.js" ]] ||
    _fail APR_AGENT_PUBLIC_ACTION_PRESENT
  printf 'APR_AGENT_REPLACEMENT_INVENTORY_OK\n'
}

run_test() {
  _require_inputs
  "${DOTNET_CMD}" test "${TEST_PROJECT}" --configuration Release --nologo
}

run_framework() {
  _run_mode framework
}

run_aot() {
  _run_mode aot
}

run_all() {
  run_test
  run_framework
  run_aot
  _assert_replacement_inventory
}

subcommand="${1:-all}"
case "${subcommand}" in
  test) run_test ;;
  framework) run_framework ;;
  aot) run_aot ;;
  all) run_all ;;
  -h|--help|help)
    cat <<'USAGE'
Usage: verify-agent-loop.sh [test|framework|aot|all]

  test        Run the focused runtime test project.
  framework   Run the complete proof under framework-dependent Release .NET.
  aot         Publish and run the complete proof as linux-x64 Native AOT.
  all         Run test, framework, AOT, and replacement inventory gates.
USAGE
    ;;
  *)
    printf 'APR_AGENT_COMMAND_INVALID %s\n' "${subcommand}" >&2
    exit 2
    ;;
esac
