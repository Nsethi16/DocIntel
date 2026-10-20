const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'admin.html'), 'utf8');
const script = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)]
  .map(match => match[1]).join('\n').replace(/\n\s*init\(\);\s*$/, '');

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
        json: async () => result };
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
