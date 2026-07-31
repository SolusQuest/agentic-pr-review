#!/usr/bin/env bash
# Bounded maintainer-only DeepSeek thinking/tool compatibility probe. Build and
# execution are intentionally separate so provider credentials are never
# available to restore, MSBuild, tests, or other repository tooling.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PROJECT="${REPO_ROOT}/runtime/tests/DeepSeekCompatibilityProbe/AgenticPrReview.Runtime.DeepSeekCompatibilityProbe.csproj"
BUILD_DIR="${REPO_ROOT}/runtime/tests/DeepSeekCompatibilityProbe/bin/compatibility-probe"
ASSEMBLY="AgenticPrReview.Runtime.DeepSeekCompatibilityProbe"
DOTNET_CMD="${DOTNET_CMD:-dotnet}"
_controlled_root=""

_cleanup() {
  if [[ -n "${_controlled_root}" && -d "${_controlled_root}" ]]; then
    rm -rf -- "${_controlled_root}"
  fi
}
trap _cleanup EXIT

_fail() {
  printf '%s\n' "$1" >&2
  return 1
}

_build() {
  "${DOTNET_CMD}" publish "${PROJECT}" --configuration Release --nologo \
    --self-contained false -p:PublishAot=false -o "${BUILD_DIR}"
  [[ -f "${BUILD_DIR}/${ASSEMBLY}.dll" ]] ||
    _fail APR_DEEPSEEK_PROBE_BUILD_OUTPUT_MISSING
  printf 'APR_DEEPSEEK_PROBE_BUILD_OK\n'
}

_run() {
  [[ "$(uname -s)" != MINGW* ]] ||
    _fail APR_DEEPSEEK_PROBE_RUN_PLATFORM_INVALID
  [[ -f "${BUILD_DIR}/${ASSEMBLY}.dll" ]] ||
    _fail APR_DEEPSEEK_PROBE_BUILD_OUTPUT_MISSING
  [[ -n "${AGENTIC_REVIEW_DEEPSEEK_API_KEY:-}" ]] ||
    _fail APR_DEEPSEEK_PROBE_CONFIG_INVALID

  local dotnet_path
  dotnet_path="$(readlink -f "$(command -v "${DOTNET_CMD}")")"
  _controlled_root="$(mktemp -d)"
  mkdir -p -- \
    "${_controlled_root}/home" \
    "${_controlled_root}/tmp" \
    "${_controlled_root}/dotnet-home"

  (
    cd -- "${_controlled_root}"
    timeout 300s env -i \
      "AGENTIC_REVIEW_DEEPSEEK_API_KEY=${AGENTIC_REVIEW_DEEPSEEK_API_KEY}" \
      "DOTNET_CLI_HOME=${_controlled_root}/dotnet-home" \
      "DOTNET_CLI_TELEMETRY_OPTOUT=1" \
      "DOTNET_NOLOGO=1" \
      "DOTNET_ROOT=$(dirname "${dotnet_path}")" \
      "HOME=${_controlled_root}/home" \
      "LANG=C.UTF-8" \
      "LC_ALL=C.UTF-8" \
      "TMPDIR=${_controlled_root}/tmp" \
      "TZ=UTC" \
      "${dotnet_path}" "${BUILD_DIR}/${ASSEMBLY}.dll"
  )
}

case "${1:-}" in
  build) _build ;;
  run) _run ;;
  *)
    printf 'Usage: probe-deepseek-request.sh [build|run]\n' >&2
    exit 2
    ;;
esac
