// Test data emitted by the real C# final-result producer. No product validator.
import { readFileSync } from 'node:fs';
import type { ActionHostCompletionDocument } from './completion.js';
export const projectionCases = JSON.parse(
  readFileSync(
    'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/final-projection.json',
    'utf8',
  ),
) as { name: string; document: ActionHostCompletionDocument }[];
export function projectionFixture(name: string, build = 'r4-h1'): ActionHostCompletionDocument {
  const row = projectionCases.find((row) => row.name === name);
  if (!row) throw new Error('projection_fixture_missing');
  return { ...structuredClone(row.document), build_discriminator: build };
}
