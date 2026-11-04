const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'agent-settings.js'), 'utf8');

test('the real Admin markup does not hide the settings navigation inside the conflict notice', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'admin.html'), 'utf8');
  const sections = [];
  const seen = new Set();
  for (const [tag] of html.matchAll(/<\/?section\b[^>]*>|<[^>]+\bid="(?:processingSettingsTab|agentSettingsTab|harnessDecisionModelSelect)"[^>]*>/g)) {
    if (tag.startsWith('</section')) {
      assert.ok(sections.length > 0, 'Unbalanced settings section');
      sections.pop();
    } else if (tag.startsWith('<section')) {
      const id = tag.match(/\bid="([^"]+)"/)?.[1] || 'anonymous';
      if (['agentSettingsPanel', 'processingSettingsPanel'].includes(id))
        assert.ok(!sections.includes('harnessSaveNotice'), `${id} cannot live inside a hidden error panel`);
      sections.push(id);
    } else {
      const id = tag.match(/\bid="([^"]+)"/)[1];
      seen.add(id);
      assert.ok(!sections.includes('harnessSaveNotice'), `${id} must remain visible when there is no conflict`);
      if (id === 'harnessDecisionModelSelect') assert.ok(sections.includes('processingSettingsPanel'));
    }
  }
  assert.equal(sections.length, 0);
  assert.equal(seen.size, 3);
});

test('compact profile fields retain associated labels, limits and the existing edit actions', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'admin.html'), 'utf8');
  const fields = [...html.matchAll(/<div class="agent-profile-field">([\s\S]*?)<\/div>/g)].map(match => match[1]);
  assert.equal(fields.length, 4);
  for (const [index, [id, action, limit]] of [
    ['agentDisplayName', 'data-input="onAgentProfileChanged"', 64],
    ['agentTone', 'data-change="onAgentProfileChanged"'],
    ['agentPurpose', 'data-input="onAgentProfileChanged"', 500],
    ['agentInstructions', 'data-input="onAgentProfileChanged"', 4000]
  ].entries()) {
    assert.ok(fields[index].includes(`for="${id}"`));
    assert.ok(fields[index].includes(`id="${id}"`));
    assert.ok(fields[index].includes(action));
    if (limit) assert.ok(fields[index].includes(`maxlength="${limit}"`));
  }
  assert.match(fields[0], /aria-describedby="agentIdentityNote"/);
  assert.match(html, /class="agent-safety-note agent-profile-note" id="agentIdentityNote"/);
  assert.equal([...html.matchAll(/class="agent-channel-header"/g)].length, 2);
});

function runtime() {
  const elements = new Map();
  const element = id => {
    if (!elements.has(id)) {
      const classes = new Set();
      elements.set(id, { id, value: '', checked: false, hidden: false, textContent: '',
        classList: {
          toggle(name, enabled) { if (enabled) classes.add(name); else classes.delete(name); },
          contains(name) { return classes.has(name); }
        },
        setAttribute(name, value) { this[name] = value; },
        set innerHTML(value) { throw new Error('Profile text must not become HTML'); } });
    }
    return elements.get(id);
  };
  const app = { element, requests: [], operations: [], navigations: [],
    response: { email: { configured: false }, teams: { configured: false } } };
  element('adminSections').addEventListener = (type, listener) => { app[type] = listener; };
  for (const id of ['processingSettingsTab', 'agentSettingsTab', 'tenantUsersTab'])
    element(id).focus = () => { app.focused = id; };
  const context = vm.createContext({
    document: { getElementById: element },
    location: { assign: url => app.navigations.push(url) },
    fetch: async (url, options) => {
      app.requests.push(url);
      app.operations.push({ url, options });
      const result = typeof app.response === 'function' ? await app.response(url, options) : app.response;
      return { ok: !result.status || result.status < 400, json: async () => result };
    }
  });
  vm.runInContext(source, context);
  app.settings = context.DocIntelAgentSettings;
  return app;
}

