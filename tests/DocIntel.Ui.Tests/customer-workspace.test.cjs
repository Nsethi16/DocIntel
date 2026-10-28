const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const { loadApplication } = require('./helpers/application.cjs');

function runtime(responses = []) {
  const nodes = new Map();
  function element(id = '') {
    if (nodes.has(id)) return nodes.get(id);
    const classes = new Set();
    const value = {
      id, value: '', textContent: '', innerHTML: '', hidden: false, disabled: false,
      dataset: {}, style: {}, children: [], events: {}, isConnected: true,
      classList: {
        add(name) { classes.add(name); }, remove(name) { classes.delete(name); },
        contains(name) { return classes.has(name); },
        toggle(name, on) {
          const enabled = on === undefined ? !classes.has(name) : on;
          if (enabled) classes.add(name); else classes.delete(name);
          return enabled;
        }
      },
      setAttribute(name, content) { this[name] = content; },
      removeAttribute(name) { delete this[name]; },
      addEventListener(name, callback) { this.events[name] = callback; },
      append(...items) { this.children.push(...items); },
      appendChild(item) { this.children.push(item); },
      querySelector() { return null; }, querySelectorAll() { return []; },
      focus() { app.focus = this; }
    };
    nodes.set(id, value);
    return value;
  }
  const calls = [];
  const notices = [];
  const app = { element, calls, notices };
  const storage = new Map();
  const context = vm.createContext({
    console, URLSearchParams, URL, setTimeout, clearTimeout,
    DocIntelIdentity: { chatPartition: 'account-one' },
    document: {
      getElementById: element, createElement: tag => element(tag + '-' + nodes.size),
      querySelector: selector => selector === '.meridian-header' ? element('header') : null,
      querySelectorAll: () => [], body: element('body'), activeElement: element('source-link')
    },
    DocIntelEvidence: { describe: () => ({ pages: [], modelPercent: null, status: 'Not found', tooltip: 'Unknown' }) },
    DocIntelUX: { inform: message => notices.push(message) },
    localStorage: { getItem: key => storage.get(key) || null, setItem: (key, value) => storage.set(key, value) },
    fetch: async (url, options) => {
      calls.push({ url, options });
      const response = responses.shift();
      assert.ok(response, 'Unexpected API call: ' + url);
      if (typeof response === 'function') return response(context);
      return { ok: response.httpStatus === undefined || response.httpStatus < 400,
        status: response.httpStatus || 200, json: async () => response };
    }
  });
  vm.runInContext(loadApplication('index'), context);
  vm.runInContext(`currentTenant = {tenantId:'tenant/one', displayName:'Customer workspace', capabilities:{}};
    showToast = message => DocIntelUX.inform(message);`, context);
  app.context = context;
  return app;
}

function documentJob(id = 'one', extra = {}) {
  return { jobId: id, tenantId: 'tenant/one', fileName: 'Customer agreement.pdf',
    status: 'Completed', totalPages: 4, segments: [], ...extra };
}

test('empty workspaces lead to upload while document-only actions are hidden', () => {
  const app = runtime();
  vm.runInContext('renderSelectedDocument()', app.context);
  assert.match(app.element('docCenterBody').innerHTML, /Start with your first document|Upload a PDF/);
  assert.match(app.element('docCenterBody').innerHTML, /25 MiB/);
  assert.equal(app.element('btnHeaderExportJson').hidden, true);
  assert.equal(app.element('btnInspectSource').hidden, true);
  assert.equal(app.element('meridianNavRow').style.display, 'none');
});

test('read-only empty workspaces explain access without offering upload', () => {
  const app = runtime();
  app.element('drawerUploadLabel').style.display = 'none';
  app.element('adminStudioSwitchLink').style.display = 'none';
  vm.runInContext('renderSelectedDocument()', app.context);
  assert.doesNotMatch(app.element('docCenterBody').innerHTML, /data-upload-drop/);
  assert.match(app.element('docCenterBody').innerHTML, /Ask a workspace administrator/);
});

