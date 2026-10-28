(() => {
  'use strict';
  const params = new URLSearchParams(location.search);
  const tenant = params.get('tenant');
  const run = params.get('run');
  const status = document.getElementById('status');
  const refresh = document.getElementById('refresh');
  let timer;
  let loading = false;
  const deadline = Date.now() + 60 * 60 * 1000;

  async function load() {
    if (loading) return;
    clearTimeout(timer);
    if (!tenant || !run || tenant.length > 128 || run.length > 512) {
      status.textContent = 'This question link is incomplete or invalid.';
      refresh.disabled = true;
      return;
    }
    loading = true;
    refresh.disabled = true;
    try {
      const prefix = `/api/harness/tenants/${encodeURIComponent(tenant)}`;
      const response = await fetch(`${prefix}/ask/runs/${encodeURIComponent(run)}`);
      if (!response.ok) throw new Error(response.status === 404 ? 'This question is unavailable.' :
        response.status === 403 ? 'Your account cannot access this question.' : 'The question could not be refreshed.');
      const result = await response.json();
      document.getElementById('workspace').href = `/?tenant=${encodeURIComponent(tenant)}`;
      document.getElementById('question').textContent = result.question || '';
      document.getElementById('answer').textContent = result.status === 'Completed' ? result.answer || '' : '';
      status.textContent = result.status === 'Failed' ? result.errorMessage || 'This question could not be completed.' :
        result.status === 'Completed' ? 'Answer complete.' : 'DocIntel is processing your question. You can return to this link later.';
      const diagnostics = document.getElementById('diagnostics');
      diagnostics.replaceChildren();
      diagnostics.hidden = result.status !== 'Failed';
      if (result.status === 'Failed') {
        const created = new Date(result.createdAtUtc);
        const provider = result.providerFailure;
        const entries = [
          ['Run Reference', result.runId || run],
          ['Failure Code', result.errorCode || 'Unavailable for this older run'],
          ['Submitted', Number.isNaN(created.getTime()) ? 'Unavailable' : created.toLocaleString()],
          ['Provider HTTP Status', provider?.httpStatus],
          ['Provider Code', provider?.code],
          ['Provider Parameter', provider?.parameter],
          ['Schema Keyword', provider?.schemaKeyword]
        ];
        for (const [label, value] of entries) {
          if (value === undefined || value === null) continue;
          const term = document.createElement('dt');
          const detail = document.createElement('dd');
          term.textContent = label;
          detail.textContent = String(value);
          diagnostics.appendChild(term);
          diagnostics.appendChild(detail);
        }
      }
      const delivery = result.delivery;
      document.getElementById('delivery').textContent = !delivery ? '' :
        delivery.status === 'Sent' ? 'The messaging service acknowledged the answer; this does not confirm receipt.' :
        delivery.status === 'OutcomeUnknown' ? 'Message delivery is uncertain. DocIntel will not automatically resend it. The answer is available here.' :
        delivery.status === 'Failed' ? `Message delivery stopped (${delivery.errorCode || 'DeliveryFailed'}).` :
        'The answer will be delivered to the original conversation after processing.';
      const citations = document.getElementById('citations');
      citations.replaceChildren();
      const citationLinks = new Map();
      for (const citation of result.citations || []) {
        const item = document.createElement('li');
        const link = document.createElement('a');
        link.textContent = `${citation.fileName || 'Document'} | ${citation.documentName || 'Source'} | ${citation.pageRange || ''}`;
        if (typeof citation.jobId === 'string' && Number.isSafeInteger(citation.sliceIndex) && citation.sliceIndex >= 0) {
          link.href = citation.evidenceKind && citation.evidenceKind !== 'OriginalSourceText'
            ? `/?tenant=${encodeURIComponent(tenant)}`
            : `${prefix}/jobs/${encodeURIComponent(citation.jobId)}/slices/${citation.sliceIndex}/pdf`;
        }
        item.appendChild(link);
        citationLinks.set(citation.citationNumber, link);
        if (Number.isSafeInteger(citation.reviewVersion)) {
          const origin = document.createElement('p');
          origin.textContent = `Historical review version ${citation.reviewVersion}; this workflow is retired. Ask a new question.`;
          item.appendChild(origin);
        }
        if (citation.excerpt) {
          const details = document.createElement('details');
          const summary = document.createElement('summary');
          const excerpt = document.createElement('p');
          summary.textContent = 'Show cited evidence';
          excerpt.textContent = citation.excerpt;
          details.appendChild(summary);
          details.appendChild(excerpt);
          item.appendChild(details);
        }
        citations.appendChild(item);
      }
      globalThis.DocIntelAnalyses?.render(document.getElementById('analyses'),
        result.status === 'Completed' ? result.analyses : [], result.citations, {
          onCitation: citation => citationLinks.get(citation.citationNumber)?.click()
        });
      const pending = ['Queued', 'Running'].includes(result.status) ||
        (result.status === 'Completed' && ['Pending', 'Sending'].includes(delivery?.status));
      if (pending && Date.now() < deadline) timer = setTimeout(load, 5000);
      else if (pending) status.textContent += ' Automatic checking has paused; refresh to check the same question.';
    } catch (error) {
      document.getElementById('question').textContent = '';
      document.getElementById('delivery').textContent = '';
      document.getElementById('answer').textContent = '';
      document.getElementById('citations').replaceChildren();
      document.getElementById('analyses').replaceChildren();
      document.getElementById('diagnostics').replaceChildren();
      document.getElementById('diagnostics').hidden = true;
      status.textContent = `${error.message} Use Refresh status to check this question again; no new question will be submitted.`;
    } finally {
      loading = false;
      refresh.disabled = false;
    }
  }
  refresh.addEventListener('click', load);
  load();
})();
