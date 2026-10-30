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
  const storage = new Map();
  let focused;
  const element = id => {
    if (nodes.has(id)) return nodes.get(id);
    const classes = new Set();
    let html = '';
    const node = {
      id, value: '', style: {}, dataset: {}, hidden: false, textContent: '',
      get innerHTML() { return html; },
      set innerHTML(value) { html = value; this.children = []; },
      children: [], open: false, inert: false,
      appendChild(child) { this.children.push(child); },
      append(...children) { this.children.push(...children); },
      replaceChildren(...children) { this.innerHTML = ''; this.children.push(...children); },
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
      querySelector() { return null; },
      querySelectorAll() { return []; }
    };
    nodes.set(id, node);
    return node;
  };
  const app = {
    element,
    storage,
    DocIntelIdentity: { chatPartition: 'test-user' },
    localStorage: {
      getItem: key => storage.get(key) || null,
      setItem: (key, value) => storage.set(key, value)
    },
    document: {
      getElementById: element, querySelector: () => null, querySelectorAll: () => [],
      createElement(tag) {
        const node = element(`created-${nodes.size}`);
        node.tagName = tag;
        return node;
      }
    },
    fetch() { throw new Error('Presentation changes must not call a provider or save configuration.'); },
    console
  };
  app.context = vm.createContext(app);
  vm.runInContext(loadApplication(page), app.context);
  app.focused = () => focused;
  return app;
}

