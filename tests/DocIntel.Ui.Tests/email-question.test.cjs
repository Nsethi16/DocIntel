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
    URLSearchParams, location: { search: '?tenant=tenant&run=one', assign: url => calls.push({ navigation: url }) },
    document: { getElementById: element },
    fetch: async (url, options) => {
      calls.push({ url, options });
      const response = responses.shift();
      if (response instanceof Error) throw response;
      assert.ok(response, 'No unrequested email mutation or retry is allowed');
      return { ok: typeof response.status !== 'number' || response.status < 400,
        status: typeof response.status === 'number' ? response.status : 200, json: async () => response };
    }
  });
  vm.runInContext(script, context);
  return { calls, element, load: context.loadQuestion };
}

test('opening an email question only reads authorized details; execution requires a separate explicit click', async () => {
  const app = runtime([{ status: 'AwaitingConfirmation', question: '<script>untrusted email</script>',
    destination: 'verified@example.com' }, { status: 404 }, { status: 'Queued' }]);
  await app.load();
  assert.equal(app.calls.length, 2);
  assert.equal(app.calls[0].options, undefined);
  assert.equal(app.element('emailQuestion').textContent, '<script>untrusted email</script>');
  assert.equal(app.element('confirmEmailQuestion').disabled, false);
  await app.element('confirmEmailQuestion').click();
  assert.equal(app.calls[2].options.method, 'POST');
  assert.equal(app.calls[2].options.body, undefined);
  assert.match(app.calls[2].url, /\/confirm$/);
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.match(app.element('emailRunLink').href, /^\/ask-run\.html/);
  await app.element('confirmEmailQuestion').click();
  assert.equal(app.calls.length, 3);
});

test('a forwarded or expired link never exposes question text or enables execution', async () => {
  const app = runtime([{ status: 404 }, { status: 404 }]);
  await app.load();
  assert.equal(app.element('emailQuestion').textContent, '');
  assert.equal(app.element('emailDestination').textContent, '');
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.match(app.element('emailNotice').textContent, /unavailable/);
});

test('unknown confirmation outcomes remain disabled and do not automatically resubmit', async () => {
  const app = runtime([{ status: 'AwaitingConfirmation', question: 'Question', destination: 'verified@example.com' },
    { status: 404 }, new Error('Connection lost')]);
  await app.load();
  await app.element('confirmEmailQuestion').click();
  await app.element('confirmEmailQuestion').click();
  assert.equal(app.calls.length, 3);
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.match(app.element('emailNotice').textContent, /No automatic retry/);
});

test('already-confirmed email links offer the original run rather than creating another question', async () => {
  const app = runtime([{ status: 'Completed', question: 'Question', destination: 'verified@example.com' }, { status: 404 }]);
  await app.load();
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.equal(app.element('emailRunLink').hidden, false);
  assert.equal(app.calls.length, 2);
});

test('personal sender verification starts only on an explicit click and never executes the email question', async () => {
  const link = '/auth/email/personal/start?request=' + 'A'.repeat(64);
  const app = runtime([{ status: 404 }, { available: true, connected: false, state: 'NotConnected' },
    { redirectUrl: link }, { available: true, connected: false, state: 'NotConnected' }]);
  await app.load();
  assert.equal(app.element('verifyPersonalEmail').disabled, false);
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  await app.element('verifyPersonalEmail').click();
  assert.equal(app.calls[2].options.method, 'POST');
  assert.match(app.calls[2].url, /\/sender\/connect$/);
  assert.ok(app.calls.some(call => call.navigation === link));
  assert.ok(app.calls.every(call => !call.url?.endsWith('/confirm')));
  assert.equal(app.element('emailQuestion').textContent, '');
});

test('personal sender controls reject external redirect links and do not retry failed changes', async () => {
  const app = runtime([{ status: 404 }, { available: true, connected: false, state: 'NotConnected' },
    { redirectUrl: 'https://untrusted.example' }, { available: true, connected: false, state: 'NotConnected' }]);
  await app.load();
  await app.element('verifyPersonalEmail').click();
  assert.ok(app.calls.every(call => !call.navigation));
  assert.equal(app.calls.filter(call => call.options?.method === 'POST').length, 1);
  assert.match(app.element('emailPersonalIdentityNotice').textContent, /invalid.*No automatic retry/);
});

test('disconnecting personal sender credentials clears the displayed question and disables confirmation', async () => {
  const app = runtime([{ status: 'AwaitingConfirmation', question: 'Authorized question', destination: 'sender@example.com' },
    { available: true, connected: true, state: 'Connected', address: 'sender@example.com' },
    { disconnected: true }, { available: true, connected: false, state: 'NotConnected' }]);
  await app.load();
  await app.element('disconnectPersonalEmail').click();
  assert.equal(app.element('confirmEmailQuestion').disabled, true);
  assert.equal(app.element('emailQuestion').textContent, '');
  assert.equal(app.element('emailDestination').textContent, '');
  assert.match(app.calls[2].url, /\/sender\/disconnect$/);
});
