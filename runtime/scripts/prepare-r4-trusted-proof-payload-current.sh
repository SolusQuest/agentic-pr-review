#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "APR_R4_E2P_CURRENT_PREPARATION_INVALID $1" >&2
  exit 1
}

declare -A options=()
names=(--source-root --control-root --expected-source-sha --output-root --github-output)
if [[ "$#" -ne 10 ]]; then
  fail usage
fi
while [[ "$#" -gt 0 ]]; do
  name="$1"
  value="$2"
  shift 2
  if [[ ! " ${names[*]} " =~ " $name " || -n "${options[$name]+x}" ]]; then
    fail arguments
  fi
  options[$name]="$value"
done
for name in "${names[@]}"; do
  [[ -n "${options[$name]+x}" ]] || fail arguments
done

if [[ "$(uname -s)" != Linux ]] ||
  grep -Eqi '(microsoft|wsl)' /proc/sys/kernel/osrelease 2>/dev/null; then
  fail native-linux-required
fi

credential_names=(
  GITHUB_TOKEN GH_TOKEN ACTIONS_RUNTIME_TOKEN ACTIONS_ID_TOKEN_REQUEST_TOKEN
  AGENTIC_PR_REVIEW_TRUSTED_PROOF_PROVIDER_CANARY
  DEEPSEEK_API_KEY OPENAI_API_KEY AZURE_CLIENT_SECRET AWS_ACCESS_KEY_ID
  AWS_SECRET_ACCESS_KEY GOOGLE_APPLICATION_CREDENTIALS
)
for credential_name in "${credential_names[@]}"; do
  [[ -z "${!credential_name:-}" ]] || fail "ambient-credential-$credential_name"
done

source_root="${options[--source-root]}"
control_root="${options[--control-root]}"
expected_source_sha="${options[--expected-source-sha]}"
output_root="${options[--output-root]}"
github_output="${options[--github-output]}"
for candidate in "$source_root" "$control_root" "$output_root" "$github_output"; do
  [[ "$candidate" == /* ]] || fail absolute-path
done
[[ "$expected_source_sha" =~ ^[0-9a-f]{40}$ ]] || fail expected-source
[[ -d "$source_root" && ! -L "$source_root" ]] || fail source-root
[[ -d "$control_root" && ! -L "$control_root" ]] || fail control-root
source_root="$(realpath -e "$source_root")"
control_root="$(realpath -e "$control_root")"
[[ "$source_root" != "$control_root" ]] || fail root-overlap
case "$source_root/" in "$control_root/"* ) fail root-overlap ;; esac
case "$control_root/" in "$source_root/"* ) fail root-overlap ;; esac

for root in "$source_root" "$control_root"; do
  if git -C "$root" config --local --get-regexp \
      '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1 ||
    git config --global --get-regexp \
      '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1 ||
    [[ -n "$(git -C "$root" status --porcelain=v1 --untracked-files=all)" ]] ||
    [[ "$(git -C "$root" rev-parse HEAD)" != "$expected_source_sha" ]]; then
    fail source-identity
  fi
done
source_tree="$(git -C "$source_root" rev-parse 'HEAD^{tree}')"
control_tree="$(git -C "$control_root" rev-parse 'HEAD^{tree}')"
[[ "$source_tree" =~ ^[0-9a-f]{40}$ && "$source_tree" == "$control_tree" ]] ||
  fail source-tree
[[ "$(node --version)" =~ ^v24\. ]] || fail node-version
[[ "$(dotnet --version)" == 10.0.109 ]] || fail dotnet-version

[[ ! -e "$output_root" && ! -L "$output_root" ]] || fail output-not-fresh
runner_temp="${RUNNER_TEMP:-}"
[[ "$runner_temp" == /* && -d "$runner_temp" && ! -L "$runner_temp" ]] || fail runner-temp
runner_temp="$(realpath -e "$runner_temp")"
canonical_output_root="$(realpath -m -- "$output_root")"
[[ "$canonical_output_root" == "$output_root" ]] || fail output-canonical
case "$canonical_output_root" in "$runner_temp/"* ) ;; * ) fail output-parent ;; esac
[[ -f "$github_output" && ! -L "$github_output" ]] || fail github-output
github_output="$(realpath -e "$github_output")"

cleanup_output=false
artifacts_root="$output_root.intermediates"
cleanup() {
  exit_code=$?
  trap - EXIT
  if [[ "$exit_code" -ne 0 && "$cleanup_output" == true ]]; then
    for cleanup_root in "$output_root" "$artifacts_root"; do
      if [[ -d "$cleanup_root" ]]; then
        chmod -R u+rwX "$cleanup_root" 2>/dev/null || true
        rm -rf -- "$cleanup_root"
      fi
    done
  fi
  exit "$exit_code"
}
trap cleanup EXIT
mkdir "$output_root"
cleanup_output=true

project="$source_root/runtime/tests/ActionHostTrustedProofPayload/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.csproj"
dotnet publish "$project" --configuration Release --runtime linux-x64 \
  --self-contained true --output "$output_root" --artifacts-path "$artifacts_root" \
  --nologo -p:PublishAot=true -p:PublishSingleFile=true \
  -p:JsonSerializerIsReflectionEnabledByDefault=false \
  -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true \
  -p:ContinuousIntegrationBuild=true \
  "-p:PayloadSourceCommit=$expected_source_sha" "-p:PayloadSourceTree=$source_tree" \
  "-p:PathMap=$source_root=/_/apr-r4-e2p%2C$artifacts_root=/_/apr-r4-e2p-artifacts" \
  -warnaserror -warnnotaserror:IL3058
payload="$output_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload"
[[ -x "$payload" && -f "$payload" && ! -L "$payload" ]] || fail payload-file
payload_sha256="$(sha256sum "$payload" | cut -d ' ' -f 1)"
[[ "$payload_sha256" =~ ^[0-9a-f]{64}$ ]] || fail payload-digest

output_lines="$(mktemp "${RUNNER_TEMP}/apr-r4-e2p-output.XXXXXXXX")"
printf 'prepared_root=%s\nprepared_executable=%s\nprepared_payload_sha256=%s\naction_source_sha=%s\npayload_source_sha=%s\npayload_source_tree=%s\npayload_build_discriminator=r4-w2\n' \
  "$output_root" "AgenticPrReview.Runtime.ActionHostTrustedProofPayload" \
  "$payload_sha256" "$expected_source_sha" "$expected_source_sha" "$source_tree" > "$output_lines"
[[ "$(wc -l < "$output_lines")" -eq 7 ]] || fail output-count
cat "$output_lines" >> "$github_output"
rm -f -- "$output_lines"
cleanup_output=false
