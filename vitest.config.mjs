import { configDefaults, defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    exclude: [...configDefaults.exclude, '.local/**', '.scratch/**', '.agentic-pr-review-local/**'],
  },
});
