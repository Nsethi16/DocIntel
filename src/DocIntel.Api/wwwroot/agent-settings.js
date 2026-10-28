(function (root) {
  'use strict';
  const defaults = Object.freeze({
    displayName: 'DocIntel', purpose: 'Help people find clear, evidence-backed answers in their documents.',
    tone: 'Professional', instructions: '', emailEnabled: false, teamsEnabled: false
  });
  const node = id => document.getElementById(id);
  let revision = 0;
  let setup = null;
  let setupTenant = null;
  const sections = [
    ['setup', 'setupSettingsTab', 'setupSettingsPanel'],
    ['processing', 'processingSettingsTab', 'processingSettingsPanel'],
    ['analysis', 'customAnalysisTab', 'customAnalysisPanel'],
    ['agent', 'agentSettingsTab', 'agentSettingsPanel'],
    ['integrations', 'integrationsSettingsTab', 'integrationsSettingsPanel'],
    ['users', 'tenantUsersTab', 'tenantUsersPanel']
  ];

  function read() {
    return {
      displayName: node('agentDisplayName').value,
      purpose: node('agentPurpose').value,
      tone: node('agentTone').value,
      instructions: node('agentInstructions').value,
      emailEnabled: node('agentEmailEnabled').checked,
      teamsEnabled: node('agentTeamsEnabled').checked
    };
  }
  function preview() {
    const profile = read();
    node('agentPreviewName').textContent = profile.displayName.trim() || defaults.displayName;
    node('agentPreviewAvatar').textContent = (profile.displayName.trim() || defaults.displayName)[0].toUpperCase();
    node('agentPreviewPurpose').textContent = profile.purpose;
    node('agentPreviewMessage').textContent = profile.tone === 'Friendly'
      ? "Happy to help. I'll look through the documents you can access, explain what I find and include the sources."
      : profile.tone === 'Concise'
        ? "I'll find the relevant agreement, lead with the answer and cite the source. If evidence is missing, I'll say so."
        : "I'll identify the relevant agreement from your authorized documents and provide an evidence-backed answer with citations.";
  }
  function render(profile) {
    profile = { ...defaults, ...profile };
    for (const [key, id] of [['displayName', 'agentDisplayName'], ['purpose', 'agentPurpose'],
      ['tone', 'agentTone'], ['instructions', 'agentInstructions']]) node(id).value = profile[key];
    node('agentEmailEnabled').checked = profile.emailEnabled;
    node('agentTeamsEnabled').checked = profile.teamsEnabled;
    preview();
  }
  function modelStatus(model) {
    if (!model || model.state === 'UnknownSelection') return 'Configuration unknown; deployment not verified';
    if (model.development) return 'Development fallback; production connection not configured';
    return model.configured ? 'Connection configured; deployment not verified'
      : 'Connection not configured; operator setup required';
  }
  function models() {
    for (const [purpose, selectId, statusId] of [
      ['decision', 'harnessDecisionModelSelect', 'decisionModelStatus'],
      ['language', 'harnessLanguageModelSelect', 'languageModelStatus']
    ]) {
      const select = node(selectId);
      const selected = select.value;
      const catalog = (setup?.models || []).filter(model => model.purpose === purpose);
      for (const model of catalog) {
        if (![...select.options].some(option => option.value === model.modelId)) {
          const option = document.createElement('option');
          option.value = model.modelId;
          option.textContent = model.displayName || model.modelId;
          select.appendChild(option);
        }
      }
      for (const option of select.options) {
        const model = catalog.find(item => item.modelId === option.value);
        option.dataset.displayName ||= option.textContent;
        const label = modelStatus(model);
        option.textContent = `${model?.displayName || option.dataset.displayName} — ${label}`;
        option.disabled = Boolean(model && model.configured === false && model.state !== 'UnknownSelection');
        option.title = label;
      }
      // The setup response must never select a different route or overwrite an in-progress edit.
      select.value = selected;
      const model = catalog.find(item => item.modelId === selected);
      node(statusId).textContent = `${modelStatus(model)}.`;
      node(purpose + 'ModelDetails').textContent = `${purpose === 'decision' ? 'Decision' : 'Language'} model: ` +
        (model?.supportsPdfInputs === true ? 'PDF inputs declared; not tested here. '
          : model ? 'PDF input support is not confirmed here. ' : 'Keep this selection or ask your operator to review it. ') +
        'Ask your operator to verify the selected route before relying on it.';
    }
  }
  function steps(id, values) {
    const list = node(id);
    list.replaceChildren();
    for (const value of values || []) {
      const item = document.createElement('li');
      item.textContent = value;
      list.appendChild(item);
    }
  }
  function renderConnections() {
    for (const [key, prefix] of [['email', 'agentEmail'], ['teams', 'agentTeams']]) {
      const channel = setup?.channels?.[key];
      node(prefix + 'Status').textContent = !channel
        ? 'Connection status unavailable. No connection was changed. Refresh status or contact your operator.'
        : channel.configured
          ? `Connection configured${key === 'email' && channel.address ? `: ${channel.address}` : ''}; ` +
            `delivery not verified. ${channel.enabled ? 'Saved workspace preference enabled.' : 'Saved workspace preference disabled.'}`
          : 'Not connected — secure operator setup required. Delivery not verified.';
      steps(prefix + 'Steps', channel?.setupSteps);
    }
  }
  function renderChecks() {
    const list = node('workspaceSetupChecks');
    list.replaceChildren();
    for (const check of setup?.checks || []) {
      const item = document.createElement('li');
      const body = document.createElement('div');
      const title = document.createElement('h3');
      title.textContent = check.title;
      const state = document.createElement('span');
      state.className = 'setup-check-state';
      state.textContent = check.state === 'Configured' ? 'Configured · not live-verified'
        : check.state === 'ActionRequired' ? 'Action required' : 'Not started';
      const description = document.createElement('p');
      description.textContent = check.description;
      body.append(title, state, description);
      const action = document.createElement('button');
      action.type = 'button';
      action.className = 'btn-mini';
      const target = check.id === 'people' ? 'users' : check.id === 'channels' ? 'integrations' : 'processing';
      action.textContent = check.id === 'data' ? 'Try a PDF ↗' : `Review ${target === 'users' ? 'people' : target}`;
      action.dataset.click = check.id === 'data' ? 'openWorkspaceOnboarding' : 'setAdminSection_arg';
      if (check.id !== 'data') action.dataset.args = JSON.stringify([target]);
      item.append(body, action);
      list.appendChild(item);
    }
  }
  async function connections(tenant) {
    const current = ++revision;
    setupTenant = tenant;
    setup = null;
    renderChecks();
    renderConnections();
    models();
    node('workspaceSetupStatus').textContent = tenant
      ? 'Checking workspace configuration…' : 'Choose a workspace to review setup.';
    if (!tenant) return;
    try {
      const response = await fetch(`/api/harness/tenants/${encodeURIComponent(tenant)}/setup`);
      if (!response.ok) throw new Error('Setup status unavailable');
      const result = await response.json();
      if (current !== revision || setupTenant !== tenant) return;
      if (result.workspace?.tenantId !== tenant || !Array.isArray(result.models) ||
          !Array.isArray(result.checks) || !result.channels) throw new Error('Incomplete setup status');
      setup = result;
      node('workspaceSetupStatus').textContent =
        'Configuration checks only. Model deployments and message delivery have not been verified.';
      renderChecks();
      renderConnections();
      models();
    } catch {
      if (current !== revision) return;
      node('workspaceSetupStatus').textContent =
        'Setup status unavailable. Configuration is unknown, not assumed complete. Refresh or contact your operator.';
      renderConnections();
      models();
    }
  }
  function section(name) {
    if (!sections.some(([key]) => key === name)) return;
    for (const [key, tabId, panelId] of sections) {
      node(panelId).hidden = name !== key;
      node(panelId).inert = name !== key;
      node(tabId).classList.toggle('selected', name === key);
      node(tabId).setAttribute('aria-selected', String(name === key));
      node(tabId).setAttribute('tabindex', name === key ? '0' : '-1');
    }
    for (const id of ['harnessHeaderActions', 'harnessVersionChip']) {
      node(id).classList.toggle('settings-context-hidden', name === 'users');
      node(id).inert = name === 'users';
      node(id).setAttribute('aria-hidden', String(name === 'users'));
    }
    node('harnessSaveNotice').classList.toggle('tenant-users-hidden', name === 'users');
    if (name !== 'users') root.DocIntelTenantUsers?.closeDialogs();
  }
  node('adminSections')?.addEventListener?.('keydown', event => {
    const tabs = sections.map(([, tabId]) => tabId);
    const index = tabs.indexOf(event.target.id);
    if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
      : (index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length;
    section(sections[next][0]);
    node(tabs[next]).focus();
  });
  root.DocIntelAgentSettings = Object.freeze({ read, render, preview, connections, section, models });
})(globalThis);
