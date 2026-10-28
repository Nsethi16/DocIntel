const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const root = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const source = fs.readFileSync(path.join(root, 'shared', 'actions.js'), 'utf8');

function runtime() {
  const listeners = new Map();
  const notices = [];
  const context = vm.createContext({
    console: { error: () => notices.push('error') },
    showToast: message => notices.push(message),
    document: { addEventListener: (name, callback) => listeners.set(name, callback) }
  });
  vm.runInContext(source, context);
  function element(attributes = {}, parent = null) {
    return {
      parentElement: parent, disabled: false, value: '',
      getAttribute: name => attributes[name] ?? null,
      closest(selector) {
        const attribute = selector.slice(1, -1);
        for (let node = this; node; node = node.parentElement) {
          if (node.getAttribute(attribute) !== null) return node;
        }
        return null;
      }
    };
  }
  function fire(type, target, key) {
    const event = { type, target, key, cancelBubble: false, defaultPrevented: false,
      preventDefault() { this.defaultPrevented = true; },
      stopPropagation() { this.cancelBubble = true; }
    };
    listeners.get(type)(event);
    return event;
  }
  return { register: context.DocIntelActions.register, element, fire, context, notices };
}

test('application pages and generated markup contain no executable inline handlers or scripts', () => {
  for (const name of ['index.html', 'admin.html', 'workspace.js', 'admin.js']) {
    const text = fs.readFileSync(path.join(root, name), 'utf8');
    assert.doesNotMatch(text, /\bon(?:click|input|change|keydown)\s*=\s*["']/i, name);
    if (name.endsWith('.html')) {
      for (const match of text.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)) {
        assert.match(match[1], /src="\/[^"]+\.js"/);
        assert.equal(match[2].trim(), '');
      }
      assert.doesNotMatch(text, /<style>/i);
      assert.match(text, /\/shared\/ui\.js/);
      assert.match(text, /\/shared\/shell\.css/);
    } else {
      assert.doesNotMatch(text, /\beval\s*\(|new Function\s*\(/);
    }
  }
});

test('action arguments remain primitive data, including strings that look like JavaScript', () => {
  const app = runtime();
  const values = [];
  app.register({ capture: { arity: 1, run: args => values.push(args[0]) } });
  const payloads = ["');globalThis.injected=true;//", '<img onerror=evil()>', 'constructor', '"quoted" & text'];
  for (const value of payloads) {
    app.fire('click', app.element({ 'data-click': 'capture', 'data-args': JSON.stringify([value]) }));
  }
  assert.deepEqual(values, payloads);
  assert.equal(app.context.injected, undefined);
  assert.deepEqual(app.notices, []);
});

test('unknown names, malformed JSON, wrong arity and nested objects cannot invoke handlers', () => {
  const app = runtime();
  let calls = 0;
  app.register({ capture: { arity: 1, run: () => calls++ } });
  for (const attributes of [
    { 'data-click': 'constructor', 'data-args': '[]' },
    { 'data-click': 'capture', 'data-args': 'not-json' },
    { 'data-click': 'capture', 'data-args': '[]' },
    { 'data-click': 'capture', 'data-args': '[{"code":"run"}]' }
  ]) {
    const event = app.fire('click', app.element(attributes));
    assert.equal(event.defaultPrevented, true);
  }
  assert.equal(calls, 0);
  assert.equal(app.notices.filter(notice => notice === 'error').length, 4);
});

test('nested actions preserve bubbling order and explicit stopPropagation', () => {
  const app = runtime();
  const calls = [];
  app.register({
    parent: { arity: 0, run: () => calls.push('parent') },
    child: { arity: 0, run: () => calls.push('child') },
    stop: { arity: 0, run: (args, event) => event.stopPropagation() }
  });
  const parent = app.element({ 'data-click': 'parent' });
  const child = app.element({ 'data-click': 'child' }, parent);
  app.fire('click', app.element({}, child));
  assert.deepEqual(calls, ['child', 'parent']);
  calls.length = 0;
  app.fire('click', app.element({ 'data-click': 'stop' }, parent));
  assert.deepEqual(calls, []);
});

test('enter and change actions receive the actual element and event without parsing expressions', () => {
  const app = runtime();
  const values = [];
  app.register({ input: { arity: 0, run: function (args, event) { values.push([this.value, event.type]); } } });
  const input = app.element({ 'data-enter': 'input', 'data-change': 'input' });
  input.value = 'Question';
  app.fire('keydown', input, 'Escape');
  app.fire('keydown', input, 'Enter');
  input.value = 'Changed';
  app.fire('change', input);
  assert.deepEqual(values, [['Question', 'keydown'], ['Changed', 'change']]);
});

test('asynchronous failures are visible without logging exception text or source values', async () => {
  const app = runtime();
  app.register({ failure: { arity: 0, run: async () => { throw new Error('PRIVATE_SOURCE_VALUE'); } } });
  app.fire('click', app.element({ 'data-click': 'failure' }));
  await Promise.resolve();
  await Promise.resolve();
  assert.ok(app.notices.length > 0);
  assert.doesNotMatch(app.notices.join(), /PRIVATE_SOURCE_VALUE/);
});

test('shared clipboard helpers report success only after acknowledgement and preserve zero values', async () => {
  const toast = { textContent: '', style: {} };
  const written = [];
  const context = vm.createContext({
    document: { getElementById: () => toast }, setTimeout: () => {},
    navigator: { clipboard: { writeText: async value => written.push(value) } }
  });
  vm.runInContext(fs.readFileSync(path.join(root, 'shared', 'ui.js'), 'utf8'), context);
  await context.copyTextValue(0);
  assert.deepEqual(written, ['0']);
  assert.equal(toast.textContent, 'Copied to clipboard');
  context.navigator.clipboard.writeText = async () => { throw new Error('Permission denied'); };
  await context.copyTextValue('Value');
  assert.match(toast.textContent, /could not be copied/);
  context.navigator.clipboard = null;
  await context.copyTextValue('Value');
  assert.match(toast.textContent, /unavailable/);
});
