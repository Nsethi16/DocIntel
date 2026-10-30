const assert = require('node:assert/strict');
const test = require('node:test');
const vm = require('node:vm');
const { loadApplication } = require('./helpers/application.cjs');

function runtime(fetch, timers = {}, page = 'index') {
  const context = vm.createContext({
    fetch, AbortController, setTimeout, clearTimeout,
    document: { getElementById: () => ({ addEventListener() {} }) },
    ...timers
  });
  vm.runInContext(loadApplication(page), context);
  return context;
}

function preview(url = '/api/harness/tenants/one/jobs/source/slices/0/pdf') {
  const frame = { hidden: true };
  const status = { setAttribute(name, value) { this[name] = value; } };
  const button = {};
  const actions = { hidden: true, querySelector: () => button };
  const elements = { iframe: frame, '.pdf-preview-status': status, '.pdf-preview-actions': actions };
  return { dataset: { pdfUrl: url }, isConnected: true, querySelector: selector => elements[selector],
    frame, status, actions, button };
}

test('only a successful PDF response starts the native viewer, using a bounded authenticated range request', async () => {
  const item = preview();
  let request;
  const app = runtime(async (url, options) => {
    request = { url, options };
    return new Response('%PDF-', { status: 206, headers: { 'Content-Type': 'application/pdf' } });
  });
  await app.loadPdfPreview(item);
  assert.equal(request.url, item.dataset.pdfUrl);
  assert.equal(request.options.headers.Range, 'bytes=0-4');
  assert.ok(request.options.signal instanceof AbortSignal);
  assert.equal(item.frame.src, item.dataset.pdfUrl);
  assert.equal(item.frame.hidden, false);
  assert.equal(item.status.hidden, true);
  assert.equal(item.actions.hidden, true);
});

for (const status of [401, 403, 404, 429, 500]) {
  test(`HTTP ${status} produces a useful error rather than embedding a broken response`, async () => {
    const item = preview();
    const app = runtime(async () => new Response('PRIVATE_STORAGE_ERROR_DETAILS', {
      status, headers: { 'Content-Type': 'application/problem+json', 'X-DocIntel-Correlation-Id': 'safe-reference' }
    }));
    await app.loadPdfPreview(item);
    assert.equal(item.frame.src, undefined);
    assert.equal(item.frame.hidden, true);
    assert.equal(item.actions.hidden, false);
    assert.equal(item.status.role, 'alert');
    assert.match(item.status.textContent, status === 404 ? /not available in storage.*original file again/
      : status === 401 || status === 403 ? /Sign in again or request access/ : new RegExp(`HTTP ${status}`));
    assert.match(item.status.textContent, /safe-reference/);
    assert.doesNotMatch(item.status.textContent, /PRIVATE_STORAGE_ERROR_DETAILS/);
  });
}

test('an HTML sign-in response is never mistaken for a PDF', async () => {
  const item = preview();
  const app = runtime(async () => new Response('<html>Sign in</html>', { headers: { 'Content-Type': 'text/html' } }));
  await app.loadPdfPreview(item);
  assert.equal(item.frame.src, undefined);
  assert.match(item.status.textContent, /did not return a PDF/);
  assert.equal(item.actions.hidden, false);
});

test('a failed connection is explicit and retry can recover without rerendering the document', async () => {
  const item = preview();
  let calls = 0;
  const app = runtime(async () => {
    if (++calls === 1) throw new TypeError('PRIVATE_NETWORK_DETAIL');
    return new Response('%PDF-', { headers: { 'Content-Type': 'application/pdf; charset=binary' } });
  });
  await app.loadPdfPreview(item);
  assert.match(item.status.textContent, /Check your connection/);
  assert.doesNotMatch(item.status.textContent, /PRIVATE_NETWORK_DETAIL/);
  await item.button.onclick();
  assert.equal(calls, 2);
  assert.equal(item.frame.src, item.dataset.pdfUrl);
  assert.equal(item.frame.hidden, false);
  assert.equal(item.actions.hidden, true);
});

test('a stalled request is canceled and exposes a retry instead of an indefinite loading state', async () => {
  const item = preview();
  let abort;
  let timerCleared = false;
  const app = runtime((_, options) => new Promise((resolve, reject) =>
    options.signal.addEventListener('abort', () => reject(new DOMException('Timed out', 'AbortError')))), {
    setTimeout(callback, milliseconds) { assert.equal(milliseconds, 15000); abort = callback; return 1; },
    clearTimeout(id) { assert.equal(id, 1); timerCleared = true; }
  });
  const pending = app.loadPdfPreview(item);
  abort();
  await pending;
  assert.match(item.status.textContent, /timed out/);
  assert.equal(item.actions.hidden, false);
  assert.equal(timerCleared, true);
});

