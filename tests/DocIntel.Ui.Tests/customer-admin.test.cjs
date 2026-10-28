const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const { loadApplication } = require('./helpers/application.cjs');

const root = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const html = fs.readFileSync(path.join(root, 'admin.html'), 'utf8');
const agentSource = fs.readFileSync(path.join(root, 'agent-settings.js'), 'utf8');

function runtime({ agents = false, responses = [], realRender = false } = {}) {
  const elements = new Map();
  const calls = [];
  const listeners = {};
  const document = { activeElement: null };
  function make(id = '') {
    const classes = new Set();
    return {
      id, value: '', textContent: '', checked: false, disabled: false, hidden: id === 'promptEditor',
      inert: false, open: false, isConnected: true, options: [], children: [], dataset: {}, style: {},
      classList: {
        add(name) { classes.add(name); }, remove(name) { classes.delete(name); },
        contains(name) { return classes.has(name); },
        toggle(name, enabled = !classes.has(name)) {
          if (enabled) classes.add(name); else classes.delete(name);
          return enabled;
        }
      },
      setAttribute(key, value) { this[key] = value; },
      addEventListener(type, handler) { this[type + 'Handler'] = handler; },
      focus() { document.activeElement = this; },
      showModal() { this.open = true; },
      close() { this.open = false; this.closeHandler?.(); },
      replaceChildren(...children) { this.children = children; },
      append(...children) { this.children.push(...children); },
      appendChild(child) { this.children.push(child); if (this.id.includes('ModelSelect')) this.options.push(child); },
      set innerHTML(value) { this.html = value; this.children = []; },
      get innerHTML() { return this.html || ''; }
    };
  }
  function element(id) {
    if (!elements.has(id)) elements.set(id, make(id));
    return elements.get(id);
  }
  document.getElementById = element;
  document.createElement = () => make();
  document.querySelectorAll = () => [];
  document.querySelector = () => null;
  for (const [id, value] of [['harnessDecisionModelSelect', 'decision-1'],
    ['harnessLanguageModelSelect', 'phi-4'], ['ocrModeSelect', 'Auto'], ['promptFilterModeSelect', 'All']]) {
    element(id).value = value;
    const option = make();
    option.value = value;
    option.textContent = value;
    element(id).options.push(option);
  }
  const context = vm.createContext({
    console, URLSearchParams, setTimeout: () => 0, clearTimeout() {},
    window: { location: { search: '', href: '' }, addEventListener(type, handler) { listeners[type] = handler; } },
    document,
    fetch: async (url, options = {}) => {
      calls.push({ url, options });
      if (url.endsWith('/jobs')) return { ok: true, json: async () => [] };
      let result = responses.shift();
      assert.ok(result, `Unexpected request ${url}`);
      if (typeof result === 'function') result = await result();
      if (result instanceof Error) throw result;
      return { ok: !result.status || result.status < 400, status: result.status || 200,
        json: async () => result.body || result };
    }
  });
  if (agents) vm.runInContext(agentSource, context);
  vm.runInContext(loadApplication('admin'), context);
  vm.runInContext(`
    currentTenant = {tenantId:'tenant', displayName:'One', harnessVersion:2, capabilities:{}, ocr:{mode:'Auto'}};
    tenants = [currentTenant, {tenantId:'second', displayName:'Two', harnessVersion:1, capabilities:{}}];
    renderSidebarTenants = () => {};
    clearPromptColumnForm();
    savedSnapshotJson = settingsJson();
    let renders = 0;
  `, context);
  if (!realRender) vm.runInContext(`
    renderMainCanvas = (preserveBudget = false) => {
      renders++;
      document.getElementById('ocrModeSelect').value = currentTenant.ocr?.mode || 'Auto';
      document.getElementById('promptEditor').hidden = true;
      if (!preserveBudget) {
        ocrBudgetSnapshot = null; ocrBudgetTenantId = null; ocrBudgetBaseline = ''; ocrBudgetUnconfirmed = false;
      }
      savedSnapshotJson = settingsJson();
    };
  `, context);
  const run = script => vm.runInContext(script, context);
  const action = (name, args = []) => context.registeredActions[name].run(args);
  return { context, document, element, calls, responses, listeners, run, action };
}

