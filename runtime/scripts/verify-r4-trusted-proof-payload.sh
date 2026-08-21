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
runtime_project="$repo_root/runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj"
fixture_root="$repo_root/runtime/tests/fixtures/action-host/trusted-proof-payload"
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
    --warning-policy "$fixture_root/aot/warning-policy.txt"
}

export repo_root project runtime_project fixture_root temporary_root
export publish_root payload artifacts_root intermediate_root architecture identity publish_log
export -f audit_warnings execute_proof
node "$repo_root/scripts/run-clean-source-proof.mjs" --repo "$repo_root" -- \
  bash -euo pipefail -c execute_proof | tee "$proof_log"
