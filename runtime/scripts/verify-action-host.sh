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
mkdir -p "$evidence_root"
"$payload" supervise \
  --root "$evidence_root" \
  --repo "$repo_root" \
  --payload "$payload" \
  --bundle "$checked_bundle" \
  --record "$repo_root/runtime/tests/fixtures/action-host/framework/replacement-record.json" \
  --inventory "$repo_root/runtime/tests/fixtures/action-host/framework/e1-base-inventory.json" \
  --golden "$repo_root/runtime/tests/fixtures/action-host/framework/expected-evidence.json.golden" \
  --canaries "$repo_root/runtime/tests/fixtures/action-host/framework/canary-routes.tsv" \
  --node "$(command -v node)"