const tick = () => new Promise(resolve => setImmediate(resolve));
function dirty(app) { app.element('ocrModeSelect').value = 'Always'; }
function choose(app, choice) { app.action('resolveSettingsDraft_arg', [choice]); }
function setup(tenantId = 'tenant', overrides = {}) {
  return { workspace: { tenantId }, models: [], checks: [],
    channels: { email: { configured: false }, teams: { configured: false } }, ...overrides };
}

test('real markup has one simple six-tab navigation, independent configure buttons, native guard and shared foundation', () => {
  const tabs = [...html.matchAll(/<button[^>]*role="tab"[^>]*>([^<]+)<\/button>/g)];
  assert.deepEqual(tabs.map(match => match[1]), ['Setup', 'Processing', 'Custom analysis', 'Agent', 'Integrations', 'People']);
  const ids = [...html.matchAll(/\bid="([^"]+)"/g)].map(match => match[1]);
  assert.equal(new Set(ids).size, ids.length, 'Control ids must stay unique');
  for (const key of ['split', 'identify', 'translation', 'insight', 'entity', 'dataExtraction']) {
    assert.match(html, new RegExp(`id="${key}Chevron"[^>]*aria-expanded="false"[^>]*aria-controls="${key}Drawer"`));
  }
  assert.doesNotMatch(html, /class="feature-tile-left"[^>]*data-click/);
  assert.match(html, /<dialog id="settingsDraftDialog"/);
  assert.ok(html.indexOf('/shared/customer.js') > html.indexOf('/shared/actions.js'));
  assert.ok(html.indexOf('/shared/customer.js') < html.indexOf('/admin.js'));
  assert.match(html, /\/shared\/customer.css/);
  assert.doesNotMatch(html, /\bon(?:click|input|change)=/i);
  const analysis = html.slice(html.indexOf('<section id="customAnalysisPanel"'));
  assert.ok(analysis.indexOf('id="savedPromptsList"') < analysis.indexOf('id="promptEditor"'));
  assert.match(analysis, /id="promptEditor" hidden/);
});

test('compact model rows keep labeled controls and short live status with one shared verification explanation', () => {
  for (const [select, status] of [['harnessDecisionModelSelect', 'decisionModelStatus'],
    ['harnessLanguageModelSelect', 'languageModelStatus']]) {
    assert.match(html, new RegExp(`<label[^>]*for="${select}"`));
    assert.match(html, new RegExp(`id="${select}"[^>]*aria-describedby="${status} modelVerificationNote"`));
    assert.match(html, new RegExp(`id="${status}"[^>]*role="status"`));
  }
  assert.equal([...html.matchAll(/id="modelVerificationNote"/g)].length, 1);
  assert.match(html, /<details class="model-verification-details">[\s\S]*per-model deployment probe/);
  assert.doesNotMatch(html, /class="model-icon-box"/);
});

test('Stay and Escape protect the active workspace and return keyboard focus', async () => {
  const app = runtime();
  const trigger = app.element('navDocumentIntel');
  trigger.focus();
  dirty(app);
  const navigating = app.action('selectTenant_arg', ['second']);
  assert.equal(app.element('settingsDraftDialog').open, true);
  let prevented = false;
  app.element('settingsDraftDialog').cancelHandler({ preventDefault() { prevented = true; } });
  assert.equal(await navigating, false);
  assert.equal(prevented, true);
  assert.equal(app.run('currentTenant.tenantId'), 'tenant');
  assert.equal(app.element('ocrModeSelect').value, 'Always');
  assert.equal(app.document.activeElement, trigger);
});

