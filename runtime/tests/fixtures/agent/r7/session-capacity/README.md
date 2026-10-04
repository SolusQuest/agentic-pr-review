# R7 SESSION capacity evidence

This directory owns R7-C3 capacity fixtures. Historical reports are immutable observations from base commit `aea38e3f8f14eeae3e2dc915598aade031b3c6e7`; `historical-manifest.json` records their digests and provenance. They are read with frozen R5/R6 measurement limits and never stand in for execution of the current runtime. Current bounded R5/R6 regressions explicitly report incomplete capacity observation. The dedicated R7 local and artifact proofs exercise the current runtime. The `bootstrap.json.golden` and `continue.json.golden` files are current producer regression contracts, separate from the four immutable reports listed in `historical-manifest.json`; their identity digests track the selected Current limits, including the R7-R4 token policy.

## Current bounds

| Surface                                         |           Bound |
| ----------------------------------------------- | --------------: |
| Serialized agent/provider request               |           8 MiB |
| Provider response body                          |           2 MiB |
| Messages / total parts                          |     4096 / 8192 |
| Parts per message                               |              32 |
| Text or decoded continuation item               |           1 MiB |
| Cumulative decoded continuation                 |           8 MiB |
| Combined SESSION records and continuation items |            8192 |
| Completed runs                                  |              64 |
| SESSION record / complete plaintext             | 16 MiB / 16 MiB |
| Restricted encrypted envelope                   |          32 MiB |
| Accepted candidates / envelope total            |      2 / 64 MiB |
| Restricted scope total                          | 96 MiB + 16 KiB |

All bounds apply together. An otherwise valid continuation can exceed the complete request bound after framing; 16 MiB of valid wire data need not be a restorable request. These are logical byte/count limits, not a peak process-memory guarantee. Bounded JSON parsing, canonicalization and authenticated copies coexist in memory. Count and payload-length preflight occurs before typed record arrays and decoded payload copies; the initial bounded JSON parse still allocates. The stored-history part count is cumulative across completed runs: assistant contents count individually, each context/tool-result/tool-error/review-outcome contributes one part, and a continuation slot counts once rather than again for its matching item. Final reconstructed-request admission additionally accounts for trusted controls and the current review context.

R7-R4 leaves the byte/state bounds above, model/tool count ceilings, deadline, provider model, tools, roles, associations, authentication and reset authority unchanged. Current uses independent post-response stopping thresholds of 2,000,000 uncached input, 38,000,000 cached input and 524,288 output tokens, with no combined-token cap. Admission never truncates or summarizes history. A refusal must leave the accepted predecessor intact.

## Nested retained-state budget

The unchanged C2 transport ceiling can carry the following defensive capacities, in bytes: restricted envelope E=33,554,432; generation G=33,832,960; physical copy P=33,849,344; acceptance recovery R=33,948,936; publication recovery T=34,014,472; opaque recovery Q=34,030,988; cleanup anchor C=34,033,036; outer control envelope O=34,049,552. The outer envelope expands to 45,399,404 base64 bytes, 45,399,694 JSON bytes and 45,413,722 ZIP bytes under the existing transport accounting.

Candidate owns both the generation and physical-copy subtype, so its class cap is P. Acceptance remains a 64 KiB receipt. Other small classes remain 1 MiB. Large PublicationIntent payloads require the recovery discriminator, and large Cleanup payloads require the opaque-write-anchor discriminator; their nested codecs still enforce complete canonical structure. The actual 16 MiB SESSION producer adds only 98–161 restricted-envelope bytes, depending on key-id length. E is a defensive decoder allowance, not a claim that this producer creates a 32 MiB state envelope.

The Node bridge retains at most 4,096 terminal correlation IDs per Host process and 32 active IDs. The former 2,048 terminal bound was exhausted during generation-two physical-copy reconciliation in the R7 artifact proof, after provider completion and publication. Terminal IDs are never evicted: duplicates remain rejected even at saturation. At the existing 256-byte identifier bound, identifier content alone is at most 1 MiB; container/runtime overhead is additional. The independent 64 MiB artifact-cache ledger is unchanged.

## Conservative context admission

`dsv41-utf8-upper-v1` admits only if its prompt upper bound plus the actual request output allowance fits 1,000,000 tokens. Current reserves `min(65536, remaining known output)`; retained named profiles reserve their fixed 8192 or 65536 tokens. It inspects the actual bounded provider projection, including all restored reasoning. Unsupported function argument shapes, duplicate object keys and nonfinite numbers fail closed before physical send. Provider usage remains separate from this estimate.

The derivation is pinned to `deepseek-ai/deepseek-recipe` commit `8cadfede7063c896b944e7bae05daa3549ae97ea`. `verify-context-bound.mjs` independently checks the four source-file SHA-256 values, the absence of normalization, the non-expanding isolated split and byte-level pretokenizer, and coverage of every single byte by the BPE vocabulary. Under those pinned assumptions, rendered UTF-8 bytes upper-bound token count. This is deliberately conservative and is not an exact tokenizer or a claim about future server templates.

The estimator reserves 4096 bytes for fixed template/instruction framing, 128 per message, 128 per function call or definition, and 128 per top-level function argument. The independently measured assistant, call and parameter wrappers are 100, 50 and 69 bytes respectively. Content and reasoning contribute their decoded UTF-8 byte lengths. JSON rendering contributes twice its original UTF-8 size plus 32 bytes per finite binary64 number; this covers canonical number expansion, quoting and separators. Tool names/descriptions receive a sixfold UTF-8 allowance for JSON escaping. Checked arithmetic and output reservation are applied to the complete sum.

Run the offline audit with the pinned tokenizer JSON, common v4 template, v4.1 template and JSON formatter as its four arguments. The runtime has no tokenizer dependency and downloads no tokenizer data.

## Proof routes

The current bootstrap and continuation goldens were regenerated for R7-R5 from the actual framework producer used by `bash runtime/scripts/verify-agent-loop.sh framework`, with the generated positive outputs captured before comparison. Current limits are `239b9a35e03c2f5a6679d9520975bdf048a392433558416f4b228fb893bb8a78`; the bootstrap file SHA-256 is `c53359bb8837d2c020d61eafeca756e52d8bebf0f2a81baccc43ded0c2608d4b` and continuation SHA-256 is `ed04935185ee172f01b3575f3c767ec22175addd2e1e06ec299953b9504ffa6a`. Current review duration is 900 seconds; retained named profiles still use 300 seconds. Historical reports and manifest remain unchanged.

`verify-agent-loop.sh all` runs `r7-capacity` in framework and NativeAOT modes. It performs twenty accepted generations through the production encrypted local state store, restoring each predecessor and checking exact reasoning associations, tool results and predecessor bytes. The final synthetic context refusal must make zero physical sends and preserve the prior acceptance. The same enlarged accepted ciphertext is then corrupted: production restore must reject authentication without modifying stored state or admitting a SESSION; restoring the original test fixture must recover the identical predecessor.

`verify-action-host.sh framework` and `verify-action-host.sh aot` additionally run twenty fresh Host processes through the synthetic official artifact platform and actual ZIP/envelope transport. Its oracle reads and decrypts the platform's stored artifacts, including Candidate physical copies, and checks the accepted SESSION. A final oversized context must preserve the tail acceptance without provider sends or sticky mutation. Receipts contain numeric measurements and digests only; raw provider, SESSION and credential material are excluded.

These routes must pass on the reviewed head before a readiness claim. Historical JSON, two-run current workload regressions and an unexecuted proof implementation are not capacity acceptance evidence.
