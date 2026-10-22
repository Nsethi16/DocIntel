const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'agent-settings.js'), 'utf8');

function runtime() {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) elements.set(id, { value: '', checked: false, hidden: false, textContent: '',
      classList: { toggle() {} }, setAttribute(name, value) { this[name] = value; },
      set innerHTML(value) { throw new Error('Profile text must not become HTML'); } });
    return elements.get(id);
  };
  const app = { element, response: { email: { configured: false }, teams: { configured: false } } };
  const context = vm.createContext({
    document: { getElementById: element },
    fetch: async () => ({ ok: true, json: async () => typeof app.response === 'function' ? app.response() : app.response })
  });
  vm.runInContext(source, context);
  app.settings = context.DocIntelAgentSettings;
  return app;
}

test('personality editing roundtrips safely with an illustrative style preview and no generated-answer claim', () => {
  const app = runtime();
  app.settings.render({ displayName: '<img onerror=evil()>', purpose: 'Explain contracts', tone: 'Friendly',
    instructions: 'Plain English', emailEnabled: true });
  assert.equal(app.element('agentPreviewName').textContent, '<img onerror=evil()>');
  assert.match(app.element('agentPreviewMessage').textContent, /Happy to help/);
  const result = app.settings.read();
  assert.equal(result.emailEnabled, true);
  assert.equal(result.teamsEnabled, false);
  assert.equal(result.instructions, 'Plain English');
  app.element('agentTone').value = 'Concise';
  app.settings.preview();
  assert.match(app.element('agentPreviewMessage').textContent, /lead with the answer/);
});

test('agent settings have a separate discoverable panel from document-processing controls', () => {
  const app = runtime();
  app.settings.section('agent');
  assert.equal(app.element('agentSettingsPanel').hidden, false);
  assert.equal(app.element('processingSettingsPanel').hidden, true);
  assert.equal(app.element('agentSettingsTab')['aria-selected'], 'true');
  app.settings.section('processing');
  assert.equal(app.element('processingSettingsPanel').hidden, false);
  assert.equal(app.element('agentSettingsPanel').hidden, true);
});

test('a tenant toggle cannot pretend a mailbox or bot is connected', async () => {
  const app = runtime();
  app.settings.render({ emailEnabled: true, teamsEnabled: true });
  await app.settings.connections('tenant');
  assert.match(app.element('agentEmailStatus').textContent, /Not connected/);
  assert.match(app.element('agentTeamsStatus').textContent, /Not connected/);
});

test('late channel-state responses cannot replace a newly selected tenant connection state', async () => {
  const app = runtime();
  let release;
  let entered;
  const body = new Promise(resolve => { entered = resolve; });
  app.response = () => { entered(); return new Promise(resolve => { release = resolve; }); };
  const old = app.settings.connections('first');
  await body;
  app.response = { email: { configured: false }, teams: { configured: false } };
  await app.settings.connections('second');
  release({ email: { configured: true, address: 'old@example.invalid' }, teams: { configured: true } });
  await old;
  assert.doesNotMatch(app.element('agentEmailStatus').textContent, /old@example/);
});
