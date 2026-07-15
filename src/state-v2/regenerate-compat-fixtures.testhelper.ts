// Bundle entry point for scripts/regenerate-state-v2-compat-fixtures.mjs.
// Not imported by production code; excluded from the .test.ts scan.
import { generateAllCompatFixtures } from './generate-compat-fixtures.testhelper.js';

await generateAllCompatFixtures();
console.log('State v2 compatibility fixtures regenerated.');
