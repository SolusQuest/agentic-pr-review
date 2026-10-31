import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';
import { parseDocument } from 'yaml';

describe('closed downstream workflow installation alternatives', () => {
  for (const variant of ['automatic', 'manual']) {
    it(`${variant} is valid YAML with one credential-bearing composite call`, async () => {
      const source = await readFile(`examples/workflows/${variant}.yml.template`, 'utf8');
      const document = parseDocument(source, { uniqueKeys: true });
      expect(document.errors).toEqual([]);
      const workflow = document.toJS();
      expect(workflow.permissions).toEqual({});
      expect(workflow.concurrency).toEqual({
        group:
          'agentic-pr-review-r4-${{ github.repository_id }}-pr-${{ github.event.workflow_run.pull_requests[0].number || inputs.pr-number }}',
        'cancel-in-progress': false,
      });
      expect(Object.keys(workflow.jobs)).toEqual(['review']);
      const job = workflow.jobs.review;
      expect(job['runs-on']).toBe('ubuntu-24.04');
      expect(job['timeout-minutes']).toBe(30);
      expect(job.permissions).toEqual({
        actions: 'write',
        contents: 'read',
        'pull-requests': 'write',
      });
      expect(job.environment).toBeUndefined();
      expect(job.steps).toHaveLength(1);
      expect(job.steps[0].uses).toBe(
        'SolusQuest/agentic-pr-review/.github/actions/agentic-pr-review@__ACTION_SOURCE_SHA__',
      );
      expect(Object.keys(job.steps[0].with)).toEqual([
        'github-token',
        'provider-api-key',
        'state-key',
        'previous-state-key',
        'config-path',
        'pr-number',
        'state-mode',
      ]);
      expect(job.steps[0].with['config-path']).toBe('.github/agentic-pr-review.json');
      expect(workflow.on.workflow_dispatch.inputs['pr-number'].type).toBe('string');
      expect(workflow.on.workflow_dispatch.inputs['state-mode'].options).toEqual(['auto', 'reset']);
      expect(workflow.on.workflow_dispatch.inputs['state-mode'].default).toBe('auto');
      expect(Object.keys(workflow.on)).toEqual(
        variant === 'automatic' ? ['workflow_run', 'workflow_dispatch'] : ['workflow_dispatch'],
      );
      if (variant === 'automatic') {
        expect(workflow.on.workflow_run).toEqual({ workflows: ['CI'], types: ['completed'] });
        expect(job.steps[0].with['state-mode']).toBe(
          "${{ github.event_name == 'workflow_dispatch' && inputs.state-mode || 'auto' }}",
        );
      }
      expect(source).not.toMatch(
        /setup-dotnet|setup-node|checkout|id-token|AGENTIC_PR_REVIEW_PREPARED|dotnet (?:build|restore)/u,
      );
      expect(source).not.toMatch(/^\s+(?:- )?run:/mu);
    });
  }
});
