const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot',
  'shared', 'shell.js'), 'utf8');

function runtime({ saved = {}, dark = false, width = 1280, defaultWidth = 280, storageDenied = false } = {}) {
  const nodes = new Map();
  const storage = new Map(Object.entries(saved));
  const events = new Map();
  const documentEvents = new Map();
  const styles = new Map();
  const emitter = () => {
    const listeners = new Map();
    return {
      addEventListener(name, callback) {
        if (!listeners.has(name)) listeners.set(name, []);
        listeners.get(name).push(callback);
      },
      emit(name, event = {}) {
        event.preventDefault ??= () => {};
        for (const callback of listeners.get(name) || []) callback(event);
      }
    };
  };
  function element(tag) {
    const classes = new Set();
    const attributes = new Map();
    const captures = new Set();
    return {
      ...emitter(), tag, children: [], dataset: {}, value: '',
      set id(value) { this.name = value; nodes.set(value, this); },
      get id() { return this.name; },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute(name) { return attributes.get(name); },
      classList: {
        add(name) { classes.add(name); }, remove(name) { classes.delete(name); },
        contains(name) { return classes.has(name); }
      },
      append(...children) { this.children.push(...children); },
      appendChild(child) { this.children.push(child); },
      remove() { nodes.delete(this.id); },
      focus() {},
      setPointerCapture(id) { captures.add(id); },
      hasPointerCapture(id) { return captures.has(id); },
      releasePointerCapture(id) { captures.delete(id); this.emit('lostpointercapture'); }
    };
  }
  const html = element('html');
  html.style = { setProperty: (name, value) => styles.set(name, value) };
  const sidebar = element('aside');
  sidebar.dataset.sidebarDefault = String(defaultWidth);
  const divider = element('div');
  sidebar.querySelector = () => divider;
  const host = element('div');
  const scheme = { ...emitter(), matches: dark };
  const narrow = { matches: width <= 700 };
  const fail = () => {
    if (storageDenied) throw Object.assign(new Error('Blocked browser storage'), { name: 'SecurityError' });
  };
  const document = {
    readyState: 'loading', body: null, documentElement: html,
    createElement: element, getElementById: id => nodes.get(id) || null,
    querySelectorAll: () => [host], querySelector: () => sidebar,
    addEventListener: (name, callback) => documentEvents.set(name, callback),
    dispatchEvent: event => { documentEvents.get(event.type)?.(event); }
  };
  const context = vm.createContext({
    document, innerWidth: width,
    matchMedia: query => query.includes('prefers-color-scheme') ? scheme : narrow,
    CustomEvent: class { constructor(type, options) { this.type = type; this.detail = options.detail; } },
    addEventListener: (name, callback) => events.set(name, callback),
    localStorage: {
      getItem(key) { fail(); return storage.get(key) ?? null; },
      setItem(key, value) { fail(); storage.set(key, value); },
      removeItem(key) { fail(); storage.delete(key); }
    }
  });
  vm.runInContext(source, context);
  const app = {
    context, html, sidebar, divider, storage, styles, scheme, narrow, events,
    ready() { document.body = element('body'); documentEvents.get('DOMContentLoaded')(); },
    theme() { return html.getAttribute('data-theme'); },
    select(value) { const select = nodes.get('appearanceSelect'); select.value = value; select.emit('change'); },
    width() { return Number(divider.getAttribute('aria-valuenow')); },
    notice() { return nodes.get('shellPreferenceNotice')?.textContent || ''; }
  };
  return app;
}

test('appearance is applied before content renders and explicit Dark survives a reload', () => {
  const app = runtime({ saved: { docintel_appearance_v1: 'dark' } });
  assert.equal(app.theme(), 'dark');
  app.ready();
  assert.equal(app.html.getAttribute('data-appearance'), 'dark');
  app.select('light');
  assert.equal(app.theme(), 'light');
  assert.equal(app.storage.get('docintel_appearance_v1'), 'light');
  const reloaded = runtime({ saved: Object.fromEntries(app.storage) });
  assert.equal(reloaded.theme(), 'light');
});

test('System follows device changes while an explicit appearance is preserved', () => {
  const app = runtime();
  app.ready();
  assert.equal(app.theme(), 'light');
  app.scheme.matches = true;
  app.scheme.emit('change');
  assert.equal(app.theme(), 'dark');
  app.select('light');
  app.scheme.emit('change');
  assert.equal(app.theme(), 'light');
  app.select('system');
  assert.equal(app.theme(), 'dark');
});

