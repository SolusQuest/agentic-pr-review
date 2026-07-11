#!/usr/bin/env bash
# Deterministic runtime verification entrypoint.
#
# Subcommands:
#   test        Run the runtime test project.
#   framework   Framework-dependent bootstrap smoke: dotnet run + cmp goldens.
#   aot         Native AOT bootstrap smoke: publish linux-x64, execute published binary,
#               cmp goldens.
#   all         test -> framework -> aot.
#
# Comparison is strictly byte-level via cmp. No JSON parsing, sorting, field removal, or
# rewriting is permitted before cmp.
#
# Each smoke subcommand allocates its own fresh temporary work directory. Framework and
# AOT smoke must not share final output paths, and the AOT publish directory is separate
# from both smoke work directories. Temporary directories are cleaned up on exit
# (best-effort).
#
# The runtime CLI refuses to overwrite existing output/trace files by design, so pre-
# existing outputs are never removed to make cmp pass.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"

RUNTIME_PROJECT="${REPO_ROOT}/runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj"
TEST_PROJECT="${REPO_ROOT}/runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj"
BOOTSTRAP_INPUT="${REPO_ROOT}/protocol/fixtures/v1/cases/bootstrap/input.json"
BOOTSTRAP_EXPECTED_RESULT="${REPO_ROOT}/runtime/tests/fixtures/deterministic/bootstrap/expected-result.json"
BOOTSTRAP_EXPECTED_TRACE="${REPO_ROOT}/runtime/tests/fixtures/deterministic/bootstrap/expected-trace.json"

_tempdirs=()

_cleanup() {
  local dir
  for dir in "${_tempdirs[@]:-}"; do
    if [[ -n "${dir:-}" && -d "${dir}" ]]; then
      rm -rf -- "${dir}" || true
    fi
  done
}
trap _cleanup EXIT

_mktemp() {
  local dir
  dir="$(mktemp -d)"
  _tempdirs+=("${dir}")
  printf '%s\n' "${dir}"
}

_require_file() {
  local path="$1"
  if [[ ! -f "${path}" ]]; then
    printf 'error: expected file not found: %s\n' "${path}" >&2
    exit 1
  fi
}

_prepare_input() {
  # Copy the bootstrap input into a caller-owned work directory. The caller passes the
  # destination path; we do not choose it.
  local dest="$1"
  cp -- "${BOOTSTRAP_INPUT}" "${dest}"
}

_assert_absent() {
  # The CLI enforces no-overwrite, but assert here too so an accidentally reused
  # temporary directory produces a clear diagnostic instead of a runtime error.
  local path="$1"
  if [[ -e "${path}" ]]; then
    printf 'error: expected output path to be absent before run: %s\n' "${path}" >&2
    exit 1
  fi
}

run_test() {
  _require_file "${TEST_PROJECT}"
  dotnet test "${TEST_PROJECT}" --nologo
}

run_framework() {
  _require_file "${RUNTIME_PROJECT}"
  _require_file "${BOOTSTRAP_INPUT}"
  _require_file "${BOOTSTRAP_EXPECTED_RESULT}"
  _require_file "${BOOTSTRAP_EXPECTED_TRACE}"

  local workdir
  workdir="$(_mktemp)"
  local input="${workdir}/input.json"
  local result="${workdir}/result.json"
  local trace="${workdir}/trace.json"

  _prepare_input "${input}"
  _assert_absent "${result}"
  _assert_absent "${trace}"

  dotnet run \
    --project "${RUNTIME_PROJECT}" \
    -c Release \
    --no-launch-profile \
    -- \
    review \
    --input "${input}" \
    --output "${result}" \
    --trace "${trace}"

  cmp "${result}" "${BOOTSTRAP_EXPECTED_RESULT}"
  cmp "${trace}" "${BOOTSTRAP_EXPECTED_TRACE}"
}

run_aot() {
  _require_file "${RUNTIME_PROJECT}"
  _require_file "${BOOTSTRAP_INPUT}"
  _require_file "${BOOTSTRAP_EXPECTED_RESULT}"
  _require_file "${BOOTSTRAP_EXPECTED_TRACE}"

  local publish_dir
  local workdir
  publish_dir="$(_mktemp)"
  workdir="$(_mktemp)"
  if [[ "${publish_dir}" == "${workdir}" ]]; then
    printf 'error: publish and work directories must differ\n' >&2
    exit 1
  fi

  local input="${workdir}/input.json"
  local result="${workdir}/result.json"
  local trace="${workdir}/trace.json"
  local binary="${publish_dir}/AgenticPrReview.Runtime"

  dotnet publish "${RUNTIME_PROJECT}" \
    -c Release \
    -r linux-x64 \
    --self-contained true \
    -p:PublishAot=true \
    -o "${publish_dir}"

  _require_file "${binary}"

  _prepare_input "${input}"
  _assert_absent "${result}"
  _assert_absent "${trace}"

  "${binary}" review \
    --input "${input}" \
    --output "${result}" \
    --trace "${trace}"

  cmp "${result}" "${BOOTSTRAP_EXPECTED_RESULT}"
  cmp "${trace}" "${BOOTSTRAP_EXPECTED_TRACE}"
}

run_all() {
  run_test
  run_framework
  run_aot
}

subcommand="${1:-all}"
case "${subcommand}" in
  test)      run_test ;;
  framework) run_framework ;;
  aot)       run_aot ;;
  all)       run_all ;;
  -h|--help|help)
    cat <<'USAGE'
Usage: verify-runtime.sh [test|framework|aot|all]

  test        Run the runtime test project.
  framework   Framework-dependent bootstrap smoke.
  aot         Native AOT bootstrap smoke (linux-x64).
  all         Run test, then framework, then aot (default).
USAGE
    ;;
  *)
    printf 'error: unknown subcommand: %s\n' "${subcommand}" >&2
    exit 2
    ;;
esac