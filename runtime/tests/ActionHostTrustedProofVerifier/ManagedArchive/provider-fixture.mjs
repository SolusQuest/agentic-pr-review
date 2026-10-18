import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { json, readBody } from './github-fixture.mjs';
import { githubKey, providerKey, stateKey } from './official-platform.mjs';
export const reasoningCanary = 'PRIVATE_D5_REASONING_CANARY';
const hash = (value) => createHash('sha256').update(JSON.stringify(value)).digest('hex');
export function createProviderFixture(github) {
  let requests = [],
    previousContext = null,
    context = null,
    mode = 'success';
  const receipts = [];
  const paths = [
    ['Lookup.cs', 'Caller.cs'],
    ['Caller.cs', 'client.ts'],
    ['client.ts', 'log.ts'],
  ];
  return {
    receipts,
    get count() {
      return requests.length;
    },
    get previousContext() {
      return structuredClone(previousContext);
    },
    configure(value = 'success', prior = previousContext) {
      requests = [];
      previousContext = structuredClone(prior);
      mode = value;
    },
    async handle(request, response) {
      assert.equal(request.method, 'POST');
      assert.ok(['/chat/completions', '/v1/chat/completions'].includes(request.url));
      assert.equal(request.headers.authorization, 'Bearer ' + providerKey);
      const bytes = await readBody(request),
        body = JSON.parse(bytes);
      for (const secret of [githubKey, providerKey, stateKey])
        assert.equal(bytes.includes(Buffer.from(secret)), false);
      assert.equal(body.model, 'deepseek-flash');
      assert.equal(body.max_tokens, 1024);
      assert.equal(body.stream, false);
      assert.deepEqual(body.thinking, { type: 'enabled' });
      assert.equal(body.reasoning_effort, 'high');
      const tools = body.tools.map((x) => x.function.name).sort();
      assert.deepEqual(tools, [
        'list_changed_files',
        'list_files',
        'read_diff',
        'read_file',
        'search_text',
      ]);
      assert.ok(body.messages.length <= 32);
      assert.ok(requests.length < 2);
      if (!requests.length && previousContext) {
        assert.ok(body.messages.length > previousContext.length);
        assert.deepEqual(
          body.messages.slice(0, previousContext.length),
          previousContext,
          'd5_complete_restored_prefix',
        );
        receipts.push({
          kind: 'restored-prefix',
          messages: previousContext.length,
          sha256: hash(previousContext),
        });
      }
      requests.push(body);
      if (mode === 'provider-failure')
        return json(
          response,
          { error: { message: 'synthetic provider refusal', type: 'invalid_request_error' } },
          400,
        );
      if (mode === 'cancel') return;
      const ordinal = github.ordinal;
      let message;
      if (requests.length === 1) {
        message = {
          role: 'assistant',
          content: null,
          reasoning_content: reasoningCanary + ordinal,
          tool_calls: ['list_changed_files', 'read_file', 'read_file'].map((name, index) => ({
            id: `d5-${ordinal}-${index}`,
            type: 'function',
            function: {
              name,
              arguments: JSON.stringify(
                index === 0
                  ? {}
                  : { path: paths[ordinal][index - 1], start_line: 1, line_count: 10 },
              ),
            },
          })),
        };
      } else {
        const results = body.messages
          .filter((x) => x.role === 'tool')
          .slice(-3)
          .map((x) => JSON.parse(x.content));
        assert.equal(results.length, 3);
        const evidence = results.at(-1);
        assert.equal(evidence.path, paths[ordinal][1]);
        assert.ok(evidence.observation_id);
        assert.ok(
          JSON.stringify(evidence).includes(
            ['Lookup.Find(key).Trim()', 'config.timeoutMs || 5000', 'console.error(accessToken)'][
              ordinal
            ],
          ),
        );
        const terminal = {
          summary: 'Current head reviewed with source evidence.',
          findings: [
            {
              severity: 'high',
              title: [
                'Handle missing lookup results',
                'Preserve a zero timeout',
                'Keep access tokens out of logs',
              ][ordinal],
              message: [
                'Find returns null; Trim throws in the caller.',
                'The logical-or expression replaces an explicit zero timeout with 5000.',
                'The authentication path writes the secret access token to the error log.',
              ][ordinal],
              evidence: [
                {
                  observation_id: evidence.observation_id,
                  path: evidence.path,
                  start_line: 1,
                  end_line: 1,
                },
              ],
            },
          ],
        };
        message = {
          role: 'assistant',
          content: JSON.stringify(terminal),
          reasoning_content: reasoningCanary + ordinal + '-final',
        };
        context = [...body.messages, message];
        previousContext = structuredClone(context);
        receipts.push({
          kind: 'completed-round',
          ordinal,
          sends: 2,
          tools: 3,
          messages: context.length,
          sha256: hash(context),
        });
        if (mode === 'stale') github.setStale();
      }
      json(response, {
        id: 'd5-synthetic',
        object: 'chat.completion',
        model: 'deepseek-flash',
        choices: [
          { index: 0, finish_reason: requests.length === 1 ? 'tool_calls' : 'stop', message },
        ],
        usage: {
          prompt_tokens: 10,
          completion_tokens: 5,
          total_tokens: 15,
          prompt_cache_hit_tokens: 4,
          prompt_cache_miss_tokens: 6,
        },
      });
    },
  };
}
