#!/usr/bin/env bash
set -euo pipefail

mode="${1:-}"
if [[ "$#" -ne 1 || ( "$mode" != framework && "$mode" != aot ) ]]; then
  echo "usage: runtime/scripts/verify-action-host.sh <framework|aot>" >&2
  exit 2
fi
if [[ "$(uname -s)" != Linux ]]; then
  echo "$mode verification requires Linux" >&2
  exit 2
fi
if [[ "$mode" == aot ]] &&
  grep -Eqi '(microsoft|wsl)' /proc/sys/kernel/osrelease 2>/dev/null; then
  echo "aot verification requires native Linux" >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
project="$repo_root/runtime/tests/ActionHostVerifierFixture/AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj"
runtime_project="$repo_root/runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj"
golden="$repo_root/runtime/tests/fixtures/action-host/framework/expected-evidence.json.golden"
record="$repo_root/runtime/tests/fixtures/action-host/framework/replacement-record.json"
inventory="$repo_root/runtime/tests/fixtures/action-host/framework/e1-base-inventory.json"
canaries="$repo_root/runtime/tests/fixtures/action-host/framework/canary-routes.tsv"
warning_policy="$repo_root/runtime/tests/fixtures/action-host/aot/warning-policy.txt"
temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/apr-action-host-${mode}.XXXXXXXX")"
cleanup() {
  local exit_code=$?
  trap - EXIT
  if [[ "$exit_code" -ne 0 && -n "${evidence_root:-}" && -d "$evidence_root" ]]; then
    local failure_diagnostic_found=false
    while IFS= read -r result_path; do
      if [[ "$(tr -d '\r\n' < "$result_path")" == fail ]]; then
        local relative_result="${result_path#"$evidence_root"/}"
        echo "APR_ACTION_HOST_FRAMEWORK_FAILED_CASE ${relative_result%/case-result.txt}" >&2
        local diagnostic_path="${result_path%/case-result.txt}/case-diagnostic.json"
        if [[ -f "$diagnostic_path" ]]; then
          echo "APR_ACTION_HOST_FRAMEWORK_FAILED_DIAGNOSTIC $(tr -d '\r\n' < "$diagnostic_path")" >&2
        fi
        failure_diagnostic_found=true
      fi
    done < <(find "$evidence_root" -type f -name case-result.txt -print | sort)
    local global_diagnostic="$evidence_root/supervisor-global-diagnostic.json"
    if [[ -f "$global_diagnostic" ]]; then
      echo "APR_ACTION_HOST_FRAMEWORK_GLOBAL_DIAGNOSTIC $(tr -d '\r\n' < "$global_diagnostic")" >&2
      failure_diagnostic_found=true
    fi
    if [[ "$failure_diagnostic_found" == false ]]; then
      echo "APR_ACTION_HOST_${mode^^}_SUPERVISOR_FAILED" >&2
    fi
    if [[ -f "$golden" && -f "$evidence_root/normalized-evidence.json" ]]; then
      node --input-type=module - "$golden" "$evidence_root/normalized-evidence.json" <<'NODE'
import fs from 'node:fs';

const [expectedPath, actualPath] = process.argv.slice(2);
const expected = JSON.parse(fs.readFileSync(expectedPath, 'utf8'));
const actual = JSON.parse(fs.readFileSync(actualPath, 'utf8'));
const maximumDetails = 256;
const details = [];
const same = (left, right) => JSON.stringify(left) === JSON.stringify(right);
const encode = (value) => {
  if (value === undefined) return '<missing>';
  const encoded = JSON.stringify(value);
  return encoded.length <= 160 ? encoded : `${encoded.slice(0, 157)}...`;
};
const visit = (left, right, location) => {
  if (same(left, right) || details.length >= maximumDetails) return;
  if (Array.isArray(left) && Array.isArray(right)) {
    if (left.length !== right.length) {
      details.push(`${location}.length:${left.length}->${right.length}`);
    }
    for (let index = 0; index < Math.max(left.length, right.length); index += 1) {
      visit(left[index], right[index], `${location}[${index}]`);
    }
    return;
  }
  if (
    left !== null && right !== null &&
    typeof left === 'object' && typeof right === 'object' &&
    !Array.isArray(left) && !Array.isArray(right)
  ) {
    const keys = [...new Set([...Object.keys(left), ...Object.keys(right)])].sort();
    for (const key of keys) visit(left[key], right[key], location ? `${location}.${key}` : key);
    return;
  }
  details.push(`${location}:${encode(left)}->${encode(right)}`);
};
visit(expected, actual, '');
if (!same(expected, actual) && details.length === maximumDetails) details.push('<truncated>');
process.stderr.write(`APR_ACTION_HOST_FRAMEWORK_EVIDENCE_MISMATCH ${details.join(',')}\n`);
NODE
    fi
  fi
  chmod -R u+rwX "$temporary_root" 2>/dev/null || true
  rm -rf -- "$temporary_root"
  exit "$exit_code"
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

checked_bundle="$repo_root/.github/actions/agentic-pr-review/dist/index.js"
publish_root="$temporary_root/payload"
payload="$publish_root/AgenticPrReview.Runtime.ActionHostVerifierFixture"
framework_bundle="$temporary_root/framework-wrapper/index.js"
evidence_root="$temporary_root/evidence"
isolated_home="$temporary_root/home"
isolated_tmp="$temporary_root/tmp"
isolated_config="$temporary_root/config"
artifacts_root="$temporary_root/artifacts"
intermediate_root="$temporary_root/aot-intermediate"
build_pair="$temporary_root/build-pair.json"
identity_output="$temporary_root/aot-identity.json"
publish_log="$temporary_root/publish.log"
proof_log="$temporary_root/clean-source.log"
mkdir -p "$evidence_root" "$isolated_home" "$isolated_tmp" \
  "$isolated_config"
node_path="$(command -v node)"

audit_aot_warnings() {
  local log="$1"
  local announce="${2:-yes}"
  local prefix expected_runtime expected_fixture line lower warning
  prefix="CSC : warning IL3058: Referenced assembly 'JsonSchema.Net' is not built with "'`true`'" and may not be compatible with AOT."
  local -a policy_lines=()
  mapfile -t policy_lines < "$warning_policy"
  if [[ "${#policy_lines[@]}" -ne 2 ||
        "${policy_lines[0]}" != "$prefix [runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj]" ||
        "${policy_lines[1]}" != "$prefix [runtime/tests/ActionHostVerifierFixture/AgenticPrReview.Runtime.ActionHostVerifierFixture.csproj]" ]]; then
    echo APR_R4_E2_AOT_WARNING_POLICY_INVALID >&2
    return 1
  fi
  expected_runtime="$prefix [$runtime_project]"
  expected_fixture="$prefix [$project]"
  local -a warnings=()
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    lower="${line,,}"
    if [[ "$lower" =~ (^|[[:space:]:])warning([[:space:]]+[a-z]+[0-9]+[[:space:]]*:|[[:space:]]*:) ]]; then
      warnings+=("$line")
    fi
  done < "$log"
  local runtime_count=0 fixture_count=0
  for warning in "${warnings[@]}"; do
    case "$warning" in
      "$expected_runtime") ((runtime_count += 1)) ;;
      "$expected_fixture") ((fixture_count += 1)) ;;
      *) echo "APR_R4_E2_AOT_WARNING_UNEXPECTED $warning" >&2; return 1 ;;
    esac
  done
  if [[ "${#warnings[@]}" -ne 2 || "$runtime_count" -ne 1 ||
        "$fixture_count" -ne 1 ]]; then
    echo APR_R4_E2_AOT_WARNING_ALLOWLIST_INVALID >&2
    return 1
  fi
  if [[ "$announce" == yes ]]; then
    printf 'APR_R4_E2_AOT_WARNING_ALLOWLIST count=%s\n' "${#warnings[@]}"
  fi
}

