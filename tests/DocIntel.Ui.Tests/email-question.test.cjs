const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'email-question.js'), 'utf8')
  .replace(/\r\n/g, '\n');
const script = source.replace(/\n  load\(\);\n\}\)\(\);\s*$/, '\n  globalThis.loadQuestion = load;\n})();');
assert.notEqual(source, script);

function runtime(responses) {
  const elements = new Map(), calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, { textContent: '', disabled: id === 'confirmEmailQuestion',
      hidden: id === 'emailRunLink', handlers: new Map(),
      set innerHTML(value) { throw new Error('Email text cannot be active HTML'); },
      addEventListener(name, callback) { this.handlers.set(name, callback); },
      click() { return this.handlers.get('click')?.(); }
    });
    return elements.get(id);
  }
  const context = vm.createContext({
    URLSearchParams, location: { search: '?tenant=tenant&run=one' },
    document: { getElementById: element },
    fetch: async (url, options) => {
      calls.push({ url, options });
      const response = responses.shift();
      if (response instanceof Error) throw response;
      assert.ok(response, 'No unrequested email mutation or retry is allowed');
      return { ok: typeof response.status !== 'number' || response.status < 400, json: async () => response };
    }
  });
  vm.runInContext(script, context);
  return { calls, element, load: context.loadQuestion };
}

test('opening an email question only reads authorized details; execution requires a separate explicit click', async () => {
  const app = runtime([{ status: 'AwaitingConfirmation', question: '<script>untrusted email</script>',
    destination: 'verified@example.com' }, { status: 'Queued' }]);
  await app.load();
  assert.equal(app.calls.length, 1);
  assert.equal(app.calls[0].options, undefined);
  assert.equal(app.element('emailQuestion').textContent, '<script>untrusted email</script>');
  assert.equal(app.element('confirmEmailQuestion').disabled, false);
  await app.element('confirmEmailQuestion').click();
  assert.equal(app.calls[1].options.method, 'POST');
  assert.equal(app.calls[1].options.body, undefined);
  assert.match(app.calls[1].url, /\/confirm$/);
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.match(app.element('emailRunLink').href, /^\/ask-run\.html/);
  await app.element('confirmEmailQuestion').click();
  assert.equal(app.calls.length, 2);
});

test('a forwarded or expired link never exposes question text or enables execution', async () => {
  const app = runtime([{ status: 404 }]);
  await app.load();
  assert.equal(app.element('emailQuestion').textContent, '');
  assert.equal(app.element('emailDestination').textContent, '');
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.match(app.element('emailNotice').textContent, /unavailable/);
});

test('unknown confirmation outcomes remain disabled and do not automatically resubmit', async () => {
  const app = runtime([{ status: 'AwaitingConfirmation', question: 'Question', destination: 'verified@example.com' },
    new Error('Connection lost')]);
  await app.load();
  await app.element('confirmEmailQuestion').click();
  await app.element('confirmEmailQuestion').click();
  assert.equal(app.calls.length, 2);
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.match(app.element('emailNotice').textContent, /No automatic retry/);
});

test('already-confirmed email links offer the original run rather than creating another question', async () => {
  const app = runtime([{ status: 'Completed', question: 'Question', destination: 'verified@example.com' }]);
  await app.load();
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.equal(app.element('emailRunLink').hidden, false);
  assert.equal(app.calls.length, 1);
});
