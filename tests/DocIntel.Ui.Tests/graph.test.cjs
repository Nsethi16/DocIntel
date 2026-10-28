const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const { loadApplication } = require('./helpers/application.cjs');

const entity = (name, value) => ({ name, value, category: 'Entity' });
const job = (id, entities) => ({
  jobId: id, fileName: `Contract ${id}.pdf`, status: 'Completed', totalPages: 2,
  segments: [{ startPage: 1, endPage: 2, extractedEntities: entities }]
});
const fixtures = [
  job('first', [entity('Customer Name', 'Northwind'), entity('Billing Currency', 'USD')]),
  job('second', [entity('Customer Name', 'Northwind'), entity('Billing Currency', 'EUR')]),
  job('third', [entity('Customer Name', 'Contoso')])
];

function runtime(jobs = fixtures) {
  const nodes = new Map();
  const frames = new Map();
  const listeners = new Map();
  let nextFrame = 0;
  const context2d = {
    font: '', labels: [],
    save() {}, restore() {}, scale() {}, translate() {}, clearRect() {}, beginPath() {},
    moveTo() {}, lineTo() {}, stroke() {}, arc() {}, rect() {}, fill() {}, fillRect() {},
    measureText(text) { return { width: Array.from(text).length * 7 }; },
    fillText(text, x, y) {
      this.labels.push({ text, x, y, font: this.font, width: this.measureText(text).width });
    }
  };
  const app = { frames, listeners, context2d, observers: [], bounds: { width: 320, height: 420 } };
  const node = id => {
    if (nodes.has(id)) return nodes.get(id);
    const classes = new Set();
    const result = {
      id, value: '', style: {}, dataset: {}, hidden: false, textContent: '', innerHTML: '',
      isConnected: true, children: [],
      classList: {
        add(name) { classes.add(name); }, remove(name) { classes.delete(name); },
        contains(name) { return classes.has(name); },
        toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); }
      },
      setAttribute(name, value) { this[name] = String(value); },
      getAttribute(name) { return this[name]; },
      focus() { app.focused = id; },
      appendChild(child) { this.children.push(child); },
      getBoundingClientRect() { return { left: 0, top: 0, ...app.bounds }; },
      getContext() { return context2d; }
    };
    nodes.set(id, result);
    return result;
  };
  const window = {
    devicePixelRatio: 2,
    addEventListener(name, callback) {
      if (!listeners.has(name)) listeners.set(name, new Set());
      listeners.get(name).add(callback);
    },
    removeEventListener(name, callback) { listeners.get(name)?.delete(callback); }
  };
  app.context = vm.createContext({
    document: { getElementById: node, querySelector: () => null, querySelectorAll: () => [] },
    window, getComputedStyle: () => ({ fontFamily: '"Segoe UI", sans-serif' }),
    ResizeObserver: class {
      constructor(callback) { this.callback = callback; app.observers.push(this); }
      observe(target) { this.target = target; }
      disconnect() { this.disconnected = true; }
    },
    requestAnimationFrame(callback) { frames.set(++nextFrame, callback); return nextFrame; },
    cancelAnimationFrame(id) { frames.delete(id); },
    fetch() { throw new Error('Graph interaction must not call a provider.'); },
    console, fixtures: jobs
  });
  vm.runInContext(loadApplication('index'), app.context);
  vm.runInContext(`workspaceMode='graph';currentTenant={tenantId:'one',displayName:'Workspace',capabilities:{}};
    tenantJobs=fixtures;selectedJobId=fixtures[0]?.jobId;`, app.context);
  app.nativeCanvas = app.context.initObsidianForceCanvas;
  app.context.initObsidianForceCanvas = () => {};
  app.node = node;
  app.flush = (limit = 300) => {
    let count = 0;
    while (frames.size && count < limit) {
      const [id, callback] = frames.entries().next().value;
      frames.delete(id);
      callback();
      count++;
    }
    return count;
  };
  app.startCanvas = () => {
    app.context.initObsidianForceCanvas = app.nativeCanvas;
    app.context.renderDocumentGraphCanvasOnly();
  };
  return app;
}

