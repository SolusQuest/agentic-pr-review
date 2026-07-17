# LedgerFixtureGen

Regenerates the ProviderSessionLedgerV1 fixtures under
`protocol/fixtures/v1/provider-session-ledger/` from the `LedgerBuilder` public API.
Run from outside the repository (the pinned SDK in `global.json` resolves by working
directory):

```bash
dotnet run --project /d/code/agentic-pr-review/runtime/tools/LedgerFixtureGen -- --artifacts-path /d/code/agentic-pr-review/protocol/fixtures/v1/provider-session-ledger --manifest-fragment /tmp/ledger-entries.json
```

The `--` separator is required: without it `dotnet run` consumes the options as its own
build options. `--manifest-fragment` is optional; when given, the 35 ledger-transition /
ledger-build manifest entries are written there as a JSON array for splicing into
`protocol/fixtures/v1/manifest.json`.

The tool writes 109 artifacts:

- 15 valid restores and 59 invalid restores, the latter built as minimal mutations of
  the canonical bytes (each self-checked: `LedgerParser` must reject it with exactly the
  expected `Diagnostics[0].Code`; the two deep-path fixtures also assert the frozen
  byte-exact message);
- 25 transition candidates (4 valid, 21 invalid), each self-checked through the matching
  `LedgerTransitionValidator` entry point;
- 10 build scenarios (4 valid, 6 invalid), each self-checked through the full
  `LedgerBuilder` pipeline, the valid ones byte-compared against the transition fixture
  they must reproduce.

Every written ledger file is re-read and verified before its oracle is printed.
`invalid-json.json` is maintained by hand and is never written by this tool.

Output is deterministic; rerun and `cmp` against the committed fixtures to confirm the
oracles.