verify_aot_warning_audit() {
  local root="$1" prefix good mutated case_id injected
  prefix="CSC : warning IL3058: Referenced assembly 'JsonSchema.Net' is not built with "'`true`'" and may not be compatible with AOT."
  good="$root/warning-good.log"
  printf '%s [%s]\n%s [%s]\n' "$prefix" "$runtime_project" \
    "$prefix" "$project" > "$good"
  audit_aot_warnings "$good" no >/dev/null
  local -a injections=(
    'clang: warning: synthetic native linker condition'
    'VerifierTask : warning : synthetic MSBuild task condition'
    'CSC : warning IL9999: synthetic coded warning [synthetic.csproj]'
  )
  for case_id in "${!injections[@]}"; do
    injected="$root/warning-injected-$case_id.log"
    cp "$good" "$injected"
    printf '%s\n' "${injections[case_id]}" >> "$injected"
    if audit_aot_warnings "$injected" no >/dev/null 2>&1; then
      echo "APR_R4_E2_AOT_WARNING_SELF_TEST_ACCEPTED_INJECTION_$case_id" >&2
      return 1
    fi
  done
  mutated="$root/warning-mutated.log"
  printf '%s [%s] appended-diagnostic\n%s [%s]\n' "$prefix" \
    "$runtime_project" "$prefix" "$project" > "$mutated"
  if audit_aot_warnings "$mutated" no >/dev/null 2>&1; then
    echo APR_R4_E2_AOT_WARNING_SELF_TEST_ACCEPTED_MUTATION >&2
    return 1
  fi
  mutated="$root/warning-duplicate.log"
  cp "$good" "$mutated"
  printf '%s [%s]\n' "$prefix" "$project" >> "$mutated"
  if audit_aot_warnings "$mutated" no >/dev/null 2>&1; then
    echo APR_R4_E2_AOT_WARNING_SELF_TEST_ACCEPTED_DUPLICATE >&2
    return 1
  fi
  mutated="$root/warning-missing.log"
  printf '%s [%s]\n' "$prefix" "$runtime_project" > "$mutated"
  if audit_aot_warnings "$mutated" no >/dev/null 2>&1; then
    echo APR_R4_E2_AOT_WARNING_SELF_TEST_ACCEPTED_MISSING >&2
    return 1
  fi
}

