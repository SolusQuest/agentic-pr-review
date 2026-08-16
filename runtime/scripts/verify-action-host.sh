#!/usr/bin/env bash
set -euo pipefail

if [[ "${1:-}" != "framework" || "$#" -ne 1 ]]; then
  echo "usage: runtime/scripts/verify-action-host.sh framework" >&2
  exit 2
fi

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "framework verification requires Linux" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/apr-action-host-framework.XXXXXXXX")"
cleanup() {
  chmod -R u+rwX "$temporary_root" 2>/dev/null || true
  rm -rf -- "$temporary_root"
}
trap cleanup EXIT

credential_names=(
  GITHUB_TOKEN GH_TOKEN ACTIONS_RUNTIME_TOKEN
  ACTIONS_ID_TOKEN_REQUEST_TOKEN ACTIONS_ID_TOKEN_REQUEST_URL
  DEEPSEEK_API_KEY OPENAI_API_KEY AZURE_CLIENT_SECRET
  AWS_ACCESS_KEY_ID AWS_SECRET_ACCESS_KEY GOOGLE_APPLICATION_CREDENTIALS
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

cd "$repo_root"
checked_bundle="$repo_root/.github/actions/agentic-pr-review/dist/index.js"
checked_bundle_sha256="$(sha256sum "$checked_bundle" | cut -d ' ' -f 1)"
npm run build:action
generated_bundle_sha256="$(sha256sum "$checked_bundle" | cut -d ' ' -f 1)"
if [[ "$checked_bundle_sha256" != "$generated_bundle_sha256" ]]; then
  echo "checked action bundle is not reproducible" >&2
  exit 1
fi

publish_root="$temporary_root/payload"
dotnet publish \
  runtime/tests/ActionHostVerifierFixture/AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false \
  --output "$publish_root" \
  --nologo \
  -p:PublishAot=false \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false

payload="$publish_root/AgenticPrReview.Runtime.ActionHostVerifierFixture"
test -x "$payload"
test ! -e "$payload.dll"
test ! -e "$payload.deps.json"
test ! -e "$payload.runtimeconfig.json"

evidence_root="$temporary_root/evidence"
isolated_home="$temporary_root/home"
isolated_tmp="$temporary_root/tmp"
isolated_config="$temporary_root/config"
mkdir -p "$evidence_root" "$isolated_home" "$isolated_tmp" "$isolated_config"
node_path="$(command -v node)"
env -i \
  HOME="$isolated_home" \
  TMPDIR="$isolated_tmp" \
  XDG_CONFIG_HOME="$isolated_config" \
  GIT_CONFIG_NOSYSTEM=1 \
  GIT_CONFIG_GLOBAL=/dev/null \
  PATH="$PATH" \
  CI=true \
  "$payload" supervise \
  --root "$evidence_root" \
  --repo "$repo_root" \
  --payload "$payload" \
  --bundle "$checked_bundle" \
  --record "$repo_root/runtime/tests/fixtures/action-host/framework/replacement-record.json" \
  --inventory "$repo_root/runtime/tests/fixtures/action-host/framework/e1-base-inventory.json" \
  --golden "$repo_root/runtime/tests/fixtures/action-host/framework/expected-evidence.json.golden" \
  --canaries "$repo_root/runtime/tests/fixtures/action-host/framework/canary-routes.tsv" \
  --node "$node_path"