function documentRuntime(job, page = 'index') {
  const app = runtime(page);
  app.context.fixtureJob = job;
  vm.runInContext(read('evidence.js'), app.context);
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[fixtureJob];selectedJobId=fixtureJob.jobId;selectedSliceIndex=-1;`, app.context);
  return app;
}

function renderDocumentTab(app, tab) {
  vm.runInContext(`docResultTab=${JSON.stringify(tab)};`, app.context);
  app.context.renderSelectedDocument();
  return app.element('docCenterBody').innerHTML;
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
  assert.match(read('index.css'), /\.meridian-tabs\s*\{[^}]*max-width:\s*100%;[^}]*overflow-x:\s*auto/);
});

test('all entry pages use one local font and neutral theme without external font downloads', () => {
  const shell = read(path.join('shared', 'shell.css'));
  assert.match(shell, /--font-sans:\s*"Segoe UI Variable Text",\s*"Segoe UI"/);
  assert.match(shell, /--text-heading:\s*#202020/);
  assert.match(shell, /--text-secondary:\s*#636363/);
  assert.match(shell, /button, input, select, textarea\s*\{\s*font:\s*inherit/);
  assert.match(shell, /prefers-reduced-motion:\s*reduce/);
  for (const page of ['index.html', 'admin.html', 'ask-run.html', 'email-question.html', 'review.html']) {
    const html = read(page);
    assert.equal([...html.matchAll(/href="\/shared\/shell\.css"/g)].length, 1, page);
    assert.match(html, /src="\/shared\/shell\.js"/);
    assert.ok(html.indexOf('src="/shared/shell.js"') < html.indexOf('href="/shared/shell.css"'),
      `${page} must resolve appearance before painting`);
    assert.match(html, /data-appearance-host/);
    assert.doesNotMatch(html, /fonts\.cdnfonts\.com|fonts\.googleapis\.com|fonts\.gstatic\.com/);
  }
  assert.doesNotMatch(read('index.css'), /font-family:\s*Georgia|Overused Grotesk/);
  assert.doesNotMatch(read('admin.css'), /--text-primary:|--font-sans:/);
});

test('sidebar resizing is an accessible separator and Graph details never create a second grid column', () => {
  for (const file of ['index.html', 'admin.html']) {
    const html = read(file);
    assert.match(html, /data-resizable-sidebar data-sidebar-default="\d+"/);
    assert.match(html, /class="sidebar-resizer" role="separator"[^>]+aria-orientation="vertical"/);
  }
  const css = read('index.css');
  assert.match(css, /\.graph-inspector-card\s*\{[^}]*position:\s*absolute/);
  assert.match(css, /\.graph-active-filters\s*\{[^}]*flex:0 0 42px/);
  assert.doesNotMatch(css, /\.graph-workbench\.details-open\s*\{[^}]*grid-template-columns/);
});

test('composer focus belongs to the rounded container instead of a rectangular inner input', () => {
  const css = read('index.css');
  assert.match(css, /\.ask-pill-bar:focus-within\s*\{[^}]*border-color:\s*var\(--text-muted\);[^}]*box-shadow:\s*0 0 0 1px var\(--text-muted\)/);
  assert.match(css, /\.ask-pill-input:focus-visible\s*\{\s*outline:\s*none/);
  assert.match(css, /\.ask-pill-input::placeholder\s*\{[^}]*color:\s*var\(--text-muted\)/);
  assert.match(read(path.join('shared', 'shell.css')), /:focus-visible\s*\{[^}]*outline:\s*2px solid var\(--focus-ring\)/);
});

test('navigation and settings labels use Title Case without transforming document content', () => {
  const html = read('admin.html');
  for (const label of ['Document Processing', 'Agent &amp; Channels', 'Users &amp; Access',
    'Add People', 'Assigned People', 'Manage Access', 'Agent Name', 'Conversation Style', 'Response Guidance'])
    assert.match(html, new RegExp(`>${label}(?:\\s|<)`), label);
  assert.doesNotMatch(html, />Document processing<|>Agent &amp; channels<|>Users &amp; access</);
  assert.doesNotMatch(read(path.join('shared', 'shell.css')), /text-transform:\s*capitalize/);
  assert.match(read('workspace.js'), /escapeHtml\(col\.columnName\)/);
  const index = read('index.html');
  assert.ok(index.indexOf('id="drawerUploadLabel"') < index.indexOf('id="docItemsList"'));
  assert.match(index, /<button[^>]+id="drawerUploadLabel"[^>]+data-click="chooseWorkspaceUpload"/);
});

test('settings use one centered 800px column for the header, navigation and every panel', () => {
  const css = read('admin.css');
  assert.match(css, /\.canvas-column\s*\{[^}]*width:\s*100%;[^}]*max-width:\s*800px;[^}]*margin-inline:\s*auto/);
  assert.doesNotMatch(css.match(/\.canvas-column\s*\{[^}]*\}/)[0], /text-align:\s*center/);
});

test('people settings use the same unboxed rows, neutral avatars and readable content width', () => {
  const css = read('tenant-users.css');
  assert.match(css, /\.tenant-users\s*\{[^}]*max-width:800px/);
  assert.match(css, /\.tenant-users-access-note\s*\{[^}]*background:transparent;[^}]*border:0/);
  assert.match(css, /\.tenant-users-list\s*\{[^}]*border:0/);
  assert.match(css, /\.tenant-users-person strong\s*\{[^}]*font-size:14px;[^}]*font-weight:500/);
  assert.match(css, /\.tenant-users-avatar\s*\{[^}]*background:var\(--pill-hover\)/);
  assert.doesNotMatch(css, /#fcf6f2|#f9e8e1|#a54431/);
});

test('vertical workspace navigation preserves capability gates and exposes the selected mode', () => {
  const app = runtime('index');
  const nav = app.element('workspaceModes');
  app.document.querySelector = selector => selector === '#docListPane .workspace-mode-switcher' ? nav : null;
  vm.runInContext(`currentTenant={capabilities:{DocumentGraph:{enabled:false},AskAi:{enabled:false}}};
    workspaceMode='graph';`, app.context);
  app.context.syncTenantWorkspaceCapabilities();
  assert.equal(app.element('btnModeGraph').style.display, 'none');
  assert.equal(app.element('btnModeAsk').style.display, 'none');
  assert.equal(app.element('btnModeDocuments')['aria-pressed'], 'true');
  assert.equal(nav.style.display, 'grid');
  assert.equal(nav.style.gridTemplateColumns, undefined);
  vm.runInContext("currentTenant.capabilities.DocumentGraph.enabled=true;workspaceMode='graph';", app.context);
  app.context.syncTenantWorkspaceCapabilities();
  assert.equal(app.element('btnModeGraph')['aria-pressed'], 'true');
  assert.equal(app.element('btnModeDocuments')['aria-pressed'], 'false');
  assert.match(read('index.css'), /\.workspace-mode-switcher\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
});

test('document rows are keyboard-native buttons and preserve filenames, search and selection', () => {
  const app = runtime('index');
  vm.runInContext(`tenantJobs=[
    {jobId:'one',fileName:'a mixed CASE.pdf',status:'Completed',totalPages:2},
    {jobId:'two',fileName:'<report>.pdf',status:'Completed',totalPages:3}
  ];selectedJobId='one';renderSelectedDocument=()=>{};`, app.context);
  app.context.renderDocumentList();
  const list = app.element('docItemsList');
  assert.equal(list.children.length, 2);
  assert.equal(list.children[0].tagName, 'button');
  assert.equal(list.children[0].type, 'button');
  assert.equal(list.children[0]['aria-pressed'], 'true');
  assert.match(list.children[0].innerHTML, /a mixed CASE\.pdf/);
  assert.match(list.children[1].innerHTML, /&lt;report&gt;\.pdf/);
  list.children[1].onclick();
  assert.equal(list.children[1]['aria-pressed'], 'true');
  assert.equal(list.children[0]['aria-pressed'], 'false');
  app.element('docSearchInput').value = 'mixed';
  app.context.renderDocumentList();
  assert.equal(list.children.length, 1);
  assert.match(list.children[0].innerHTML, /a mixed CASE\.pdf/);
});

test('models and features use quiet rows while configuration remains in existing drawers', () => {
  const css = read('admin.css');
  assert.match(css, /\.features-grid\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(css, /\.feature-tile\s*\{[^}]*border:\s*0;/);
  assert.doesNotMatch(css.match(/\.feature-tile\s*\{([^}]+)\}/)[1], /border-bottom/);
  assert.match(css, /\.model-harness-card\s*\{[^}]*grid-template-columns:\s*minmax\(0,\s*1fr\)/);
  assert.match(css, /\.feature-illustration\s*\{\s*display:\s*none/);
  assert.equal([...read('admin.html').matchAll(/<span>Manage<\/span>/g)].length, 6);
  assert.match(css, /\.chevron-btn,\s*\.agent-manage-action\s*\{[^}]*background:\s*var\(--pill-hover\);[^}]*border:\s*0/);
  assert.match(read(path.join('shared', 'shell.css')), /--switch-on:\s*#252525/);
  assert.match(css, /#processingSettingsPanel\s*\{[^}]*max-width:\s*800px/);
  assert.match(css, /\.feature-title\s*\{[^}]*font-weight:\s*500/);
});

test('Agent and channels share the processing Manage, switch and quiet-row theme', () => {
  const css = read('admin.css');
  const agent = read('agent-settings.css');
  assert.match(css, /#agentSettingsPanel\s*\{[^}]*max-width:\s*800px/);
  assert.match(css, /\.switch\s*\{[^}]*width:\s*40px/);
  assert.match(css, /:is\(#processingSettingsPanel, #agentSettingsPanel\)\s+\.switch\s*\{[^}]*height:\s*20px/);
  assert.match(agent, /\.agent-settings-card\s*\{[^}]*border:\s*0;[^}]*box-shadow:\s*none/);
  assert.match(agent, /\.agent-setting-copy strong\s*\{[^}]*font-size:\s*14px;[^}]*font-weight:\s*500/);
  assert.match(agent, /\.agent-channel-card h3\s*\{[^}]*font-weight:\s*500/);
  assert.doesNotMatch(agent, /\.agent-settings-card label span/);
  const app = runtime('admin');
  const calls = [];
  app.context.DocIntelAgentSettings = { toggleConnection: name => calls.push(name) };
  app.context.registeredActions.toggleAgentChannel_arg.run(['email']);
  app.context.registeredActions.toggleAgentChannel_arg.run(['teams']);
  assert.deepEqual(calls, ['email', 'teams']);
});

test('workspace labels preserve technical tenant identifiers and source-document Tenant ID fields', () => {
  for (const file of ['admin.html', 'index.html']) {
    const html = read(file);
    const visible = html.replace(/<!--[\s\S]*?-->/g, '').replace(/<[^>]*>/g, '');
    assert.doesNotMatch(visible, /\btenants?\b/i);
    const labels = [...html.matchAll(/(?:aria-label|title|placeholder)="([^"]*)"/g)]
      .map(match => match[1]).join('\n');
    assert.doesNotMatch(labels, /\btenants?\b/i);
  }
  assert.match(read('admin.js'), /`Workspace ID: \$\{currentTenant\.tenantId\}`/);
  assert.match(read('admin.js'), /'Tenant ID'/);
  assert.match(read('admin.js'), /\/api\/harness\/tenants/);
  assert.match(read('tenant-users.js'), /\['TenantAdmin', 'Workspace Admin'\]/);
  assert.match(read('workspace.js'), /<dt>Workspace<\/dt><dd>\$\{escapeHtml\(job\.tenantId\)\}/);
  assert.match(read('identity.js'), /not assigned to a DocIntel workspace/);
});

test('opening another feature hides only the previous drawer and preserves its entered values', () => {
  const app = runtime('admin');
  app.element('footersToIgnoreInput').value = 'My unsaved rule';
  app.context.toggleFeatureDrawer('split');
  assert.equal(app.element('splitDrawer').classList.contains('open'), true);
  assert.equal(app.element('splitChevron')['aria-expanded'], 'true');
  assert.equal(app.element('splitChevron')['aria-label'], 'Manage file splitting and grouping settings');
  assert.equal(app.element('splitChevron').title, 'Manage file splitting and grouping settings');
  app.context.toggleFeatureDrawer('entity');
  assert.equal(app.element('splitDrawer').classList.contains('open'), false);
  assert.equal(app.element('entityDrawer').classList.contains('open'), true);
  assert.equal(app.element('splitChevron')['aria-expanded'], 'false');
  assert.equal(app.element('entityChevron')['aria-label'], 'Manage entity extraction settings');
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

test('Ask AI leaves the shared document rail and its toggle unchanged when switching modes', () => {
  const app = runtime('index');
  app.element('btnHeaderExpandDocList').style.display = 'none';
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    syncTenantWorkspaceCapabilities=()=>{};loadTenantChats=()=>{};
    getActiveChat=()=>({messages:[]});renderDocumentList=()=>{};renderSelectedDocument=()=>{};`, app.context);
  app.context.setWorkspaceMode('ask');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), false);
  assert.equal(app.element('docListPane').inert, false);
  app.context.setWorkspaceMode('documents');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), false);
  assert.equal(app.element('docListPane').inert, false);
  assert.equal(app.element('btnHeaderExpandDocList').style.display, 'none');
});

