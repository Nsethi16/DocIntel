(() => {
  'use strict';
  const parameters = new URLSearchParams(location.search);
  const tenant = parameters.get('tenant'), run = parameters.get('run');
  const element = id => document.getElementById(id);
  const button = element('confirmEmailQuestion');
  const valid = tenant && run && tenant.length <= 128 && run.length <= 512;
  const prefix = `/api/harness/tenants/${encodeURIComponent(tenant || '')}`;
  const endpoint = `${prefix}/ask/email/runs/${encodeURIComponent(run || '')}`;
  const statusUrl = `/ask-run.html?tenant=${encodeURIComponent(tenant || '')}&run=${encodeURIComponent(run || '')}`;
  const identityEndpoint = `${prefix}/email/personal/sender`;
  let identityAvailable = false, identityBusy = false;
  async function loadIdentity() {
    try {
      const response = await fetch(identityEndpoint);
      if (response.status === 404) return;
      if (!response.ok) throw new Error('Personal email verification status is unavailable.');
      const result = await response.json();
      identityAvailable = result.available === true;
      element('emailPersonalIdentity').hidden = !identityAvailable;
      element('verifyPersonalEmail').disabled = !identityAvailable || identityBusy;
      element('disconnectPersonalEmail').disabled = !identityAvailable || result.state === 'NotConnected' || identityBusy;
      element('emailPersonalIdentityNotice').textContent = result.connected
        ? `Verified personal sender: ${result.address}`
        : result.state === 'ReconnectionRequired' ? 'Reconnect your personal Microsoft account to verify your sender address.'
          : 'No personal sender email is linked. Verify it on the Microsoft sign-in page if needed.';
    } catch (error) {
      identityAvailable = false;
      element('verifyPersonalEmail').disabled = true;
      element('disconnectPersonalEmail').disabled = true;
      element('emailPersonalIdentityNotice').textContent = error.message + ' No email identity was changed.';
    }
  }
  async function personalIdentity(action) {
    if (!valid || !identityAvailable || identityBusy) return;
    identityBusy = true;
    element('verifyPersonalEmail').disabled = true;
    element('disconnectPersonalEmail').disabled = true;
    let notice;
    try {
      const returning = /^mail-[a-fA-F0-9]{64}$/.test(run)
        ? `?returnRunId=${encodeURIComponent(run)}` : '';
      const response = await fetch(identityEndpoint + '/' + action + (action === 'connect' ? returning : ''),
        { method: 'POST' });
      if (!response.ok) throw new Error('Personal email verification change was not acknowledged.');
      const result = await response.json();
      if (action === 'connect') {
        if (typeof result.redirectUrl !== 'string' ||
          !/^\/auth\/email\/personal\/start\?request=[a-fA-F0-9]{64}$/.test(result.redirectUrl))
          throw new Error('The Microsoft verification link was invalid. No sign-in was started.');
        location.assign(result.redirectUrl);
      } else {
        notice = 'Personal sender credentials were disconnected.';
        button.disabled = true;
        element('emailQuestion').textContent = '';
        element('emailDestination').textContent = '';
      }
    } catch (error) {
      notice = error.message + ' No automatic retry was made.';
    } finally {
      identityBusy = false;
      await loadIdentity();
      if (notice) element('emailPersonalIdentityNotice').textContent = notice;
    }
  }
  function statusLink() {
    element('emailRunLink').href = statusUrl;
    element('emailRunLink').hidden = false;
  }
  async function load() {
    button.disabled = true;
    if (!valid) { element('emailNotice').textContent = 'This confirmation link is incomplete.'; return; }
    try {
      const response = await fetch(endpoint);
      if (!response.ok) throw new Error('This question is expired, disabled or unavailable for your signed-in email account.');
      const result = await response.json();
      element('emailQuestion').textContent = result.question;
      element('emailDestination').textContent = `The answer will be sent to ${result.destination}.`;
      if (result.status === 'AwaitingConfirmation') {
        element('emailNotice').textContent = 'Your account matches the original sender. Review the question before confirming.';
        button.disabled = false;
      } else {
        element('emailNotice').textContent = 'This question was already confirmed. Do not submit it again.';
        statusLink();
      }
    } catch (error) {
      element('emailNotice').textContent = error.message;
      element('emailQuestion').textContent = '';
      element('emailDestination').textContent = '';
    }
    await loadIdentity();
  }
  button.addEventListener('click', async () => {
    if (button.disabled) return;
    button.disabled = true;
    try {
      const response = await fetch(endpoint + '/confirm', { method: 'POST' });
      if (!response.ok) throw new Error('Confirmation was not acknowledged. Reload this page to check the same question before trying again.');
      element('emailNotice').textContent = 'Confirmed. Your question is queued and the agent will email its answer.';
      statusLink();
    } catch (error) {
      element('emailNotice').textContent = error.message + ' No automatic retry was made.';
    }
  });
  element('verifyPersonalEmail').addEventListener('click', () => personalIdentity('connect'));
  element('disconnectPersonalEmail').addEventListener('click', () => personalIdentity('disconnect'));
  load();
})();
