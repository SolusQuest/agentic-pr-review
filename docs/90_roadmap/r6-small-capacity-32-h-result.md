# Reduced-capacity H32 live result

Issue #296. Independent population c2-11663a96ec03458ea930 completed the direct-growth capacity/reset experiment with the real DeepSeek provider. The isolated runtime uses 32 messages; production remains 64. **Do not merge or distribute this experimental runtime.**

Execution source: a72339860c1b4e104e3e2cb4bb7acaaceb1d62d3; tree: dff263b164b22ae7eb4275a0da6527e178a2688d; native build: 8a6eba136c61e9e3a137526dc4c6c1cde57e0ca2d664316fd98a292e739073fc. The exact head passed 69 selected offline tests, including all 18 experimental cases and 12 hostile-receipt cases. Three original fixed-history record cases remain excluded under the reduced limit. Earlier failures are preserved separately.

Of 14 reserved stages, 7 ran and 6 were accepted. Capacity was observed at index 4: agent_failed / agent_response_invalid, with 33 response messages and 2671 continuation bytes. The capacity worker had restored an accepted predecessor; the parent independently read that predecessor back after rejection. It then reset local restricted state, accepted a fresh session at index 12, and independently restored and accepted its successor at index 13. The 7 unused growth slots remain unattempted.

All 16 dispatched calls have known usage: 39798 input tokens (35456 cache-read, 4342 uncached), 1709 output. Fixed-reference amount: USD 0.003566136, including the rejected capacity attempt. This is arithmetic at the frozen tariff, not a current price or actual bill. Full USD 11.20 / 4,340-second reservation was retained; capture, worker exit, private temporary-state deletion and credential absence checks passed.

| Index | Outcome                   | Accepted | Restored | Reset | Response messages |
| ----- | ------------------------- | -------- | -------- | ----- | ----------------- |
| 0     | completed                 | true     | false    | false | 8                 |
| 1     | completed                 | true     | true     | false | 16                |
| 2     | completed                 | true     | true     | false | 22                |
| 3     | completed                 | true     | true     | false | 28                |
| 4     | capacity_stop             | false    | true     | false | 33                |
| 5     | not_needed_after_capacity | false    | false    | false | unattempted       |
| 6     | not_needed_after_capacity | false    | false    | false | unattempted       |
| 7     | not_needed_after_capacity | false    | false    | false | unattempted       |
| 8     | not_needed_after_capacity | false    | false    | false | unattempted       |
| 9     | not_needed_after_capacity | false    | false    | false | unattempted       |
| 10    | not_needed_after_capacity | false    | false    | false | unattempted       |
| 11    | not_needed_after_capacity | false    | false    | false | unattempted       |
| 12    | completed                 | true     | false    | true  | 8                 |
| 13    | completed                 | true     | true     | false | 14                |

This supplies real-provider evidence for the local capacity rejection, predecessor preservation, explicit reset and successor restoration path under the reduced limit. It does not prove production-default capacity adequacy, production ActionHost epoch/publication behavior, reliability rates, C1, actual billing, R6 exit or R7 promotion. The experimental report has no native journal/pricing export.

E32 stopped at a replay-entry construction limit; F48 stopped on a no-tool response, with one unknown-usage call; G48 exhausted its model-call budget before capacity. These are separate incomplete populations and remain unchanged. H removes the unrelated replay prerequisite rather than retrying any prior population. Since F contains unknown usage, no complete combined fee is claimed. Historical A/B/C/D and PR320's existing comparison are unchanged.