test('Ask AI preserves a document rail the user had already collapsed', () => {
  const app = runtime('index');
  app.element('docListPane').classList.add('collapsed');
  app.element('docListPane').inert = true;
  app.element('btnHeaderExpandDocList').style.display = 'inline-flex';
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    syncTenantWorkspaceCapabilities=()=>{};loadTenantChats=()=>{};
    getActiveChat=()=>({messages:[]});renderDocumentList=()=>{};renderSelectedDocument=()=>{};`, app.context);
  app.context.setWorkspaceMode('ask');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), true);
  assert.equal(app.element('docListPane').inert, true);
  assert.equal(app.element('btnHeaderExpandDocList').style.display, 'inline-flex');
  app.context.setWorkspaceMode('documents');
  assert.equal(app.element('docListPane').classList.contains('collapsed'), true);
});

test('Ask AI keeps the right pane focused on the conversation and moves history into the shared sidebar', () => {
  const app = runtime('index');
  const header = app.element('workspaceHeader');
  header.style.display = 'none';
  app.document.querySelector = selector => selector === '.meridian-header' ? header : null;
  vm.runInContext(`currentTenant={tenantId:'one',displayName:'Workspace',capabilities:{}};
    workspaceMode='ask';loadTenantChats=()=>{};
    tenantChats=[
      {id:'chat-one',title:'<untrusted title>',messages:[{role:'assistant',text:'First answer'}]},
      {id:'chat-two',title:'Second conversation',messages:[{role:'assistant',text:'Second answer'}]}
    ];activeChatId='chat-one';`, app.context);
  app.context.renderSelectedDocument();
  const body = app.element('docCenterBody');
  assert.equal(header.style.display, '');
  assert.equal(body.dataset.tab, 'ask');
  assert.equal(app.element('selectedDocTitle').textContent, 'Ask AI');
  assert.equal(app.element('meridianNavRow').style.display, 'none');
  assert.match(body.innerHTML, /First answer/);
  assert.doesNotMatch(body.innerHTML, /<aside|ask-inner-sidebar|toggleDocListPane|toggleAskInnerSidebar|workspace-mode-switcher|ask-conversation-toolbar|askChatSelect|New Chat|Delete Chat/);
  app.context.renderDocumentList();
  const list = app.element('docItemsList');
  assert.equal(list.children.length, 2);
  assert.match(list.children[0].children[0].innerHTML, /&lt;untrusted title&gt;/);
  assert.equal(list.children[0].children[0].type, 'button');
  assert.equal(list.children[0].children[1].type, 'button');
  list.children[1].children[0].onclick();
  assert.match(body.innerHTML, /Second answer/);
});

test('the contextual sidebar swaps actions and searches without changing document state or upload permission', () => {
  const app = runtime('index');
  app.storage.set('docintel_chats_v2_test-user_one', JSON.stringify([
    { id: 'chat-one', title: 'First question', messages: [{ role: 'user', text: 'Northwind terms' }] },
    { id: 'chat-two', title: 'Second question', messages: [{ role: 'user', text: 'Contoso terms' }] }
  ]));
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'document-one',fileName:'Contract.pdf',status:'Completed',totalPages:3}];
    selectedJobId='document-one';selectedSliceIndex=2;docResultTab='prompt:custom';
    renderSelectedDocument=()=>{};`, app.context);
  app.element('drawerUploadLabel').style.display = 'none';
  app.element('docSearchInput').value = 'Contract';
  app.context.setWorkspaceMode('ask');
  assert.equal(app.element('drawerUploadLabel').hidden, true);
  assert.equal(app.element('drawerNewChatButton').hidden, false);
  assert.equal(app.element('docSearchBoxWrap').hidden, true);
  assert.equal(app.element('chatSearchBoxWrap').hidden, false);
  assert.equal(app.element('sidebarCollectionTitle').textContent, 'Past Chats');
  assert.equal(app.element('docListCountLabel').textContent, '2 chats');
  app.element('chatSearchInput').value = 'northwind';
  app.context.renderDocumentList();
  assert.equal(app.element('docItemsList').children.length, 1);
  assert.equal(app.element('docListCountLabel').textContent, '1 chat');
  assert.match(app.element('docItemsList').children[0].children[0].innerHTML, /First question/);
  app.context.setWorkspaceMode('graph');
  assert.equal(app.element('drawerUploadLabel').hidden, false);
  assert.equal(app.element('drawerUploadLabel').style.display, 'none', 'Read-only upload permission must remain intact');
  assert.equal(app.element('drawerNewChatButton').hidden, true);
  assert.equal(app.element('docSearchBoxWrap').hidden, false);
  assert.equal(app.element('chatSearchBoxWrap').hidden, true);
  assert.equal(app.element('sidebarCollectionTitle').textContent, 'Documents');
  assert.equal(app.element('docSearchInput').value, 'Contract');
  assert.equal(app.element('docItemsList').children.length, 1);
  assert.equal(vm.runInContext('selectedJobId', app.context), 'document-one');
  assert.equal(vm.runInContext('selectedSliceIndex', app.context), 2);
  assert.equal(vm.runInContext('docResultTab', app.context), 'prompt:custom');
  app.context.setWorkspaceMode('ask');
  assert.equal(app.element('chatSearchInput').value, 'northwind');
  assert.equal(app.element('docListCountLabel').textContent, '1 chat');
  vm.runInContext('currentTenant.capabilities.AskAi={enabled:false};', app.context);
  app.context.setWorkspaceMode('ask');
  assert.equal(app.element('drawerNewChatButton').hidden, true);
  assert.equal(app.element('sidebarCollectionTitle').textContent, 'Documents');
});

