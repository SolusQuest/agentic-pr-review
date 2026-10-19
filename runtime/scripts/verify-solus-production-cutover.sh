#!/usr/bin/env bash
set -euo pipefail

# The complete proof owns both original independently built managed archives,
# actual generated Action, runtime-only isolation and direct argv cutover cases.
# No public restore or native FDD result substitutes for the private .4 cache.
[[ "$#" -eq 0 ]] || { echo APR_SOLUS_CUTOVER_ARGUMENTS_INVALID >&2; exit 2; }
node runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-archive.mjs
