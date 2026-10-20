const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'ask-run.js'), 'utf8')
  .replace(/\r\n/g, '\n');
const instrumented = source.replace(/\n  load\(\);\n\}\)\(\);\s*$/, '\n  globalThis.loadRun = load;\n})();');
assert.notEqual(source, instrumented);

function runtime(search = '?tenant=tenant&run=one') {
  const elements = new Map();
  const calls = [];
  let pending;
  function node() {
    const handlers = new Map();
    return { textContent: '', disabled: false, children: [],
      set innerHTML(value) { throw new Error('Untrusted HTML rendering is forbidden'); },
      appendChild(child) { this.children.push(child); },
      replaceChildren(...children) { this.children = children; },
      addEventListener(name, callback) { handlers.set(name, callback); },
      click() { return handlers.get('click')?.(); }
    };
  }
  function element(id) { if (!elements.has(id)) elements.set(id, node()); return elements.get(id); }
  const app = {
    response: { status: 'Queued' }, httpStatus: 200,
    get pending() { return pending; },
    element, calls
  };
  const context = vm.createContext({
    URLSearchParams, location: { search }, Date,
    document: { getElementById: element, createElement: node },
    setTimeout(callback, delay) { pending = { callback, delay }; return 1; },
    clearTimeout() { pending = null; },
    fetch: async (url, options) => {
      calls.push({ url, options });
      return { ok: app.httpStatus === 200, status: app.httpStatus, json: async () => app.response };
    }
  });
  vm.runInContext(instrumented, context);
  app.load = context.loadRun;
  return app;
}

test('the run link only polls the same authorized question and never submits a new execution', async () => {
  const app = runtime();
  await app.load();
  assert.equal(app.pending.delay, 5000);
  assert.equal(app.calls[0].url, '/api/harness/tenants/tenant/ask/runs/one');
  app.response = { status: 'Completed', answer: 'Answer', delivery: { status: 'Sent' } };
  await app.pending.callback();
  assert.equal(app.calls.length, 2);
  assert.equal(app.element('answer').textContent, 'Answer');
  assert.equal(app.pending, null);
  assert.match(app.element('delivery').textContent, /does not confirm receipt/);
  assert.ok(app.calls.every(call => call.options === undefined));
});

test('large cited evidence stays collapsed and untrusted model text or URLs never become active HTML', async () => {
  const app = runtime();
  app.response = {
    status: 'Completed', question: '<img onerror=evil()>', answer: '<script>evil()</script>',
    citations: [{
      jobId: 'doc', sliceIndex: 2, fileName: '<unsafe>', excerpt: 'x'.repeat(3000),
      workspaceUrl: 'javascript:evil()', pdfUrl: 'https://attacker.example/'
    }]
  };
  await app.load();
  assert.equal(app.element('answer').textContent, '<script>evil()</script>');
  const item = app.element('citations').children[0];
  assert.equal(item.children[0].href, '/api/harness/tenants/tenant/jobs/doc/slices/2/pdf');
  assert.equal(item.children[1].open, undefined);
  assert.equal(item.children[1].children[1].textContent.length, 3000);
});

test('reviewed citations use the human review workspace and disclose their version', async () => {
  const app = runtime();
  app.response = {
    status: 'Completed', answer: 'A correction [1]', citations: [{
      jobId: 'doc', sliceIndex: 0, evidenceKind: 'HumanReviewedField', reviewVersion: 8
    }]
  };
  await app.load();
  const item = app.element('citations').children[0];
  assert.equal(item.children[0].href, '/review?tenant=tenant&job=doc');
  assert.match(item.children[1].textContent, /Human review version 8/);
});

test('uncertain delivery stops polling and cannot trigger an automatic resend', async () => {
  const app = runtime();
  app.response = { status: 'Completed', answer: 'Available here', delivery: { status: 'OutcomeUnknown' } };
  await app.load();
  assert.match(app.element('delivery').textContent, /will not automatically resend/);
  assert.equal(app.pending, null);
  await app.element('refresh').click();
  assert.equal(app.calls.length, 2);
  assert.ok(app.calls.every(call => call.options === undefined));
});

test('a denied refresh clears the displayed answer and offers only polling of the existing question', async () => {
  const app = runtime();
  app.response = { status: 'Completed', question: 'Confidential question', answer: 'Previous answer',
    delivery: { status: 'Sent' }, citations: [{ jobId: 'doc', sliceIndex: 0 }] };
  await app.load();
  app.httpStatus = 403;
  await app.element('refresh').click();
  assert.equal(app.element('answer').textContent, '');
  assert.equal(app.element('question').textContent, '');
  assert.equal(app.element('delivery').textContent, '');
  assert.equal(app.element('citations').children.length, 0);
  assert.match(app.element('status').textContent, /cannot access/);
  assert.match(app.element('status').textContent, /no new question/);
});

test('an incomplete run link never makes a request', async () => {
  const app = runtime('?tenant=tenant');
  await app.load();
  assert.equal(app.calls.length, 0);
  assert.equal(app.element('refresh').disabled, true);
});
