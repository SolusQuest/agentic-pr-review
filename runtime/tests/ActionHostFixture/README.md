# New R7-D0 executable fixture

This directory is new coverage for #341. `verify-entrypoint.mjs` launches the actual `AgenticPrReview.Runtime` executable with zero arguments, matching the existing private Node launcher. It loads no linked Host library or fake provider into that child. It exercises the BE32 framed launch, strict malformed-input rejection, production-composition credential-free denial, requested cancellation, safe current completion/accounting/exit parity, and Linux SIGTERM/SIGINT while framing is stalled. Synthetic private canaries must remain absent from both child streams. Native execution has neither `PATH` nor `DOTNET_ROOT` in its closed environment and requires no consumer SDK.

The existing Runtime CI gate now runs these checks after the retained deterministic bootstrap smoke:

```bash
bash runtime/scripts/verify-runtime.sh framework
bash runtime/scripts/verify-runtime.sh aot
```

The new supervisor commands can also inspect already built outputs:

```bash
node runtime/tests/ActionHostFixture/verify-entrypoint.mjs framework /absolute/path/to/dotnet /absolute/path/to/AgenticPrReview.Runtime.dll
node runtime/tests/ActionHostFixture/verify-entrypoint.mjs native /absolute/path/to/AgenticPrReview.Runtime
```

The runtime's zero-argument route accepts exactly one nonempty BE32-prefixed current `r4-w2` launch document plus EOF. It emits one current validated completion and returns its `process_exit_code`. Invalid framing/launch returns exit 1, no completion and fixed `APR_ACTION_HOST_INPUT_INVALID`; cancellation before launch admission similarly uses `APR_ACTION_HOST_CANCELLED`. Escaped initialization, Host, serialization or output failure uses `APR_ACTION_HOST_INTERNAL`, exit 1 and no invented replacement completion. A failed write/flush can leave a prefix or an already written frame; the failing process outcome remains authoritative. Ordinary caller cancellation does not suppress an already admitted Host completion. The existing composition exclusively owns its Host-entry deadline, state horizon, reconciliation and cleanup; the existing wrapper owns bounded drain and kill escalation. Hard kill can leave an unknown outcome. Windows signal cases are explicitly skipped because Windows is development-only.

`ActionHostEntrypointTests` and `ActionHostCompositionTests.Entrypoint.cs` are explicitly **linked-library** stream/Host tests. Their synthetic outer ports and provider cover successful completion mapping, post-provider accounting retention, cancellation, output failures and cleanup. They do not prove a successful full review by the actual executable. V2/#359 separately owns complete same-binary path qualification. This fixture creates no public fake-provider/endpoint/credential selector, proof-payload dispatch, compatibility reader, package, release or provider-call authority.
