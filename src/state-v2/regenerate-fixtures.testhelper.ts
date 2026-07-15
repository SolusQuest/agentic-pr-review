// Bundle entry point for scripts/regenerate-state-v2-fixtures.mjs.
// Not imported by production code; excluded from the .test.ts scan.
import { generateAllPositiveFixtures } from './generate-fixtures.testhelper.js';

await generateAllPositiveFixtures();
console.log('State v2 positive fixtures regenerated.');
