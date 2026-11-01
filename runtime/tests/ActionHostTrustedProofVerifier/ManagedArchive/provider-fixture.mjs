import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { json, readBody } from './github-fixture.mjs';
import { githubKey, providerKey, stateKey } from './official-platform.mjs';
export const reasoningCanary = 'PRIVATE_D5_REASONING_CANARY';
const hash = (value) => createHash('sha256').update(JSON.stringify(value)).digest('hex');
export function createProviderFixture(github, transaction) {
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
      transaction?.event('provider');
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
      const extended = ['useful-six', 'call-capacity', 'tool-capacity', 'batch-capacity'].includes(
        mode,
      );
      const maximum =
        mode === 'tool-capacity' ? 3 : mode === 'batch-capacity' ? 1 : extended ? 6 : 2;
      assert.ok(requests.length < maximum);
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
      const toolTurn = extended
        ? mode !== 'useful-six' || requests.length < 6
        : requests.length === 1;
      if (toolTurn) {
        const file = (path) => ['read_file', { path, start_line: 1, line_count: 10 }];
        const ordinary = [
          ['list_changed_files', {}],
          file(paths[ordinal][0]),
          file(paths[ordinal][1]),
        ];
        const sequence = [
          ordinary.slice(0, 2),
          [ordinary[2]],
          [['list_files', {}]],
          [['read_diff', { path: paths[ordinal][1] }]],
          [
            [
              'search_text',
              {
                query: ordinal === 0 ? 'Trim' : ordinal === 1 ? 'timeoutMs' : 'accessToken',
                path: paths[ordinal][1],
              },
            ],
          ],
        ];
        const selected =
          mode === 'useful-six'
            ? sequence[requests.length - 1]
            : mode === 'tool-capacity'
              ? [file(paths[ordinal][1]), file(paths[ordinal][1]), file(paths[ordinal][1])]
              : mode === 'call-capacity'
                ? [file(paths[ordinal][1])]
                : mode === 'batch-capacity'
                  ? [...ordinary, file(paths[ordinal][1])]
                  : ordinary;
        message = {
          role: 'assistant',
          content: null,
          reasoning_content: reasoningCanary + ordinal,
          tool_calls: selected.map(([name, args], index) => ({
            id: extended ? `d5-${ordinal}-${requests.length}-${index}` : `d5-${ordinal}-${index}`,
            type: 'function',
            function: {
              name,
              arguments: JSON.stringify(args),
            },
          })),
        };
      } else {
        const count = mode === 'useful-six' ? 6 : 3;
        const results = body.messages
          .filter((x) => x.role === 'tool')
          .slice(-count)
          .map((x) => JSON.parse(x.content));
        assert.equal(results.length, count);
        if (mode === 'useful-six') {
          assert.ok(results.every((result) => typeof result.observation_id === 'string'));
          const executed = requests
            .slice(1)
            .flatMap((request) =>
              request.messages
                .filter((message) => message.role === 'tool')
                .map((message) => message.tool_call_id),
            );
          const proposed = [2, 1, 1, 1, 1].flatMap((count, turn) =>
            Array.from({ length: count }, (_, index) => `d5-${ordinal}-${turn + 1}-${index}`),
          );
          assert.ok(
            proposed.every((id) => executed.includes(id)),
            'qualification_all_tools_returned',
          );
        }
        const evidence =
          mode === 'useful-six'
            ? results.find(
                (result) =>
                  result.path === paths[ordinal][1] &&
                  JSON.stringify(result).includes(
                    [
                      'Lookup.Find(key).Trim()',
                      'config.timeoutMs || 5000',
                      'console.error(accessToken)',
                    ][ordinal],
                  ),
              )
            : results.at(-1);
        assert.ok(evidence);
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
          content: mode === 'malformed-final' ? '{' : JSON.stringify(terminal),
          reasoning_content: reasoningCanary + ordinal + '-final',
        };
        if (mode !== 'malformed-final') {
          context = [...body.messages, message];
          previousContext = structuredClone(context);
          receipts.push({
            kind: 'completed-round',
            ordinal,
            sends: requests.length,
            tools: count,
            messages: context.length,
            sha256: hash(context),
          });
        }
        if (mode === 'stale') github.setStale();
      }
      const result = {
        id: 'd5-synthetic',
        object: 'chat.completion',
        model: 'deepseek-flash',
        choices: [
          {
            index: 0,
            finish_reason:
              mode === 'incomplete-tools' ? 'length' : toolTurn ? 'tool_calls' : 'stop',
            message,
          },
        ],
        usage: {
          prompt_tokens: 10,
          completion_tokens: 5,
          total_tokens: 15,
          prompt_cache_hit_tokens: 4,
          prompt_cache_miss_tokens: 6,
        },
      };
      if (mode === 'missing-usage') delete result.usage;
      if (mode === 'missing-cache') {
        delete result.usage.prompt_cache_hit_tokens;
        delete result.usage.prompt_cache_miss_tokens;
      }
      json(response, result);
    },
  };
}