test('Discard switches workspaces without saving; clean navigation does not prompt', async () => {
  const app = runtime();
  dirty(app);
  const navigating = app.action('selectTenant_arg', ['second']);
  choose(app, 'discard');
  assert.equal(await navigating, true);
  assert.equal(app.run('currentTenant.tenantId'), 'second');
  assert.equal(app.calls.length, 0);
  await app.action('selectTenant_arg', ['tenant']);
  assert.equal(app.element('settingsDraftDialog').open, false);
});

test('Save changes awaits a successful atomic versioned PUT before switching', async () => {
  let release;
  const app = runtime({ responses: [() => new Promise(resolve => { release = resolve; })] });
  dirty(app);
  const navigating = app.action('selectTenant_arg', ['second']);
  choose(app, 'save');
  await tick();
  assert.equal(app.run('currentTenant.tenantId'), 'tenant');
  const sent = JSON.parse(app.calls[0].options.body);
  assert.equal(sent.harnessVersion, 2);
  assert.equal(app.calls[0].options.method, 'PUT');
  release({ ...sent, harnessVersion: 3 });
  assert.equal(await navigating, true);
  assert.equal(app.run('currentTenant.tenantId'), 'second');
});

for (const outcome of [
  { status: 409, current: { tenantId: 'tenant', harnessVersion: 3 } },
  { status: 500 }, new Error('Connection lost')
]) test(`failed, conflicting or unknown save (${outcome.status || 'network'}) never navigates`, async () => {
  const app = runtime({ responses: [outcome] });
  dirty(app);
  const navigating = app.action('openWorkspaceForTenant');
  choose(app, 'save');
  await tick();
  assert.equal(app.context.window.location.href, '');
  assert.equal(app.element('settingsDraftDialog').open, true);
  assert.match(app.element('settingsDraftStatus').textContent, /save failed, conflicted/);
  assert.equal(app.element('ocrModeSelect').value, 'Always');
  assert.equal(app.run('harnessSaveBlocked'), true);
  choose(app, 'stay');
  assert.equal(await navigating, false);
  assert.equal(app.calls.length, 1);
});

test('edits typed during a successful save remain dirty and prevent navigation', async () => {
  let release;
  const app = runtime({ responses: [() => new Promise(resolve => { release = resolve; })] });
  dirty(app);
  const navigating = app.action('openWorkspaceOnboarding');
  choose(app, 'save');
  await tick();
  const sent = JSON.parse(app.calls[0].options.body);
  app.element('ocrModeSelect').value = 'Never';
  release({ ...sent, harnessVersion: 3 });
  await tick();
  assert.equal(app.context.window.location.href, '');
  assert.equal(app.element('ocrModeSelect').value, 'Never');
  assert.equal(app.run('currentTenant.harnessVersion'), 3);
  assert.equal(app.run('hasUnsavedSettings()'), true);
  choose(app, 'stay');
  assert.equal(await navigating, false);
});

test('workspace links, new workspace and conflict reload share the draft guard', async () => {
  const app = runtime();
  dirty(app);
  for (const name of ['openWorkspaceOnboarding', 'expandAndOpenNewTenant', 'reloadHarnessAfterConflict']) {
    const pending = app.action(name);
    assert.equal(app.element('settingsDraftDialog').open, true);
    choose(app, 'stay');
    assert.equal(await pending, false);
    assert.equal(app.calls.length, 0);
  }
  const leave = app.action('openWorkspaceOnboarding');
  choose(app, 'discard');
  await leave;
  assert.equal(app.context.window.location.href, '/?tenant=tenant&onboarding=1');
});