test('New Chat reuses an empty draft, clears chat filtering and focuses the composer without a request', () => {
  const app = runtime('index');
  app.storage.set('docintel_chats_v2_test-user_one', JSON.stringify([
    { id: 'saved', title: 'Saved question', messages: [{ role: 'user', text: 'Question' }] },
    { id: 'draft', title: 'New chat', messages: [] }
  ]));
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};workspaceMode='ask';
    renderSelectedDocument=()=>{};`, app.context);
  app.context.renderDocumentList();
  app.element('chatSearchInput').value = 'No match';
  app.context.startNewAskAiChat();
  app.context.startNewAskAiChat();
  assert.equal(vm.runInContext('activeChatId', app.context), 'draft');
  assert.equal(vm.runInContext('tenantChats.length', app.context), 2);
  assert.equal(app.element('chatSearchInput').value, '');
  assert.equal(app.element('docListCountLabel').textContent, '1 chat', 'Unused drafts are not past chats');
  assert.equal(app.focused(), 'askAiMainInputEl');
});

test('chat searches, empty states and names remain safe text in the shared list', () => {
  const app = runtime('index');
  app.storage.set('docintel_chats_v2_test-user_one', JSON.stringify([
    { id: 'saved', title: '<img src=x onerror=evil()>', messages: [{ role: 'user', text: 'Find my agreement' }] }
  ]));
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};workspaceMode='ask';`, app.context);
  app.context.renderDocumentList();
  const list = app.element('docItemsList');
  assert.match(list.children[0].children[0].innerHTML, /&lt;img src=x onerror=evil\(\)&gt;/);
  assert.equal(list.children[0].children[1]['aria-label'], 'Delete chat: <img src=x onerror=evil()>');
  app.element('chatSearchInput').value = 'AGREEMENT';
  app.context.renderDocumentList();
  assert.equal(list.children.length, 1);
  app.element('chatSearchInput').value = 'missing';
  app.context.renderDocumentList();
  assert.equal(list.children[0].textContent, 'No matching chats. Try another search.');
});

test('chat actions cannot reuse a stale account or workspace list', () => {
  const app = runtime('index');
  const original = JSON.stringify([
    { id: 'private', title: 'Private question', messages: [{ role: 'user', text: 'Private contents' }] }
  ]);
  app.storage.set('docintel_chats_v2_test-user_one', original);
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};workspaceMode='ask';
    showToast=message=>{globalThis.notice=message;};renderSelectedDocument=()=>{};`, app.context);
  app.context.renderDocumentList();
  const stale = app.element('docItemsList').children[0];
  app.element('chatSearchInput').value = 'Private';
  app.context.DocIntelIdentity.chatPartition = 'another-user';
  stale.children[0].onclick();
  assert.equal(app.element('chatSearchInput').value, '');
  assert.match(app.context.notice, /no longer available/);
  assert.equal(app.element('docListCountLabel').textContent, '0 chats');
  stale.children[1].onclick({ stopPropagation() {} });
  assert.equal(app.storage.get('docintel_chats_v2_test-user_one'), original);
  assert.doesNotMatch(app.storage.get('docintel_chats_v2_another-user_one'), /Private/);
  vm.runInContext("currentTenant={tenantId:'two',capabilities:{}};", app.context);
  app.context.renderDocumentList();
  assert.equal(app.element('docListCountLabel').textContent, '0 chats');
  assert.doesNotMatch(app.storage.get('docintel_chats_v2_another-user_two'), /Private/);
});

test('deleting chats is separate from selecting them and deleting the last chat leaves a reusable draft', () => {
  const app = runtime('index');
  app.storage.set('docintel_chats_v2_test-user_one', JSON.stringify([
    { id: 'first', title: 'First', messages: [{ role: 'user', text: 'First question' }] },
    { id: 'second', title: 'Second', messages: [{ role: 'assistant', text: 'Second answer' }] }
  ]));
  app.storage.set('docintel_chats_v2_test-user_other', 'Unchanged other workspace');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};workspaceMode='ask';
    renderSelectedDocument=()=>{};`, app.context);
  app.context.renderDocumentList();
  let stopped = 0;
  app.element('docItemsList').children[0].children[1].onclick({ stopPropagation() { stopped++; } });
  assert.equal(stopped, 1);
  assert.equal(vm.runInContext('activeChatId', app.context), 'second');
  assert.equal(app.element('docListCountLabel').textContent, '1 chat');
  app.element('docItemsList').children[0].children[1].onclick({ stopPropagation() {} });
  assert.equal(app.element('docListCountLabel').textContent, '0 chats');
  assert.equal(vm.runInContext('tenantChats.length', app.context), 1);
  assert.equal(vm.runInContext('tenantChats[0].messages.length', app.context), 0);
  assert.equal(app.storage.get('docintel_chats_v2_test-user_other'), 'Unchanged other workspace');
});

test('the existing sidebar toggle does not rebuild Ask AI or discard its unsent question', () => {
  const app = runtime('index');
  vm.runInContext("workspaceMode='ask';renderSelectedDocument=()=>{throw new Error('Sidebar toggle rebuilt the right pane');};", app.context);
  app.context.registeredActions.updateAskDraft_value.run.call({ value: 'My unsent question' });
  app.context.toggleDocListPane();
  assert.equal(app.element('docListPane').classList.contains('collapsed'), true);
  assert.equal(app.element('docListPane').inert, true);
  assert.equal(app.element('btnHeaderExpandDocList').style.display, 'inline-flex');
  app.context.toggleDocListPane();
  assert.equal(app.element('docListPane').classList.contains('collapsed'), false);
  assert.equal(app.element('docListPane').inert, false);
  assert.equal(vm.runInContext('askAiQuestionInput', app.context), 'My unsent question');
});

