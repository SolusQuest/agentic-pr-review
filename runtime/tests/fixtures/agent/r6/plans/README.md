# R6 economics preparation examples

Use `economics-plan` as described in [the runner workflow](../../../../../../docs/10_workflow/r6-live-economics.md) to generate a complete plan for the currently built evaluator. Source/build/corpus/tariff digests are late-bound. Checked-in source hashes or example timestamps must not become reusable execution grants.

The default authored selection is:

```json
[
  { "profile": "replay", "phases": 3, "repeats": 1, "reset_after_capacity": false },
  { "profile": "tools", "phases": 6, "repeats": 1, "reset_after_capacity": true },
  { "profile": "continuation", "phases": 7, "repeats": 1, "reset_after_capacity": true }
]
```

This is a selection fragment, not an executable plan. It expands to twenty fresh review workers and 160 reserved call slots. Calls that are not made are not charged as observed traffic, but their parent allocation is not recycled. Independent repetitions start new authored chains and retain the same parent campaign budget.

A reduced entry preparation can select two or three replay phases. Two selected workers still require sixteen call reservations. A four-call campaign split into two two-call worker quotas is deliberately rejected before key access or worker launch; T2 cannot interpret local two-call exhaustion as global four-call exhaustion.

The deterministic tests construct bounded plans and synthetic tariff snapshots at runtime. No example authorizes paid provider requests or proves live model quality.
