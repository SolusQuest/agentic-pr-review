#!/usr/bin/env bash
set -euo pipefail

fail() {
  echo "APR_R4_E2P_PREPARATION_INVALID $1" >&2
  exit 1
}

declare -A options=()
names=(--source-root --control-root --receipt --output-root --github-output)
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
  DEEPSEEK_API_KEY OPENAI_API_KEY AZURE_CLIENT_SECRET AWS_ACCESS_KEY_ID
  AWS_SECRET_ACCESS_KEY GOOGLE_APPLICATION_CREDENTIALS
)
for credential_name in "${credential_names[@]}"; do
  [[ -z "${!credential_name:-}" ]] || fail "ambient-credential-$credential_name"
done

source_root="${options[--source-root]}"
control_root="${options[--control-root]}"
receipt="${options[--receipt]}"
output_root="${options[--output-root]}"
github_output="${options[--github-output]}"
for candidate in "$source_root" "$control_root" "$receipt" "$output_root" "$github_output"; do
  [[ "$candidate" == /* ]] || fail absolute-path
done
[[ -d "$source_root" && ! -L "$source_root" ]] || fail source-root
[[ -d "$control_root" && ! -L "$control_root" ]] || fail control-root
source_root="$(realpath -e "$source_root")"
control_root="$(realpath -e "$control_root")"
[[ "$source_root" != "$control_root" ]] || fail root-overlap
case "$source_root/" in "$control_root/"* ) fail root-overlap ;; esac
case "$control_root/" in "$source_root/"* ) fail root-overlap ;; esac

expected_receipt="$control_root/runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt.json"
[[ -f "$receipt" && ! -L "$receipt" ]] || fail receipt-file
[[ "$(realpath -e "$receipt")" == "$expected_receipt" ]] || fail receipt-path
[[ ! -e "$output_root" && ! -L "$output_root" ]] || fail output-not-fresh
runner_temp="${RUNNER_TEMP:-}"
[[ "$runner_temp" == /* && -d "$runner_temp" && ! -L "$runner_temp" ]] || fail runner-temp
runner_temp="$(realpath -e "$runner_temp")"
canonical_output_root="$(realpath -m -- "$output_root")"
[[ "$canonical_output_root" == "$output_root" ]] || fail output-canonical
case "$canonical_output_root" in "$runner_temp/"* ) ;; * ) fail output-parent ;; esac
case "$canonical_output_root/" in "$source_root/"*|"$control_root/"* ) fail root-overlap ;; esac
[[ -f "$github_output" && ! -L "$github_output" ]] || fail github-output
github_output="$(realpath -e "$github_output")"

if git -C "$source_root" config --local --get-regexp \
    '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1 ||
  git config --global --get-regexp \
    '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1; then
  fail git-credential-helper
fi
[[ -z "$(git -C "$source_root" status --porcelain=v1 --untracked-files=all)" ]] ||
  fail dirty-source
source_commit="$(git -C "$source_root" rev-parse HEAD)"
source_tree="$(git -C "$source_root" rev-parse 'HEAD^{tree}')"
[[ "$source_commit" =~ ^[0-9a-f]{40}$ && "$source_tree" =~ ^[0-9a-f]{40}$ ]] ||
  fail source-identity
[[ "$(node --version)" =~ ^v24\. ]] || fail node-version
[[ "$(dotnet --version)" == 10.0.109 ]] || fail dotnet-version

receipt_commit="$(node --input-type=module - "$receipt" <<'NODE'
import fs from 'node:fs';
const value = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (!/^[0-9a-f]{40}$/u.test(value.source_commit ?? '')) process.exit(1);
process.stdout.write(value.source_commit);
NODE
)" || fail receipt-source
receipt_tree="$(node --input-type=module - "$receipt" <<'NODE'
import fs from 'node:fs';
const value = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
if (!/^[0-9a-f]{40}$/u.test(value.source_tree ?? '')) process.exit(1);
process.stdout.write(value.source_tree);
NODE
)" || fail receipt-source
[[ "$receipt_commit" == "$source_commit" && "$receipt_tree" == "$source_tree" ]] ||
  fail source-receipt-mismatch

cleanup_output=false
artifacts_root="$output_root.intermediates"
canonical_artifacts_root="$(realpath -m -- "$artifacts_root")"
[[ "$canonical_artifacts_root" == "$artifacts_root" ]] || fail artifacts-canonical
case "$canonical_artifacts_root" in "$runner_temp/"* ) ;; * ) fail artifacts-parent ;; esac
case "$canonical_artifacts_root/" in "$source_root/"*|"$control_root/"* ) fail root-overlap ;; esac
case "$github_output" in "$output_root/"*|"$artifacts_root/"* ) fail github-output-overlap ;; esac
[[ ! -e "$artifacts_root" && ! -L "$artifacts_root" ]] || fail artifacts-not-fresh
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
  -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true \
  -p:ContinuousIntegrationBuild=true \
  "-p:PathMap=$source_root=/_/apr-r4-e2p"
payload="$output_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload"
[[ -x "$payload" && -f "$payload" && ! -L "$payload" ]] || fail payload-file
[[ "$(realpath -e "$payload")" == "$payload" ]] || fail payload-path
node "$source_root/scripts/check-r4-e2p-receipt.mjs" \
  --receipt "$receipt" --source-root "$source_root" --payload "$payload"
payload_sha256="$(sha256sum "$payload" | cut -d ' ' -f 1)"
[[ "$payload_sha256" =~ ^[0-9a-f]{64}$ ]] || fail payload-digest

output_lines="$(mktemp "${RUNNER_TEMP}/apr-r4-e2p-output.XXXXXXXX")"
printf 'prepared_root=%s\nprepared_executable=%s\nprepared_payload_sha256=%s\naction_source_sha=%s\npayload_build_discriminator=r4-w2\n' \
  "$output_root" "AgenticPrReview.Runtime.ActionHostTrustedProofPayload" \
  "$payload_sha256" "$source_commit" > "$output_lines"
[[ "$(wc -l < "$output_lines")" -eq 5 ]] || fail output-count
cat "$output_lines" >> "$github_output"
rm -f -- "$output_lines"
cleanup_output=false
