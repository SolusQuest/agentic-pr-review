// Historical R7 native-policy unit fixtures from e33e1a9. Not a current producer.
import { canonicalJson } from '../../scripts/release/build-payload.format.mjs';
function assert(condition, code) {
  if (!condition) throw new Error(code);
}
export function admitToolchain(sdk, clangOutput, policy) {
  assert(
    sdk === policy.sdk &&
      policy.sdk === '10.0.109' &&
      policy.clang === '18.1.3' &&
      /\bclang version 18\.1\.3\b/.test(clangOutput),
    'toolchain_version_mismatch',
  );
}
export function admitPublishInventory(names) {
  assert(
    canonicalJson([...names].sort()) === canonicalJson(['AgenticPrReview.Runtime']),
    'unexpected_publish_inventory',
  );
}
