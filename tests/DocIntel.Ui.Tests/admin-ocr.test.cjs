const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');

const { loadApplication } = require('./helpers/application.cjs');
const script = loadApplication('admin');

function runtime(responses = []) {
  const elements = new Map();
  const calls = [];
  function element(id) {
    if (!elements.has(id)) elements.set(id, {
      value: '', checked: false, disabled: false,
      addEventListener() {},
      classList: { toggle() {}, add() {}, remove() {} }
    });
    return elements.get(id);
  }
  const context = vm.createContext({
    console, URLSearchParams, setTimeout: () => 0,
    window: { location: { search: '' } },
    document: { getElementById: element, querySelectorAll: () => [] },
    fetch: async (url, options = {}) => {
      calls.push({ url, options });
      let result = responses.shift();
      if (typeof result === 'function') result = await result();
      assert.ok(result, 'Unexpected budget request');
      if (result instanceof Error) throw result;
      return { ok: !result.status || result.status < 400, status: result.status || 200,
        json: result.readBody || (async () => result) };
    }
  });
  vm.runInContext(script, context);
  vm.runInContext("currentTenant = {tenantId: 'tenant', capabilities: {}, ocr: {mode: 'Auto'}};", context);
  element('ocrModeSelect').value = 'Auto';
  return { context, element, calls };
}

test('the admin OCR selector persists every supported mode without altering the source tenant object', () => {
  const app = runtime();
  for (const mode of ['Auto', 'Always', 'Never']) {
    app.element('ocrModeSelect').value = mode;
    const payload = vm.runInContext('buildPayloadFromUi()', app.context);
    assert.equal(payload.ocr.mode, mode);
    assert.equal(vm.runInContext('currentTenant.ocr.mode', app.context), 'Auto');
  }
});

test('changing only OCR policy marks the harness dirty and reverting it restores the clean state', () => {
  const app = runtime();
  vm.runInContext(`
    const initial = buildPayloadFromUi();
    savedSnapshotJson = JSON.stringify({
      decisionModelId: initial.decisionModelId, defaultModelId: initial.defaultModelId,
      ocr: initial.ocr, reviewPolicy: initial.reviewPolicy, capabilities: initial.capabilities
    });

    updateDirtyState();
  `, app.context);
  assert.equal(app.element('btnSaveHarness').disabled, true);
  app.element('ocrModeSelect').value = 'Always';
  vm.runInContext('updateDirtyState()', app.context);
  assert.equal(app.element('btnSaveHarness').disabled, false);
  app.element('ocrModeSelect').value = 'Auto';
  vm.runInContext('updateDirtyState()', app.context);
  assert.equal(app.element('btnSaveHarness').disabled, true);
});

function allowance(version = '1') {
  return { period: '2026-10', reservedPages: 5, requestCount: 2, remainingPages: 95,
    budget: { monthlyPageLimit: 100, warningPercent: 80, version } };
}

test('live budget saves use the loaded version, refresh usage, and do not modify the processing harness', async () => {
  const app = runtime([allowance(), { monthlyPageLimit: 250, warningPercent: 70, version: '2' }, allowance('2')]);
  await vm.runInContext('loadOcrUsage()', app.context);
  assert.match(app.element('ocrUsageStatus').textContent, /5 reserved pages/);
  app.element('ocrPageLimit').value = '250';
  app.element('ocrWarningPercent').value = '70';
  await vm.runInContext('saveOcrBudget()', app.context);
  const sent = JSON.parse(app.calls[1].options.body);
  assert.deepEqual(sent, { monthlyPageLimit: 250, warningPercent: 70, version: '1' });
  assert.equal(app.calls[1].options.method, 'PUT');
  assert.equal(app.calls.length, 3);
  assert.equal(vm.runInContext('currentTenant.ocr.mode', app.context), 'Auto');
});

test('a conflicting or unconfirmed budget save requires reload and is not automatically repeated', async () => {
  const app = runtime([allowance(), { status: 409, title: 'The OCR budget changed.' }]);
  await vm.runInContext('loadOcrUsage()', app.context);
  app.element('ocrPageLimit').value = '250';
  app.element('ocrWarningPercent').value = '80';
  await vm.runInContext('saveOcrBudget()', app.context);
  assert.match(app.element('ocrUsageStatus').textContent, /Reload/);
  await vm.runInContext('saveOcrBudget()', app.context);
  assert.equal(app.calls.length, 2);
  assert.equal(app.element('ocrBudgetSave').disabled, true);
});

test('an outstanding allowance read cannot enable edits for a newly selected tenant', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const app = runtime([() => pending]);
  const loaded = vm.runInContext('loadOcrUsage()', app.context);
  vm.runInContext("currentTenant = { tenantId: 'second' };", app.context);
  release(allowance());
  await loaded;
  assert.equal(app.element('ocrBudgetSave').disabled, true);
  assert.equal(vm.runInContext('ocrBudgetSnapshot', app.context), null);
});