test('Ask AI uses one contextual rail, a multiline composer and honest workspace scope', () => {
  const app = runtime();
  vm.runInContext("workspaceMode='ask'; renderSelectedDocument()", app.context);
  const markup = app.element('docCenterBody').innerHTML;
  assert.doesNotMatch(markup, /ask-inner-sidebar/);
  assert.match(markup, /<textarea[^>]+maxlength="8000"/);
  assert.match(markup, /documents you can access/);
  assert.match(markup, /Shift \+ Enter/);
  assert.equal(app.element('header').style.display, '');
});

test('custom analysis results remain separate view tabs and have selected-state semantics', () => {
  const app = runtime();
  app.job = documentJob('one', { segments: [{
    segmentIndex: 0, startPage: 1, endPage: 4, insightSummary: 'Summary',
    extractedEntities: [], promptColumnResults: [{
      columnId: 'commercial', columnName: 'Commercial risks', filterMatched: true, resultText: 'No risks reported.'
    }]
  }] });
  app.context.job = app.job;
  vm.runInContext("tenantJobs=[job]; selectedJobId='one'; docResultTab='prompt:commercial'; renderSelectedDocument()", app.context);
  const tabs = app.element('insightsTabBar').innerHTML;
  assert.match(tabs, /Commercial risks/);
  assert.match(tabs, /data-tab="prompt:commercial"/);
  assert.match(tabs, /role="tab" aria-selected="true"/);
  assert.match(app.element('docCenterBody').innerHTML, /No risks reported/);
});

test('processing renders reported stages, not invented percentages or automatic retries', () => {
  const app = runtime();
  app.context.job = documentJob('one', { status: 'Running', executionTrace: [
    { executorName: 'IdentifyDocumentsExecutor', executed: true, succeeded: true }
  ] });
  vm.runInContext("tenantJobs=[job]; selectedJobId='one'; renderSelectedDocument()", app.context);
  const markup = app.element('docCenterBody').innerHTML;
  assert.match(markup, /Identify document types/);
  assert.match(markup, /1 completed steps/);
  assert.match(markup, /does not submit another upload/);
  assert.doesNotMatch(markup, /\d+%/);
});

test('document failures remain visible with a support reference and escape external text', () => {
  const app = runtime();
  app.context.job = documentJob('one', { status: 'Failed', errorMessage: '<img onerror=bad()>' });
  vm.runInContext("tenantJobs=[job]; selectedJobId='one'; renderSelectedDocument()", app.context);
  assert.match(app.element('docCenterBody').innerHTML, /role="alert"/);
  assert.match(app.element('docCenterBody').innerHTML, /Reference: <code>one/);
  assert.doesNotMatch(app.element('docCenterBody').innerHTML, /<img/);
});

test('library requests are bounded and fetch full details only for the selected document', async () => {
  const app = runtime([
    { items: [documentJob()], total: 80, nextOffset: 50 }, documentJob(),
    { items: [documentJob('two')], total: 80, nextOffset: null }, documentJob()
  ]);
  vm.runInContext('renderDocumentList=()=>{};renderSelectedDocument=()=>{};', app.context);
  await app.context.loadTenantJobs();
  assert.match(app.calls[0].url, /\/documents\?offset=0&limit=50$/);
  assert.match(app.calls[1].url, /\/jobs\/one$/);
  await app.context.loadMoreDocuments();
  assert.match(app.calls[2].url, /offset=50&limit=50/);
  assert.equal(vm.runInContext('tenantJobs.length', app.context), 2);
  assert.equal(vm.runInContext('libraryTotal', app.context), 80);
  assert.equal(app.calls.filter(call => call.options?.method === 'POST').length, 0);
});

