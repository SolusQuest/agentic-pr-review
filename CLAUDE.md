# Claude Code Entry

Read `AGENTS.md` first. It defines the startup reading order and safety rules for this repository.

## Code Conventions

- TypeScript with strict mode.
- ES modules (`"type": "module"` in `package.json`).
- Use `vitest` for tests.
- Use `prettier` for formatting.

## Validation

After code or documentation changes, run:

```bash
npm run check
```

This runs format checking, type checking, and tests.

For generated action bundle changes, also run:

```bash
npm run dist:check
```

## Text Hygiene

- Keep commit messages, PR bodies, issue bodies, and comments clear and focused.
- Do not wrap text in decorative marker strings.