test('a harness conflict preserves the draft, displays the server version, and blocks blind retries', async () => {
  const app = runtime([{ status: 409, current: { tenantId: 'tenant', harnessVersion: 3,
    displayName: '<server text>', ocr: { mode: 'Never' } } }]);
  vm.runInContext('currentTenant.harnessVersion = 2;', app.context);
  app.element('ocrModeSelect').value = 'Always';
  await vm.runInContext('saveTenantHarness()', app.context);
  assert.equal(app.calls.length, 1);
  assert.equal(JSON.parse(app.calls[0].options.body).harnessVersion, 2);
  assert.equal(app.element('ocrModeSelect').value, 'Always');
  assert.equal(vm.runInContext('currentTenant.harnessVersion', app.context), 2);
  assert.equal(JSON.parse(app.element('harnessDraft').textContent).ocr.mode, 'Always');
  assert.equal(JSON.parse(app.element('harnessServer').textContent).harnessVersion, 3);
  assert.match(app.element('harnessSaveMessage').textContent, /draft is preserved/);
  assert.equal(app.element('btnSaveHarness').disabled, true);
  await vm.runInContext('saveTenantHarness()', app.context);
  assert.equal(app.calls.length, 1);
});

test('an unknown harness save outcome keeps the draft and requires reload instead of automatic resubmission', async () => {
  const app = runtime([new Error('Connection lost')]);
  app.element('ocrModeSelect').value = 'Always';
  await vm.runInContext('saveTenantHarness()', app.context);
  assert.match(app.element('harnessSaveMessage').textContent, /outcome is unknown/);
  assert.equal(app.element('ocrModeSelect').value, 'Always');
  assert.equal(app.element('btnSaveHarness').disabled, true);
  await vm.runInContext('saveTenantHarness()', app.context);
  assert.equal(app.calls.length, 1);
});

test('a completed save keeps edits typed while the request was in flight and advances only the base version', async () => {
  let release;
  const pending = new Promise(resolve => { release = resolve; });
  const app = runtime([() => pending]);
  vm.runInContext(`
    currentTenant.harnessVersion = 2;
    renderSidebarTenants = () => {};
    renderMainCanvas = () => { throw new Error('Must not discard the newer draft'); };
    showToast = () => {};
  `, app.context);
  app.element('ocrModeSelect').value = 'Always';
  const saving = vm.runInContext('saveTenantHarness()', app.context);
  app.element('ocrModeSelect').value = 'Never';
  await vm.runInContext('saveTenantHarness()', app.context);
  assert.equal(app.calls.length, 1);
  release({ tenantId: 'tenant', harnessVersion: 3, displayName: 'Tenant', ocr: { mode: 'Always' } });
  await saving;
  assert.equal(vm.runInContext('currentTenant.harnessVersion', app.context), 3);
  assert.equal(app.element('ocrModeSelect').value, 'Never');
  assert.equal(app.element('btnSaveHarness').disabled, false);
  assert.equal(JSON.parse(vm.runInContext('savedSnapshotJson', app.context)).ocr.mode, 'Always');
});

test('switching tenants during a harness save cannot overwrite the newly selected tenant', async () => {
  let release;
  const app = runtime([() => new Promise(resolve => { release = resolve; })]);
  const saving = vm.runInContext('saveTenantHarness()', app.context);
  vm.runInContext("currentTenant = { tenantId: 'second', harnessVersion: 8 };", app.context);
  release({ tenantId: 'tenant', harnessVersion: 3, ocr: { mode: 'Always' } });
  await saving;
  assert.equal(vm.runInContext('currentTenant.tenantId', app.context), 'second');
  assert.equal(vm.runInContext('currentTenant.harnessVersion', app.context), 8);
  assert.equal(app.calls.length, 1);
});

test('a delayed conflict body cannot block or compare the newly selected tenant', async () => {
  let release;
  let bodyStarted;
  const readingBody = new Promise(resolve => { bodyStarted = resolve; });
  const app = runtime([{ status: 409, readBody: () => new Promise(resolve => {
    release = resolve;
    bodyStarted();
  }) }]);
  const saving = vm.runInContext('saveTenantHarness()', app.context);
  await readingBody;
  assert.equal(typeof release, 'function');
  vm.runInContext("currentTenant = { tenantId: 'second', harnessVersion: 8 };", app.context);
  release({ current: { tenantId: 'tenant', harnessVersion: 3 } });
  await saving;
  assert.equal(vm.runInContext('currentTenant.tenantId', app.context), 'second');
  assert.equal(vm.runInContext('harnessSaveBlocked', app.context), false);
  assert.equal(app.element('harnessServer').textContent, undefined);
  assert.equal(app.calls.length, 1);
});
