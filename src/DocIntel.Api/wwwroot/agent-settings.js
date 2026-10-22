(function (root) {
  'use strict';
  const defaults = Object.freeze({
    displayName: 'DocIntel', purpose: 'Help people find clear, evidence-backed answers in their documents.',
    tone: 'Professional', instructions: '', emailEnabled: false, teamsEnabled: false
  });
  const node = id => document.getElementById(id);
  let revision = 0;

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
  async function connections(tenant) {
    const current = ++revision;
    for (const id of ['agentEmailStatus', 'agentTeamsStatus']) node(id).textContent = 'Checking secure connection...';
    try {
      const response = await fetch(`/api/harness/tenants/${encodeURIComponent(tenant)}/agent/channels`);
      if (!response.ok) throw new Error('Connection state unavailable');
      const result = await response.json();
      if (current !== revision) return;
      node('agentEmailStatus').textContent = result.email.configured
        ? `Mailbox configured: ${result.email.address} - live delivery must be verified`
        : 'Not connected - mailbox setup required';
      node('agentTeamsStatus').textContent = result.teams.configured
        ? 'Bot connection configured - installation and live delivery must be verified'
        : 'Not connected - Teams bot setup required';
    } catch {
      if (current !== revision) return;
      for (const id of ['agentEmailStatus', 'agentTeamsStatus'])
        node(id).textContent = 'Connection status unavailable. No connection was changed.';
    }
  }
  function section(name) {
    if (!['processing', 'agent', 'users'].includes(name)) return;
    node('agentSettingsPanel').hidden = name !== 'agent';
    node('processingSettingsPanel').hidden = name !== 'processing';
    node('tenantUsersPanel').hidden = name !== 'users';
    node('harnessHeaderActions').hidden = name === 'users';
    node('harnessVersionChip').hidden = name === 'users';
    node('harnessSaveNotice').classList.toggle('tenant-users-hidden', name === 'users');
    if (name !== 'users') root.DocIntelTenantUsers?.closeDialogs();
    for (const [id, selected] of [['processingSettingsTab', name === 'processing'],
      ['agentSettingsTab', name === 'agent'], ['tenantUsersTab', name === 'users']]) {
      node(id).classList.toggle('selected', selected);
      node(id).setAttribute('aria-selected', String(selected));
      node(id).setAttribute('tabindex', selected ? '0' : '-1');
    }
  }
  node('adminSections')?.addEventListener?.('keydown', event => {
    const tabs = ['processingSettingsTab', 'agentSettingsTab', 'tenantUsersTab'];
    const index = tabs.indexOf(event.target.id);
    if (index < 0 || !['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    event.preventDefault();
    const next = event.key === 'Home' ? 0 : event.key === 'End' ? 2
      : (index + (event.key === 'ArrowRight' ? 1 : 2)) % 3;
    section(['processing', 'agent', 'users'][next]);
    node(tabs[next]).focus();
  });
  root.DocIntelAgentSettings = Object.freeze({ read, render, preview, connections, section });
})(globalThis);
