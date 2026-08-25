#!/usr/bin/env bash
set -euo pipefail

if [[ "$#" -ne 0 ]]; then
  printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=usage status=failed\n' >&2
  exit 2
fi
if [[ "$(uname -s)" != Linux ]] ||
  grep -Eqi '(microsoft|wsl)' /proc/sys/kernel/osrelease 2>/dev/null; then
  printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=native-linux-required status=failed\n' >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)"
payload_source_commit="$(git -C "$repo_root" rev-parse HEAD)"
payload_source_tree="$(git -C "$repo_root" rev-parse 'HEAD^{tree}')"
[[ "$payload_source_commit" =~ ^[0-9a-f]{40}$ && "$payload_source_tree" =~ ^[0-9a-f]{40}$ ]] || exit 1
project="$repo_root/runtime/tests/ActionHostTrustedProofPayload/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.csproj"
verifier_project="$repo_root/runtime/tests/ActionHostTrustedProofVerifier/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.csproj"
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
    printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=ambient-credential status=failed\n' >&2
    exit 1
  fi
done
if git -C "$repo_root" config --local --get-regexp \
    '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1 ||
  git config --global --get-regexp \
    '^(http\..*\.extraheader|credential\..*\.helper)$' >/dev/null 2>&1; then
  printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=ambient-git-config status=failed\n' >&2
  exit 1
fi
node_version="$(node --version 2>/dev/null || true)"
[[ "$node_version" =~ ^v24\. ]] || {
  printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=node-version status=failed\n' >&2
  exit 1
}
dotnet_version="$(dotnet --version 2>/dev/null || true)"
[[ "$dotnet_version" == 10.0.109 ]] || {
  printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=dotnet-version status=failed\n' >&2
  exit 1
}

publish_root="$temporary_root/payload"
payload="$publish_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload"
verifier_publish_root="$temporary_root/verifier"
verifier="$verifier_publish_root/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier"
artifacts_root="$temporary_root/artifacts"
intermediate_root="$temporary_root/managed"
verifier_intermediate_root="$temporary_root/verifier-managed"
architecture="$temporary_root/managed-architecture.json"
verifier_architecture="$temporary_root/verifier-managed-architecture.json"
identity="$temporary_root/identity.json"
publish_log="$temporary_root/publish.log"
verifier_publish_log="$temporary_root/verifier-publish.log"
proof_log="$temporary_root/clean-source.log"
proof_evidence="$temporary_root/proof-evidence"
receipt_line="$temporary_root/receipt.line"
receipt_json="$temporary_root/receipt.json"

audit_warnings() {
  local log="$1" policy="$2" expected_count="$3" label="$4" prefix line normalized
  prefix="CSC : warning IL3058: Referenced assembly 'JsonSchema.Net' is not built with "'`true`'" and may not be compatible with AOT."
  local -a actual=()
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    if [[ "${line,,}" =~ (^|[[:space:]:])warning([[:space:]]+[a-z]+[0-9]+[[:space:]]*:|[[:space:]]*:) ]]; then
      normalized="${line//"$repo_root/"/}"
      actual+=("$normalized")
    fi
  done < "$log"
  mapfile -t expected < "$policy"
  [[ "${#actual[@]}" -eq "$expected_count" &&
      "${#expected[@]}" -eq "$expected_count" ]] || return 1
  mapfile -t actual < <(printf '%s\n' "${actual[@]}" | sort)
  mapfile -t expected < <(printf '%s\n' "${expected[@]}" | sort)
  local index
  for ((index = 0; index < expected_count; index++)); do
    [[ "${actual[$index]}" == "${expected[$index]}" ]] || return 1
  done
  printf 'APR_R4_E2P_AOT_WARNING_ALLOWLIST target=%s count=%s\n' \
    "$label" "$expected_count"
}