test('graph filters retain strict AND semantics for entity types and values', () => {
  const app = runtime();
  assert.equal(app.context.computeFilteredGraphData().docNodes.length, 3);
  vm.runInContext("graphSelectedEntityValues.add('Customer Name||Northwind');", app.context);
  assert.equal(app.context.computeFilteredGraphData().docNodes.length, 2);
  vm.runInContext("graphSelectedEntityValues.add('Billing Currency||USD');", app.context);
  const data = app.context.computeFilteredGraphData();
  assert.equal(data.docNodes.length, 1);
  assert.equal(data.docNodes[0].jobId, 'first');
  assert.equal(data.edges.length, 2);
  vm.runInContext("graphSelectedEntityNames.add('Missing entity type');", app.context);
  assert.equal(app.context.computeFilteredGraphData().docNodes.length, 0);
});

test('the default graph is a compact filter bar and canvas without dashboard cards or an open inspector', () => {
  const app = runtime();
  app.context.renderDocumentGraphTab(app.node('docCenterBody'));
  const shell = app.node('docCenterBody').innerHTML;
  const workbench = app.node('graphWorkbenchContainer').innerHTML;
  assert.match(shell, /Filter Entities/);
  assert.match(shell, /id="graphDetailsToggle"/);
  assert.doesNotMatch(shell + workbench, /Commercial Exposure|Counterparties|Dossier|graph-info-card/);
  assert.match(workbench, /id="graphInspectorAside"[^>]* hidden/);
  assert.match(workbench, /graph-document-dot/);
  assert.match(workbench, /graph-entity-dot/);
  assert.doesNotMatch(workbench, /Party \/ Customer|Identifier|Financial|Metadata/);
  assert.equal(app.node('graphDetailsToggle')['aria-expanded'], 'false');
  assert.equal(app.node('graphInfoStripContainer').textContent, '5 connections');
});

test('graph details are on demand and document navigation stays connected to the existing actions', () => {
  const app = runtime();
  app.context.toggleGraphDetails();
  assert.equal(app.node('graphDetailsToggle')['aria-expanded'], 'true');
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /Documents \(3\)/);
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /data-click="jumpFromGraphToSlice_arg_arg"/);
  app.context.focusGraphNode('ent:Customer Name||Northwind');
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /Connected Documents \(2\)/);
  app.context.focusGraphNode('doc:first');
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /Open Document/);
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /Entities \(2\)/);
  app.context.toggleGraphDetails();
  assert.equal(app.node('graphDetailsToggle')['aria-expanded'], 'false');
  assert.equal(app.node('graphWorkbenchContainer').classList.contains('details-open'), false);
});

test('missing extraction and filters with no matches have distinct honest empty states', () => {
  const app = runtime([job('empty', [])]);
  app.context.renderDocumentGraphTab(app.node('docCenterBody'));
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /No Connections Yet/);
  assert.doesNotMatch(app.node('graphWorkbenchContainer').innerHTML, /data-click="resetGraphFilters"/);
  assert.equal(app.node('graphDetailsToggle').disabled, true);
  vm.runInContext("graphSelectedEntityValues.add('Customer Name||Missing');", app.context);
  app.context.renderDocumentGraphCanvasOnly();
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /No Matching Connections/);
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /data-click="resetGraphFilters"/);
});

test('entity presence and exact-value filters stay distinct and use keyboard-native controls', () => {
  const app = runtime();
  vm.runInContext("graphSelectedEntityNames.add('Customer Name');", app.context);
  app.context.renderDocumentGraphTab(app.node('docCenterBody'));
  const html = app.node('docCenterBody').innerHTML;
  assert.match(html, /<button[^>]+class="entity-group-header" aria-pressed="true"/);
  assert.match(html, /<button[^>]+class="entity-value-option " aria-pressed="false"/);
  assert.match(html, /Has Customer Name/);
  app.context.toggleEntityMultiSelectDropdown({ stopPropagation() {} });
  assert.equal(app.node('entityMultiSelectBox')['aria-expanded'], 'true');
  assert.equal(app.focused, 'entityPopoverSearchInput');
  app.context.closeGraphDropdown();
  assert.equal(app.node('entityMultiSelectBox')['aria-expanded'], 'false');
});

test('untrusted entity values and document names are escaped in graph filters and details', () => {
  const app = runtime([{
    ...job('one', [entity('Customer <unsafe>', '<img src=x onerror=evil()>')]),
    fileName: '<script>document</script>.pdf'
  }]);
  app.context.renderDocumentGraphTab(app.node('docCenterBody'));
  assert.match(app.node('docCenterBody').innerHTML, /Customer &lt;unsafe&gt;/);
  assert.match(app.node('docCenterBody').innerHTML, /&lt;img src=x onerror=evil\(\)&gt;/);
  app.context.toggleGraphDetails();
  const html = app.node('graphWorkbenchContainer').innerHTML;
  assert.match(html, /&lt;script&gt;document&lt;\/script&gt;/);
  assert.doesNotMatch(html, /<script>/);
});

