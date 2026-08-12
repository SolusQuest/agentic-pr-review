import * as core from '@actions/core';

import type { ActionInputToolkit } from '../launcher/inputs.js';
import type { ActionHostCompletionDocument } from './completion.js';
import { renderStepSummary } from './completion.js';

export interface ActionPresentationToolkit extends ActionInputToolkit {
  writeSummary(markdown: string): Promise<void>;
  warning(message: string): void;
  error(message: string): void;
}

export function createActionsToolkit(): ActionPresentationToolkit {
  return {
    getInput: (name, options) => core.getInput(name, options),
    setSecret: (secret) => core.setSecret(secret),
    writeSummary: async (markdown) => {
      core.summary.addRaw(markdown);
      await core.summary.write({ overwrite: true });
    },
    warning: (message) => core.warning(message),
    error: (message) => core.error(message),
  };
}

export async function presentCompletion(
  toolkit: ActionPresentationToolkit,
  completion: ActionHostCompletionDocument,
): Promise<void> {
  await toolkit.writeSummary(renderStepSummary(completion));
  const annotation = completion.annotations[0];
  if (!annotation) return;
  if (annotation.severity === 'warning') toolkit.warning(annotation.message);
  else toolkit.error(annotation.message);
}

export async function presentFixedWrapperFailure(
  toolkit: ActionPresentationToolkit,
): Promise<void> {
  try {
    await toolkit.writeSummary(
      '## Agentic PR Review\n\nThe private review wrapper failed safely.\n',
    );
    toolkit.error('The private review wrapper failed.');
  } catch {
    // The fixed failure path never forwards toolkit errors or retries presentation.
  }
}