test('personal Outlook connection state exposes owner consent controls without sending or signing in automatically', async () => {
    const app = runtime();
    app.response = { email: { configured: true, personalAvailable: true, provider: 'OutlookCom',
      address: 'vault@example.com', state: 'NotConnected', connected: false }, teams: { configured: false } };
    await app.settings.connections('tenant');
    assert.equal(app.element('agentPersonalEmailControls').hidden, false);
    assert.equal(app.element('agentEmailConnect').disabled, false);
    assert.equal(app.element('agentEmailDisconnect').disabled, true);
    assert.match(app.element('agentEmailStatus').textContent, /not connected/);
    assert.equal(app.navigations.length, 0);
    assert.equal(app.operations.filter(operation => operation.options?.method === 'POST').length, 0);
  });

  test('personal mailbox connection uses a protected POST and accepts only the local opaque start route', async () => {
    const app = runtime();
    const status = { email: { configured: true, personalAvailable: true, provider: 'OutlookCom',
      address: 'vault@example.com', state: 'NotConnected', connected: false }, teams: { configured: false } };
    app.response = status;
    await app.settings.connections('tenant');
    const redirect = '/auth/email/personal/start?request=' + 'A'.repeat(64);
    app.response = (url, options) => options?.method === 'POST' ? { redirectUrl: redirect } : status;
    await app.settings.personalEmail('connect');
    assert.deepEqual(app.navigations, [redirect]);
    assert.equal(app.operations.filter(operation => operation.options?.method === 'POST').length, 1);
    assert.match(app.operations.find(operation => operation.options?.method === 'POST').url, /\/mailbox\/connect$/);
    app.response = (url, options) => options?.method === 'POST'
      ? { redirectUrl: 'https://untrusted.example' } : status;
    await app.settings.personalEmail('connect');
    assert.equal(app.navigations.length, 1);
    assert.match(app.element('agentEmailConnectionNotice').textContent, /invalid.*No automatic retry/);
  });

  test('renewal and disconnect states are explicit rather than a successful delivery claim', async () => {
    const app = runtime();
    const status = { email: { configured: true, personalAvailable: true, provider: 'OutlookCom',
      address: 'vault@example.com', state: 'ReconnectionRequired', connected: false }, teams: { configured: false } };
    app.response = status;
    await app.settings.connections('tenant');
    assert.match(app.element('agentEmailStatus').textContent, /consent needs renewal/);
    assert.equal(app.element('agentEmailDisconnect').disabled, false);
    app.response = (url, options) => options?.method === 'POST' ? { disconnected: true } : status;
    await app.settings.personalEmail('disconnect');
    assert.equal(app.navigations.length, 0);
    assert.match(app.element('agentEmailConnectionNotice').textContent, /no longer use/);
  });

  test('a late personal mailbox action cannot navigate or update a newly selected workspace', async () => {
    const app = runtime();
    const status = { email: { configured: true, personalAvailable: true, provider: 'OutlookCom',
      address: 'vault@example.com', state: 'NotConnected', connected: false }, teams: { configured: false } };
    app.response = status;
    await app.settings.connections('first');
    let resolve;
    app.response = (url, options) => options?.method === 'POST'
      ? new Promise(done => { resolve = done; }) : { email: { configured: false }, teams: { configured: false } };
    const pending = app.settings.personalEmail('connect');
    app.settings.render({ displayName: 'Second workspace' });
    await app.settings.connections('second');
    resolve({ redirectUrl: '/auth/email/personal/start?request=' + 'B'.repeat(64) });
    await pending;
    assert.equal(app.navigations.length, 0);
    assert.equal(app.element('agentPersonalEmailControls').hidden, true);
    assert.match(app.element('agentEmailStatus').textContent, /Not connected/);
  });
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

test('per-question confirmation is a separate saved preference and does not change email enablement', () => {
  const app = runtime();
  app.settings.render({ emailEnabled: true });
  assert.equal(app.settings.read().emailConfirmationRequired, false);
  app.element('agentEmailConfirmationRequired').checked = true;
  assert.equal(app.settings.read().emailConfirmationRequired, true);
  assert.equal(app.settings.read().emailEnabled, true);
  app.settings.render({ emailEnabled: true, emailConfirmationRequired: true });
  assert.equal(app.element('agentEmailConfirmationRequired').checked, true);
});