write_smoke_frame() {
  local mode="$1" output="$2"
  node --input-type=module - "$mode" \
    "$repo_root/runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/launch-js-safe-boundary.json" \
    "$temporary_root/smoke-event.json" "$output" <<'NODE'
import fs from 'node:fs';
const [mode, fixture, eventPath, output] = process.argv.slice(2);
let bytes;
if (mode === 'zero') bytes = Buffer.alloc(4);
else if (mode === 'malformed') bytes = Buffer.from([0, 0, 0, 1, 0x78]);
else if (mode === 'oversized') bytes = Buffer.from([0xff, 0xff, 0xff, 0xff]);
else {
  const value = JSON.parse(fs.readFileSync(fixture, 'utf8'));
  value.event_json_path = eventPath;
  value.artifact_bridge_endpoint = '/tmp/apr-r4-e2p-smoke.sock';
  value.repository_id = '42';
  value.run_id = '900';
  value.inputs.pr_number = '147';
  value.build_discriminator = mode === 'wrong-discriminator' ? 'r4-w2-wrong' : 'r4-w2';
  const document = Buffer.from(JSON.stringify(value), 'utf8');
  const header = Buffer.alloc(4);
  header.writeUInt32BE(document.length);
  bytes = Buffer.concat([header, document, ...(mode === 'trailing' ? [Buffer.from('x')] : [])]);
}
fs.writeFileSync(output, bytes, { flag: 'wx' });
NODE
}

run_production_smoke() {
  command -v strace >/dev/null || return 1
  printf '{}\n' > "$temporary_root/smoke-event.json"
  local mode frame stdout stderr trace status
  for mode in zero malformed oversized trailing wrong-discriminator missing-github; do
    frame="$temporary_root/smoke-$mode.frame"
    stdout="$temporary_root/smoke-$mode.stdout"
    stderr="$temporary_root/smoke-$mode.stderr"
    trace="$temporary_root/smoke-$mode.network"
    write_smoke_frame "$mode" "$frame"
    set +e
    strace -qq -f -e trace=network -o "$trace" \
      "$payload" < "$frame" > "$stdout" 2> "$stderr"
    status=$?
    set -e
    [[ "$status" -eq 1 && ! -s "$stdout" ]] || return 1
    if [[ "$mode" == missing-github ]]; then
      [[ "$(cat "$stderr")" == \
        'APR_R4_E2P_UNHANDLED InvalidOperationException' ]] || return 1
    else
      [[ ! -s "$stderr" ]] || return 1
    fi
    [[ "$(wc -c < "$stdout")" -le 128 &&
        "$(wc -c < "$stderr")" -le 128 ]] || return 1
    if grep -Eq '(^|[[:space:]])(socket|socketpair|connect|accept|accept4|bind|listen|sendto|sendmsg|recvfrom|recvmsg|getsockname|getpeername|setsockopt|getsockopt)\(' "$trace"; then
      return 1
    fi
  done
  printf 'APR_R4_E2P_PRODUCTION_SMOKE result=passed cases=6 network_syscalls=0\n'
}

emit_safe_supervisor_failure() {
  local evidence="$1"
  if [[ ! -f "$evidence" ]]; then
    printf 'APR_R4_E2P_V2_FAILURE stage=synthetic-route case=supervisor code=no-evidence status=failed\n' >&2
    return
  fi
  node --input-type=module - "$evidence" <<'NODE' >&2
import fs from 'node:fs';
const allowedCases = new Set(['dispatch-bootstrap', 'dispatch-continuation', 'stale-head']);
const allowedStatuses = new Set([
  'reviewed', 'reviewed_with_inline_warnings', 'stale_head',
  'authorization_failed', 'credentials_missing', 'agent_result_invalid',
  'provider_failed', 'null',
]);
let value;
try { value = JSON.parse(fs.readFileSync(process.argv[2], 'utf8')); } catch {}
const failed = Array.isArray(value?.cases) ? value.cases.filter((item) => item?.Passed !== true) : [];
if (failed.length === 0) {
  process.stderr.write('APR_R4_E2P_V2_FAILURE stage=synthetic-route case=supervisor code=invalid-evidence status=failed\n');
} else {
  for (const item of failed.slice(0, 3)) {
    const id = allowedCases.has(item?.Name) ? item.Name : 'unknown';
    const raw = item?.ActualStatus === null ? 'null' : item?.ActualStatus;
    const status = allowedStatuses.has(raw) ? raw : 'unknown';
    process.stderr.write(`APR_R4_E2P_V2_FAILURE stage=synthetic-route case=${id} code=case-failed status=${status}\n`);
  }
}
NODE
}

