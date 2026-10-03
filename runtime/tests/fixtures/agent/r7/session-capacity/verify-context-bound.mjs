import fs from 'node:fs';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';

// Offline source audit: node verify-context-bound.mjs tokenizer.json v4-mod.rs v41.rs json_formatter.rs
const paths = process.argv.slice(2);
assert.equal(paths.length, 4);
const inputs = paths.map((path) => fs.readFileSync(path));
const hashes = inputs.map((bytes) => crypto.createHash('sha256').update(bytes).digest('hex'));
assert.deepEqual(hashes, [
  'df385ab3ba4bbb1815327f3e71bba5b263263f5d015a838e6ff295ca69b3198e',
  '39b92cd1f087c794276fe624cdbb9b75bf392b9246f29def2b7a6af050c31fb2',
  'e32d071d89d634ebd663242fe090cc2df4693ccd9e7d36f0f2e8c71b0b887375',
  'bd7997b20663951bac824d2b415199ad139b8862e23b6829cda95bdf0ac77293',
]);
const tokenizer = JSON.parse(inputs[0]);
assert.equal(tokenizer.model.type, 'BPE');
assert.deepEqual(tokenizer.normalizer, { type: 'Sequence', normalizers: [] });
const stages =
  tokenizer.pre_tokenizer.type === 'Sequence'
    ? tokenizer.pre_tokenizer.pretokenizers
    : [tokenizer.pre_tokenizer];
const byteLevel = stages.filter((stage) => stage.type === 'ByteLevel');
assert.equal(byteLevel.length, 1);
assert.equal(byteLevel[0].add_prefix_space, false);
assert.ok(
  stages.every(
    (stage) =>
      stage.type === 'ByteLevel' ||
      (stage.type === 'Split' && stage.behavior === 'Isolated' && stage.invert === false),
  ),
);
const alphabet = [...Array(256).keys()].filter(
  (b) => (b >= 33 && b <= 126) || (b >= 161 && b <= 172) || b >= 174,
);
const codepoints = new Map(alphabet.map((b) => [b, b]));
let next = 256;
for (let b = 0; b < 256; b++) if (!codepoints.has(b)) codepoints.set(b, next++);
for (const point of codepoints.values())
  assert.ok(Object.hasOwn(tokenizer.model.vocab, String.fromCodePoint(point)));
const common = inputs[1].toString('utf8');
const v41 = inputs[2].toString('utf8');
const formatter = inputs[3].toString('utf8');
assert.ok(common.includes('render_tool_arguments') && common.includes('reasoning_content'));
assert.ok(v41.includes('DeepseekV41Encoding') && formatter.includes('PythonStyleFormatter'));
const bytes = (text) => Buffer.byteLength(text, 'utf8');
const assistantWrapper =
  '<｜Assistant｜><think></think><｜end▁of▁sentence｜>\n\n<｜DSML｜ calls>\n\n</｜DSML｜ calls>';
const callWrapper = '<｜DSML｜ invoke name="">\n\n</｜DSML｜ invoke>\n';
const parameterWrapper = '<｜DSML｜ parameter name="" string="false"></｜DSML｜ parameter>\n';
assert.ok(bytes(assistantWrapper) <= 128);
assert.ok(bytes(callWrapper) <= 128);
assert.ok(bytes(parameterWrapper) <= 128);
console.log(
  JSON.stringify(
    {
      source_revision: '8cadfede7063c896b944e7bae05daa3549ae97ea',
      hashes,
      model: 'BPE',
      normalizer_count: 0,
      single_byte_tokens: codepoints.size,
      wrapper_utf8_bytes: [assistantWrapper, callWrapper, parameterWrapper].map(bytes),
    },
    null,
    2,
  ),
);
