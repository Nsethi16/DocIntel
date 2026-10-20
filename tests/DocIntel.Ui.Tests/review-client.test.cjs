const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const script = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api',
  'wwwroot', 'review-client.js'), 'utf8');
function runtime(results = []) {
  const calls = [];
  const context = vm.createContext({
    fetch: async (url, options) => {
      calls.push({ url, options });
      const result = results.shift();
      assert.ok(result, 'Unexpected review request');
      if (result instanceof Error) throw result;
      return {
        ok: !result.status || result.status < 400, status: result.status || 200,
        json: async () => result.body || {},
        headers: { get: name => result.headers?.[name] || null }
      };
    }
  });
  vm.runInContext(script, context);
  return { client: context.DocIntelReview, calls };
}

test('the focused workspace script parses without requiring provider or document access', () => {
  const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'review.js'), 'utf8');
  assert.doesNotThrow(() => new vm.Script(source));
});

test('review mutations use the current strong If-Match token and preserve exact command data', async () => {
  const app = runtime([{ body: { version: 3 }, headers: { ETag: '"review-3"' } }]);
  const command = { fieldId: 'entity:0:0', outcome: 'Corrected', value: 'Verified', reason: 'Source checked' };
  const result = await app.client.change('/review', 'field', command, '"review-2"');
  assert.equal(app.calls[0].options.headers['If-Match'], '"review-2"');
  assert.equal(app.calls[0].options.method, 'POST');
  assert.deepEqual(JSON.parse(app.calls[0].options.body), command);
  assert.equal(result.tag, '"review-3"');
});

test('missing or weak review versions do not submit a mutation', async () => {
  const app = runtime();
  for (const tag of [null, '*', 'W/"review-2"', '"review-0"'])
    await assert.rejects(app.client.change('/review', 'claim', {}, tag), /Reload/);
  assert.equal(app.calls.length, 0);
});

test('a conflict or unconfirmed network outcome does not cause an automatic second mutation', async () => {
  const conflict = runtime([{ status: 409, body: { title: 'The review changed.' } }]);
  await assert.rejects(conflict.client.change('/review', 'submit', {}, '"review-2"'), /changed/);
  assert.equal(conflict.calls.length, 1);
  const network = runtime([new TypeError('Network lost')]);
  await assert.rejects(network.client.change('/review', 'submit', {}, '"review-2"'), /could not be confirmed/);
  assert.equal(network.calls.length, 1);
});

test('resource reads preserve the server review capability without granting writes locally', async () => {
  const app = runtime([{ body: { version: 1 }, headers: { ETag: '"review-1"', 'X-DocIntel-Can-Review': 'false' } }]);
  const result = await app.client.read('/review');
  assert.equal(result.canReview, false);
  assert.equal(result.tag, '"review-1"');
});

test('source polygon hit testing uses all corners rather than a guessed axis-aligned region', () => {
  const { client } = runtime();
  const diamond = [0.5, 0.1, 0.9, 0.5, 0.5, 0.9, 0.1, 0.5];
  assert.equal(client.contains(diamond, 0.5, 0.5), true);
  assert.equal(client.contains(diamond, 0.15, 0.15), false);
  assert.equal(client.contains([0, Infinity], 0.1, 0.1), false);
});