test('canvas uses its actual CSS size, local font and readable separated labels on a narrow view', () => {
  const app = runtime([{
    ...job('one', [entity('Customer Name', 'Northwind Example')]),
    fileName: 'Northwind service agreement.pdf'
  }]);
  app.startCanvas();
  const canvas = app.node('obsidianGraphCanvas');
  assert.equal(canvas.width, 640, '320 CSS pixels at 2x density, not a forced 640 CSS-pixel minimum');
  assert.equal(canvas.height, 840);
  assert.ok(app.flush() < 200);
  assert.equal(app.frames.size, 0, 'Settled graphs must not animate forever');
  const labels = app.context2d.labels.slice(-2);
  assert.ok(labels.every(label => label.font.includes('13px "Segoe UI"')));
  assert.ok(labels.every(label => label.width <= 112));
  assert.ok(Math.abs(labels[0].x - labels[1].x) > (labels[0].width + labels[1].width) / 2);
  app.context.stopGraphCanvas();
});

test('canvas redraws on hover after settling and disposes animation, resize and mouse listeners', () => {
  const app = runtime();
  app.startCanvas();
  app.flush();
  assert.equal(app.frames.size, 0);
  app.node('obsidianGraphCanvas').onmousemove({ clientX: 0, clientY: 0 });
  assert.equal(app.frames.size, 1);
  app.flush();
  app.context.stopGraphCanvas();
  assert.equal(app.frames.size, 0);
  assert.equal(app.observers[0].disconnected, true);
  assert.equal(app.listeners.get('mouseup').size, 0);
});

test('canvas clicks use CSS coordinates rather than backing-store pixels', () => {
  const app = runtime([job('first', [entity('Customer Name', 'Northwind')])]);
  app.startCanvas();
  app.flush();
  const canvas = app.node('obsidianGraphCanvas');
  const position = vm.runInContext("graphCustomNodePositions['doc:first']", app.context);
  canvas.onmousedown({
    clientX: position.x * app.bounds.width / (canvas.width / 2),
    clientY: position.y * app.bounds.height / (canvas.height / 2)
  });
  for (const listener of Array.from(app.listeners.get('mouseup'))) listener();
  assert.equal(vm.runInContext('graphFocusedNodeId', app.context), 'doc:first');
  assert.equal(app.node('graphDetailsToggle')['aria-expanded'], 'true');
  app.context.stopGraphCanvas();
});

test('resizing rebuilds the canvas at the new size without accumulating listeners', () => {
  const app = runtime();
  app.startCanvas();
  app.bounds = { width: 480, height: 420 };
  app.observers[0].callback();
  assert.equal(app.observers[0].disconnected, true);
  assert.equal(app.node('obsidianGraphCanvas').width, 960);
  assert.equal(app.listeners.get('mouseup').size, 1);
  app.context.stopGraphCanvas();
});

test('switching away from Graph stops its canvas and switching workspaces clears Graph-only state', async () => {
  const app = runtime();
  app.startCanvas();
  vm.runInContext("workspaceMode='documents';tenantJobs=[];selectedJobId=null;", app.context);
  app.context.renderSelectedDocument();
  assert.equal(app.frames.size, 0);
  assert.equal(app.observers[0].disconnected, true);
  vm.runInContext(`graphSelectedEntityNames.add('Customer Name');graphDetailsOpen=true;
    graphZoom=2;tenants=[{tenantId:'two'}];loadTenantChats=()=>{};
    renderTenantSelect=()=>{};loadTenantJobs=async()=>{};`, app.context);
  await app.context.onTenantSwitched('two');
  assert.equal(vm.runInContext('graphSelectedEntityNames.size', app.context), 0);
  assert.equal(vm.runInContext('graphDetailsOpen', app.context), false);
  assert.equal(vm.runInContext('graphZoom', app.context), 1);
});

test('label fitting respects the measured width without splitting Unicode characters', () => {
  const app = runtime();
  const label = '\u{1f4c4}'.repeat(20);
  const fitted = app.context.fitGraphLabel(app.context2d, label, 50);
  assert.ok(app.context2d.measureText(fitted).width <= 50);
  assert.ok(fitted.endsWith('\u2026'));
  assert.equal(Array.from(fitted.slice(0, -1)).every(character => character === '\u{1f4c4}'), true);
});