test('preferences synchronize between pages without rewriting them', () => {
  const app = runtime();
  app.ready();
  app.events.get('storage')({ key: 'docintel_appearance_v1', newValue: 'dark' });
  assert.equal(app.theme(), 'dark');
  assert.equal(app.storage.size, 0);
  app.events.get('storage')({ key: 'docintel_sidebar_width_v1', newValue: '360' });
  assert.equal(app.width(), 360);
});

test('sidebar drag uses the pointer delta, saves only on completion and supports cancellation', () => {
  const app = runtime();
  app.ready();
  app.divider.emit('pointerdown', { button: 0, pointerId: 1, clientX: 274 });
  app.divider.emit('pointermove', { pointerId: 1, clientX: 354 });
  assert.equal(app.width(), 360);
  assert.equal(app.storage.size, 0);
  app.divider.emit('pointerup', { pointerId: 1 });
  assert.equal(app.storage.get('docintel_sidebar_width_v1'), '360');
  assert.equal(app.html.classList.contains('sidebar-resizing'), false);
  app.divider.emit('pointerdown', { button: 0, pointerId: 2, clientX: 354 });
  app.divider.emit('pointermove', { pointerId: 2, clientX: 600 });
  assert.equal(app.width(), 480);
  app.divider.emit('pointercancel');
  assert.equal(app.width(), 360);
  assert.equal(app.storage.get('docintel_sidebar_width_v1'), '360');
});

test('keyboard resizing is bounded and double-click restores the page default', () => {
  const app = runtime();
  app.ready();
  app.divider.emit('keydown', { key: 'ArrowRight' });
  assert.equal(app.width(), 290);
  app.divider.emit('keydown', { key: 'ArrowRight', shiftKey: true });
  assert.equal(app.width(), 320);
  app.divider.emit('keydown', { key: 'End' });
  assert.equal(app.width(), 480);
  app.divider.emit('keydown', { key: 'Home' });
  assert.equal(app.width(), 200);
  app.divider.emit('dblclick');
  assert.equal(app.width(), 280);
  assert.equal(app.storage.has('docintel_sidebar_width_v1'), false);
});

test('viewport bounds preserve space for content without losing the saved preferred width', () => {
  const app = runtime({ saved: { docintel_sidebar_width_v1: '480' }, width: 768 });
  app.ready();
  assert.equal(app.width(), 408);
  assert.equal(app.divider.getAttribute('aria-valuemax'), '408');
  app.context.innerWidth = 1280;
  app.events.get('resize')();
  assert.equal(app.width(), 480);
  assert.equal(app.storage.get('docintel_sidebar_width_v1'), '480');
});

test('mobile and collapsed sidebars do not start a resizing gesture', () => {
  const app = runtime({ width: 390 });
  app.ready();
  const before = app.width();
  app.divider.emit('pointerdown', { button: 0, pointerId: 1, clientX: 200 });
  app.divider.emit('pointermove', { pointerId: 1, clientX: 300 });
  assert.equal(app.width(), before);
  app.narrow.matches = false;
  app.sidebar.classList.add('collapsed');
  app.divider.emit('keydown', { key: 'End' });
  assert.equal(app.width(), before);
});

test('admin and document sidebars retain their defaults and share an explicit width preference', () => {
  const admin = runtime({ defaultWidth: 216 });
  admin.ready();
  assert.equal(admin.width(), 216);
  admin.divider.emit('keydown', { key: 'ArrowRight' });
  const documents = runtime({ saved: Object.fromEntries(admin.storage) });
  documents.ready();
  assert.equal(documents.width(), 226);
});

test('invalid saved preferences and unavailable storage are reported instead of pretending to persist', () => {
  const invalid = runtime({ saved: { docintel_appearance_v1: 'unknown', docintel_sidebar_width_v1: '900' } });
  invalid.ready();
  assert.equal(invalid.theme(), 'light');
  assert.equal(invalid.width(), 280);
  assert.match(invalid.notice(), /invalid/);
  const denied = runtime({ storageDenied: true });
  denied.ready();
  denied.select('dark');
  assert.equal(denied.theme(), 'dark');
  assert.match(denied.notice(), /only to this page/);
  assert.equal(denied.storage.size, 0);
});