test('Admin describes the actual operator automation policy and both admission limits', async () => {
  const app = runtime();
  app.response = { email: { configured: true, authorizationMode: 'ApprovedUsers',
    automaticQuestionsPerSenderHour: 20, automaticQuestionsPerWorkspaceHour: 100 }, teams: { configured: false } };
  await app.settings.connections('tenant');
  assert.match(app.element('agentEmailPolicy').textContent, /ApprovedUsers.*20.*100/);
  app.response = { email: { configured: true, authorizationMode: 'ConfirmSender' }, teams: { configured: false } };
  await app.settings.connections('tenant');
  assert.match(app.element('agentEmailPolicy').textContent, /confirmation is required by the operator/);
});

test('Manage summaries show current drafts as text without exposing the full response guidance', () => {
  const app = runtime();
  app.settings.render({ displayName: '<img onerror=evil()>', purpose: '<script>purpose</script>',
    tone: 'Friendly', instructions: '<script>guidance</script>' });
  assert.equal(app.element('agentNameSummary').textContent, '<img onerror=evil()>');
  assert.equal(app.element('agentPurposeSummary').textContent, '<script>purpose</script>');
  assert.equal(app.element('agentToneSummary').textContent, 'Friendly');
  assert.equal(app.element('agentInstructionsSummary').textContent, 'Custom response guidance is set');
  app.element('agentDisplayName').value = 'Unsaved name';
  app.element('agentPurpose').value = '';
  app.element('agentInstructions').value = '  ';
  app.settings.preview();
  assert.equal(app.element('agentNameSummary').textContent, 'Unsaved name');
  assert.equal(app.element('agentPurposeSummary').textContent, 'Describe its purpose and audience');
  assert.equal(app.element('agentInstructionsSummary').textContent, 'Optional instructions for your assistant');
  assert.equal(app.settings.read().displayName, 'Unsaved name');
  assert.equal(app.requests.length, 0);
});

test('channel Manage only discloses guidance without changing settings, saving or making requests', () => {
  const app = runtime();
  app.settings.render({ displayName: 'Unsaved name', emailEnabled: true, teamsEnabled: false });
  const before = JSON.stringify(app.settings.read());
  app.settings.toggleConnection('email');
  assert.equal(app.element('agentEmailDetails').hidden, false);
  assert.equal(app.element('agentEmailManage')['aria-expanded'], 'true');
  app.settings.toggleConnection('teams');
  assert.equal(app.element('agentEmailDetails').hidden, true);
  assert.equal(app.element('agentEmailManage')['aria-expanded'], 'false');
  assert.equal(app.element('agentTeamsDetails').hidden, false);
  assert.equal(app.element('agentTeamsManage')['aria-expanded'], 'true');
  app.settings.toggleConnection('teams');
  assert.equal(app.element('agentTeamsDetails').hidden, true);
  assert.equal(app.element('agentTeamsManage')['aria-expanded'], 'false');
  assert.throws(() => app.settings.toggleConnection('__proto__'), { name: 'RangeError' });
  assert.equal(JSON.stringify(app.settings.read()), before);
  assert.equal(app.requests.length, 0);
});

test('rendering another workspace resets disclosed channel guidance and all profile summaries', () => {
  const app = runtime();
  app.settings.render({ displayName: 'First', instructions: 'First guidance', emailEnabled: true });
  app.settings.toggleConnection('email');
  app.settings.render({ displayName: 'Second', teamsEnabled: true });
  assert.equal(app.element('agentNameSummary').textContent, 'Second');
  assert.equal(app.element('agentInstructionsSummary').textContent, 'Optional instructions for your assistant');
  assert.equal(app.settings.read().emailEnabled, false);
  assert.equal(app.settings.read().teamsEnabled, true);
  for (const channel of ['Email', 'Teams']) {
    assert.equal(app.element(`agent${channel}Details`).hidden, true);
    assert.equal(app.element(`agent${channel}Manage`)['aria-expanded'], 'false');
  }
});

