const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const root = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const source = fs.readFileSync(path.join(root, 'shared', 'customer.js'), 'utf8');
const uiSource = fs.readFileSync(path.join(root, 'shared', 'ui.js'), 'utf8');
const css = fs.readFileSync(path.join(root, 'shared', 'customer.css'), 'utf8');
const preferenceKey = 'docintel.ui.preferences.v1';

class FakeNode {
    constructor(tag, document) {
        this.tagName = tag.toUpperCase();
        this.ownerDocument = document;
        this.children = [];
        this.attributes = new Map();
        this.handlers = new Map();
        this.style = {};
        this.value = '';
        this.open = false;
        this._text = '';
    }
    set innerHTML(_) { throw new Error('Untrusted text must not be interpreted as HTML'); }
    set textContent(value) { this.replaceChildren(); this._text = String(value); }
    get textContent() { return this._text + this.children.map(node => node.textContent).join(''); }
    get isConnected() { return this === this.ownerDocument.documentElement || !!this.parentNode?.isConnected; }
    get isContentEditable() {
        const owner = this.closest('[contenteditable]');
        return !!owner && owner.getAttribute('contenteditable') !== 'false';
    }
    setAttribute(name, value) {
        this.attributes.set(name, String(value));
        if (name === 'id') this.id = String(value);
        if (name === 'class') this.className = String(value);
    }
    getAttribute(name) {
        if (name === 'open') return this.open ? '' : null;
        return this.attributes.get(name) ?? (typeof this[name] === 'string' ? this[name] : null);
    }
    removeAttribute(name) { this.attributes.delete(name); }
    appendChild(node) {
        node.remove();
        node.parentNode = this;
        this.children.push(node);
        return node;
    }
    replaceChildren(...nodes) {
        this.children.forEach(node => { node.parentNode = null; });
        this.children = [];
        this._text = '';
        nodes.forEach(node => this.appendChild(node));
    }
    remove() {
        if (this.parentNode) this.parentNode.children = this.parentNode.children.filter(node => node !== this);
        this.parentNode = null;
    }
    contains(node) { return node === this || this.children.some(child => child.contains(node)); }
    matches(selector) {
        const negative = [...selector.matchAll(/:not\(([^)]+)\)/g)].map(match => match[1]);
        if (negative.some(value => this.matches(value))) return false;
        selector = selector.replace(/:not\([^)]+\)/g, '');
        const tag = selector.match(/^[a-z][\w-]*/i)?.[0];
        if (tag && this.tagName !== tag.toUpperCase()) return false;
        const id = selector.match(/#([\w-]+)/)?.[1];
        if (id && id !== this.id) return false;
        if ([...selector.matchAll(/\.([\w-]+)/g)].some(match =>
            !(this.className || '').split(/\s+/).includes(match[1]))) return false;
        return [...selector.matchAll(/\[([\w-]+)(?:="([^"]*)")?\]/g)].every(([, name, value]) =>
            value === undefined ? this.getAttribute(name) !== null : this.getAttribute(name) === value);
    }
    closest(selectors) {
        for (let node = this; node; node = node.parentNode) {
            if (selectors.split(',').some(selector => node.matches(selector.trim()))) return node;
        }
        return null;
    }
    querySelector(selectors) {
        for (const child of this.children) {
            if (selectors.split(',').some(selector => child.matches(selector.trim()))) return child;
            const nested = child.querySelector(selectors);
            if (nested) return nested;
        }
        return null;
    }
    addEventListener(type, handler) {
        if (!this.handlers.has(type)) this.handlers.set(type, []);
        this.handlers.get(type).push(handler);
    }
    emit(type, options = {}) {
        const event = {
            type, target: this, key: '', defaultPrevented: false,
            preventDefault() { this.defaultPrevented = true; },
            composedPath() { return [this.target]; },
            ...options
        };
        for (const handler of this.handlers.get(type) || []) handler(event);
        if (type === 'keydown') this.ownerDocument.emit(type, event);
        return event;
    }
    click() { return this.emit('click'); }
    focus() { this.ownerDocument.activeElement = this; }
    scrollIntoView() {}
    showModal() {
        assert.equal(this.ownerDocument.querySelector('dialog[open]'), null, 'Never stack modal dialogs');
        this.open = true;
    }
    close() { this.open = false; this.emit('close'); }
}

