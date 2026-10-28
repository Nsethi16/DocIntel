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
    button.disabled = true;
    element('emailRunLink').hidden = true;
    if (!valid) { element('emailNotice').textContent = 'This confirmation link is incomplete.'; return; }
    try {
      const response = await fetch(endpoint);
      if (!response.ok) throw new Error('This question is expired, disabled or unavailable for your signed-in email account.');
      const result = await response.json();
      element('emailQuestion').textContent = result.question;
      element('emailDestination').textContent = `Requested reply address: ${result.destination || ''}. Delivery is not yet confirmed.`;
      if (result.status === 'AwaitingConfirmation') {
        element('emailNotice').textContent = 'Your account matches the original sender. Review the question before confirming.';
        button.disabled = false;
      } else if (['Queued', 'Running', 'Completed', 'Failed'].includes(result.status)) {
        element('emailNotice').textContent = 'This question was already confirmed. Do not submit it again.';
        statusLink();
      } else {
        element('emailNotice').textContent = 'This question cannot be confirmed. It may have expired or become unavailable.';
      }
    } catch {
      element('emailNotice').textContent = 'This question is expired, disabled or unavailable for your signed-in email account. ' +
        'Check your connection or return to the original confirmation link.';
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
      element('emailNotice').textContent = 'Confirmed. Your question is queued. DocIntel will attempt to email the answer; ' +
        'delivery is not yet confirmed. View question status to check progress.';
      statusLink();
    } catch {
      element('emailNotice').textContent = 'Confirmation was not acknowledged. Reload this page to check the same question ' +
        'before trying again. No automatic retry was made.';
    }
  });
  globalThis.DocIntelUX?.registerCommands([{
    id: 'email.question-status',
    title: 'View this email question status',
    keywords: 'answer delivery',
    enabled: () => !element('emailRunLink').hidden,
    run: () => location.assign(statusUrl)
  }]);
  load();
})();