test('beforeunload protects unsaved settings, editor-only changes and an in-flight save', async () => {
  const app = runtime();
  const event = () => ({ prevented: false, preventDefault() { this.prevented = true; } });
  let attempt = event();
  app.listeners.beforeunload(attempt);
  assert.equal(attempt.prevented, false);
  await app.action('newCustomAnalysis');
  app.element('promptColumnNameInput').value = 'Draft';
  attempt = event();
  app.listeners.beforeunload(attempt);
  assert.equal(attempt.prevented, true);
  app.run("document.getElementById('promptEditor').hidden = true; harnessSaveInFlight = true;");
  attempt = event();
  app.listeners.beforeunload(attempt);
  assert.equal(attempt.prevented, true);
});

test('custom analyses stage, update and remove without autosaving; ids and individual definitions survive', async () => {
  const app = runtime();
  await app.action('newCustomAnalysis');
  app.element('promptColumnNameInput').value = 'Contract check';
  app.element('promptInstructionsInput').value = 'Find {{Document.Content}} citations.';
  assert.equal(app.action('savePromptColumnFromForm'), true);
  const id = app.run('workingPromptColumns[0].columnId');
  assert.equal(app.run('hasUnsavedSettings()'), true);
  await app.action('editPromptColumn_arg', [0]);
  app.element('promptInstructionsInput').value = 'Updated instructions';
  app.action('savePromptColumnFromForm');
  assert.equal(app.run('workingPromptColumns[0].columnId'), id);
  assert.equal(app.run('workingPromptColumns[0].promptInstructions'), 'Updated instructions');
  await app.action('deletePromptColumn_arg', [0]);
  assert.equal(app.run('workingPromptColumns.length'), 0);
  assert.equal(app.calls.length, 0);
});

test('incomplete custom-analysis editor blocks Save and keeps both the editor and navigation draft', async () => {
  const app = runtime();
  await app.action('newCustomAnalysis');
  app.element('promptColumnNameInput').value = 'Incomplete';
  app.element('promptInstructionsInput').value = '';
  const pending = app.action('selectTenant_arg', ['second']);
  choose(app, 'save');
  await tick();
  assert.equal(app.run('currentTenant.tenantId'), 'tenant');
  assert.equal(app.element('promptColumnNameInput').value, 'Incomplete');
  assert.equal(app.calls.length, 0);
  choose(app, 'stay');
  await pending;
});

test('a late conflict reload cannot erase a newly typed draft', async () => {
  let release;
  const app = runtime({ responses: [() => new Promise(resolve => { release = resolve; })] });
  dirty(app);
  app.run('harnessSaveBlocked = true;');
  const pending = app.action('reloadHarnessAfterConflict');
  choose(app, 'discard');
  await tick();
  app.element('ocrModeSelect').value = 'Never';
  release({ tenantId: 'tenant', harnessVersion: 5, ocr: { mode: 'Auto' } });
  await pending;
  assert.equal(app.element('ocrModeSelect').value, 'Never');
  assert.equal(app.run('currentTenant.harnessVersion'), 2);
  assert.match(app.element('harnessSaveMessage').textContent, /New edits/);
});

test('real rendering keeps an unknown selected model and independent OCR allowance draft', () => {
  const app = runtime({ realRender: true });
  app.run(`
    currentTenant.defaultModelId = 'private-model-alias';
    ocrBudgetTenantId = 'tenant';
    ocrBudgetSnapshot = {monthlyPageLimit:100, warningPercent:80, version:'1'};
    ocrBudgetBaseline = JSON.stringify(['100','80']);
  `);
  app.element('ocrPageLimit').value = '250';
  app.element('ocrWarningPercent').value = '80';
  app.run('renderMainCanvas(true)');
  assert.equal(app.element('harnessLanguageModelSelect').value, 'private-model-alias');
  assert.ok(app.element('harnessLanguageModelSelect').options.some(option => option.value === 'private-model-alias'));
  assert.equal(app.element('ocrPageLimit').value, '250');
  assert.equal(app.run('Boolean(ocrBudgetDirty())'), true);
});