function runtime(options = {}) {
    const handlers = new Map();
    const windowHandlers = new Map();
    const timers = new Map();
    const saved = [];
    let timerId = 0;
    const document = {
        readyState: options.loading ? 'loading' : 'complete',
        createElement(tag) { return new FakeNode(tag, document); },
        getElementById(id) { return this.documentElement.querySelector(`#${id}`); },
        querySelector(selector) { return this.documentElement.querySelector(selector); },
        addEventListener(type, handler) {
            if (!handlers.has(type)) handlers.set(type, []);
            handlers.get(type).push(handler);
        },
        dispatchEvent(event) { this.emit(event.type, event); },
        emit(type, event = {}) { for (const handler of handlers.get(type) || []) handler(event); }
    };
    document.documentElement = document.createElement('html');
    document.body = document.createElement('body');
    document.documentElement.appendChild(document.body);
    document.activeElement = document.body;
    const host = document.createElement('div');
    host.setAttribute('data-docintel-ux-host', '');
    document.body.appendChild(host);
    const storage = {
        getItem() {
            if (options.readFailure) throw new Error('Private browser/storage details must never be shown');
            return options.stored ?? null;
        },
        setItem(key, value) {
            if (options.writeFailure) throw new Error('Private browser/storage details must never be shown');
            saved.push({ key, value });
        }
    };
    const sandbox = {
        document, localStorage: storage,
        CustomEvent: class { constructor(type, input) { this.type = type; this.detail = input.detail; } },
        setTimeout(callback, delay) { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
        clearTimeout(id) { timers.delete(id); },
        addEventListener(type, handler) { windowHandlers.set(type, handler); },
        fetch() { throw new Error('The shared customer layer must never discover API data'); }
    };
    if (options.storageGetterFailure) Object.defineProperty(sandbox, 'localStorage', {
        get() { throw new Error('Storage access denied'); }
    });
    const context = vm.createContext(sandbox);
    vm.runInContext(source, context);
    return {
        context, document, host, saved, timers,
        ux: context.DocIntelUX,
        element: id => document.getElementById(id),
        create(tag, attributes = {}) {
            const node = document.createElement(tag);
            Object.entries(attributes).forEach(([name, value]) => node.setAttribute(name, value));
            document.body.appendChild(node);
            return node;
        },
        search(value) {
            const search = document.getElementById('di-command-search');
            search.value = value;
            search.emit('input');
        },
        options() { return document.getElementById('di-command-results').children; },
        storage(event) { windowHandlers.get('storage')(event); },
        runTimers() {
            for (const [id, timer] of [...timers]) { timers.delete(id); timer.callback(); }
        },
        loadUi() { vm.runInContext(uiSource, context); }
    };
}

test('default appearance follows the system and exposes a frozen, UI-only contract without API discovery', () => {
    const app = runtime();
    assert.ok(Object.isFrozen(app.ux));
    assert.ok(Object.isFrozen(app.ux.getPreferences()));
    assert.equal(app.element('di-theme').tagName, 'SELECT');
    assert.equal(app.document.documentElement.getAttribute('data-di-theme'), 'system');
    assert.equal(app.document.documentElement.getAttribute('data-di-density'), 'comfortable');
    assert.equal(app.saved.length, 0);
    assert.equal(app.host.children.length, 1);
    assert.equal(app.ux.openCommands(), true);
    assert.equal(app.options().length, 7);
    assert.ok(app.options().every(option => !/tenant|document|delete/i.test(option.textContent)));
});

test('valid stored appearance restores only the validated theme and density', () => {
    const app = runtime({ stored: JSON.stringify({ version: 1, theme: 'dark', density: 'compact' }) });
    assert.equal(app.document.documentElement.getAttribute('data-di-theme'), 'dark');
    assert.equal(app.document.documentElement.getAttribute('data-di-density'), 'compact');
    assert.doesNotMatch(app.document.body.textContent, /could not be read/);
});

test('malformed, unsupported, or extended preferences fall back explicitly without overwriting storage', () => {
    for (const stored of ['broken', 'null', '[]',
        JSON.stringify({ version: 1, theme: 'javascript:bad', density: 'compact' }),
        JSON.stringify({ version: 1, theme: 'light', density: 'tiny' }),
        JSON.stringify({ version: 2, theme: 'light', density: 'compact' }),
        JSON.stringify({ version: 1, theme: 'light', density: 'compact', agent: 'changed' })]) {
        const app = runtime({ stored });
        assert.equal(app.ux.getPreferences().theme, 'system');
        assert.equal(app.ux.getPreferences().density, 'comfortable');
        assert.match(app.document.body.textContent, /could not be read/);
        assert.equal(app.saved.length, 0);
        assert.equal(app.timers.size, 0);
    }
});

test('unavailable storage is explicit and saving failure keeps the current page usable without raw errors', () => {
    for (const options of [{ readFailure: true, writeFailure: true }, { storageGetterFailure: true }]) {
        const app = runtime(options);
        assert.match(app.document.body.textContent, /could not be read/);
        assert.equal(app.ux.setPreferences({ theme: 'dark' }), false);
        assert.equal(app.ux.getPreferences().theme, 'dark');
        assert.match(app.document.body.textContent, /changed for this page only/);
        assert.doesNotMatch(app.document.body.textContent, /Private browser|Storage access denied/);
        app.runTimers();
        assert.match(app.document.body.textContent, /browser blocked saving/);
    }
});

test('preference changes validate before writing and cannot autosave business configuration', () => {
    const app = runtime();
    assert.equal(app.ux.setPreferences({ theme: 'light', density: 'compact' }), true);
    assert.equal(app.saved[0].key, preferenceKey);
    assert.deepEqual(JSON.parse(app.saved[0].value), { version: 1, theme: 'light', density: 'compact' });
    for (const patch of [{ theme: 'night' }, { density: 'tiny' }, { agent: 'other' }, null, []]) {
        assert.equal(app.ux.setPreferences(patch), false);
    }
    assert.equal(app.saved.length, 1);
    assert.equal(app.ux.getPreferences().theme, 'light');
    assert.match(app.document.body.textContent, /Appearance was not changed/);
});

test('appearance selections require Save, Cancel does not persist, and dialogs restore keyboard focus', () => {
    const app = runtime();
    const trigger = app.host.children[0].children[1];
    trigger.focus();
    assert.equal(app.ux.openPreferences(), true);
    const dialog = app.element('di-appearance-dialog');
    assert.equal(dialog.getAttribute('aria-labelledby'), 'di-appearance-dialog-title');
    assert.equal(app.document.activeElement, app.element('di-theme'));
    app.element('di-theme').value = 'dark';
    assert.equal(app.saved.length, 0);
    dialog.emit('cancel');
    assert.equal(dialog.open, false);
    assert.equal(app.document.activeElement, trigger);
    assert.equal(app.ux.getPreferences().theme, 'system');
    app.ux.openPreferences();
    app.element('di-theme').value = 'dark';
    app.element('di-density').value = 'compact';
    dialog.children[2].children[1].click();
    assert.equal(app.saved.length, 1);
    assert.equal(app.ux.getPreferences().density, 'compact');
    assert.equal(dialog.open, false);
});

test('unsupported native dialogs report the limitation instead of opening an inaccessible fake modal', () => {
    const app = runtime();
    app.element('di-command-dialog').showModal = undefined;
    assert.equal(app.ux.openCommands(), false);
    assert.equal(app.element('di-command-dialog').open, false);
    assert.match(app.document.body.textContent, /cannot open the accessible dialog/);
});

test('cross-tab appearance is validated, reset can follow system, and unrelated keys are ignored', () => {
    const app = runtime();
    app.storage({ key: preferenceKey, newValue: JSON.stringify({ version: 1, theme: 'dark', density: 'compact' }) });
    assert.equal(app.ux.getPreferences().theme, 'dark');
    app.storage({ key: preferenceKey, newValue: '{"theme":"unsupported"}' });
    assert.equal(app.ux.getPreferences().theme, 'dark');
    assert.match(app.document.body.textContent, /was invalid and was not applied/);
    app.storage({ key: 'agentSettings', newValue: 'invalid' });
    assert.equal(app.ux.getPreferences().density, 'compact');
    app.storage({ key: preferenceKey, newValue: null });
    assert.equal(app.ux.getPreferences().theme, 'system');
    assert.equal(app.ux.getPreferences().density, 'comfortable');
});

test('only registered enabled commands appear and authorization is checked again at activation', () => {
    const app = runtime();
    let allowed = true, count = 0;
    app.ux.registerCommands([
        { id: 'screen.allowed', title: 'Find my source', keywords: ['evidence'], enabled: () => allowed, run: () => count++ },
        { id: 'screen.hidden', title: 'Secret admin listing', enabled: () => false, run: () => assert.fail() },
        { id: 'screen.broken', title: 'Broken authorization', enabled: () => { throw new Error(); }, run: () => assert.fail() }
    ]);
    app.ux.openCommands();
    app.search('secret');
    assert.equal(app.options().length, 0);
    assert.match(app.element('di-command-status').textContent, /No available commands/);
    assert.equal(app.element('di-command-search').getAttribute('aria-activedescendant'), null);
    app.search('evidence');
    assert.equal(app.options().length, 1);
    allowed = false;
    app.options()[0].click();
    assert.equal(count, 0);
    assert.equal(app.options().length, 0);
});

test('reregistering safely replaces handlers and stale cleanup cannot unregister a newer render', async () => {
    const app = runtime();
    let first = 0, second = 0;
    const cleanOld = app.ux.registerCommands([{ id: 'screen.open', title: 'Old action', run: () => first++ }]);
    const cleanNew = app.ux.registerCommands([{ id: 'screen.open', title: 'Current action', run: () => second++ }]);
    cleanOld();
    app.ux.openCommands();
    app.search('Current');
    app.options()[0].click();
    await Promise.resolve();
    assert.equal(first, 0);
    assert.equal(second, 1);
    assert.equal(app.element('di-command-dialog').open, false);
    cleanNew();
    app.ux.openCommands();
    app.search('Current');
    assert.equal(app.options().length, 0);
});

test('invalid command batches are atomic and cannot replace reserved visual commands', () => {
    const app = runtime();
    assert.throws(() => app.ux.registerCommands([
        { id: 'screen.valid', title: 'Never registered', run() {} },
        { id: 'screen.invalid', title: 'Missing handler' }
    ]), /command needs/i);
    assert.throws(() => app.ux.registerCommands([{ id: 'docintel.theme.light', title: 'Override', run() {} }]));
    app.ux.openCommands();
    app.search('Never registered');
    assert.equal(app.options().length, 0);
});

test('palette options use safe text and expose combobox, listbox and active-descendant keyboard semantics', async () => {
    const app = runtime();
    let selected = '';
    app.ux.registerCommands([
        { id: 'screen.one', title: 'Special <img onerror=evil()>', run: () => { selected = 'one'; } },
        { id: 'screen.two', title: 'Special second', run: () => { selected = 'two'; } }
    ]);
    app.ux.openCommands();
    app.search('special');
    const input = app.element('di-command-search');
    assert.equal(input.getAttribute('role'), 'combobox');
    assert.equal(app.element(input.getAttribute('aria-controls')).getAttribute('role'), 'listbox');
    assert.match(app.options()[0].textContent, /<img onerror/);
    assert.equal(app.options()[0].children.length, 0);
    assert.equal(app.options()[0].getAttribute('aria-selected'), 'true');
    input.emit('keydown', { key: 'ArrowDown' });
    assert.equal(app.options()[1].getAttribute('aria-selected'), 'true');
    assert.equal(input.getAttribute('aria-activedescendant'), app.options()[1].id);
    input.emit('keydown', { key: 'Enter' });
    await Promise.resolve();
    assert.equal(selected, 'two');
    assert.equal(input.getAttribute('aria-expanded'), 'false');
});

test('command failures are persistent, sanitized and never automatically retried', async () => {
    const app = runtime();
    let calls = 0;
    app.ux.registerCommands([{ id: 'screen.failure', title: 'Fail safely', run: async () => {
        calls++;
        throw new Error('SECRET raw backend error');
    } }]);
    app.ux.openCommands();
    app.search('Fail safely');
    app.options()[0].click();
    await new Promise(resolve => setImmediate(resolve));
    app.runTimers();
    assert.equal(calls, 1);
    assert.match(app.document.body.textContent, /No automatic retry/);
    assert.doesNotMatch(app.document.body.textContent, /SECRET raw/);
    assert.equal(app.document.querySelector('[data-kind="error"]').getAttribute('role'), 'alert');
});

test('an in-flight command cannot be run twice, including from a newly opened palette', async () => {
    const app = runtime();
    let calls = 0, finish;
    app.ux.registerCommands([{ id: 'screen.slow', title: 'Slow command', run: () => {
        calls++;
        return new Promise(resolve => { finish = resolve; });
    } }]);
    app.ux.openCommands();
    app.search('Slow command');
    app.options()[0].click();
    app.ux.openCommands();
    app.search('Slow command');
    assert.equal(app.options().length, 0);
    assert.equal(calls, 1);
    finish();
    await new Promise(resolve => setImmediate(resolve));
    app.search('Slow command');
    assert.equal(app.options().length, 1);
});

test('Ctrl+K and Meta+K work on the body while composition, editors and existing modals retain input', () => {
    for (const modifier of ['ctrlKey', 'metaKey']) {
        const app = runtime();
        const event = app.document.body.emit('keydown', { key: 'k', [modifier]: true });
        assert.equal(event.defaultPrevented, true);
        assert.equal(app.element('di-command-dialog').open, true);
    }
    const variants = [
        ['textarea', {}], ['select', {}], ['div', { contenteditable: 'true' }], ['input', { type: 'password' }],
        ['div', { 'data-di-shortcuts': 'off' }], ['div', { class: 'monaco-editor' }],
        ['div', { role: 'textbox' }]
    ];
    for (const [tag, attributes] of variants) {
        const app = runtime();
        const event = app.create(tag, attributes).emit('keydown', { key: 'k', ctrlKey: true });
        assert.equal(event.defaultPrevented, false);
        assert.equal(app.element('di-command-dialog').open, false);
    }
    for (const flags of [{ isComposing: true }, { keyCode: 229 }, { repeat: true }, { defaultPrevented: true }]) {
        const app = runtime();
        app.document.body.emit('keydown', { key: 'k', ctrlKey: true, ...flags });
        assert.equal(app.element('di-command-dialog').open, false);
    }
    const app = runtime();
    app.document.emit('compositionstart');
    app.document.body.emit('keydown', { key: 'k', ctrlKey: true });
    assert.equal(app.element('di-command-dialog').open, false);
    app.document.emit('compositionend');
    const external = app.create('dialog');
    external.showModal();
    assert.equal(app.ux.openCommands(), false);
    assert.equal(app.document.body.emit('keydown', { key: 'k', ctrlKey: true }).defaultPrevented, false);
});

test('question-mark help never intercepts input, select, editable, modified or composition keystrokes', () => {
    for (const [tag, attributes] of [['input', {}], ['select', {}], ['textarea', {}],
        ['div', { contenteditable: '' }], ['div', { 'data-di-shortcuts': 'off' }]]) {
        const app = runtime();
        assert.equal(app.create(tag, attributes).emit('keydown', { key: '?' }).defaultPrevented, false);
        assert.equal(app.element('di-shortcut-dialog').open, false);
    }
    for (const flags of [{ isComposing: true }, { ctrlKey: true }, { metaKey: true }, { altKey: true }]) {
        const app = runtime();
        app.document.body.emit('keydown', { key: '?', ...flags });
        assert.equal(app.element('di-shortcut-dialog').open, false);
    }
    const app = runtime();
    assert.equal(app.document.body.emit('keydown', { key: '?', shiftKey: true }).defaultPrevented, true);
    assert.equal(app.element('di-shortcut-dialog').open, true);
});

test('errors and statuses persist, safe references are validated, and successes remain transient', () => {
    const app = runtime();
    const dismiss = app.ux.inform('<script>plain text</script>', { kind: 'error', reference: 'safe-123:abc' });
    app.ux.inform('Status stays until dismissed');
    app.ux.inform('Saved', { kind: 'success' });
    const error = app.document.querySelector('[data-kind="error"]');
    assert.match(error.textContent, /<script>plain text<\/script>/);
    assert.match(error.textContent, /Reference: safe-123:abc/);
    assert.equal(error.getAttribute('aria-live'), 'assertive');
    app.runTimers();
    assert.match(app.document.body.textContent, /Status stays/);
    assert.equal(app.document.querySelector('[data-kind="success"]'), null);
    app.ux.inform('No unsafe reference', { reference: '<unsafe@example.com>' });
    assert.doesNotMatch(app.document.body.textContent, /<unsafe@example.com>/);
    dismiss();
    assert.equal(error.isConnected, false);
    const status = app.document.querySelector('[data-kind="info"]');
    status.querySelector('button').click();
    assert.equal(status.isConnected, false);
});

test('shared showToast stays backward-compatible without making all acknowledgements persistent', () => {
    const app = runtime();
    app.loadUi();
    app.context.showToast('Copied');
    app.context.showToast('Check permission', { kind: 'error' });
    assert.equal(app.document.querySelector('[data-kind="success"]').getAttribute('role'), 'status');
    app.runTimers();
    assert.equal(app.document.querySelector('[data-kind="success"]'), null);
    assert.match(app.document.querySelector('[data-kind="error"]').textContent, /Check permission/);
});

test('deferred loading queues notices, applies theme early and mounts controls only once', () => {
    const app = runtime({ loading: true });
    assert.equal(app.ux.getPreferences().theme, 'system');
    assert.equal(app.host.children.length, 0);
    app.ux.inform('Queued status');
    app.document.emit('DOMContentLoaded');
    app.document.emit('DOMContentLoaded');
    assert.equal(app.host.children.length, 1);
    assert.match(app.document.body.textContent, /Queued status/);
    vm.runInContext(source, app.context);
    assert.equal(app.host.children.length, 1);
});

test('foundation CSS bridges legacy tokens with no global pane reflow, supports contrast, density and reduced motion', () => {
    for (const token of ['di-bg', 'di-surface', 'di-text', 'di-muted', 'di-border', 'di-accent',
        'text-primary', 'text-secondary', 'text-muted', 'header-surface', 'canvas-bg', 'rail-bg', 'pill-active']) {
        assert.match(css, new RegExp(`--${token}:`));
    }
    assert.match(css, /prefers-color-scheme: dark/);
    assert.match(css, /data-di-theme="light"/);
    assert.match(css, /data-di-density="compact"/);
    assert.match(css, /pointer: coarse/);
    assert.match(css, /prefers-reduced-motion: reduce/);
    assert.match(css, /forced-colors: active/);
    assert.match(css, /focus-visible/);
    assert.doesNotMatch(css, /(?:\.workspace-main|\.sidebar|\.doc-drawer)\s*\{[^}]*(?:display|width|position):/);
});

test('light and dark tokens meet normal-text and non-text control contrast thresholds', () => {
    function luminance(hex) {
        const [red, green, blue] = hex.slice(1).match(/../g).map(value => parseInt(value, 16) / 255)
            .map(value => value <= .04045 ? value / 12.92 : ((value + .055) / 1.055) ** 2.4);
        return red * .2126 + green * .7152 + blue * .0722;
    }
    function contrast(first, second) {
        const values = [luminance(first), luminance(second)].sort((a, b) => b - a);
        return (values[0] + .05) / (values[1] + .05);
    }
    const blocks = [
        css.match(/:root \{([\s\S]*?)\n\}/)[1],
        css.match(/:root\[data-di-theme="dark"\] \{([\s\S]*?)\n\}/)[1]
    ];
    for (const block of blocks) {
        const tokens = Object.fromEntries([...block.matchAll(/--(di-[\w-]+):\s*(#[\da-f]{6})/g)]
            .map(([, key, value]) => [key, value]));
        for (const [foreground, background] of [
            ['di-text', 'di-surface'], ['di-muted', 'di-surface'], ['di-muted', 'di-surface-soft'],
            ['di-accent', 'di-surface'], ['di-on-accent', 'di-accent'],
            ['di-text', 'di-danger-soft'], ['di-text', 'di-warning-soft']
        ]) assert.ok(contrast(tokens[foreground], tokens[background]) >= 4.5, `${foreground}/${background}`);
        for (const foreground of ['di-border', 'di-focus']) {
            assert.ok(contrast(tokens[foreground], tokens['di-surface']) >= 3, `${foreground}/di-surface`);
        }
    }
});

test('standalone pages load identity and customer foundations before page scripts, with no executable inline content', () => {
    for (const page of ['ask-run', 'email-question']) {
        const html = fs.readFileSync(path.join(root, `${page}.html`), 'utf8');
        assert.ok(html.indexOf('/identity.js') < html.indexOf('/shared/customer.js'));
        assert.ok(html.indexOf('/shared/customer.js') < html.indexOf(`/${page}.js`));
        assert.ok(html.indexOf('/ask-run.css') < html.indexOf('/shared/customer.css'));
        assert.doesNotMatch(html, /\son[a-z]+\s*=|javascript:|<script(?![^>]*\bsrc=)[^>]*>/i);
        assert.match(html, /aria-live="polite"/);
        assert.match(html, /di-skip-link/);
        assert.match(html, /data-docintel-ux-host/);
    }
    assert.doesNotMatch(source, /\.innerHTML\s*=|insertAdjacentHTML|\beval\s*\(|new Function|fetch\s*\(/);
    assert.doesNotMatch(fs.readFileSync(path.join(root, 'ask-run.js'), 'utf8'), /error\.message|result\.errorMessage/);
    assert.doesNotMatch(fs.readFileSync(path.join(root, 'email-question.js'), 'utf8'), /error\.message/);
});
