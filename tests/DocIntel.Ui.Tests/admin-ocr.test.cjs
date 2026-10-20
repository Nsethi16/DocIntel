const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'admin.html'), 'utf8');
const script = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)]
  .map(match => match[1]).join('\n').replace(/\n\s*init\(\);\s*$/, '');

function runtime() {
  const elements = new Map();
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
    document: { getElementById: element, querySelectorAll: () => [] }
  });
  vm.runInContext(script, context);
  vm.runInContext("currentTenant = {tenantId: 'tenant', capabilities: {}, ocr: {mode: 'Auto'}};", context);
  element('ocrModeSelect').value = 'Auto';
  return { context, element };
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
      ocr: initial.ocr, capabilities: initial.capabilities
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