test('profile Manage disclosures remain native, exclusive and closed initially', () => {
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'admin.html'), 'utf8');
  const rows = [...html.matchAll(/<details class="agent-setting-row"[^>]*>[\s\S]*?<\/details>/g)];
  assert.equal(rows.length, 4);
  for (const [row] of rows) {
    assert.match(row.split('>')[0], /name="agent-profile-settings"/);
    assert.doesNotMatch(row.split('>')[0], /\bopen\b/);
    assert.match(row, /<summary>[\s\S]*?class="agent-manage-action">Manage<\/span><\/summary>/);
    assert.match(row, /class="agent-profile-field"/);
  }
  assert.match(html, /<details class="agent-style-disclosure">\s*<summary>Preview Assistant Style/);
  for (const channel of ['Email', 'Teams']) {
    assert.match(html, new RegExp(`id="agent${channel}Manage"[\\s\\S]*?aria-controls="agent${channel}Details"`));
    assert.match(html, new RegExp(`id="agent${channel}Details" class="agent-channel-details" hidden`));
  }
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

test('users tab reserves header geometry while removing harness actions from focus and preserving unsaved drafts', () => {
  const app = runtime();
  app.settings.render({ displayName: 'Unsaved agent name', instructions: 'Unsaved instructions' });
  app.element('harnessSaveNotice').hidden = false;
  app.element('btnSaveHarness').disabled = false;
  app.settings.section('users');
  assert.equal(app.element('tenantUsersPanel').hidden, false);
  assert.equal(app.element('agentSettingsPanel').hidden, true);
  assert.equal(app.element('processingSettingsPanel').hidden, true);
  for (const id of ['harnessHeaderActions', 'harnessVersionChip']) {
    assert.equal(app.element(id).hidden, false, 'Header space must not collapse');
    assert.equal(app.element(id).classList.contains('settings-context-hidden'), true);
    assert.equal(app.element(id).inert, true);
    assert.equal(app.element(id)['aria-hidden'], 'true');
  }
  assert.equal(app.element('tenantUsersTab')['aria-selected'], 'true');
  assert.equal(app.element('tenantUsersTab').tabindex, '0');
  app.settings.section('agent');
  assert.equal(app.element('harnessHeaderActions').hidden, false);
  for (const id of ['harnessHeaderActions', 'harnessVersionChip']) {
    assert.equal(app.element(id).classList.contains('settings-context-hidden'), false);
    assert.equal(app.element(id).inert, false);
    assert.equal(app.element(id)['aria-hidden'], 'false');
  }
  assert.equal(app.element('harnessSaveNotice').hidden, false);
  assert.equal(app.element('btnSaveHarness').disabled, false);
  assert.equal(app.settings.read().displayName, 'Unsaved agent name');
  assert.equal(app.settings.read().instructions, 'Unsaved instructions');
});

test('section tabs preserve keyboard selection, focus and labels with decorative icons', () => {
  const app = runtime();
  let prevented = 0;
  for (const [target, key, expected] of [
    ['processingSettingsTab', 'ArrowRight', 'agentSettingsTab'],
    ['agentSettingsTab', 'End', 'tenantUsersTab'],
    ['tenantUsersTab', 'ArrowRight', 'processingSettingsTab'],
    ['processingSettingsTab', 'ArrowLeft', 'tenantUsersTab'],
    ['tenantUsersTab', 'Home', 'processingSettingsTab']
  ]) {
    app.keydown({ target: app.element(target), key, preventDefault() { prevented++; } });
    assert.equal(app.focused, expected);
    assert.equal(app.element(expected)['aria-selected'], 'true');
    assert.equal(app.element(expected).tabindex, '0');
  }
  assert.equal(prevented, 5);
  const html = fs.readFileSync(path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot', 'admin.html'), 'utf8');
  assert.equal([...html.matchAll(/class="admin-section-icon" aria-hidden="true"/g)].length, 3);
  assert.ok(html.indexOf('id="adminSections"') < html.indexOf('id="harnessSaveNotice"'),
    'A section-specific conflict must not move the navigation');
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
