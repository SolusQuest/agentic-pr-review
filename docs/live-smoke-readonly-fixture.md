# Live Smoke Readonly Fixture

This file exists to validate that the readonly tool mode can inspect
real checkout workspace files during a same-repo PR review.

- Validates: PR metadata extraction, changed file list, bounded patch context.
- Validates: Readonly tools (Read, Glob, Grep) are enabled.
- Validates: Observed turns and lineage totals are reported.
- Validates: Raw debug artifact is disabled.

This file contains only public-safe content.
