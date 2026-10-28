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
  function statusLink() {
    element('emailRunLink').href = statusUrl;
    element('emailRunLink').hidden = false;
  }
  async function load() {
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
  load();
})();
