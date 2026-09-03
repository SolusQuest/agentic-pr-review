#!/usr/bin/env bash

# WSL2 is a Linux environment. Check the selected tools, not kernel branding.
# Inspect rather than execute them here: callers retain their credential,
# version, clean-source and build checks before admitting any proof.
apr_require_linux_toolchain() {
  [[ "$(uname -s)" == Linux ]] || return 1
  local tool executable magic
  for tool in "$@"; do
    [[ "$(type -t -- "$tool")" == file ]] || return 1
    executable="$(command -v -- "$tool")" || return 1
    [[ -f "$executable" && -x "$executable" ]] || return 1
    magic="$(LC_ALL=C od -An -tx1 -N4 -- "$executable")" || return 1
    [[ "${magic//[[:space:]]/}" == 7f454c46 ]] || return 1
  done
}
