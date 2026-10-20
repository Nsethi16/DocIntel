const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'review.js'), 'utf8')
  .replace(/\r\n/g, '\n');
const instrumented = source.replace(/\n  init\(\);\n\}\)\(\);\s*$/,
  '\n  globalThis.workspace = { state, renderFields, act, selectJob, bind };\n})();');
assert.notEqual(instrumented, source);

function runtime() {
  const elements = new Map();
  function node() {
    const handlers = new Map();
    return { value: '', textContent: '', hidden: false, readOnly: false, disabled: false, children: [],
      classList: { toggle() {}, add() {}, remove() {} }, scrollIntoView() {}, focus() {},
      addEventListener(name, action) { handlers.set(name, action); },
      append(...children) { this.children.push(...children); },
      replaceChildren(...children) { this.children = children; },
      click() { return handlers.get('click')?.(); }
    };
  }
  function element(id) { if (!elements.has(id)) elements.set(id, node()); return elements.get(id); }
  const client = { change: async () => { throw new Error('Unexpected review change'); },
    read: async () => { const error = new Error('Not started'); error.status = 404; error.canReview = true; throw error; } };
  const context = vm.createContext({
    URLSearchParams, window: { confirm: () => true, location: { search: '' } },
    document: { getElementById: element, createElement: node, addEventListener() {} },
    DocIntelReview: client, DocIntelEvidence: { describe: () => ({ tooltip: 'Uncalibrated source signal' }) }
  });
  vm.runInContext(instrumented, context);
  const api = context.workspace;
  Object.assign(api.state, { tenant: 'tenant', job: 'one', canReview: true, claimed: true, tag: '"review-2"',
    session: { id: 'review', status: 'InReview', version: 2, dueAtUtc: '2026-10-21T00:00:00Z',
      fields: [{ id: 'entity:0:0', label: 'Name', originalValue: 'Original', currentValue: 'Original',
        outcome: 'Pending', reason: null }] } });
  api.renderFields();
  return { api, client, element };
}

test('renewing a claim preserves the unsaved correction and reasons while preventing new typing in flight', async () => {
  const app = runtime();
  app.element('correctedValue').value = 'Unsaved correction';
  app.element('reason').value = 'Field draft reason';
  app.element('documentReason').value = 'Document draft reason';
  let release;
  app.client.change = () => new Promise(resolve => { release = resolve; });
  const pending = app.api.act('claim');
  assert.equal(app.element('correctedValue').readOnly, true);
  assert.equal(app.element('reason').readOnly, true);
  assert.equal(app.element('documentReason').readOnly, true);
  release({ body: { ...app.api.state.session, version: 3 }, tag: '"review-3"' });
  await pending;
  assert.equal(app.element('correctedValue').value, 'Unsaved correction');
  assert.equal(app.element('reason').value, 'Field draft reason');
  assert.equal(app.element('documentReason').value, 'Document draft reason');
  assert.equal(app.element('correctedValue').readOnly, false);
});

test('a document without fields can be rejected with an independent current-document reason', async () => {
  const app = runtime();
  app.api.state.session.fields = [];
  app.api.renderFields();
  app.api.bind();
  app.element('reason').value = 'Stale hidden field reason';
  app.element('documentReason').value = 'Current document is unreadable.';
  let sent;
  app.client.change = async (base, action, command) => {
    sent = { action, command };
    return { body: { ...app.api.state.session, status: 'Rejected', version: 3 }, tag: '"review-3"' };
  };
  await app.element('rejectDocument').click();
  assert.equal(app.element('editor').hidden, true);
  assert.equal(sent.action, 'reject');
  assert.equal(sent.command.reason, 'Current document is unreadable.');
  assert.equal(app.api.state.session.status, 'Rejected');
});

test('switching documents clears field and document-level drafts rather than reusing prior audit reasons', async () => {
  const app = runtime();
  app.element('reason').value = 'Old field reason';
  app.element('correctedValue').value = 'Old correction';
  app.element('documentReason').value = 'Old document reason';
  await app.api.selectJob('second');
  assert.equal(app.element('originalValue').value, '');
  assert.equal(app.element('correctedValue').value, '');
  assert.equal(app.element('reason').value, '');
  assert.equal(app.element('documentReason').value, '');
  assert.equal(app.api.state.job, 'second');
});
