#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 0 ]]; then
  echo "usage: runtime/scripts/verify-r4-trusted-proof-payload.sh" >&2
  exit 2
fi
if [[ "$(uname -s)" != Linux ]] ||
  grep -Eqi '(microsoft|wsl)' /proc/sys/kernel/osrelease 2>/dev/null; then
  echo "trusted proof payload verification requires native Linux" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
project="$repo_root/runtime/tests/ActionHostTrustedProofPayload/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.csproj"
supervisor_project="$repo_root/runtime/tests/ActionHostVerifierFixture/AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj"
supervisor="$repo_root/runtime/tests/ActionHostVerifierFixture/bin/Release/net10.0/AgenticPrReview.Runtime.ActionHostVerifierFixture.dll"
runtime_project="$repo_root/runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj"
fixture_root="$repo_root/runtime/tests/fixtures/action-host/trusted-proof-payload"
framework_fixture_root="$repo_root/runtime/tests/fixtures/action-host/framework"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/apr-r4-e2p.XXXXXXXX")"
cleanup() {
  exit_code=$?
  trap - EXIT
  chmod -R u+rwX "$temporary_root" 2>/dev/null || true
  rm -rf -- "$temporary_root"
  exit "$exit_code"
}
trap cleanup EXIT

credential_names=(
  GITHUB_TOKEN GH_TOKEN ACTIONS_RUNTIME_TOKEN ACTIONS_ID_TOKEN_REQUEST_TOKEN
  ACTIONS_ID_TOKEN_REQUEST_URL DEEPSEEK_API_KEY OPENAI_API_KEY
  AZURE_CLIENT_SECRET AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY
  GOOGLE_APPLICATION_CREDENTIALS
)
for credential_name in "${credential_names[@]}"; do
  if [[ -n "${!credential_name:-}" ]]; then
    echo "ambient credential variable is visible: $credential_name" >&2
    exit 1
  fi
done
if git -C "$repo_root" config --local --get-regexp \
    '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1 ||
  git config --global --get-regexp \
    '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1; then
  echo "ambient Git credential configuration is visible" >&2
  exit 1
fi
[[ "$(node --version)" =~ ^v24\. ]] || {
  echo APR_R4_E2P_NODE_VERSION_INVALID >&2
  exit 1
}
[[ "$(dotnet --version)" == 10.0.109 ]] || {
  echo APR_R4_E2P_DOTNET_VERSION_INVALID >&2
  exit 1
}

publish_root="$temporary_root/payload"
payload="$publish_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload"
artifacts_root="$temporary_root/artifacts"
intermediate_root="$temporary_root/managed"
architecture="$temporary_root/managed-architecture.json"
identity="$temporary_root/identity.json"
publish_log="$temporary_root/publish.log"
proof_log="$temporary_root/clean-source.log"
proof_evidence="$temporary_root/proof-evidence"
receipt_line="$temporary_root/receipt.line"
receipt_json="$temporary_root/receipt.json"

audit_warnings() {
  local log="$1" prefix line normalized
  prefix="CSC : warning IL3058: Referenced assembly 'JsonSchema.Net' is not built with "'`true`'" and may not be compatible with AOT."
  local -a actual=()
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    if [[ "${line,,}" =~ (^|[[:space:]:])warning([[:space:]]+[a-z]+[0-9]+[[:space:]]*:|[[:space:]]*:) ]]; then
      normalized="${line//"$repo_root/"/}"
      actual+=("$normalized")
    fi
  done < "$log"
  mapfile -t expected < "$fixture_root/aot/warning-policy.txt"
  [[ "${#actual[@]}" -eq 2 && "${#expected[@]}" -eq 2 ]] || return 1
  mapfile -t actual < <(printf '%s\n' "${actual[@]}" | sort)
  mapfile -t expected < <(printf '%s\n' "${expected[@]}" | sort)
  [[ "${actual[0]}" == "${expected[0]}" && "${actual[1]}" == "${expected[1]}" ]] ||
    return 1
  printf 'APR_R4_E2P_AOT_WARNING_ALLOWLIST count=2\n'
}

