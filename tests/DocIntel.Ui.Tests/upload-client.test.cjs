const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const { loadApplication } = require('./helpers/application.cjs');
const script = loadApplication('index');

function runtime(response) {
  const calls = [];
  const notices = [];
  const selections = [];
  const label = { textContent: '' };
  const file = { name: 'sample.pdf' };
  const input = { files: [file], value: 'sample.pdf', disabled: false };
  const context = vm.createContext({
    console,
    document: { getElementById: id => id === 'uploadBtnLabel' ? label : null },
    FormData: class {
      append(name, value) { this[name] = value; }
    },
    fetch: async (url, options) => {
      calls.push({ url, options });
      assert.equal(input.disabled, true);
      assert.equal(label.textContent, 'Uploading...');
      if (response instanceof Error) throw response;
      if (typeof response === 'function') return response(context);
      return response;
    }
  });
  vm.runInContext(script, context);
  context.notice = message => notices.push(message);
  context.selection = id => selections.push(id);
  vm.runInContext(`
    currentTenant = { tenantId: 'tenant/one' };
    showToast = notice;
    loadTenantJobs = selection;
  `, context);
  return { context, calls, notices, selections, label, input, file };
}

test('a successful upload submits once and selects the accepted job', async () => {
  const app = runtime({ ok: true, json: async () => ({ jobId: 'accepted-job' }) });
  await app.context.uploadAndRunTenantDocument(app.input);
  assert.equal(app.calls.length, 1);
  assert.equal(app.calls[0].url, '/api/harness/tenants/tenant%2Fone/jobs');
  assert.equal(app.calls[0].options.method, 'POST');
  assert.equal(app.calls[0].options.body.file, app.file);
  assert.deepEqual(app.selections, ['accepted-job']);
  assert.match(app.notices[0], /Submitted sample.pdf/);
  assert.equal(app.input.disabled, false);
  assert.equal(app.input.value, '');
  assert.equal(app.label.textContent, 'Upload PDF');
});

for (const [status, message] of [
  [400, /valid PDF|verification/], [401, /Sign in again/], [403, /permission/],
  [413, /25 MiB/], [429, /allowance/], [500, /HTTP 500/]
]) {
  test(`HTTP ${status} shows actionable failure and correlation without retrying or exposing the body`, async () => {
    const app = runtime({
      ok: false, status,
      headers: { get: () => 'safe-reference:one' },
      json: () => { throw new Error('Raw provider or request details must not be read.'); }
    });
    await app.context.uploadAndRunTenantDocument(app.input);
    assert.equal(app.calls.length, 1);
    assert.equal(app.selections.length, 0);
    assert.match(app.notices[0], message);
    assert.match(app.notices[0], /Reference: safe-reference:one/);
    assert.equal(app.input.disabled, false);
    assert.equal(app.input.value, '');
    assert.equal(app.label.textContent, 'Upload PDF');
  });
}

test('network failure is visible, does not resubmit and restores the upload controls', async () => {
  const app = runtime(new TypeError('private network details'));
  await app.context.uploadAndRunTenantDocument(app.input);
  assert.equal(app.calls.length, 1);
  assert.match(app.notices[0], /could not be confirmed/);
  assert.doesNotMatch(app.notices[0], /private network details/);
  assert.equal(app.input.disabled, false);
  assert.equal(app.label.textContent, 'Upload PDF');
});

test('an accepted upload cannot switch selection into another tenant', async () => {
  const app = runtime(context => {
    vm.runInContext("currentTenant = { tenantId: 'other' };", context);
    return { ok: true, json: async () => ({ jobId: 'first-tenant-job' }) };
  });
  await app.context.uploadAndRunTenantDocument(app.input);
  assert.equal(app.selections.length, 0);
});

test('malformed correlation text is not displayed', async () => {
  const app = runtime({ ok: false, status: 500, headers: { get: () => '<script>untrusted</script>' } });
  await app.context.uploadAndRunTenantDocument(app.input);
  assert.match(app.notices[0], /HTTP 500/);
  assert.doesNotMatch(app.notices[0], /script|untrusted|Reference/);
});
