import esbuild from 'esbuild';

await esbuild.build({
  entryPoints: ['src/main.ts'],
  bundle: true,
  platform: 'node',
  target: 'node24',
  format: 'esm',
  outfile: '.github/actions/agentic-pr-review/dist/index.js',
  legalComments: 'external',
  banner: {
    js: [
      "import { createRequire as __agenticCreateRequire } from 'node:module';",
      'const require = __agenticCreateRequire(import.meta.url);',
    ].join('\n'),
  },
});