test('payload preserves unedited advanced policy and capability fields and the expected version', () => {
  const app = runtime();
  app.run(`
    currentTenant.reviewPolicy = {enabled:true, requiresDualReview:true, dueHours:72};
    currentTenant.capabilities.DocumentTranslation = {
      enabled:true, customFlag:'keep', documentTranslation:{targetLanguage:'en', providerOption:'keep'}
    };
    currentTenant.capabilities.UnmappedFeature = {enabled:true, important:'keep'};
  `);
  const payload = app.run('buildPayloadFromUi()');
  assert.equal(payload.harnessVersion, 2);
  assert.equal(payload.reviewPolicy.requiresDualReview, true);
  assert.equal(payload.capabilities.DocumentTranslation.customFlag, 'keep');
  assert.equal(payload.capabilities.DocumentTranslation.documentTranslation.providerOption, 'keep');
  assert.equal(payload.capabilities.UnmappedFeature.important, 'keep');
});

test('setup uses only the admin setup GET and never equates endpoint configuration with deployment availability', async () => {
  const app = runtime({ agents: true, responses: [setup('tenant', {
    models: [
      { modelId: 'decision-1', displayName: 'Decision', purpose: 'decision', configured: true,
        state: 'EndpointConfiguredUnverified' },
      { modelId: 'phi-4', displayName: 'Phi', purpose: 'language', configured: false, state: 'ActionRequired' },
      { modelId: 'configured-route', displayName: 'Configured route', purpose: 'language', configured: true,
        supportsPdfInputs: true, state: 'EndpointConfiguredUnverified' }
    ],
    checks: [{ id: 'models', title: '<script>not executable</script>', state: 'ActionRequired', description: 'Review models' }],
    channels: { email: { configured: true, enabled: true, verified: false, address: 'approved@example.invalid',
      setupSteps: ['Ask your operator to verify delivery.'] }, teams: { configured: false, verified: false } }
  })] });
  await app.context.DocIntelAgentSettings.connections('tenant');
  assert.match(app.calls[0].url, /\/api\/harness\/tenants\/tenant\/setup$/);
  assert.equal(app.calls[0].options.method, undefined);
  assert.match(app.element('decisionModelStatus').textContent, /Connection configured; deployment not verified/);
  assert.doesNotMatch(app.element('decisionModelStatus').textContent, /\bAvailable\b|\bReady\b/);
  assert.equal(app.element('harnessLanguageModelSelect').options.find(option => option.value === 'phi-4').disabled, true);
  assert.equal(app.element('harnessLanguageModelSelect').value, 'phi-4');
  assert.match(app.element('agentEmailStatus').textContent, /delivery not verified/);
  assert.match(app.element('agentTeamsStatus').textContent, /Not connected/);
  assert.equal(app.element('workspaceSetupChecks').children[0].children[0].children[0].textContent,
    '<script>not executable</script>');
});

test('missing setup endpoint never implies complete setup and does not disable unknown model routes', async () => {
  const app = runtime({ agents: true, responses: [{ status: 404 }] });
  await app.context.DocIntelAgentSettings.connections('tenant');
  assert.match(app.element('workspaceSetupStatus').textContent, /unavailable.*unknown/);
  assert.match(app.element('agentEmailStatus').textContent, /unavailable/);
  assert.match(app.element('languageModelStatus').textContent, /Configuration unknown/);
  assert.equal(app.element('harnessLanguageModelSelect').value, 'phi-4');
  assert.equal(app.element('harnessLanguageModelSelect').options[0].disabled, false);
  assert.equal(app.calls.length, 1);
});

test('late setup reads cannot change a newly selected model draft or workspace', async () => {
  let release;
  const app = runtime({ agents: true, responses: [
    () => new Promise(resolve => { release = resolve; }), setup('second')
  ] });
  const pending = app.context.DocIntelAgentSettings.connections('tenant');
  await tick();
  await app.context.DocIntelAgentSettings.connections('second');
  app.element('harnessLanguageModelSelect').value = 'new-draft-alias';
  release(setup('tenant', { channels: { email: { configured: true, address: 'old@example.invalid' } } }));
  await pending;
  assert.equal(app.element('harnessLanguageModelSelect').value, 'new-draft-alias');
  assert.doesNotMatch(app.element('agentEmailStatus').textContent, /old@example/);
});