execute_proof() {
  cd "$repo_root"
  dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj \
    --configuration Release --nologo --filter FullyQualifiedName~TrustedProof -m:1 \
    "-p:PayloadSourceCommit=$payload_source_commit" \
    "-p:PayloadSourceTree=$payload_source_tree"
  if ! dotnet publish "$project" --configuration Release --runtime linux-x64 \
    --self-contained true --output "$publish_root" --artifacts-path "$artifacts_root" \
    --nologo -p:PublishAot=true -p:PublishSingleFile=true \
    -p:JsonSerializerIsReflectionEnabledByDefault=false \
    -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true \
    -p:ContinuousIntegrationBuild=true \
    "-p:PayloadSourceCommit=$payload_source_commit" \
    "-p:PayloadSourceTree=$payload_source_tree" \
    "-p:PathMap=$repo_root=/_/apr-r4-e2p%2C$artifacts_root=/_/apr-r4-e2p-artifacts" \
    -warnaserror -warnnotaserror:IL3058 > "$publish_log" 2>&1; then
    return 1
  fi
  audit_warnings "$publish_log" "$fixture_root/aot/warning-policy.txt" 2 production || {
    echo APR_R4_E2P_AOT_WARNING_ALLOWLIST_INVALID >&2
    return 1
  }
  [[ -x "$payload" && ! -e "$payload.dll" && ! -e "$payload.deps.json" ]] ||
    return 1
  run_production_smoke || {
    echo 'APR_R4_E2P_V2_FAILURE stage=production-smoke case=aggregate code=smoke-failed status=failed' >&2
    return 1
  }
  proof_build_output="$artifacts_root/bin/AgenticPrReview.Runtime.ActionHostTrustedProofPayload/release_linux-x64"
  proof_intermediate="$intermediate_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll"
  runtime_intermediate="$intermediate_root/AgenticPrReview.Runtime.dll"
  [[ -f "$proof_build_output/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll" &&
      -f "$proof_build_output/AgenticPrReview.Runtime.dll" ]] || return 1
  mkdir "$intermediate_root"
  cp "$proof_build_output/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll" \
    "$proof_intermediate"
  cp "$proof_build_output/AgenticPrReview.Runtime.dll" "$runtime_intermediate"
  [[ "$(find "$intermediate_root" -maxdepth 1 -type f -name '*.dll' | wc -l)" -eq 2 ]] ||
    return 1
  if ! dotnet publish "$verifier_project" --configuration Release \
    --runtime linux-x64 --self-contained true --output "$verifier_publish_root" \
    --artifacts-path "$artifacts_root" --nologo \
    -p:PublishAot=true -p:PublishSingleFile=true \
    -p:JsonSerializerIsReflectionEnabledByDefault=false \
    -p:DebugType=None -p:DebugSymbols=false -p:Deterministic=true \
    -p:ContinuousIntegrationBuild=true \
    "-p:PayloadSourceCommit=$payload_source_commit" \
    "-p:PayloadSourceTree=$payload_source_tree" \
    "-p:PathMap=$repo_root=/_/apr-r4-e2p%2C$artifacts_root=/_/apr-r4-e2p-artifacts" \
    -warnaserror -warnnotaserror:IL3058 > "$verifier_publish_log" 2>&1; then
    return 1
  fi
  audit_warnings "$verifier_publish_log" \
    "$fixture_root/aot/verifier-warning-policy-v2.txt" 2 verifier || {
    echo 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=warning-policy status=failed' >&2
    return 1
  }
  [[ -x "$verifier" && ! -e "$verifier.dll" &&
      ! -e "$verifier.deps.json" ]] || return 1
  verifier_build_output="$artifacts_root/bin/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier/release_linux-x64"
  verifier_intermediate="$verifier_intermediate_root/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll"
  verifier_proof_intermediate="$verifier_intermediate_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll"
  verifier_runtime_intermediate="$verifier_intermediate_root/AgenticPrReview.Runtime.dll"
  [[ -f "$verifier_build_output/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll" &&
      -f "$verifier_build_output/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll" &&
      -f "$verifier_build_output/AgenticPrReview.Runtime.dll" ]] || return 1
  mkdir "$verifier_intermediate_root"
  cp "$verifier_build_output/AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll" \
    "$verifier_intermediate"
  cp "$verifier_build_output/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.dll" \
    "$verifier_proof_intermediate"
  cp "$verifier_build_output/AgenticPrReview.Runtime.dll" \
    "$verifier_runtime_intermediate"
  [[ "$(find "$verifier_intermediate_root" -maxdepth 1 -type f -name '*.dll' | wc -l)" -eq 3 ]] || return 1
  node scripts/check-r4-e2p-managed-architecture.mjs \
    --proof "$proof_intermediate" --runtime "$runtime_intermediate" \
    --verifier "$verifier_intermediate" \
    --verifier-proof "$verifier_proof_intermediate" \
    --verifier-runtime "$verifier_runtime_intermediate" \
    --proof-output "$architecture" \
    --verifier-output "$verifier_architecture"
  if printf '\000\000\000\001x' | "$payload" >/dev/null 2>&1; then
    echo APR_R4_E2P_AOT_MALFORMED_FRAME_ACCEPTED >&2
    return 1
  fi
  dotnet build "$supervisor_project" --configuration Release --nologo \
    -p:PublishAot=false
  mkdir -p "$proof_evidence"
  verifier_sha="$(sha256sum "$verifier" | cut -d ' ' -f 1)"
  production_payload_sha="$(sha256sum "$payload" | cut -d ' ' -f 1)"
  set +e
  dotnet "$supervisor" supervise \
    --root "$proof_evidence" --repo "$repo_root" --payload "$verifier" \
    --bundle "$repo_root/.github/actions/agentic-pr-review/dist/index.js" \
    --record "$framework_fixture_root/replacement-record.json" \
    --inventory "$framework_fixture_root/e1-base-inventory.json" \
    --golden "$framework_fixture_root/expected-evidence.json.golden" \
    --canaries "$framework_fixture_root/canary-routes.tsv" \
    --node "$(command -v node)" --trusted-proof-only true \
    --payload-source-commit "$payload_source_commit" \
    --payload-source-tree "$payload_source_tree" \
    --production-payload-sha256 "$production_payload_sha" \
    --verifier-sha256 "$verifier_sha" \
    > "$temporary_root/supervisor.stdout" \
    2> "$temporary_root/supervisor.stderr"
  supervisor_status=$?
  set -e
  if [[ "$supervisor_status" -ne 0 ]]; then
    emit_safe_supervisor_failure \
      "$proof_evidence/trusted-proof-payload-evidence.json"
    return "$supervisor_status"
  fi
  [[ -f "$proof_evidence/trusted-proof-payload-evidence.json" ]] || return 1
  normalized_evidence="$temporary_root/trusted-proof-payload-evidence.normalized.json"
  node --input-type=module - \
    "$proof_evidence/trusted-proof-payload-evidence.json" \
    "$normalized_evidence" <<'NODE'
import fs from 'node:fs';
const input = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const expectedCases = ['dispatch-bootstrap', 'dispatch-continuation', 'stale-head'];
if (input?.passed !== true || !Array.isArray(input.cases) ||
    input.cases.length !== expectedCases.length ||
    typeof input.payload_sha256 !== 'string' ||
    !/^[0-9a-f]{64}$/.test(input.payload_sha256) ||
    input.compiled_payload_proof_kind !==
      'apr-r4-e2p-trusted-proof-payload-v2' ||
    !/^[0-9a-f]{40}$/.test(input.compiled_payload_source_commit ?? '') ||
    !/^[0-9a-f]{40}$/.test(input.compiled_payload_source_tree ?? '') ||
    input.transaction_partition?.kind !==
      'apr-r4-e4-synthetic-transaction-partition-v1') {
  throw new Error('The trusted-proof evidence envelope is invalid.');
}
const cases = input.cases.map((item, index) => {
  if (item?.Name !== expectedCases[index] || item.Passed !== true ||
      !Number.isSafeInteger(item.HostPid) || item.HostPid <= 0) {
    throw new Error('The trusted-proof case evidence is invalid.');
  }
  const { HostPid: _hostPid, ...stable } = item;
  return stable;
});
fs.writeFileSync(process.argv[3], JSON.stringify({
  passed: input.passed,
  payload_sha256: input.payload_sha256,
  compiled_payload_proof_kind: input.compiled_payload_proof_kind,
  compiled_payload_source_commit: input.compiled_payload_source_commit,
  compiled_payload_source_tree: input.compiled_payload_source_tree,
  cases,
  transaction_partition: input.transaction_partition,
}) + '\n');
NODE
  payload_sha="$(sha256sum "$payload" | cut -d ' ' -f 1)"
  proof_sha="$(sha256sum "$proof_intermediate" | cut -d ' ' -f 1)"
  runtime_sha="$(sha256sum "$runtime_intermediate" | cut -d ' ' -f 1)"
  architecture_sha="$(sha256sum "$architecture" | cut -d ' ' -f 1)"
  verifier_sha="$(sha256sum "$verifier" | cut -d ' ' -f 1)"
  verifier_managed_sha="$(sha256sum "$verifier_intermediate" | cut -d ' ' -f 1)"
  verifier_proof_sha="$(sha256sum "$verifier_proof_intermediate" | cut -d ' ' -f 1)"
  verifier_runtime_sha="$(sha256sum "$verifier_runtime_intermediate" | cut -d ' ' -f 1)"
  verifier_architecture_sha="$(sha256sum "$verifier_architecture" | cut -d ' ' -f 1)"
  verifier_evidence_sha="$(sha256sum "$normalized_evidence" | cut -d ' ' -f 1)"
  pair_sha="$(printf '%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n%s\n' \
    apr-r4-e2p-build-pair-v2 "$payload_sha" "$proof_sha" "$runtime_sha" \
    "$architecture_sha" "$verifier_sha" "$verifier_managed_sha" \
    "$verifier_proof_sha" "$verifier_runtime_sha" \
    "$verifier_architecture_sha" "$verifier_evidence_sha" | \
    sha256sum | cut -d ' ' -f 1)"
  source_commit="$(git rev-parse HEAD)"
  source_tree="$(git rev-parse 'HEAD^{tree}')"
  node --input-type=module - "$normalized_evidence" "$identity" \
    "$source_commit" "$source_tree" "$payload_sha" "$proof_sha" \
    "$runtime_sha" "$architecture_sha" "$verifier_sha" \
    "$verifier_managed_sha" "$verifier_proof_sha" \
    "$verifier_runtime_sha" "$verifier_architecture_sha" \
    "$verifier_evidence_sha" "$pair_sha" <<'NODE'
import fs from 'node:fs';
const [evidencePath, outputPath, sourceCommit, sourceTree, payloadSha,
  proofSha, runtimeSha, architectureSha, verifierSha, verifierManagedSha,
  verifierProofSha, verifierRuntimeSha, verifierArchitectureSha,
  verifierEvidenceSha, pairSha] = process.argv.slice(2);
const evidence = JSON.parse(fs.readFileSync(evidencePath, 'utf8'));
if (evidence.compiled_payload_proof_kind !==
      'apr-r4-e2p-trusted-proof-payload-v2' ||
    evidence.compiled_payload_source_commit !== sourceCommit ||
    evidence.compiled_payload_source_tree !== sourceTree) {
  throw new Error('The compiled payload identity does not match the verified source.');
}
fs.writeFileSync(outputPath, JSON.stringify({
  source_commit: sourceCommit,
  source_tree: sourceTree,
  compiled_payload_source_commit: evidence.compiled_payload_source_commit,
  compiled_payload_source_tree: evidence.compiled_payload_source_tree,
  compiled_payload_proof_kind: evidence.compiled_payload_proof_kind,
  payload_sha256: payloadSha,
  proof_managed_intermediate_sha256: proofSha,
  runtime_managed_intermediate_sha256: runtimeSha,
  managed_architecture_sha256: architectureSha,
  verifier_sha256: verifierSha,
  verifier_managed_intermediate_sha256: verifierManagedSha,
  verifier_payload_managed_intermediate_sha256: verifierProofSha,
  verifier_runtime_managed_intermediate_sha256: verifierRuntimeSha,
  verifier_managed_architecture_sha256: verifierArchitectureSha,
  verifier_evidence_sha256: verifierEvidenceSha,
  build_pair_sha256: pairSha,
  transaction_partition: evidence.transaction_partition,
}) + '\n');
NODE
  node scripts/compose-r4-e2p-receipt-v2.mjs \
    --identity "$identity" --contract "$fixture_root/aot/receipt-contract-v2.json" \
    --predecessor "$fixture_root/predecessor-anchor.json" \
    --action .github/actions/agentic-pr-review/action.yml \
    --bundle .github/actions/agentic-pr-review/dist/index.js \
    --workflow "$fixture_root/workflow/r4-trusted-proof-v2.yml.template" \
    --preflight-contract "$fixture_root/workflow/preflight-contract-v2.json" \
    --provider-contract "$fixture_root/deterministic-provider-contract.json" \
    --control-contract "$fixture_root/proof-control-contract.json" \
    --stale-contract "$fixture_root/stale-window-contract.json" \
    --trusted-config .github/agentic-pr-review/trusted-proof.json \
    --trusted-instructions .github/agentic-pr-review/trusted-proof-instructions.md \
    --preparation-contract "$fixture_root/preparation-contract-v2.json" \
    --preparation-script runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh \
    --warning-policy "$fixture_root/aot/warning-policy.txt" \
    --verifier-warning-policy "$fixture_root/aot/verifier-warning-policy-v2.txt" \
    > "$receipt_line"
  sed 's/^APR_R4_E2P_RECEIPT_V2 //' "$receipt_line" > "$receipt_json"
  verify_two_root_preparation
  cat "$receipt_line"
}

