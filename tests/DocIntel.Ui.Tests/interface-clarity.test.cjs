const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const { loadApplication } = require('./helpers/application.cjs');
const root = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const read = file => fs.readFileSync(path.join(root, file), 'utf8');

function runtime(page) {
  const nodes = new Map();
  let focused;
  const element = id => {
    if (nodes.has(id)) return nodes.get(id);
    const classes = new Set();
    const node = {
      id, value: '', style: {}, dataset: {}, hidden: false, textContent: '', innerHTML: '',
      children: [], open: false, inert: false,
      classList: {
        add(name) { classes.add(name); }, remove(name) { classes.delete(name); },
        contains(name) { return classes.has(name); },
        toggle(name, force) {
          const value = force === undefined ? !classes.has(name) : force;
          if (value) classes.add(name); else classes.delete(name);
          return value;
        }
      },
      setAttribute(name, value) { this[name] = value; },
      addEventListener() {},
      focus() { focused = id; },
      querySelector() { return null; }
    };
    nodes.set(id, node);
    return node;
  };
  const app = {
    element,
    document: {
      getElementById: element, querySelector: () => null, querySelectorAll: () => []
    },
    fetch() { throw new Error('Presentation changes must not call a provider or save configuration.'); },
    console
  };
  app.context = vm.createContext(app);
  vm.runInContext(loadApplication(page), app.context);
  app.focused = () => focused;
  return app;
}

test('the familiar three Admin sections and document result views are retained without new navigation', () => {
  const admin = read('admin.html');
  assert.equal([...admin.matchAll(/role="tab"\s/g)].length, 3);
  for (const id of ['processingSettingsPanel', 'agentSettingsPanel', 'tenantUsersPanel'])
    assert.match(admin, new RegExp(`id="${id}"`));
  assert.doesNotMatch(admin, /setupSettingsTab|integrationsSettingsTab|customAnalysisTab|customer\.js/);
  const workspace = read('index.html');
  for (const id of ['btnModeDocuments', 'btnModeGraph', 'btnModeAsk'])
    assert.match(workspace, new RegExp(`id="${id}"`));
  assert.match(read('workspace.js'), /docResultTab\.startsWith\('prompt:'\)/);
});

test('models and features use quiet rows while configuration remains in existing drawers', () => {
  const css = read('admin.css');
  assert.match(css, /\.features-grid\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(css, /\.feature-tile\s*\{[^}]*border:\s*0;[^}]*border-bottom:\s*1px/);
  assert.match(css, /\.model-harness-card\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(css, /\.feature-illustration\s*\{\s*display:\s*none/);
  assert.equal([...read('admin.html').matchAll(/<span>Edit<\/span>/g)].length, 6);
});

test('opening another feature hides only the previous drawer and preserves its entered values', () => {
  const app = runtime('admin');
  app.element('footersToIgnoreInput').value = 'My unsaved rule';
  app.context.toggleFeatureDrawer('split');
  assert.equal(app.element('splitDrawer').classList.contains('open'), true);
  assert.equal(app.element('splitChevron')['aria-expanded'], 'true');
  app.context.toggleFeatureDrawer('entity');
  assert.equal(app.element('splitDrawer').classList.contains('open'), false);
  assert.equal(app.element('entityDrawer').classList.contains('open'), true);
  assert.equal(app.element('splitChevron')['aria-expanded'], 'false');
  assert.equal(app.element('entityChevron')['aria-controls'], 'entityDrawer');
  assert.equal(app.element('footersToIgnoreInput').value, 'My unsaved rule');
  app.context.toggleFeatureDrawer('entity');
  assert.equal(app.element('entityDrawer').classList.contains('open'), false);
});

test('the prompt editor is one closed native disclosure containing every editing control', () => {
  const html = read('admin.html');
  const start = html.indexOf('<details class="prompt-studio-card" id="promptEditor">');
  assert.ok(start >= 0);
  const editor = html.slice(start, html.indexOf('</details>', start));
  for (const id of ['promptColumnNameInput', 'promptInstructionsInput', 'promptFilterModeSelect', 'btnSavePromptLabel'])
    assert.ok(editor.includes(`id="${id}"`), `${id} must be inside the disclosure`);
  assert.match(editor, /<summary class="prompt-editor-summary">/);
  assert.doesNotMatch(editor.split('>')[0], /\bopen\b/);
  assert.match(html, /prompt-studio-card saved-prompts-card/);
});

test('editing a saved prompt reveals its editor and focuses the existing name field', () => {
  const app = runtime('admin');
  vm.runInContext(`workingPromptColumns=[{
    columnId:'existing',columnName:'Existing result',promptInstructions:'Existing instructions',
    filterMode:'All',entityFilters:[]
  }];`, app.context);
  app.context.editPromptColumn(0);
  assert.equal(app.element('promptEditor').open, true);
  assert.equal(app.element('promptColumnNameInput').value, 'Existing result');
  assert.equal(app.element('promptInstructionsInput').value, 'Existing instructions');
  assert.equal(app.element('editingPromptColumnId').value, 'existing');
  assert.equal(app.focused(), 'promptColumnNameInput');
});

test('Ask AI temporarily hides the redundant document rail and restores the original Documents layout', () => {
  const app = runtime('index');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    syncTenantWorkspaceCapabilities=()=>{};loadTenantChats=()=>{};
    getActiveChat=()=>({messages:[]});renderDocumentList=()=>{};renderSelectedDocument=()=>{};`, app.context);
  app.context.setWorkspaceMode('ask');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), true);
  assert.equal(app.element('docListPane').inert, true);
  app.context.setWorkspaceMode('documents');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), false);
  assert.equal(app.element('docListPane').inert, false);
  assert.equal(app.element('btnHeaderExpandDocList').style.display, 'none');
});

test('Ask AI preserves a document rail the user had already collapsed', () => {
  const app = runtime('index');
  app.element('docListPane').classList.add('collapsed');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    syncTenantWorkspaceCapabilities=()=>{};loadTenantChats=()=>{};
    getActiveChat=()=>({messages:[]});renderDocumentList=()=>{};renderSelectedDocument=()=>{};`, app.context);
  app.context.setWorkspaceMode('ask');
  app.context.setWorkspaceMode('documents');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), true);
});

test('the document-first empty state hides irrelevant result tabs and checks existing upload permission', () => {
  const app = runtime('index');
  vm.runInContext("currentTenant={tenantId:'one',displayName:'Workspace',capabilities:{}};", app.context);
  app.context.renderSelectedDocument();
  assert.match(app.element('docCenterBody').innerHTML, /Upload a PDF/);
  assert.match(app.element('docCenterBody').innerHTML, /25 MiB/);
  assert.equal(app.element('btnHeaderExportJson').hidden, true);
  assert.equal(app.element('meridianNavRow').style.display, 'none');
  app.element('drawerUploadLabel').style.display = 'none';
  app.context.renderSelectedDocument();
  assert.doesNotMatch(app.element('docCenterBody').innerHTML, /data-click="chooseWorkspaceUpload"/);
  assert.match(app.element('docCenterBody').innerHTML, /workspace administrator/);
});