test('an unsent Ask AI question is cleared when switching workspaces', async () => {
  const app = runtime('index');
  vm.runInContext(`tenants=[{tenantId:'one'},{tenantId:'two'}];currentTenant=tenants[0];
    askAiQuestionInput='Question for the first workspace';loadTenantChats=()=>{};
    renderTenantSelect=()=>{};loadTenantJobs=async()=>{};`, app.context);
  await app.context.onTenantSwitched('two');
  assert.equal(vm.runInContext('askAiQuestionInput', app.context), '');
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

test('processing and failure notices stay outside the tab content and preserve available results', () => {
  const app = runtime('index');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'file',tenantId:'one',fileName:'bundle.pdf',status:'Running',totalPages:2,
      executionTrace:[{executed:true},{executed:true},{executed:false}],segments:[
        {segmentIndex:0,startPage:1,endPage:2,bodyText:'Source document',promptColumnResults:[
          {columnId:'custom',columnName:'Custom Result',filterMatched:true,resultText:'Derived result'}]}]}];
    selectedJobId='file';selectedSliceIndex=-1;`, app.context);
  const body = app.element('docCenterBody');
  for (const status of ['Queued', 'Running', 'Failed']) {
    vm.runInContext(`tenantJobs[0].status='${status}';tenantJobs[0].errorMessage='<provider failure>';`, app.context);
    let sharedNotice;
    for (const tab of ['insights', 'document', 'entities', 'structured', 'metadata', 'prompt:custom']) {
      vm.runInContext(`docResultTab='${tab}';`, app.context);
      body.dataset.layout = 'pdf';
      app.context.renderSelectedDocument();
      const notice = app.element('documentStatusNotice');
      assert.equal(body.dataset.tab, tab);
      assert.equal(body.dataset.layout, tab === 'document' ? 'pdf' : '');
      sharedNotice ??= notice.textContent;
      assert.equal(notice.textContent, sharedNotice, `${status}: ${tab} must use the shared notice`);
      assert.equal(notice.hidden, false);
      assert.equal(notice.dataset.kind, status === 'Failed' ? 'failed' : 'running');
      assert.match(notice.textContent, status === 'Failed'
        ? /Processing failed:.*<provider failure>/ : /Agent Harness \(2 steps executed\)/);
      assert.doesNotMatch(body.innerHTML, /Processing failed:|Processing document through|No PDF available/);
      if (tab === 'document') assert.match(body.innerHTML, /class="pdf-full-frame"/);
      if (tab === 'metadata') assert.match(body.innerHTML, /Pipeline Execution Trace/);
      if (tab === 'prompt:custom') assert.match(body.innerHTML, /Derived result/);
    }
  }
  const html = read('index.html');
  assert.ok(html.indexOf('id="documentStatusNotice"') < html.indexOf('id="docCenterBody"'));
  assert.match(html, /id="documentStatusNotice"[^>]+role="status"[^>]+aria-live="polite"/);
  const css = read('index.css');
  assert.match(css, /\.workspace-body\[data-layout="pdf"\]\s*\{[^}]*padding:\s*0;[^}]*overflow:\s*hidden/);
  assert.match(read(path.join('shared', 'shell.css')), /\.document-status-notice\s*\{[^}]*flex:\s*0 0 auto/);
  assert.doesNotMatch(css, /\.workspace-body\[data-tab="document"\]\s*\{/);
});

test('only actual PDF previews use the full-bleed layout and it resets when leaving the preview', () => {
  const app = runtime('index');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'file',tenantId:'one',fileName:'bundle.pdf',status:'Completed',totalPages:2,segments:[
      {segmentIndex:0,startPage:1,endPage:2,bodyText:'Source document',insightSummary:'Complete document'}]}];
    selectedJobId='file';selectedSliceIndex=-1;docResultTab='document';`, app.context);
  const body = app.element('docCenterBody');
  app.context.renderSelectedDocument();
  assert.equal(body.dataset.layout, 'pdf');
  assert.match(body.innerHTML, /class="pdf-full-frame"/);
  vm.runInContext("tenantJobs[0].segments[0].wasTranslated=true;", app.context);
  app.context.renderSelectedDocument();
  assert.equal(body.dataset.layout, 'pdf');
  assert.match(body.innerHTML, /class="pdf-side-by-side"/);
  vm.runInContext("docResultTab='insights';", app.context);
  app.context.renderSelectedDocument();
  assert.equal(body.dataset.layout, '');
  vm.runInContext("docResultTab='document';tenantJobs[0].segments=[];", app.context);
  app.context.renderSelectedDocument();
  assert.equal(body.dataset.layout, 'pdf');
  assert.match(body.innerHTML, /data-pdf-url="\/api\/harness\/tenants\/one\/jobs\/file\/original"/);
  assert.doesNotMatch(body.innerHTML, /No PDF available|\?translated=true/);
  vm.runInContext("tenantJobs=[];", app.context);
  body.dataset.layout = 'pdf';
  app.context.renderSelectedDocument();
  assert.equal(body.dataset.layout, '');
  assert.match(body.innerHTML, /Your documents, ready to explore/);
});

test('complete-file insights display once even when processing produced page fragments', () => {
  const app = runtime('index');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'file',tenantId:'one',fileName:'bundle.pdf',status:'Completed',totalPages:3,
      packetSummary:'Complete summary <unsafe>',segments:[
        {segmentIndex:0,startPage:1,endPage:1,bodyText:'Page one'},
        {segmentIndex:1,startPage:2,endPage:2,bodyText:'Page two'},
        {segmentIndex:2,startPage:3,endPage:3,bodyText:'Page three'}]}];
    selectedJobId='file';selectedSliceIndex=1;docResultTab='insights';`, app.context);
  app.context.renderSelectedDocument();
  const html = app.element('docCenterBody').innerHTML;
  assert.equal([...html.matchAll(/<article\b/g)].length, 1);
  assert.match(html, /File Insights/);
  assert.match(html, /Complete File/);
  assert.match(html, /Pages 1–3/);
  assert.match(html, /Complete summary &lt;unsafe&gt;/);
  assert.match(html, /\/jobs\/file\/original/);
  assert.doesNotMatch(html, /No insight summary generated for this slice/);
  assert.equal(app.element('sliceSelectorRow').children.length, 0);
});

test('insight prose uses the available width rather than a character-limited reading column', () => {
  const css = read('index.css');
  assert.match(css, /\.content-column\s*\{[^}]*width:\s*100%;[^}]*max-width:\s*100%/);
  assert.match(css, /\.insight-prose\s*\{[^}]*max-width:\s*100%/);
  assert.doesNotMatch(css, /\.workspace-body\[data-tab="insights"\]\s+\.insight-prose\s*\{[^}]*max-width:\s*\d+ch/);
});

test('entities omit the Source column while preserving values, scores, copying and category/slice filters', () => {
  const app = runtime('index');
  vm.runInContext(read('evidence.js'), app.context);
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'file',tenantId:'one',fileName:'invoice.pdf',status:'Completed',totalPages:2,segments:[
      {segmentIndex:0,startPage:1,endPage:1,extractedEntities:[
        {name:'Customer Name',value:'Northwind <Example>',category:'Party',confidence:0.94}]},
      {segmentIndex:1,startPage:2,endPage:2,extractedEntities:[
        {name:'Invoice Number',value:'INV-123',category:'Identifier',confidence:0.9}]}]}];
    selectedJobId='file';selectedSliceIndex=-1;docResultTab='entities';`, app.context);
  app.context.renderSelectedDocument();
  const html = app.element('docCenterBody').innerHTML;
  assert.deepEqual([...html.matchAll(/<th>(.*?)<\/th>/g)].map(match => match[1]),
    ['Entity', 'Value', 'Category', 'Model score', '']);
  const rows = [...html.match(/<tbody>([\s\S]*?)<\/tbody>/)[1].matchAll(/<tr>([\s\S]*?)<\/tr>/g)];
  assert.equal(rows.length, 2);
  assert.ok(rows.every(row => [...row[1].matchAll(/<td\b/g)].length === 5));
  assert.match(html, /Northwind &lt;Example&gt;/);
  assert.match(html, /94%/);
  assert.equal([...html.matchAll(/data-click="copyTextValue_arg"/g)].length, 2);
  assert.doesNotMatch(html, /<th>Source<\/th>|data-click="jumpToSlicePdf_arg"/);

  app.context.setEntityCategoryFilter('PARTY');
  assert.match(app.element('docCenterBody').innerHTML, /Customer Name/);
  assert.doesNotMatch(app.element('docCenterBody').innerHTML, /Invoice Number/);
  vm.runInContext('selectedSliceIndex=1;', app.context);
  app.context.renderSelectedDocument();
  assert.match(app.element('docCenterBody').innerHTML, /Invoice Number/);
  assert.doesNotMatch(app.element('docCenterBody').innerHTML, /Customer Name/);
});

