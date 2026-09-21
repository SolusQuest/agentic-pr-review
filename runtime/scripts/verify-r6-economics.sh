#!/usr/bin/env bash
# Linux x64 offline R6 gate. No provider credentials or live dispatch.
set -euo pipefail
unset GIT_DIR GIT_WORK_TREE
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exec python3 "${ROOT}/runtime/scripts/r6-economics-gate.py" "${1:-all}"