execute_action_host_proof() {
  cd "$repo_root"
  local checked_bundle_sha256 generated_bundle_sha256 intermediate
  local runtime_intermediate
  checked_bundle_sha256="$(sha256sum "$checked_bundle" | cut -d ' ' -f 1)"
  npm run build:action
  generated_bundle_sha256="$(sha256sum "$checked_bundle" | cut -d ' ' -f 1)"
  if [[ "$checked_bundle_sha256" != "$generated_bundle_sha256" ]]; then
    echo "checked action bundle is not reproducible" >&2
    return 1
  fi
  node --input-type=module - "$repo_root" "$framework_bundle" <<'NODE'
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [repoRoot, outputPath] = process.argv.slice(2);
const { writeFrameworkFixtureActionBundle } = await import(
  pathToFileURL(path.join(repoRoot, 'scripts', 'build-action.mjs')).href,
);
await writeFrameworkFixtureActionBundle(outputPath, repoRoot);
NODE
  test -f "$framework_bundle"
  local -a publish_arguments=(
    "$project" --configuration Release --runtime linux-x64
    --output "$publish_root" --artifacts-path "$artifacts_root" --nologo
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false
    -p:Deterministic=true -p:ContinuousIntegrationBuild=true
    "-p:PathMap=$temporary_root=/_/apr-action-host"
  )
  if [[ "$mode" == framework ]]; then
    dotnet publish "${publish_arguments[@]}" --self-contained false \
      -p:PublishAot=false
  else
    verify_aot_warning_audit "$temporary_root"
    if ! dotnet publish "${publish_arguments[@]}" --self-contained true \
      -p:PublishAot=true \
      -p:JsonSerializerIsReflectionEnabledByDefault=false \
      -p:ActionHostVerifierAotIntermediateDirectory="$intermediate_root" \
      -warnaserror -warnnotaserror:IL3058 > "$publish_log" 2>&1; then
      tail -n 100 "$publish_log" >&2
      return 1
    fi
    cat "$publish_log"
    audit_aot_warnings "$publish_log"
  fi
  test -x "$payload"
  test ! -e "$payload.dll"
  test ! -e "$payload.deps.json"
  test ! -e "$payload.runtimeconfig.json"
  local -a aot_arguments=()
  if [[ "$mode" == aot ]]; then
    intermediate="$intermediate_root/AgenticPrReview.Runtime.ActionHostVerifierFixture.dll"
    runtime_intermediate="$intermediate_root/AgenticPrReview.Runtime.dll"
    [[ -f "$intermediate" && -f "$runtime_intermediate" ]]
    [[ "$(find "$intermediate_root" -maxdepth 1 -type f -name '*.dll' | wc -l)" -eq 2 ]]
    local payload_sha intermediate_sha runtime_intermediate_sha pair_sha
    payload_sha="$(sha256sum "$payload" | cut -d ' ' -f 1)"
    intermediate_sha="$(sha256sum "$intermediate" | cut -d ' ' -f 1)"
    runtime_intermediate_sha="$(sha256sum "$runtime_intermediate" | cut -d ' ' -f 1)"
    pair_sha="$(printf '%s\n%s\n%s\n%s\n%s\n' \
      apr-r4-e2-action-host-build-pair-v1 native-aot "$payload_sha" \
      "$intermediate_sha" "$runtime_intermediate_sha" | sha256sum | cut -d ' ' -f 1)"
    printf '{"kind":"apr-r4-e2-action-host-build-pair-v1","execution_kind":"native-aot","payload_sha256":"%s","managed_intermediate_sha256":"%s","runtime_intermediate_sha256":"%s","build_pair_sha256":"%s"}\n' \
      "$payload_sha" "$intermediate_sha" "$runtime_intermediate_sha" \
      "$pair_sha" > "$build_pair"
    aot_arguments=(
      --execution-kind native-aot --intermediate "$intermediate"
      --runtime-intermediate "$runtime_intermediate"
      --build-pair "$build_pair" --identity-output "$identity_output"
    )
  fi
  env -i HOME="$isolated_home" TMPDIR="$isolated_tmp" \
    XDG_CONFIG_HOME="$isolated_config" GIT_CONFIG_NOSYSTEM=1 \
    GIT_CONFIG_GLOBAL=/dev/null PATH="$PATH" CI=true \
    "$payload" supervise \
    --root "$evidence_root" --repo "$repo_root" --payload "$payload" \
    --bundle "$framework_bundle" --record "$record" --inventory "$inventory" \
    --golden "$golden" --canaries "$canaries" --node "$node_path" \
    "${aot_arguments[@]}"
}

export mode repo_root project runtime_project golden record inventory canaries
export warning_policy temporary_root checked_bundle framework_bundle
export publish_root payload evidence_root isolated_home isolated_tmp isolated_config
export artifacts_root intermediate_root build_pair identity_output publish_log node_path PATH
export -f audit_aot_warnings verify_aot_warning_audit execute_action_host_proof
node "$repo_root/scripts/run-clean-source-proof.mjs" --repo "$repo_root" -- \
  bash -euo pipefail -c execute_action_host_proof | tee "$proof_log"
