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
      this.labels.push({ text, x, y, font: this.font, color: this.fillStyle, width: this.measureText(text).width });
    }
  };
  const app = {
    frames, listeners, context2d, observers: [], bounds: { width: 320, height: 420 },
    colors: {
      '--graph-document': '#252525', '--graph-entity': '#388e80', '--graph-edge': '#bec5c9',
      '--graph-edge-dim': '#bbbbbb', '--graph-halo': '#dddddd', '--graph-label-bg': '#ffffff',
      '--text-heading': '#202020', '--surface-soft': '#f8f8f8'
    }
  };
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
    window, getComputedStyle: () => ({
      fontFamily: '"Segoe UI", sans-serif', getPropertyValue: name => app.colors[name]
    }),
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

test('canvas uses its actual CSS size and reveals a readable local-font label only on hover', () => {
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
  assert.equal(app.context2d.labels.length, 0, 'Even a small graph must start without labels');
  const position = vm.runInContext("graphCustomNodePositions['ent:Customer Name||Northwind Example']", app.context);
  canvas.onmousemove({ clientX: position.x, clientY: position.y });
  app.flush();
  assert.equal(app.context2d.labels.length, 1, 'Hover must not reveal neighboring document labels');
  const [label] = app.context2d.labels;
  assert.ok(label.font.includes('13px "Segoe UI"'));
  assert.ok(label.width <= 112);
  app.context2d.labels = [];
  canvas.onmouseleave();
  app.flush();
  assert.equal(app.context2d.labels.length, 0);
  app.context.stopGraphCanvas();
});

test('shared entity hubs and zoom never enable default labels', () => {
  const app = runtime(Array.from({ length: 25 }, (_, i) =>
    job(`document-${i}`, [entity('Customer Name', 'Shared entity')])));
  app.startCanvas();
  app.flush();
  assert.equal(app.context2d.labels.length, 0);
  vm.runInContext('graphZoom=2.6;graphCanvasRefresh();', app.context);
  app.flush();
  assert.equal(app.context2d.labels.length, 0, 'Zoom must not make a dense graph crowded with text');
  app.context.stopGraphCanvas();
});