execute_proof() {
  cd "$repo_root"
  dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj \
    --configuration Release --nologo --filter FullyQualifiedName~TrustedProof -m:1
  if ! dotnet publish "$project" --configuration Release --runtime linux-x64 \
    --self-contained true --output "$publish_root" --artifacts-path "$artifacts_root" \
    --nologo -p:PublishAot=true -p:PublishSingleFile=true \
    -p:JsonSerializerIsReflectionEnabledByDefault=false \
    -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true \
    -p:ContinuousIntegrationBuild=true \
    "-p:PathMap=$repo_root=/_/apr-r4-e2p" \
    -p:TrustedProofPayloadAotIntermediateDirectory="$intermediate_root" \
    -warnaserror -warnnotaserror:IL3058 > "$publish_log" 2>&1; then
    tail -n 100 "$publish_log" >&2
    return 1
  fi
  cat "$publish_log"
  audit_warnings "$publish_log" || {
    echo APR_R4_E2P_AOT_WARNING_ALLOWLIST_INVALID >&2
    return 1
  }
  [[ -x "$payload" && ! -e "$payload.dll" && ! -e "$payload.deps.json" ]] ||
    return 1
  proof_intermediate="$intermediate_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll"
  runtime_intermediate="$intermediate_root/AgenticPrReview.Runtime.dll"
  [[ -f "$proof_intermediate" && -f "$runtime_intermediate" ]] || return 1
  [[ "$(find "$intermediate_root" -maxdepth 1 -type f -name '*.dll' | wc -l)" -eq 2 ]] ||
    return 1
  node scripts/check-r4-e2p-managed-architecture.mjs \
    --proof "$proof_intermediate" --runtime "$runtime_intermediate" \
    --output "$architecture"
  if printf '\000\000\000\001x' | "$payload" >/dev/null 2>&1; then
    echo APR_R4_E2P_AOT_MALFORMED_FRAME_ACCEPTED >&2
    return 1
  fi
  dotnet build "$supervisor_project" --configuration Release --nologo \
    -p:PublishAot=false
  mkdir -p "$proof_evidence"
  set +e
  dotnet "$supervisor" supervise \
    --root "$proof_evidence" --repo "$repo_root" --payload "$payload" \
    --bundle "$repo_root/.github/actions/agentic-pr-review/dist/index.js" \
    --record "$framework_fixture_root/replacement-record.json" \
    --inventory "$framework_fixture_root/e1-base-inventory.json" \
    --golden "$framework_fixture_root/expected-evidence.json.golden" \
    --canaries "$framework_fixture_root/canary-routes.tsv" \
    --node "$(command -v node)" --trusted-proof-only true
  supervisor_status=$?
  set -e
  if [[ -f "$proof_evidence/trusted-proof-payload-evidence.json" ]]; then
    cat "$proof_evidence/trusted-proof-payload-evidence.json"
  fi
  [[ "$supervisor_status" -eq 0 ]] || return "$supervisor_status"
  [[ -f "$proof_evidence/trusted-proof-payload-evidence.json" ]] || return 1
  payload_sha="$(sha256sum "$payload" | cut -d ' ' -f 1)"
  proof_sha="$(sha256sum "$proof_intermediate" | cut -d ' ' -f 1)"
  runtime_sha="$(sha256sum "$runtime_intermediate" | cut -d ' ' -f 1)"
  architecture_sha="$(sha256sum "$architecture" | cut -d ' ' -f 1)"
  pair_sha="$(printf '%s\n%s\n%s\n%s\n%s\n' \
    apr-r4-e2p-build-pair-v1 "$payload_sha" "$proof_sha" "$runtime_sha" \
    "$architecture_sha" | sha256sum | cut -d ' ' -f 1)"
  source_commit="$(git rev-parse HEAD)"
  source_tree="$(git rev-parse 'HEAD^{tree}')"
  printf '{"source_commit":"%s","source_tree":"%s","payload_sha256":"%s","proof_managed_intermediate_sha256":"%s","runtime_managed_intermediate_sha256":"%s","managed_architecture_sha256":"%s","build_pair_sha256":"%s"}\n' \
    "$source_commit" "$source_tree" "$payload_sha" "$proof_sha" \
    "$runtime_sha" "$architecture_sha" "$pair_sha" > "$identity"
  node scripts/compose-r4-e2p-receipt.mjs \
    --identity "$identity" --contract "$fixture_root/aot/receipt-contract.json" \
    --predecessor "$fixture_root/predecessor-anchor.json" \
    --action .github/actions/agentic-pr-review/action.yml \
    --bundle .github/actions/agentic-pr-review/dist/index.js \
    --workflow "$fixture_root/workflow/r4-trusted-proof.yml.template" \
    --preflight-contract "$fixture_root/workflow/preflight-contract.json" \
    --provider-contract "$fixture_root/deterministic-provider-contract.json" \
    --control-contract "$fixture_root/proof-control-contract.json" \
    --stale-contract "$fixture_root/stale-window-contract.json" \
    --trusted-config .github/agentic-pr-review/trusted-proof.json \
    --trusted-instructions .github/agentic-pr-review/trusted-proof-instructions.md \
    --preparation-contract "$fixture_root/preparation-contract.json" \
    --preparation-script runtime/scripts/prepare-r4-trusted-proof-payload.sh \
    --warning-policy "$fixture_root/aot/warning-policy.txt" > "$receipt_line"
  sed 's/^APR_R4_E2P_RECEIPT //' "$receipt_line" > "$receipt_json"
  verify_two_root_preparation
  cat "$receipt_line"
}

