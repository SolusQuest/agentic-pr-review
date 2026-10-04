import * as core from '@actions/core';

import type { ActionInputToolkit } from '../launcher/inputs.js';
import type { ActionHostCompletionDocument } from './completion.js';
import { renderStepSummary } from './completion.js';
import {
  FIXED_WRAPPER_FAILURE_OUTPUTS,
  projectCompletionOutputs,
  type ActionOutputName,
} from './outputs.js';

export interface ActionPresentationToolkit extends ActionInputToolkit {
  setOutput(name: ActionOutputName, value: string): void;
  writeSummary(markdown: string): Promise<void>;
  warning(message: string): void;
  error(message: string): void;
}

export function createActionsToolkit(): ActionPresentationToolkit {
  return {
    getInput: (name, options) => core.getInput(name, options),
    setSecret: (secret) => core.setSecret(secret),
    setOutput: (name, value) => core.setOutput(name, value),
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
  const outputs = projectCompletionOutputs(completion);
  const summary = renderStepSummary(completion);
  for (const [name, value] of Object.entries(outputs)) {
    toolkit.setOutput(name as ActionOutputName, value);
  }
  await toolkit.writeSummary(summary);
  const annotation = completion.annotations[0];
  if (!annotation) return;
  if (annotation.severity === 'warning') toolkit.warning(annotation.message);
  else toolkit.error(annotation.message);
}

export async function presentFixedWrapperFailure(
  toolkit: ActionPresentationToolkit,
): Promise<void> {
  for (const [name, value] of Object.entries(FIXED_WRAPPER_FAILURE_OUTPUTS)) {
    try {
      toolkit.setOutput(name as ActionOutputName, value);
    } catch {
      // Each fixed fact is attempted once; never substitute numerical facts.
    }
  }
  try {
    await toolkit.writeSummary(
      '## Agentic PR Review\n\nThe private review wrapper failed safely.\n\nStatus: failed. Review termination: host_failure. Attempt accounting completeness: unavailable. Usage completeness: unavailable. Provider counts, token sums and state disposition: Not available.\n',
    );
  } catch {
    // Fixed sinks are independent and never forward raw errors.
  }
  try {
    toolkit.error('The private review wrapper failed.');
  } catch {
    // No retry or exception presentation.
  }
}