verify_two_root_preparation() {
  local source_copy="$temporary_root/source-copy"
  local control_root="$temporary_root/control-root"
  local runner_temp="$temporary_root/preparation-runner"
  local prepared_root="$runner_temp/prepared"
  local github_output="$temporary_root/github-output"
  local control_receipt="$control_root/runtime/tests/fixtures/action-host/trusted-proof/trusted-proof-payload-receipt-v2.json"
  local source_commit
  source_commit="$(git -C "$repo_root" rev-parse HEAD)"
  git clone --quiet --no-checkout --no-local --no-hardlinks \
    "$repo_root" "$source_copy"
  git -C "$source_copy" checkout --quiet --detach "$source_commit"
  mkdir -p "$(dirname "$control_receipt")" "$control_root/scripts" "$runner_temp"
  cp "$receipt_json" "$control_receipt"
  cp "$repo_root/scripts/check-r4-e2p-receipt-v2.mjs" \
    "$control_root/scripts/check-r4-e2p-receipt-v2.mjs"
  : > "$github_output"

  local unchanged="APR_R4_E2P_OUTPUT_SENTINEL"
  printf '%s\n' "$unchanged" > "$github_output"
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
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
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
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
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
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
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" --output-root "$prepared_root" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_DIRTY_SOURCE_ACCEPTED >&2
    return 1
  fi
  rm -f -- "$source_copy/apr-r4-e2p-dirty"
  [[ "$(cat "$github_output")" == "$unchanged" && ! -e "$prepared_root" ]] ||
    return 1

  cp "$source_copy/scripts/check-r4-e2p-receipt-v2.mjs" \
    "$source_copy/scripts/check-r4-e2p-receipt-v2.mjs.original"
  printf '%s\n' 'process.stdout.write("forged-local-checker\\n");' > \
    "$source_copy/scripts/check-r4-e2p-receipt-v2.mjs"
  if RUNNER_TEMP="$runner_temp" bash \
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
      --source-root "$source_copy" --control-root "$control_root" \
      --receipt "$control_receipt" --output-root "$prepared_root" \
      --github-output "$github_output" >/dev/null 2>&1; then
    echo APR_R4_E2P_PREPARATION_LOCAL_CHECKER_REPLACEMENT_ACCEPTED >&2
    return 1
  fi
  mv "$source_copy/scripts/check-r4-e2p-receipt-v2.mjs.original" \
    "$source_copy/scripts/check-r4-e2p-receipt-v2.mjs"
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
      "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
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
    "$source_copy/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh" \
    --source-root "$source_copy" --control-root "$control_root" \
    --receipt "$control_receipt" --output-root "$prepared_root" \
    --github-output "$github_output"
  [[ "$(wc -l < "$github_output")" -eq 5 ]] || return 1
  grep -Fx "prepared_root=$prepared_root" "$github_output" >/dev/null
  grep -Fx 'prepared_executable=AgenticPrReview.Runtime.ActionHostTrustedProofPayload' \
    "$github_output" >/dev/null
  grep -Fx "prepared_payload_sha256=$(sha256sum "$prepared_root/AgenticPrReview.Runtime.ActionHostTrustedProofPayload" | cut -d ' ' -f 1)" \
    "$github_output" >/dev/null
  grep -Fx 'action_source_sha=5b5769753653bb3fd3e68cf8b7bb88a1bd350613' \
    "$github_output" >/dev/null
  ! grep -q '^payload_source_sha=' "$github_output"
  grep -Fx 'payload_build_discriminator=r4-w2' "$github_output" >/dev/null
}

