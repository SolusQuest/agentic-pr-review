# S5 native continuation and writer test retirement

Status: historical implementation-step record. Its counts and source coordinates describe that intermediate revision. The final source closure and current generic owners are now verified together by [`assertion-dispositions.mjs`](../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs) and its existing successor/migration ledgers. Original assertion and disposition JSON records remain immutable; intermediate source-checker functions have been consolidated. Source identity never qualifies installed SDK execution.

Six tests of the retiring native SESSION writer and synthetic continuation codec
have explicit per-assertion dispositions. This batch removes six declarations
from the remaining 48-method round-trip suite and preserves its other 42 test
methods and all helper declarations. It changes no production limit or runtime
acceptance rule.

The [closed batch record](../../runtime/tests/SolusEvaluationFixture/s5-native-codec-retirements.json)
binds the original and retirement-input declarations, attributes, all 43 assertion
expressions, exact remaining source, retained generic owners and separate current
Solus proof owners. The [verifier](../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs)
rejects substituted assertions, declarations, source bodies and owner sources.

| Retired method                                               | Retired responsibility                                                                           |
| ------------------------------------------------------------ | ------------------------------------------------------------------------------------------------ |
| `CanonicalWritersEnforceExactRecordAndSessionCaps`           | Native record and whole-SESSION writer byte caps, including intentionally overlong workflow text |
| `MalformedDeclaredContinuationSlotUsesContinuationCode`      | Missing native serialized slot identifier and its native parser failure code                     |
| `ClassificationAndContinuationMutationsFailWithStableCodes`  | Native review-context classification and continuation payload-digest mutation codes              |
| `SyntheticContinuationVariantsRoundTripValueExactly`         | Test-only native readable, opaque and structured continuation reconstruction                     |
| `SyntheticStructuredCodecPreservesSignatureAndOrderedFields` | Test-only synthetic signature, ordered JSON fields, finite field bounds and encoding grammar     |
| `ContinuationItemAndAggregateCapsAreExact`                   | Native builder enforcement of decoded continuation-item and aggregate caps                       |

The same numeric constants can still govern generic Chat, Loop, Canonical and
request-bound checks. Those paths, their limits and actual boundary tests remain.
This batch retires only the listed native writer, builder and synthetic codec
entrypoints. Current authenticated-envelope tamper refusal, absence of fallback
and actual opaque SDK capacity retain their independent source-bound proofs; no
native field, byte count, item count or synthetic signature is a current SDK
format or capacity guarantee.

The earlier [five-method grammar record](../../runtime/tests/SolusEvaluationFixture/s5-native-grammar-dispositions.json)
keeps its original five dispositions. Its 53-to-48 result describes that earlier batch; this separate
record accounts for the subsequent 48-to-42 change. Its source verifier admits
the later source only through this exact batch's independently pinned input and
output identities. The original V1 152-row mapping and original five-format
record remain unchanged. All six declarations are outside V1.

Other native SESSION consumers remain pending their own dispositions. Neither
this source audit nor a passing native component suite qualifies SDK execution
or completes production SESSION retirement.