test('explicit entity selection reveals only the selected label, not every connected document', () => {
  const app = runtime();
  vm.runInContext("graphSelectedEntityValues.add('Customer Name||Northwind');", app.context);
  app.startCanvas();
  app.flush();
  assert.deepEqual([...new Set(app.context2d.labels.map(label => label.text))], ['Northwind']);
  app.context2d.labels = [];
  vm.runInContext("graphSelectedEntityValues.clear();graphFocusedNodeId='doc:first';graphCanvasRefresh();", app.context);
  app.flush();
  assert.deepEqual(app.context2d.labels.map(label => label.text), ['Contract first']);
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

test('focusing nodes and opening details do not rebuild or move a settled canvas', () => {
  const app = runtime([job('first', [entity('Customer Name', 'Northwind')])]);
  app.startCanvas();
  app.flush();
  const positions = vm.runInContext('JSON.stringify(graphCustomNodePositions)', app.context);
  const observers = app.observers.length;
  app.context.focusGraphNode('doc:first');
  app.flush();
  assert.equal(app.observers.length, observers);
  assert.equal(vm.runInContext('JSON.stringify(graphCustomNodePositions)', app.context), positions);
  assert.equal(app.node('graphInspectorAside').hidden, false);
  app.context.toggleGraphDetails();
  app.flush();
  assert.equal(app.node('graphInspectorAside').hidden, true);
  assert.equal(app.observers.length, observers);
  app.context.stopGraphCanvas();
});

test('theme changes repaint Graph labels without restarting its layout', () => {
  const app = runtime();
  app.startCanvas();
  app.flush();
  app.context.focusGraphNode('doc:first');
  app.flush();
  const positions = vm.runInContext('JSON.stringify(graphCustomNodePositions)', app.context);
  app.colors['--text-heading'] = '#f7f7f7';
  vm.runInContext('graphCanvasRefresh()', app.context);
  app.flush();
  assert.equal(app.context2d.labels.at(-1).color, '#f7f7f7');
  assert.equal(vm.runInContext('JSON.stringify(graphCustomNodePositions)', app.context), positions);
  app.context.stopGraphCanvas();
});

test('large workspaces retain complete counts but bound canvas nodes, edges and inspector links', () => {
  const jobs = Array.from({ length: 3000 }, (_, index) =>
    job(`document-${index}`, [entity('Record', `Unique-${index}`)]));
  const app = runtime(jobs);
  const data = app.context.computeFilteredGraphData();
  assert.equal(data.docNodes.length, 3000);
  assert.equal(data.entityNodes.length, 3000);
  const window = app.context.buildGraphCanvasWindow(data.docNodes, data.entityNodes, data.edges);
  assert.equal(window.totalNodes, 6000);
  assert.equal(window.totalEdges, 3000);
  assert.ok(window.docNodes.length + window.entityNodes.length <= 500);
  assert.ok(window.edges.length <= 2000);
  assert.equal(window.limited, true);
  app.startCanvas();
  assert.ok(app.flush() < 200);
  assert.equal(app.context2d.labels.length, 0);
  assert.match(app.node('graphWorkbenchContainer').innerHTML, /Canvas shows 500 of 6000 nodes/);
  app.context.toggleGraphDetails();
  const inspector = app.node('graphInspectorAside').innerHTML;
  assert.match(inspector, /Showing 100 of 3000 documents/);
  assert.equal((inspector.match(/data-click="jumpFromGraphToSlice_arg_arg"/g) || []).length, 100);
  app.context.focusGraphNode('doc:document-2999');
  assert.equal(vm.runInContext("graphCanvasNodeIds.has('doc:document-2999')", app.context), true);
  app.context.stopGraphCanvas();
});

test('entity-option rendering is bounded while search can reach values beyond the initial window', () => {
  const app = runtime(Array.from({ length: 3000 }, (_, index) =>
    job(`document-${index}`, [entity('Record', `Unique-${index}`)])));
  app.context.renderDocumentGraphTab(app.node('docCenterBody'));
  const html = app.node('docCenterBody').innerHTML;
  assert.match(html, /Showing 200 of 3000 values/);
  assert.equal((html.match(/class="entity-value-option/g) || []).length, 200);
  app.context.filterEntityPopoverOptions('Unique-2999');
  assert.match(app.node('entityPopoverList').innerHTML, /Unique-2999/);
  assert.equal((app.node('entityPopoverList').innerHTML.match(/class="entity-value-option/g) || []).length, 1);
});

test('graph canvas keyboard focus reveals one label and selection remains keyboard-operable', () => {
  const app = runtime([job('first', [entity('Customer Name', 'Northwind')])]);
  app.startCanvas();
  app.flush();
  const canvas = app.node('obsidianGraphCanvas');
  let prevented = 0;
  canvas.onkeydown({ key: 'ArrowRight', preventDefault() { prevented++; } });
  app.flush();
  assert.equal(app.context2d.labels.length, 1);
  assert.match(canvas['aria-label'], /Northwind/);
  canvas.onkeydown({ key: 'Enter', preventDefault() { prevented++; } });
  app.flush();
  assert.equal(vm.runInContext('graphFocusedNodeId', app.context), 'ent:Customer Name||Northwind');
  app.context2d.labels = [];
  canvas.onkeydown({ key: 'Escape', preventDefault() { prevented++; } });
  app.flush();
  assert.equal(app.context2d.labels.length, 0);
  assert.equal(vm.runInContext('graphFocusedNodeId', app.context), null);
  assert.equal(prevented, 3);
  app.context.stopGraphCanvas();
});

test('graph entity keys do not collapse distinct names and values containing the delimiter', () => {
  const app = runtime([
    job('first', [entity('A||B', 'C')]),
    job('second', [entity('A', 'B||C')]),
    job('third', [entity('A', '%7C')])
  ]);
  const data = app.context.computeFilteredGraphData();
  assert.equal(data.entityNodes.length, 3);
  const pair = app.context.graphEntityPairKey('A||B', 'C');
  vm.runInContext(`graphSelectedEntityValues.add(${JSON.stringify(pair)});`, app.context);
  assert.deepEqual(Array.from(app.context.computeFilteredGraphData().docNodes, node => node.jobId), ['first']);
  app.context.renderDocumentGraphTab(app.node('docCenterBody'));
  assert.match(app.node('docCenterBody').innerHTML, /A\|\|B/);
});

test('filter rerenders keep an existing layout fixed when its geometry is unchanged', () => {
  const app = runtime();
  app.startCanvas();
  app.flush();
  const positions = vm.runInContext('JSON.stringify(graphCustomNodePositions)', app.context);
  app.context.renderDocumentGraphCanvasOnly();
  assert.equal(app.frames.size, 0);
  assert.equal(vm.runInContext('JSON.stringify(graphCustomNodePositions)', app.context), positions);
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