test('responses for a document that was closed cannot start or alter its viewer', async () => {
  const item = preview();
  let finish;
  const app = runtime(() => new Promise(resolve => { finish = resolve; }));
  const pending = app.loadPdfPreview(item);
  item.isConnected = false;
  finish(new Response('%PDF-', { headers: { 'Content-Type': 'application/pdf' } }));
  await pending;
  assert.equal(item.frame.src, undefined);
  assert.equal(item.frame.hidden, true);
});

test('a missing translated PDF does not hide the original or substitute it as a translation', async () => {
  const original = preview();
  const translated = preview(original.dataset.pdfUrl + '?translated=true');
  const app = runtime(async url => url.includes('?translated=true')
    ? new Response('', { status: 404 })
    : new Response('%PDF-', { headers: { 'Content-Type': 'application/pdf' } }));
  await Promise.all([app.loadPdfPreview(original), app.loadPdfPreview(translated)]);
  assert.equal(original.frame.src, original.dataset.pdfUrl);
  assert.equal(original.frame.hidden, false);
  assert.equal(translated.frame.src, undefined);
  assert.equal(translated.frame.hidden, true);
  assert.match(translated.status.textContent, /not available in storage/);
  assert.match(translated.status.textContent, /original is shown separately/);
});

test('preview markup uses safe links and leaves the iframe source unset until validation', () => {
  const app = runtime(() => { throw new Error('No request expected.'); });
  const html = app.pdfPreviewMarkup('/api/harness/tenants/one/jobs/source/slices/0/pdf', '<untrusted>');
  assert.match(html, /title="&lt;untrusted&gt;"/);
  assert.match(html, /rel="noopener noreferrer"/);
  assert.match(html, /role="status"/);
  assert.doesNotMatch(html, /<iframe[^>]+\bsrc=/);
});

test('a source-file fallback still requires PDF preflight and never treats a failed job as permission to embed it', async () => {
  const original = preview('/api/harness/tenants/one/jobs/failed/original');
  let allowed = false;
  const app = runtime(async (url, options) => {
    assert.equal(url, original.dataset.pdfUrl);
    assert.equal(options.headers.Range, 'bytes=0-4');
    return allowed
      ? new Response('%PDF-', { headers: { 'Content-Type': 'application/pdf' } })
      : new Response('PRIVATE_ACCESS_DETAIL', { status: 403 });
  });
  await app.loadPdfPreview(original);
  assert.equal(original.frame.src, undefined);
  assert.equal(original.frame.hidden, true);
  assert.match(original.status.textContent, /request access/);
  assert.doesNotMatch(original.status.textContent, /PRIVATE_ACCESS_DETAIL/);
  allowed = true;
  await original.button.onclick();
  assert.equal(original.frame.src, original.dataset.pdfUrl);
  assert.equal(original.frame.hidden, false);
});

test('Admin previews use the shared authenticated PDF preflight and keep original access independent of translated failure', async () => {
  const original = preview();
  const translated = preview(original.dataset.pdfUrl + '?translated=true');
  let accessAllowed = false;
  const app = runtime(async (url, options) => {
    assert.equal(options.headers.Range, 'bytes=0-4');
    return url.includes('?translated=true') ? new Response('PRIVATE_TRANSLATION_ERROR', { status: 404 })
      : accessAllowed ? new Response('%PDF-', { headers: { 'Content-Type': 'application/pdf' } })
      : new Response('PRIVATE_ACCESS_ERROR', { status: 403 });
  }, {}, 'admin');
  await app.loadPdfPreview(original);
  assert.equal(original.frame.src, undefined);
  assert.match(original.status.textContent, /request access/);
  accessAllowed = true;
  await Promise.all([original.button.onclick(), app.loadPdfPreview(translated)]);
  assert.equal(original.frame.src, original.dataset.pdfUrl);
  assert.equal(original.frame.hidden, false);
  assert.equal(translated.frame.src, undefined);
  assert.equal(translated.frame.hidden, true);
  assert.match(translated.status.textContent, /translated PDF is not available/);
  assert.doesNotMatch(translated.status.textContent, /PRIVATE_TRANSLATION_ERROR|PRIVATE_ACCESS_ERROR/);
});