export repo_root project verifier_project supervisor_project supervisor runtime_project fixture_root
export framework_fixture_root temporary_root publish_root payload verifier_publish_root verifier
export artifacts_root intermediate_root verifier_intermediate_root
export architecture verifier_architecture identity publish_log verifier_publish_log
export proof_evidence receipt_line receipt_json
export payload_source_commit payload_source_tree
export -f audit_warnings write_smoke_frame run_production_smoke
export -f emit_safe_supervisor_failure verify_two_root_preparation execute_proof
set +e
node "$repo_root/scripts/run-clean-source-proof.mjs" --repo "$repo_root" -- \
  bash -euo pipefail -c execute_proof > "$proof_log" 2>&1
proof_status=$?
set -e
if [[ "$proof_status" -ne 0 ]]; then
  node "$repo_root/scripts/project-r4-e2p-diagnostics.mjs" "$proof_log" >&2
  exit "$proof_status"
fi
if [[ ! -f "$receipt_line" || "$(wc -l < "$receipt_line")" -ne 1 ||
    "$(wc -c < "$receipt_line")" -gt 32768 ]] ||
  ! grep -Eq '^APR_R4_E2P_RECEIPT_V2 \{.*\}$' "$receipt_line"; then
  printf 'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source code=child-failed status=failed\n' >&2
  exit 1
fi
cat "$receipt_line"