verify_two_root_preparation() {
  local source_copy="$temporary_root/source-copy"
  local control_root="$temporary_root/control-root"
  local runner_temp="$temporary_root/preparation-runner"
  local prepared_root="$runner_temp/prepared"
  local github_output="$temporary_root/github-output"
  local control_receipt="$control_root/runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt.json"
  local source_commit
  source_commit="$(git -C "$repo_root" rev-parse HEAD)"
  git clone --quiet --no-checkout --no-local --no-hardlinks \
    "$repo_root" "$source_copy"
  git -C "$source_copy" checkout --quiet --detach "$source_commit"
  mkdir -p "$(dirname "$control_receipt")" "$runner_temp"
  cp "$receipt_json" "$control_receipt"
  : > "$github_output"

  local unchanged="APR_R4_E2P_OUTPUT_SENTINEL"
  printf '%s\n' "$unchanged" > "$github_output"
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" \
      --output-root "$runner_temp/../escaped" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_TRAVERSAL_ACCEPTED >&2
    return 1
  fi
  [[ "$(cat "$github_output")" == "$unchanged" &&
      ! -e "$temporary_root/escaped" ]] || return 1

  mkdir -p "${runner_temp}-prefix-escape"
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" \
      --output-root "${runner_temp}-prefix-escape/payload" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_PREFIX_ESCAPE_ACCEPTED >&2
    return 1
  fi
  [[ "$(cat "$github_output")" == "$unchanged" ]] || return 1

  mkdir -p "$runner_temp/symlink-target"
  ln -s "$runner_temp/symlink-target" "$runner_temp/symlink-parent"
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" \
      --output-root "$runner_temp/symlink-parent/payload" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_SYMLINK_ACCEPTED >&2
    return 1
  fi
  [[ "$(cat "$github_output")" == "$unchanged" ]] || return 1

  printf 'dirty\n' > "$source_copy/apr-r4-e2p-dirty"
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" --output-root "$prepared_root" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_DIRTY_SOURCE_ACCEPTED >&2
    return 1
  fi
  rm -f -- "$source_copy/apr-r4-e2p-dirty"
  [[ "$(cat "$github_output")" == "$unchanged" && ! -e "$prepared_root" ]] ||
    return 1

  node --input-type=module - "$control_receipt" <<'NODE'
import fs from 'node:fs';
const path = process.argv[2];
const receipt = JSON.parse(fs.readFileSync(path, 'utf8'));
receipt.source_commit = '0'.repeat(40);
fs.writeFileSync(path, `${JSON.stringify(receipt)}\n`);
NODE
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" --output-root "$prepared_root" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_STALE_RECEIPT_ACCEPTED >&2
    return 1
  fi
  cp "$receipt_json" "$control_receipt"
  [[ "$(cat "$github_output")" == "$unchanged" && ! -e "$prepared_root" ]] ||
    return 1

  : > "$github_output"
  RUNNER_TEMP="$runner_temp" bash \
    "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload.sh" \
    --source-root "$source_copy" --control-root "$control_root" \
    --receipt "$control_receipt" --output-root "$prepared_root" \
    --github-output "$github_output"
  [[ "$(wc -l < "$github_output")" -eq 5 ]] || return 1
  grep -Fx "prepared_root=$prepared_root" "$github_output" >/dev/null
  grep -Fx 'prepared_executable=AgenticPrReview.Runtime.ActionHostTrustedProofPayload' \
    "$github_output" >/dev/null
  grep -Fx "prepared_payload_sha256=$(sha256sum "$prepared_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload" | cut -d ' ' -f 1)" \
    "$github_output" >/dev/null
  grep -Fx "action_source_sha=$(git -C "$source_copy" rev-parse HEAD)" \
    "$github_output" >/dev/null
  grep -Fx 'payload_build_discriminator=r4-w2' "$github_output" >/dev/null
}

export repo_root project supervisor_project supervisor runtime_project fixture_root
export framework_fixture_root temporary_root publish_root payload artifacts_root
export intermediate_root architecture identity publish_log proof_evidence receipt_line receipt_json
export -f audit_warnings verify_two_root_preparation execute_proof
node "$repo_root/scripts/run-clean-source-proof.mjs" --repo "$repo_root" -- \
  bash -euo pipefail -c execute_proof | tee "$proof_log"
