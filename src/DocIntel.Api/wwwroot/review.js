(function () {
  'use strict';
  const el = id => document.getElementById(id);
  const state = { tenant: null, job: null, session: null, tag: null, canReview: false,
    claimed: false, busy: false, revision: 0, queue: [], offset: 0, next: null, field: 0,
    pdf: null, page: 1, render: 0, renderTask: null };
  const notice = message => { el('notice').textContent = message || ''; };
  const base = () => `/api/harness/tenants/${encodeURIComponent(state.tenant)}`;
  const reviewBase = () => `${base()}/jobs/${encodeURIComponent(state.job)}/review`;
  let pdfModule;

  function renderQueue() {
    el('queue').replaceChildren();
    for (const item of state.queue) {
      const button = document.createElement('button');
      button.textContent = `${item.fileName} - ${item.status}`;
      button.title = button.textContent;
      button.classList.toggle('selected', item.jobId === state.job);
      button.addEventListener('click', () => selectJob(item.jobId));
      el('queue').append(button);
    }
    el('more').hidden = state.next === null;
  }

  function controls() {
    const final = ['Approved', 'Rejected'].includes(state.session?.status);
    for (const id of ['claim', 'release', 'approve', 'correct', 'rejectField', 'submit', 'rejectDocument'])
      el(id).disabled = state.busy || !state.session || !state.canReview || final ||
        (id !== 'claim' && !state.claimed);
    if (state.session?.status === 'AwaitingSecondReview')
      for (const id of ['approve', 'correct', 'rejectField']) el(id).disabled = true;
    el('reload').disabled = state.busy || !state.job;
    el('audit').disabled = state.busy || !state.session;
    el('start').disabled = state.busy || !state.canReview;
    el('correctedValue').readOnly = state.busy || !state.canReview || !state.claimed || final;
    el('reason').readOnly = state.busy || !state.canReview || !state.claimed || final;
    el('documentReason').readOnly = state.busy || !state.canReview || !state.claimed || final;
    if (state.session?.version === 9999 && !final &&
        (!state.session.claimUntilUtc || new Date(state.session.claimUntilUtc).getTime() <= Date.now()) &&
        state.canReview && !state.busy) {
      el('rejectDocument').disabled = false;
      el('documentReason').readOnly = false;
    }
  }

  async function loadQueue(append = false) {
    const tenant = state.tenant;
    const revision = state.revision;
    const offset = append ? state.next : 0;
    try {
      const response = await DocIntelReview.read(`${base()}/reviews?limit=20&offset=${offset || 0}` +
        (el('status').value ? `&status=${encodeURIComponent(el('status').value)}` : ''));
      if (state.tenant !== tenant || state.revision !== revision) return;
      state.queue = append ? [...state.queue, ...response.body.items] : response.body.items;
      state.next = response.body.nextOffset;
      renderQueue();
    } catch (error) { if (state.tenant === tenant && state.revision === revision) notice(error.message); }
  }

  function renderFields() {
    const session = state.session;
    el('heading').textContent = state.job
      ? state.queue.find(item => item.jobId === state.job)?.fileName || `Document ${state.job}` : 'Select a document';
    el('reviewStatus').textContent = session
      ? `${session.status} - version ${session.version} - due ${new Date(session.dueAtUtc).toISOString()}` +
        (session.requiresDualReview ? ' - dual review' : '') +
        (session.hasDevelopmentActivity ? ' - DEVELOPMENT review activity' : '') : '';
    el('fields').replaceChildren();
    for (let index = 0; index < (session?.fields.length || 0); index++) {
      const field = session.fields[index];
      const button = document.createElement('button');
      button.textContent = `${field.label}: ${field.currentValue} - ${field.outcome}`;
      button.title = button.textContent;
      button.classList.toggle('selected', index === state.field);
      button.addEventListener('click', () => selectField(index));
      el('fields').append(button);
    }
    el('editor').hidden = !session?.fields.length;
    if (session?.fields.length) showField();
    else if (session) el('fieldEvidence').textContent = 'No typed fields. Review the original document before submission.';
    controls();
  }

  function showField() {
    const field = state.session?.fields[state.field];
    if (!field) return;
    el('fieldLabel').textContent = field.label;
    el('originalValue').value = field.originalValue;
    el('correctedValue').value = field.currentValue;
    el('reason').value = field.reason || '';
    const info = DocIntelEvidence.describe({ confidenceSignals: field.confidenceSignals,
      provenance: field.originalProvenance }, 1);
    el('fieldEvidence').textContent = info.tooltip;
    [...el('fields').children].forEach((button, index) => button.classList.toggle('selected', index === state.field));
    el('fields').children[state.field]?.scrollIntoView({ block: 'nearest' });
  }

  function hasDraft() {
    const field = state.session?.fields[state.field];
    return !!el('documentReason').value.trim() || !!field &&
      (el('correctedValue').value !== field.currentValue || el('reason').value !== (field.reason || ''));
  }
  function discardDraft() { return !hasDraft() || window.confirm('Discard this unsaved review draft?'); }
  function clearSource() {
    state.render++;
    state.renderTask?.cancel();
    const previous = state.pdf;
    state.pdf = null;
    state.page = 1;
    el('pdf').width = 1;
    el('pdf').height = 1;
    el('highlights').replaceChildren();
    el('original').hidden = true;
    el('previousPage').disabled = true;
    el('nextPage').disabled = true;
    el('pageLabel').textContent = 'Original source';
    el('pdfNotice').textContent = '';
    if (previous) previous.destroy().catch(() => notice('The previous PDF viewer could not be fully closed.'));
  }
  function selectField(index, seek = true) {
    if (state.busy) { notice('Wait for the review action to finish before changing fields.'); return; }
    if (!discardDraft()) return;
    const count = state.session?.fields.length || 0;
    if (!count) return;
    state.field = (index + count) % count;
    showField();
    const page = state.session.fields[state.field].originalProvenance?.boxes?.[0]?.page;
    if (seek && Number.isSafeInteger(page) && page > 0) state.page = page;
    renderPage();
  }

  async function loadReview() {
    const revision = ++state.revision;
    state.claimed = false;
    state.session = null;
    state.tag = null;
    el('start').hidden = true;
    el('auditText').hidden = true;
    controls();
    renderFields();
    try {
      const result = await DocIntelReview.read(reviewBase());
      if (revision !== state.revision) return;
      state.session = result.body;
      state.tag = result.tag;
      state.canReview = result.canReview;
      state.field = Math.min(state.field, Math.max(0, result.body.fields.length - 1));
      renderFields();
      await loadPdf(revision);
    } catch (error) {
      if (revision !== state.revision) return;
      state.canReview = error.canReview === true;
      el('start').hidden = error.status !== 404 || !state.canReview;
      renderFields();
      notice(error.status === 404 ? 'Review not started. An authorized reviewer can create a source-bound session.' : error.message);
    }
  }

  async function selectJob(job) {
    if (!discardDraft()) return;
    state.job = job;
    renderQueue();
    state.field = 0;
    el('originalValue').value = '';
    el('correctedValue').value = '';
    el('reason').value = '';
    el('documentReason').value = '';
    state.page = 1;
    clearSource();
    const original = `${base()}/jobs/${encodeURIComponent(job)}/original`;
    el('original').href = original;
    el('original').hidden = false;
    await loadReview();
  }

  async function act(action, command = {}) {
    if (state.busy) return;
    if (action === 'release' && !discardDraft()) return;
    const field = state.session?.fields[state.field];
    if ((action === 'submit' && hasDraft()) ||
        (action === 'field' && command.outcome === 'Approved' &&
         el('correctedValue').value !== field?.currentValue)) {
      notice('Save the correction or discard the draft before approving or submitting.');
      return;
    }
    state.busy = true;
    controls();
    const revision = state.revision;
    const draft = action === 'claim' ? { fieldId: field?.id, value: el('correctedValue').value,
      reason: el('reason').value, documentReason: el('documentReason').value } : null;
    try {
      const result = await DocIntelReview.change(reviewBase(), action, command, state.tag);
      if (revision !== state.revision) return;
      state.session = result.body;
      state.tag = result.tag;
      const item = state.queue.find(item => item.jobId === state.job);
      if (item) { item.status = result.body.status; renderQueue(); }
      if (action === 'claim') state.claimed = true;
      if (['release', 'submit', 'reject'].includes(action)) {
        state.claimed = false;
        el('documentReason').value = '';
      }
      if (action === 'start') await loadReview();
      renderFields();
      if (draft) {
        if (draft.fieldId === state.session.fields[state.field]?.id) {
          el('correctedValue').value = draft.value;
          el('reason').value = draft.reason;
        }
        el('documentReason').value = draft.documentReason;
      }
      renderPage();
      notice('Review action saved.');
    } catch (error) {
      if (revision === state.revision) {
        state.claimed = false;
        state.tag = null;
        notice(`${error.message} Reload before another action.`);
      }
    } finally { state.busy = false; controls(); }
  }

  async function loadPdf(revision) {
    el('pdfNotice').textContent = 'Loading original PDF and highlighting support...';
    try {
      pdfModule ||= await import('https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.6.82/pdf.min.mjs');
      pdfModule.GlobalWorkerOptions.workerSrc = 'https://cdnjs.cloudflare.com/ajax/libs/pdf.js/4.6.82/pdf.worker.min.mjs';
      const pdf = await pdfModule.getDocument({ url: el('original').href, withCredentials: true }).promise;
      if (revision !== state.revision) { await pdf.destroy(); return; }
      state.pdf = pdf;
      el('pdfNotice').textContent = '';
      await renderPage();
    } catch (error) {
      if (revision === state.revision)
        el('pdfNotice').textContent = 'PDF highlighting is unavailable. Open the original PDF to review it. No region accuracy is implied.';
    }
  }

  async function renderPage() {
    if (!state.pdf) return;
    const render = ++state.render;
    const revision = state.revision;
    const pageNumber = Math.min(Math.max(1, state.page), state.pdf.numPages);
    state.page = pageNumber;
    try {
      if (state.renderTask) {
        state.renderTask.cancel();
        try { await state.renderTask.promise; }
        catch (error) { if (error.name !== 'RenderingCancelledException') throw error; }
        state.renderTask = null;
      }
      const page = await state.pdf.getPage(pageNumber);
      if (render !== state.render || revision !== state.revision) return;
      const viewport = page.getViewport({ scale: 1.3 });
      const canvas = el('pdf');
      const ratio = window.devicePixelRatio || 1;
      canvas.width = Math.ceil(viewport.width * ratio);
      canvas.height = Math.ceil(viewport.height * ratio);
      const task = page.render({ canvasContext: canvas.getContext('2d'), viewport,
        transform: ratio === 1 ? null : [ratio, 0, 0, ratio, 0, 0] });
      state.renderTask = task;
      await task.promise;
      if (state.renderTask === task) state.renderTask = null;
      if (render !== state.render || revision !== state.revision) return;
      el('pageLabel').textContent = `Original page ${pageNumber} of ${state.pdf.numPages}`;
      el('previousPage').disabled = pageNumber <= 1;
      el('nextPage').disabled = pageNumber >= state.pdf.numPages;
      const overlay = el('highlights');
      overlay.setAttribute('viewBox', `0 0 ${viewport.width} ${viewport.height}`);
      overlay.replaceChildren();
      state.session?.fields.forEach((field, index) => {
        if (!field.originalProvenance?.grounded) return;
        for (const box of field.originalProvenance.boxes) {
          if (box.page !== pageNumber) continue;
          const polygon = document.createElementNS('http://www.w3.org/2000/svg', 'polygon');
          polygon.setAttribute('points', box.polygon.reduce((points, coordinate, position) =>
            points + (position % 2 === 0 ? `${coordinate * viewport.width},` : `${coordinate * viewport.height} `), '').trim());
          polygon.classList.toggle('active', index === state.field);
          polygon.addEventListener('click', () => selectField(index, false));
          overlay.append(polygon);
        }
      });
    } catch (error) {
      if (error.name !== 'RenderingCancelledException' && render === state.render && revision === state.revision)
        el('pdfNotice').textContent = 'This page could not be rendered. Use the original PDF link; highlights are unavailable.';
    }
  }

  function bind() {
    el('tenant').addEventListener('change', async () => {
      if (!discardDraft()) { el('tenant').value = state.tenant; return; }
      state.revision++;
      clearSource();
      state.tenant = el('tenant').value;
      state.job = null; state.session = null; state.pdf = null; state.claimed = false;
      el('originalValue').value = '';
      el('correctedValue').value = '';
      el('reason').value = '';
      el('documentReason').value = '';
      renderFields(); await loadQueue();
    });
    el('refresh').addEventListener('click', () => loadQueue());
    el('status').addEventListener('change', () => loadQueue());
    el('more').addEventListener('click', () => loadQueue(true));
    el('reload').addEventListener('click', () => {
      if (!discardDraft()) return;
      el('documentReason').value = '';
      loadReview();
    });
    el('start').addEventListener('click', () => act('start'));
    el('claim').addEventListener('click', () => act('claim'));
    el('release').addEventListener('click', () => act('release'));
    el('submit').addEventListener('click', () => act('submit'));
    el('approve').addEventListener('click', () => act('field', {
      fieldId: state.session.fields[state.field].id, outcome: 'Approved', reason: el('reason').value || null }));
    el('correct').addEventListener('click', () => act('field', {
      fieldId: state.session.fields[state.field].id, outcome: 'Corrected',
      value: el('correctedValue').value, reason: el('reason').value }));
    el('rejectField').addEventListener('click', () => act('field', {
      fieldId: state.session.fields[state.field].id, outcome: 'Rejected', reason: el('reason').value }));
    el('rejectDocument').addEventListener('click', () => act('reject', { reason: el('documentReason').value }));
    el('audit').addEventListener('click', async () => {
      const revision = state.revision;
      try {
        const result = await DocIntelReview.read(`${reviewBase()}/audit`);
        if (revision !== state.revision) return;
        el('auditText').textContent = JSON.stringify(result.body, null, 2);
        el('auditText').hidden = false;
      } catch (error) { if (revision === state.revision) notice(error.message); }
    });
    el('previousPage').addEventListener('click', () => { state.page--; renderPage(); });
    el('nextPage').addEventListener('click', () => { state.page++; renderPage(); });
    el('zoom').addEventListener('change', () => {
      el('pdfPage').classList.toggle('zoom150', el('zoom').value === '150');
      el('pdfPage').classList.toggle('zoom200', el('zoom').value === '200');
    });
    el('help').addEventListener('click', () => el('shortcuts').showModal());
    el('closeHelp').addEventListener('click', () => el('shortcuts').close());
    el('highlights').addEventListener('pointermove', event => {
      const bounds = el('highlights').getBoundingClientRect();
      const x = (event.clientX - bounds.left) / bounds.width, y = (event.clientY - bounds.top) / bounds.height;
      const index = state.session?.fields.findIndex(field => field.originalProvenance?.grounded &&
        field.originalProvenance.boxes.some(box => box.page === state.page && DocIntelReview.contains(box.polygon, x, y)));
      if (index >= 0) [...el('fields').children].forEach((button, current) => button.classList.toggle('selected', current === index));
    });
    document.addEventListener('keydown', event => {
      if (['INPUT', 'TEXTAREA', 'SELECT'].includes(event.target.tagName) || el('shortcuts').open) return;
      if (event.key === '?') { el('shortcuts').showModal(); return; }
      if (event.key === 'Tab' && state.session?.fields.length) {
        event.preventDefault(); selectField(state.field + (event.shiftKey ? -1 : 1)); return;
      }
      if (event.key === 'Enter' && !event.shiftKey && !el('approve').disabled) {
        event.preventDefault(); el('approve').click(); return;
      }
      if (event.key === 'e') el('correctedValue').focus();
      if (event.key === '/' || event.key === 'Escape') {
        event.preventDefault(); el(state.session?.fields.length ? 'reason' : 'documentReason').focus();
      }
      if (['j', 'k'].includes(event.key)) {
        const index = state.queue.findIndex(item => item.jobId === state.job) + (event.key === 'j' ? 1 : -1);
        if (state.queue[index]) selectJob(state.queue[index].jobId);
      }
    });
  }

  async function init() {
    bind();
    try {
      const query = new URLSearchParams(window.location.search);
      const tenants = await DocIntelReview.read('/api/harness/tenants');
      for (const tenant of tenants.body) {
        const option = document.createElement('option'); option.value = tenant.tenantId;
        option.textContent = tenant.displayName; el('tenant').append(option);
      }
      state.tenant = tenants.body.find(tenant => tenant.tenantId === query.get('tenant'))?.tenantId ||
        tenants.body[0]?.tenantId;
      if (!state.tenant) { notice('No application tenant is available for this account.'); return; }
      el('tenant').value = state.tenant;
      await loadQueue();
      if (query.get('job')) await selectJob(query.get('job'));
    } catch (error) { notice(error.message); }
  }
  init();
})();
