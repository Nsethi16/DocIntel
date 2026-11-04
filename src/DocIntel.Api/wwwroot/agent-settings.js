(function (root) {
  'use strict';
  const defaults = Object.freeze({
    displayName: 'DocIntel', purpose: 'Help people find clear, evidence-backed answers in their documents.',
    tone: 'Professional', instructions: '', emailEnabled: false, emailConfirmationRequired: false, teamsEnabled: false
  });
  const node = id => document.getElementById(id);
  const channelControls = {
    email: ['agentEmailDetails', 'agentEmailManage'],
    teams: ['agentTeamsDetails', 'agentTeamsManage']
  };
  let revision = 0;
  let emailTenant = null;
  let emailBusy = false;
  let emailOperation = 0;

  function read() {
    return {
      displayName: node('agentDisplayName').value,
      purpose: node('agentPurpose').value,
      tone: node('agentTone').value,
      instructions: node('agentInstructions').value,
      emailEnabled: node('agentEmailEnabled').checked,
      emailConfirmationRequired: node('agentEmailConfirmationRequired').checked,
      teamsEnabled: node('agentTeamsEnabled').checked
    };
  }
  function preview() {
    const profile = read();
    node('agentPreviewName').textContent = profile.displayName.trim() || defaults.displayName;
    node('agentPreviewAvatar').textContent = (profile.displayName.trim() || defaults.displayName)[0].toUpperCase();
    node('agentPreviewPurpose').textContent = profile.purpose;
    node('agentNameSummary').textContent = profile.displayName.trim() || defaults.displayName;
    node('agentToneSummary').textContent = profile.tone;
    node('agentPurposeSummary').textContent = profile.purpose.trim() || 'Describe its purpose and audience';
    node('agentInstructionsSummary').textContent = profile.instructions.trim()
      ? 'Custom response guidance is set' : 'Optional instructions for your assistant';
    node('agentPreviewMessage').textContent = profile.tone === 'Friendly'
      ? "Happy to help. I'll look through the documents you can access, explain what I find and include the sources."
      : profile.tone === 'Concise'
        ? "I'll find the relevant agreement, lead with the answer and cite the source. If evidence is missing, I'll say so."
        : "I'll identify the relevant agreement from your authorized documents and provide an evidence-backed answer with citations.";
  }
  function render(profile) {
    revision++;
    emailOperation++;
    emailBusy = false;
    emailTenant = null;
    if (node('agentPersonalEmailControls')) node('agentPersonalEmailControls').hidden = true;
    for (const id of ['agentEmailConnect', 'agentEmailDisconnect']) if (node(id)) node(id).disabled = true;
    if (node('agentEmailConnectionNotice')) node('agentEmailConnectionNotice').textContent = '';
    profile = { ...defaults, ...profile };
    for (const [key, id] of [['displayName', 'agentDisplayName'], ['purpose', 'agentPurpose'],
      ['tone', 'agentTone'], ['instructions', 'agentInstructions']]) node(id).value = profile[key];
    node('agentEmailEnabled').checked = profile.emailEnabled;
    node('agentEmailConfirmationRequired').checked = profile.emailConfirmationRequired;
    node('agentTeamsEnabled').checked = profile.teamsEnabled;
    for (const [panel, button] of Object.values(channelControls)) {
      node(panel).hidden = true;
      node(button).setAttribute('aria-expanded', 'false');
    }
    preview();
  }
  async function connections(tenant) {
    const current = ++revision;
    if (emailTenant && emailTenant !== tenant) {
      emailOperation++;
      emailBusy = false;
    }
    emailTenant = null;
    if (node('agentPersonalEmailControls')) node('agentPersonalEmailControls').hidden = true;
    for (const id of ['agentEmailConnect', 'agentEmailDisconnect']) if (node(id)) node(id).disabled = true;
    for (const id of ['agentEmailStatus', 'agentTeamsStatus']) node(id).textContent = 'Checking secure connection...';
    try {
      const response = await fetch(`/api/harness/tenants/${encodeURIComponent(tenant)}/agent/channels`);
      if (!response.ok) throw new Error('Connection state unavailable');
      const result = await response.json();
      if (current !== revision) return;
      const personal = result.email.provider === 'OutlookCom' && result.email.personalAvailable === true;
      node('agentEmailPolicy').textContent = result.email.authorizationMode === 'ApprovedUsers'
        ? `ApprovedUsers is enabled by the operator. Limits: ${result.email.automaticQuestionsPerSenderHour} ` +
          `automatic questions per user and ${result.email.automaticQuestionsPerWorkspaceHour} per workspace per UTC hour.`
        : result.email.authorizationMode === 'TrustedInternal'
          ? 'Trusted corporate transport authentication is required. The per-question confirmation setting does not bypass it.'
          : 'Per-question confirmation is required by the operator. Enable ApprovedUsers mailbox mode to allow automatic replies.';
      node('agentEmailStatus').textContent = personal
        ? result.email.connected
          ? `Outlook.com connected: ${result.email.address} - live delivery must be verified`
          : result.email.state === 'ReconnectionRequired'
            ? 'Microsoft consent needs renewal - reconnect Outlook.com'
            : `Outlook.com is not connected: ${result.email.address}`
        : result.email.configured
        ? `Mailbox configured: ${result.email.address} - live delivery must be verified`
        : 'Not connected - mailbox setup required';
      if (personal) {
        emailTenant = tenant;
        node('agentPersonalEmailControls').hidden = false;
        node('agentEmailConnect').disabled = emailBusy;
        node('agentEmailConnect').textContent = result.email.connected ? 'Reconnect Outlook.com' : 'Connect Outlook.com';
        node('agentEmailDisconnect').disabled = emailBusy || result.email.state === 'NotConnected';
      }
      node('agentTeamsStatus').textContent = result.teams.configured
        ? 'Bot connection configured - installation and live delivery must be verified'
        : 'Not connected - Teams bot setup required';
    } catch {
      if (current !== revision) return;
      for (const id of ['agentEmailStatus', 'agentTeamsStatus'])
        node(id).textContent = 'Connection status unavailable. No connection was changed.';
    }
  }
  async function personalEmail(action) {
    if (!emailTenant || emailBusy) return;
    const tenant = emailTenant, current = revision, operation = ++emailOperation;
    emailBusy = true;
    node('agentEmailConnect').disabled = true;
    node('agentEmailDisconnect').disabled = true;
    node('agentEmailConnectionNotice').textContent = action === 'connect'
      ? 'Preparing Microsoft account consent...' : 'Disconnecting the mailbox...';
    try {
      const response = await fetch(`/api/harness/tenants/${encodeURIComponent(tenant)}/email/personal/mailbox/${action}`,
        { method: 'POST' });
      if (!response.ok) throw new Error('Mailbox connection change was not acknowledged. Refresh before trying again.');
      const result = await response.json();
      if (current !== revision || tenant !== emailTenant) return;
      if (action === 'connect') {
        if (typeof result.redirectUrl !== 'string' ||
          !/^\/auth\/email\/personal\/start\?request=[a-fA-F0-9]{64}$/.test(result.redirectUrl))
          throw new Error('The Microsoft connection link was invalid. No sign-in was started.');
        root.location.assign(result.redirectUrl);
      } else {
        node('agentEmailConnectionNotice').textContent = 'Disconnected. This app will no longer use the stored mailbox credentials.';
      }
    } catch (error) {
      if (current === revision)
        node('agentEmailConnectionNotice').textContent = error.message + ' No automatic retry was made.';
    } finally {
      if (operation === emailOperation) {
        emailBusy = false;
        if (emailTenant === tenant) await connections(tenant);
      }
    }
  }
  node('agentEmailConnect')?.addEventListener?.('click', () => personalEmail('connect'));
  node('agentEmailDisconnect')?.addEventListener?.('click', () => personalEmail('disconnect'));
  function toggleConnection(name) {
    if (!Object.hasOwn(channelControls, name)) throw new RangeError('Unsupported channel settings.');
    const opening = node(channelControls[name][0]).hidden;
    for (const [channel, [panel, button]] of Object.entries(channelControls)) {
      const expanded = channel === name && opening;
      node(panel).hidden = !expanded;
      node(button).setAttribute('aria-expanded', String(expanded));
    }
  }
  function section(name) {
    if (!['processing', 'agent', 'users'].includes(name)) return;
    node('agentSettingsPanel').hidden = name !== 'agent';
    node('processingSettingsPanel').hidden = name !== 'processing';
    node('tenantUsersPanel').hidden = name !== 'users';
    for (const id of ['harnessHeaderActions', 'harnessVersionChip']) {
      node(id).classList.toggle('settings-context-hidden', name === 'users');
      node(id).inert = name === 'users';
      node(id).setAttribute('aria-hidden', String(name === 'users'));
    }
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
  root.DocIntelAgentSettings = Object.freeze({ read, render, preview, connections, toggleConnection, section, personalEmail });
})(globalThis);
