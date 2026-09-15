# R5 explicit reset after capacity exhaustion

S2 reuses the admitted S1 `../growth` corpus. `ResetWorkload` derives its tool-heavy script, substitutes the actual synthetic snapshot path, and inserts separate old/fresh reasoning markers. Eight real `read_file` calls per accepted run grow one production Host history until the next response exceeds `AgentLimits.Messages`. The clock and production limits remain fixed. The accepted predecessor is read back after rejection, explicitly reset, accepted at generation zero, and continued in a separate Host invocation.

Run from the repository root on Linux:

```sh
dotnet test runtime/tests/AgenticPrReview.Runtime.Tests/AgenticPrReview.Runtime.Tests.csproj --configuration Release --nologo --filter 'FullyQualifiedName~R5CapacityResetTests|FullyQualifiedName~R5ResetHandoffTests' --logger 'console;verbosity=detailed'
```

The passing connected lifecycle emits `r5-reset-capacity-v1`: source commit/tree/clean status, the admitted seed corpus digest, actual adapted script digests, actual policy/limits/toolset digests, bounded per-invocation state/provider/publication observations, the measured capacity phase, and verified reset/content/continuation assertions. `publicationWrites` is cumulative committed writes; a null `modelCalls` means no diagnostic count was supplied. Failed assertions fail the test and do not emit a passing lifecycle report. Capture the test runner's exit status alongside the JSON. This is a local framework lifecycle report, not a scored Q1 outcome, a token-cost measurement, or hosted GitHub evidence. No session IDs, artifact references, comment URLs, raw model requests or decrypted SESSION bytes enter the report.

The source commit binds the synthetic port setup and snapshot content; the script digest covers the adapted bytes actually supplied to the replay transport. The seed corpus digest alone does not identify this adapted workload. Dirty-source output is iteration evidence only. Rebuild at the exact clean commit for review evidence.

The independent executable owner probe uses the same synthetic HTTP/artifact ports, with actual Host authorization, reset codecs, Agent execution, P5 recovery, P6 sticky publication and acceptance. It exercises target carry, completed reset reentry, successor continuation, final target substitution rejection, ordinary recreation of a missing predecessor comment, and rejection when a comment appears after that absence proof. The ordinary absence cases exercise both the initial write and the known-not-written retry:

```sh
dotnet run --project runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release -- reset --fixture self-test
dotnet publish runtime/tests/ReviewEvaluationFixture/AgenticPrReview.Runtime.ReviewEvaluationFixture.csproj --configuration Release --runtime linux-x64 --self-contained true -p:PublishAot=true -o /tmp/apr-reset-native
/tmp/apr-reset-native/AgenticPrReview.Runtime.ReviewEvaluationFixture reset --fixture self-test
```

Both emit `r5-reset-owner-v1`; record the launch command to distinguish framework from actual Native AOT execution. The native probe covers those eight owner cases, not the complete capacity/fault matrix. All inputs are synthetic and no real GitHub mutation or provider call occurs.

`R5ResetHandoffTests` covers source-inventory races, unresolved publication admission, intent/successor upload and deletion outcomes, target substitution/absence/expiry, exact recovery after source expiry, tampering and old-epoch replay. Existing `LineageServiceLifecycleTests.UnauthorizedResetDoesNotMutateState` retains wrong-run and wrong-route capability controls; the normal production factory creates every positive reset capability in S2. The [D08–D10 supplement](../../../../../../docs/20_architecture/r4-reset-publication-handoff.md) defines the publication-only handoff and its authority limits.
