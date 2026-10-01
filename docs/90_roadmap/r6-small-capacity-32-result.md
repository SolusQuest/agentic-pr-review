# Reduced32 capacity observation E

This immutable independent experiment executed source616a0a8cee9033b92956d519b8b232a577507dd1 with message cap32 (production64). It stopped with representative_history_insufficient before the selected growth/reset stages:16scheduled,2attempted,1accepted,1restored,0reset,14unattempted. Both Agent invocations succeeded; the report outcomes retain one completed evaluation and one failed evaluation because the second SESSION build failed with session_construction_limit, so the failed build was not accepted.

The bootstrap used6sends/9tools and ended at17response messages. The restored replay used4sends/9tools and ended at32response messages with29820continuation bytes. The builder checks both construction bounds and capacity for a reconstructible NEXT request, so Agent success at the current message bound does not imply a persistable successor. The bounded diagnostic does not expose which internal builder check rejected; no exact internal trigger is asserted.

All10sends have known usage:48754input (43648cache-read,5106uncached),7031output. Fixed-reference cost USD0.010230888, not an actual bill. The full USD12.80/4960second reservation is retained. Capture completed, workers exited, privateTEMPwas empty/deleted, and the key was absent from outputs.

No reset or complete experimental path is claimed. The predeclared runner only permits reset after a capacity stop in its later growth chain, so it correctly stopped instead of relocating reset in this population. A subsequent plan may use a larger but still reduced message ceiling to leave room for the replay entry, and recognize capacity after an accepted predecessor plus successful restoration rather than demand an arbitrary number of successful growth appends. It must have a new source/build/plan/authorization binding and preserve this failed population unchanged.

Artifacts: [full report](../../runtime/tests/fixtures/agent/r6/plans/r6-v6-observation/small-capacity-32-2026-10-01/live-report.json), [audit](../../runtime/tests/fixtures/agent/r6/plans/r6-v6-observation/small-capacity-32-2026-10-01/live-audit.json). This experimental branch must not be merged or distributed.