test('all sections use tabpanel hidden/inert state and People still removes harness actions from focus', () => {
  const app = runtime({ agents: true });
  for (const [name, id] of [['setup', 'setupSettingsPanel'], ['processing', 'processingSettingsPanel'],
    ['analysis', 'customAnalysisPanel'], ['agent', 'agentSettingsPanel'],
    ['integrations', 'integrationsSettingsPanel'], ['users', 'tenantUsersPanel']]) {
    app.context.DocIntelAgentSettings.section(name);
    assert.equal(app.element(id).hidden, false);
    assert.equal(app.element(id).inert, false);
    assert.equal(app.element('harnessHeaderActions').inert, name === 'users');
    assert.equal(app.element('agentSettingsPanel').hidden, name !== 'agent');
  }
});

test('processing changes are draft-only and incomplete recognition rules cannot be silently dropped on save', async () => {
  const app = runtime();
  const fields = { docName: { value: '', focus() {} }, prompt: { value: '', focus() {} } };
  const row = { querySelector: selector => fields[selector.match(/"([^"]+)"/)[1]] };
  app.document.querySelectorAll = selector => selector === '#identificationRulesList .rule-item' ? [row] : [];
  app.run('processingDraftBaseline = processingDraftJson(); savedSnapshotJson = settingsJson();');
  fields.docName.value = 'Draft document type';
  app.action('onHarnessChanged');
  assert.equal(app.run('hasUnsavedSettings()'), true);
  assert.equal(await app.action('saveTenantHarness'), false);
  assert.match(app.element('toast').children[0].textContent, /Complete or remove unfinished/);
  assert.equal(fields.docName.value, 'Draft document type');
  assert.equal(app.calls.length, 0);
});

test('recognition rules retain inactive flags and advanced properties when edited', () => {
  const app = runtime();
  const values = { docName: { value: 'Agreement' }, prompt: { value: 'Recognize agreement headings' } };
  const row = { ruleSource: { isActive: false, customThreshold: 0.9 },
    querySelector: selector => values[selector.match(/"([^"]+)"/)[1]] };
  app.document.querySelectorAll = selector => selector === '#identificationRulesList .rule-item' ? [row] : [];
  const rule = app.run('buildPayloadFromUi().capabilities.IdentifyDocuments.identifyDocuments.rules[0]');
  assert.equal(rule.isActive, false);
  assert.equal(rule.customThreshold, 0.9);
  assert.equal(rule.documentName, 'Agreement');
});

test('an unconfirmed OCR allowance save remains guarded and cannot be blindly repeated', async () => {
  const app = runtime({ responses: [{ status: 409, title: 'The allowance changed.' }] });
  app.run(`
    ocrBudgetTenantId='tenant';
    ocrBudgetSnapshot={monthlyPageLimit:100, warningPercent:80, version:'1'};
    ocrBudgetBaseline=JSON.stringify(['100','80']);
  `);
  app.element('ocrPageLimit').value = '250';
  app.element('ocrWarningPercent').value = '80';
  assert.equal(await app.action('saveOcrBudget'), false);
  assert.equal(app.run('Boolean(ocrBudgetDirty())'), true);
  const pending = app.action('openWorkspaceForTenant');
  choose(app, 'save');
  await tick();
  assert.equal(app.context.window.location.href, '');
  assert.equal(app.calls.length, 1);
  choose(app, 'stay');
  await pending;
});

test('new OCR allowance edits typed during a save survive with the updated independent version', async () => {
  let release;
  const app = runtime({ responses: [() => new Promise(resolve => { release = resolve; })] });
  app.run(`
    ocrBudgetTenantId='tenant';
    ocrBudgetSnapshot={monthlyPageLimit:100, warningPercent:80, version:'1'};
    ocrBudgetBaseline=JSON.stringify(['100','80']);
  `);
  app.element('ocrPageLimit').value = '250';
  app.element('ocrWarningPercent').value = '80';
  const saving = app.action('saveOcrBudget');
  await tick();
  app.element('ocrPageLimit').value = '300';
  release({ version: '2' });
  assert.equal(await saving, true);
  assert.equal(app.element('ocrPageLimit').value, '300');
  assert.equal(app.run('ocrBudgetSnapshot.version'), '2');
  assert.equal(app.run('Boolean(ocrBudgetDirty())'), true);
  assert.equal(app.run('currentTenant.harnessVersion'), 2);
  assert.equal(app.calls.length, 1);
});

test('unknown selected model aliases stay visible and actionable without being relabeled as deployed', async () => {
  const app = runtime({ agents: true, responses: [setup('tenant', {
    models: [{ modelId: 'private-alias', displayName: 'private-alias', purpose: 'language',
      selected: true, configured: false, state: 'UnknownSelection' }]
  })] });
  app.run("setModelSelection('harnessLanguageModelSelect', 'private-alias')");
  await app.context.DocIntelAgentSettings.connections('tenant');
  assert.equal(app.element('harnessLanguageModelSelect').value, 'private-alias');
  const option = app.element('harnessLanguageModelSelect').options.find(item => item.value === 'private-alias');
  assert.equal(option.disabled, false);
  assert.match(option.textContent, /Configuration unknown; deployment not verified/);
  assert.match(app.element('languageModelStatus').textContent, /not verified/);
});

test('empty workspaces never seed example document types or extraction profiles and cannot author a schema yet', () => {
  const app = runtime();
  app.run('renderDataExtractionDocDropdown([])');
  assert.equal(app.element('dataExtractionDocSelect').disabled, true);
  assert.equal(app.element('dataExtractionSchemaControls').disabled, true);
  assert.equal(app.element('dataExtractionDocGuidance').hidden, false);
  assert.equal(app.element('dataExtractionDocSelect').children[0].value, '');
  assert.match(app.element('dataExtractionDocSelect').children[0].textContent, /No document types/);
  app.action('addDataExtractionFieldRow');
  app.action('addDataExtractionTableBlock');
  assert.equal(app.element('dataExtractionFieldsList').children.length, 0);
  assert.equal(app.element('dataExtractionTablesList').children.length, 0);
  assert.equal(app.run('workingDataExtractionProfiles.length'), 0);
  assert.equal(app.run('buildPayloadFromUi().capabilities.DataExtraction.dataExtraction.profiles.length'), 0);
  assert.equal(app.calls.length, 0);
});

test('real document types enable extraction authoring while an existing saved schema selection stays intact', () => {
  const app = runtime();
  app.run(`
    workingDataExtractionProfiles = [{documentName:'Saved type', fields:[], tables:[], customOption:'preserve'}];
    activeExtractionDocName='Saved type';
    renderDataExtractionDocDropdown(['Another real type']);
  `);
  assert.equal(app.element('dataExtractionDocSelect').disabled, false);
  assert.equal(app.element('dataExtractionSchemaControls').disabled, false);
  assert.equal(app.element('dataExtractionDocGuidance').hidden, true);
  assert.equal(app.element('dataExtractionDocSelect').value, 'Saved type');
  assert.equal(app.run('workingDataExtractionProfiles[0].customOption'), 'preserve');
  assert.deepEqual(app.element('dataExtractionDocSelect').children.map(option => option.value),
    ['Another real type', 'Saved type']);
  app.run("workingDataExtractionProfiles=[]; renderDataExtractionDocDropdown(['Service agreement']);");
  assert.equal(app.element('dataExtractionDocSelect').value, 'Service agreement');
  assert.equal(app.element('dataExtractionSchemaControls').disabled, false);
});