test('identified document insights retain their complete ranges instead of generating page cards', () => {
  const app = runtime('index');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'file',tenantId:'one',fileName:'bundle.pdf',status:'Completed',totalPages:4,
      packetSummary:'Combined document summaries',segments:[
        {segmentIndex:0,startPage:1,endPage:2,identifiedDocumentName:'Invoice',insightSummary:'First document summary'},
        {segmentIndex:1,startPage:3,endPage:4,identifiedDocumentName:'Invoice',insightSummary:'Second document summary'}]}];
    selectedJobId='file';selectedSliceIndex=-1;docResultTab='insights';`, app.context);
  app.context.renderSelectedDocument();
  const html = app.element('docCenterBody').innerHTML;
  assert.equal([...html.matchAll(/<article\b/g)].length, 2);
  assert.match(html, /Document 1 · Pages 1–2/);
  assert.match(html, /Document 2 · Pages 3–4/);
  assert.match(html, /First document summary/);
  assert.match(html, /Second document summary/);
  assert.doesNotMatch(html, /File Insights/);
});

test('an empty insight result shows one file-level notice rather than a notice per page', () => {
  const app = runtime('index');
  vm.runInContext(`currentTenant={tenantId:'one',capabilities:{}};
    tenantJobs=[{jobId:'file',tenantId:'one',fileName:'bundle.pdf',status:'Completed',totalPages:2,segments:[
      {segmentIndex:0,startPage:1,endPage:1},{segmentIndex:1,startPage:2,endPage:2}]}];
    selectedJobId='file';selectedSliceIndex=-1;docResultTab='insights';`, app.context);
  app.context.renderSelectedDocument();
  assert.equal([...app.element('docCenterBody').innerHTML.matchAll(/No insight summary is stored/g)].length, 1);
});

for (const [page, status] of [
  ['index', 'Completed'], ['index', 'Failed'], ['admin', 'Completed'], ['admin', 'Failed']
]) {
  test(`${page}: ${status} jobs retain independent results when translation fails and show a consistent safe notice`, () => {
    const app = documentRuntime({
      jobId: 'file', tenantId: 'one', fileName: 'Somatosensory.pdf', status, totalPages: 4,
      hasWarnings: status === 'Completed', errorMessage: '<img src=x onerror=evil()>',
      executionTrace: [
        { executorName: 'NativeDecision', executed: true, succeeded: true, notes: 'One document' },
        { executorName: 'TranslateDocumentsExecutor', executed: true, succeeded: false,
          modelIdUsed: 'Translator', notes: '<unsafe translation note>' },
        { executorName: 'OptionalStep', executed: false, succeeded: false }
      ],
      segments: [{
        segmentIndex: 0, startPage: 1, endPage: 4, identifiedDocumentName: 'Somatosensory',
        wasTranslated: false, detectedLanguage: 'en', targetLanguage: 'fr',
        translationErrorCode: 'translation_failed', translationErrorMessage: 'Translation <unavailable>.',
        insightSummary: 'Stored insight <prose>', extractedEntities: [
          { name: 'Topic', value: 'Somatosensory <system>', category: 'Subject', confidence: 0.9 }
        ],
        dataExtraction: {
          documentName: 'Somatosensory', pageRange: '1–4',
          fields: [{ fieldName: 'Subject', fieldValue: 'Neuroscience <research>', confidenceValue: 95 }], tables: []
        },
        promptColumnResults: [{
          columnId: 'custom', columnName: 'Custom <Result>', filterMatched: true,
          resultText: 'Stored custom <analysis>'
        }]
      }]
    }, page);
    const expected = {
      insights: /Stored insight &lt;prose&gt;/, entities: /Somatosensory &lt;system&gt;/,
      structured: /Neuroscience &lt;research&gt;/, 'prompt:custom': /Stored custom &lt;analysis&gt;/,
      document: /Translation unavailable.*Translation &lt;unavailable&gt;/,
      metadata: /<td>Failed<\/td>/
    };
    if (page === 'admin') delete expected['prompt:custom'];
    let noticeText;
    for (const [tab, content] of Object.entries(expected)) {
      const html = renderDocumentTab(app, tab);
      const notice = app.element('documentStatusNotice');
      assert.match(html, content, tab);
      assert.doesNotMatch(html, /<unsafe|<analysis>|<prose>|<system>|<research>|<unavailable>|onerror=/);
      assert.equal(app.element('docCenterBody').dataset.layout, tab === 'document' ? 'pdf' : '');
      assert.equal(notice.hidden, false);
      assert.equal(notice.dataset.kind, status === 'Completed' ? 'warning' : 'failed');
      noticeText ??= notice.textContent;
      assert.equal(notice.textContent, noticeText);
      assert.equal(notice.innerHTML, '', 'Failure details are written as text, never HTML');
      if (tab === 'document') {
        assert.match(html, /data-pdf-url="\/api\/harness\/tenants\/one\/jobs\/file\/slices\/0\/pdf"/);
        assert.doesNotMatch(html, /\?translated=true|pdf-side-by-side|<iframe[^>]+\bsrc=/);
      }
      if (tab === 'metadata') {
        assert.match(html, /<td>Succeeded<\/td>/);
        assert.match(html, /<td>Skipped<\/td>/);
        assert.match(html, /&lt;unsafe translation note&gt;/);
        if (page === 'index')
          assert.match(html, new RegExp(`<dt>Status</dt><dd>${status === 'Completed' ? 'Completed with warnings' : 'Failed'}`));
      }
    }
    assert.match(noticeText, status === 'Completed' ? /Completed with warnings.*Translation failed/
      : /Processing failed:.*<img src=x onerror=evil\(\)>/);
    if (page === 'index') {
      assert.match(app.element('insightsTabBar').innerHTML, /Custom &lt;Result&gt;/);
    } else {
      assert.match(app.element('selectedDocStatusBadge').textContent,
        status === 'Completed' ? /Completed with warnings/ : /Failed/);
    }
  });
}

test('completed-with-warnings list responses use accessible text and theme tokens instead of an all-success dot', () => {
  const app = runtime('index');
  vm.runInContext(`tenantJobs=[
    {jobId:'warning',fileName:'Warning.pdf',status:'Completed',hasWarnings:true},
    {jobId:'legacy-warning',fileName:'Legacy.pdf',status:'Completed',
      executionTrace:[{executorName:'TranslateDocumentsExecutor',executed:true,succeeded:false}]},
    {jobId:'success',fileName:'Success.pdf',status:'Completed',hasWarnings:false}
  ];`, app.context);
  app.context.renderDocumentList();
  const rows = app.element('docItemsList').children;
  for (const row of rows.slice(0, 2)) {
    assert.match(row.innerHTML, /status-dot warning/);
    assert.match(row.innerHTML, /doc-row-warning">Completed with warnings/);
    assert.match(row['aria-label'], /Completed with warnings/);
    assert.match(row.title, /Completed with warnings/);
  }
  assert.doesNotMatch(rows[2].innerHTML, /doc-row-warning|status-dot warning/);
  assert.match(read('index.css'), /\.status-dot\.warning\s*\{\s*background:\s*var\(--warning\)/);
  assert.match(read(path.join('shared', 'shell.css')),
    /\.document-status-notice\[data-kind="warning"\][^}]+var\(--warning-bg\)/);
});

for (const [page, tab, executorName, featureName] of [
  ['index', 'insights', 'GenerateDocumentInsightsExecutor', 'Insight generation'],
  ['index', 'entities', 'ExtractEntitiesExecutor', 'Entity extraction'],
  ['index', 'structured', 'ExtractDocumentDataExecutor', 'Structured data extraction'],
  ['index', 'prompt:custom', 'EvaluatePromptColumnsExecutor', 'Custom analysis'],
  ['admin', 'insights', 'GenerateDocumentInsightsExecutor', 'Insight generation'],
  ['admin', 'entities', 'ExtractEntitiesExecutor', 'Entity extraction'],
  ['admin', 'structured', 'ExtractDocumentDataExecutor', 'Structured data extraction']
]) {
  test(`${page}: ${tab} missing results distinguish failed, skipped, succeeded-empty and unrecorded execution`, () => {
    const app = documentRuntime({
      jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed',
      executionTrace: [], segments: [{
        segmentIndex: 0, startPage: 1, endPage: 1,
        promptColumnResults: [{ columnId: 'custom', columnName: 'Custom', filterMatched: true, resultText: '' }]
      }]
    }, page);
    for (const [trace, expected] of [
      [{ executorName, executed: true, succeeded: false }, `${featureName} failed.`],
      [{ executorName, executed: false, succeeded: false }, `${featureName} was skipped for this run.`],
      [{ executorName, executed: true, succeeded: true }, `${featureName} succeeded.`],
      [null, 'This feature may not have run.']
    ]) {
      app.context.fixtureJob.executionTrace = trace ? [trace] : [];
      const html = renderDocumentTab(app, tab);
      assert.ok(html.includes(expected), html);
      assert.match(html, /No .* (?:is|are) stored for this selection\./);
      assert.doesNotMatch(html, /capability was disabled|No structured key-value fields or tables configured/);
      assert.equal(app.element('docCenterBody').dataset.layout, '');
    }
  });
}

test('a selected document without insights shows an explicit missing result instead of a blank pane or another document summary', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed', segments: [
      { segmentIndex: 0, startPage: 1, endPage: 2, insightSummary: 'Other document insight' },
      { segmentIndex: 1, startPage: 3, endPage: 4 }
    ]
  });
  vm.runInContext('selectedSliceIndex=1;', app.context);
  const html = renderDocumentTab(app, 'insights');
  assert.match(html, /No insight summary is stored for this selection/);
  assert.doesNotMatch(html, /Other document insight/);
});

test('historical failed jobs without segments retain file-level results, Metadata and a preflighted original PDF request', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed', segments: [],
    packetSummary: 'Actual file insight', extractedEntities: [{ name: 'Topic', value: 'Stored entity' }],
    promptColumnResults: [
      { columnId: 'custom', columnName: 'Custom', filterMatched: true, resultText: 'Actual file custom result' }
    ],
    executionTrace: [{ executorName: 'HistoricalStep', executed: true }]
  });
  assert.match(renderDocumentTab(app, 'insights'), /Actual file insight/);
  assert.match(renderDocumentTab(app, 'entities'), /Stored entity/);
  assert.match(renderDocumentTab(app, 'prompt:custom'), /Actual file custom result/);
  assert.match(renderDocumentTab(app, 'metadata'), /Executed \(outcome unknown\)/);
  const html = renderDocumentTab(app, 'document');
  assert.match(html, /data-pdf-url="\/api\/harness\/tenants\/one\/jobs\/file\/original"/);
  assert.doesNotMatch(html, /<iframe[^>]+\bsrc=|No PDF available|\?translated=true/);
});

test('running and queued jobs with no segments do not claim the source is missing or hide Metadata', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Queued', segments: [],
    executionTrace: [{ executorName: 'PendingStep', executed: false }]
  });
  for (const status of ['Queued', 'Running']) {
    app.context.fixtureJob.status = status;
    assert.match(renderDocumentTab(app, 'insights'), /Processing is still in progress/);
    assert.match(renderDocumentTab(app, 'metadata'), /PendingStep/);
    assert.match(renderDocumentTab(app, 'document'), /data-pdf-url="[^"]+\/original"/);
    assert.match(app.element('documentStatusNotice').textContent, /0 steps executed/);
    assert.doesNotMatch(app.element('docCenterBody').innerHTML, /No PDF available|not available in storage/);
  }
});

test('same-language documents show why translation was unnecessary without claiming translated output', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'English.pdf', status: 'Completed', hasWarnings: false,
    executionTrace: [{ executorName: 'TranslateDocumentsExecutor', executed: true, succeeded: true }],
    segments: [{ segmentIndex: 0, startPage: 1, endPage: 4, detectedLanguage: 'en', targetLanguage: 'EN',
      wasTranslated: false, insightSummary: 'English document insight' }]
  });
  const html = renderDocumentTab(app, 'document');
  assert.match(html, /Translation not needed: the detected and target language are EN/);
  assert.match(html, /No translated PDF was generated/);
  assert.doesNotMatch(html, /\?translated=true|pdf-side-by-side|data-kind="warning"/);
  assert.equal(app.element('documentStatusNotice').hidden, true);
  assert.doesNotMatch(renderDocumentTab(app, 'insights'), /→ EN translated/);
});

test('a failed translation on another slice does not label native or genuinely translated slices as failed', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'Mixed.pdf', status: 'Completed', hasWarnings: true,
    executionTrace: [{ executorName: 'TranslateDocumentsExecutor', executed: true, succeeded: false }],
    segments: [
      { segmentIndex: 0, startPage: 1, endPage: 1, detectedLanguage: 'es', targetLanguage: 'fr',
        wasTranslated: false, translationErrorCode: 'translation_failed' },
      { segmentIndex: 1, startPage: 2, endPage: 2, detectedLanguage: 'fr', targetLanguage: 'fr', wasTranslated: false },
      { segmentIndex: 2, startPage: 3, endPage: 4, detectedLanguage: 'en', targetLanguage: 'fr',
        wasTranslated: true, insightSummary: 'Real translated document insight' }
    ]
  });
  vm.runInContext('selectedSliceIndex=0;', app.context);
  assert.match(renderDocumentTab(app, 'document'), /Translation unavailable/);
  vm.runInContext('selectedSliceIndex=1;', app.context);
  const native = renderDocumentTab(app, 'document');
  assert.match(native, /Translation not needed/);
  assert.doesNotMatch(native, /Translation unavailable|\?translated=true/);
  vm.runInContext('selectedSliceIndex=2;', app.context);
  const translated = renderDocumentTab(app, 'document');
  assert.match(translated, /class="pdf-side-by-side"/);
  assert.match(translated, /slices\/2\/pdf\?translated=true/);
  assert.match(translated, /Translated Document \(FR\)/);
  assert.doesNotMatch(translated, /Translation unavailable/);
  assert.match(renderDocumentTab(app, 'insights'), /→ FR translated/);
  assert.equal(app.element('documentStatusNotice').dataset.kind, 'warning');
});

test('stored results remain accessible after current feature switches are turned off', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed',
    segments: [{
      segmentIndex: 0, startPage: 1, endPage: 1, insightSummary: 'Historical insight',
      extractedEntities: [{ name: 'Name', value: 'Historical entity' }],
      dataExtraction: { fields: [{ fieldName: 'Name', fieldValue: 'Historical field' }] },
      promptColumnResults: [{
        columnId: 'custom', columnName: 'Custom', filterMatched: true, resultText: 'Historical custom result'
      }]
    }]
  });
  vm.runInContext(`currentTenant.capabilities={
    DocumentInsight:{enabled:false},EntityExtraction:{enabled:false},
    DataExtraction:{enabled:false},PromptColumns:{enabled:false}};`, app.context);
  for (const [tab, content] of [
    ['insights', /Historical insight/], ['entities', /Historical entity/],
    ['structured', /Historical field/], ['prompt:custom', /Historical custom result/]
  ]) {
    assert.match(renderDocumentTab(app, tab), content);
    assert.equal(app.element('docCenterBody').dataset.tab, tab);
  }
});

test('a previous failure notice is cleared on a successful job or when no document is selected', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed', segments: []
  });
  renderDocumentTab(app, 'insights');
  assert.equal(app.element('documentStatusNotice').hidden, false);
  app.context.fixtureJob.status = 'Completed';
  renderDocumentTab(app, 'insights');
  assert.equal(app.element('documentStatusNotice').hidden, true);
  assert.equal(app.element('documentStatusNotice').textContent, '');
  app.context.fixtureJob.status = 'Failed';
  renderDocumentTab(app, 'insights');
  vm.runInContext('selectedJobId=null;', app.context);
  app.context.renderSelectedDocument();
  assert.equal(app.element('documentStatusNotice').hidden, true);
});

test('Admin keeps one accessible status notice outside every preview tab and clears it when selection is removed', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed',
    errorMessage: '<unsafe provider error>', segments: []
  }, 'admin');
  const lookup = app.document.getElementById;
  let notice;
  let insertions = 0;
  app.document.getElementById = id => id === 'documentStatusNotice' ? notice || null : lookup(id);
  app.element('docCenterBody').before = node => { notice = node; insertions++; };
  for (const status of ['Failed', 'Running', 'Queued']) {
    app.context.fixtureJob.status = status;
    let sharedText;
    for (const tab of ['insights', 'entities', 'structured', 'metadata', 'document']) {
      const html = renderDocumentTab(app, tab);
      sharedText ??= notice.textContent;
      assert.equal(notice.textContent, sharedText);
      assert.equal(notice.hidden, false);
      assert.equal(notice.id, 'documentStatusNotice');
      assert.equal(notice.role, 'status');
      assert.equal(notice['aria-live'], 'polite');
      assert.equal(notice.innerHTML, '');
      assert.equal(app.element('docCenterBody').dataset.layout, tab === 'document' ? 'pdf' : '');
      assert.doesNotMatch(html, /Processing Failed|<unsafe provider error>|No PDF available/);
      if (tab === 'document') assert.match(html, /data-pdf-url="[^"]+\/original"/);
      if (tab === 'metadata') assert.match(html, /Execution Trace/);
    }
  }
  assert.equal(insertions, 1);
  vm.runInContext('selectedJobId=null;', app.context);
  app.context.renderSelectedDocument();
  assert.equal(notice.hidden, true);
  assert.equal(app.element('selectedDocStatusBadge').className, 'status-pill-mini');
  assert.equal(app.element('docCenterBody').dataset.layout, '');
  const css = read('admin.css');
  assert.match(css, /\.doc-center-body\[data-layout="pdf"\][^}]+overflow:\s*hidden/);
  assert.doesNotMatch(css, /\.doc-center-body\[data-tab="document"\]/);
});

test('Admin list and preview badge distinguish warning-only list responses from successful completion', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Completed', hasWarnings: true
  }, 'admin');
  app.context.renderDocumentList();
  const row = app.element('docItemsList').children[0];
  assert.match(row.innerHTML, /status-pill-mini warning">Completed with warnings/);
  assert.match(row.title, /Completed with warnings/);
  renderDocumentTab(app, 'metadata');
  assert.match(app.element('selectedDocStatusBadge').textContent, /Completed with warnings/);
  assert.match(app.element('selectedDocStatusBadge').className, /warning/);
  assert.match(read('admin.css'), /\.status-pill-mini\.warning[^}]+var\(--warning-bg\)/);
});

test('Admin preview explains same-language skips and only requests translated PDFs for genuine translated output', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Completed',
    segments: [{ segmentIndex: 0, startPage: 1, endPage: 4, detectedLanguage: 'en',
      targetLanguage: 'en', wasTranslated: false }]
  }, 'admin');
  const source = renderDocumentTab(app, 'document');
  assert.match(source, /Translation not needed/);
  assert.match(source, /data-pdf-url="[^"]+\/slices\/0\/pdf"/);
  assert.doesNotMatch(source, /\?translated=true|<iframe[^>]+\bsrc=/);
  Object.assign(app.context.fixtureJob.segments[0], {
    wasTranslated: true, targetLanguage: 'fr', insightSummary: 'Real stored insight'
  });
  const translated = renderDocumentTab(app, 'document');
  assert.match(translated, /class="pdf-side-by-side"/);
  assert.match(translated, /data-pdf-url="[^"]+\/slices\/0\/pdf\?translated=true"/);
  assert.doesNotMatch(translated, /<iframe[^>]+\bsrc=/);
  const insight = renderDocumentTab(app, 'insights');
  assert.match(insight, /EN → FR/);
  assert.doesNotMatch(insight, /→ EN/);
});

test('Admin historical file-level results remain visible without slice records or fabricated slice links', () => {
  const app = documentRuntime({
    jobId: 'file', tenantId: 'one', fileName: 'File.pdf', status: 'Failed', segments: [],
    packetSummary: 'Actual file insight', extractedEntities: [{ name: 'Topic', value: 'Stored file entity' }],
    executionTrace: [{ executorName: 'HistoricalStep', executed: true }]
  }, 'admin');
  assert.match(renderDocumentTab(app, 'insights'), /Actual file insight/);
  const entities = renderDocumentTab(app, 'entities');
  assert.match(entities, /Stored file entity/);
  assert.doesNotMatch(entities, /Slice #NaN|data-args="\[undefined\]"/);
  assert.match(renderDocumentTab(app, 'metadata'), /Executed \(outcome unknown\)/);
});
