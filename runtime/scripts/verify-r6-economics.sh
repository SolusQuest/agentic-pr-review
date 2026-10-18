#!/usr/bin/env bash
# Current managed Solus gate; explicit all/framework/aot retain legacy R6 proof.
set -euo pipefail
unset GIT_DIR GIT_WORK_TREE
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
if (( $# > 1 )); then
  echo 'r6_economics_gate result=rejected stage=arguments' >&2
  exit 2
fi
exec python3 "${ROOT}/runtime/scripts/r6-economics-gate.py" "${1:-managed}"