test('a late library page cannot replace another workspace or publish stale source details', async () => {
  let release;
  const app = runtime([() => new Promise(resolve => { release = resolve; })]);
  vm.runInContext('renderDocumentList=()=>{};renderSelectedDocument=()=>{};', app.context);
  const pending = app.context.loadTenantJobs();
  vm.runInContext("currentTenant={tenantId:'different',displayName:'Different',capabilities:{}};", app.context);
  release({ ok: true, json: async () => ({ items: [documentJob()], total: 1, nextOffset: null }) });
  await pending;
  assert.equal(vm.runInContext('tenantJobs.length', app.context), 0);
});

test('sources use an authorized same-origin route and do not change the active result view', async () => {
  const app = runtime();
  app.context.job = documentJob('one', { segments: [{ segmentIndex: 0, startPage: 1, endPage: 4 }] });
  vm.runInContext("tenantJobs=[job];selectedJobId='one';docResultTab='entities';", app.context);
  await app.context.inspectSource('one', 0, 3);
  assert.equal(app.element('sourceInspectorFrame').src,
    '/api/harness/tenants/tenant%2Fone/jobs/one/original#page=3');
  assert.equal(vm.runInContext('docResultTab', app.context), 'entities');
  app.context.closeSourceInspector();
  assert.equal(app.element('sourceInspector').hidden, true);
  assert.equal(app.element('sourceInspectorFrame').src, undefined);
});

test('closing a source cancels a late preview response without reopening the pane', async () => {
  let release;
  const app = runtime([() => new Promise(resolve => { release = resolve; })]);
  const pending = app.context.inspectSource('one');
  app.context.closeSourceInspector();
  release({ ok: true, json: async () => documentJob() });
  await pending;
  assert.equal(app.element('sourceInspector').hidden, true);
  assert.equal(app.element('sourceInspectorFrame').src, undefined);
});

test('a late detail response cannot replace a newer snapshot of the same selected document', async () => {
  let release;
  const app = runtime([
    () => new Promise(resolve => { release = resolve; }),
    documentJob('one', { status: 'Completed' })
  ]);
  vm.runInContext("selectedJobId='one';renderDocumentList=()=>{};renderSelectedDocument=()=>{};", app.context);
  const previous = app.context.loadSelectedDocument();
  await app.context.loadSelectedDocument();
  release({ ok: true, json: async () => documentJob('one', { status: 'Running' }) });
  await previous;
  assert.equal(vm.runInContext('selectedDocumentRecord.status', app.context), 'Completed');
});

test('Enter submission respects multiline editing and composition without creating duplicate questions', () => {
  const app = runtime();
  const sent = [];
  app.context.sent = value => sent.push(value);
  vm.runInContext('runCrossDocumentAskAi=sent;', app.context);
  const handler = app.context.registeredActions.submitComposer_event.run;
  let prevented = 0;
  for (const modifier of [{ shiftKey: true }, { isComposing: true }, { ctrlKey: true }]) {
    handler.call({ value: 'Draft' }, [], { ...modifier, preventDefault() { prevented++; } });
  }
  assert.deepEqual(sent, []);
  handler.call({ value: 'Question' }, [], { preventDefault() { prevented++; } });
  assert.deepEqual(sent, ['Question']);
  assert.equal(prevented, 1);
});

test('relationship filters preserve strict AND semantics and provide a non-canvas view', () => {
  const app = runtime();
  app.context.jobs = [
    documentJob('one', { segments: [{ extractedEntities: [{ name: 'Customer', value: 'A' }, { name: 'Currency', value: 'USD' }] }] }),
    documentJob('two', { segments: [{ extractedEntities: [{ name: 'Customer', value: 'A' }] }] })
  ];
  vm.runInContext("workspaceMode='graph';relationshipJobs=jobs;graphSelectedEntityValues=new Set(['Customer||A','Currency||USD']);", app.context);
  const data = app.context.computeFilteredGraphData();
  assert.equal(data.docNodes.length, 1);
  assert.equal(data.docNodes[0].jobId, 'one');
  const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'workspace.js'), 'utf8');
  assert.match(source, /aria-label="Relationship view"/);
  assert.match(source, /setRelationshipView_arg/);
});
