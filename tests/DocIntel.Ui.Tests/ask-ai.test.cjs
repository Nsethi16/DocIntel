const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const webRoot = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const clientScript = fs.readFileSync(path.join(webRoot, 'ask-ai.js'), 'utf8');
const html = fs.readFileSync(path.join(webRoot, 'index.html'), 'utf8');
const pageScript = [...html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g)]
  .map(match => match[1]).join('\n').replace(/\n\s*init\(\);\s*$/, '');

function runtime(responses = []) {
  let elapsedMs = 0;
  const calls = [];
  const storage = new Map();
  const context = vm.createContext({
    console,
    AbortSignal,
    Date: class extends Date {
      static now() { return elapsedMs; }
    },
    setTimeout: (callback, milliseconds) => {
      elapsedMs += milliseconds;
      queueMicrotask(callback);
    },
    document: { getElementById: () => null },
    localStorage: {
      getItem: key => storage.get(key) || null,
      setItem: (key, value) => storage.set(key, value)
    },
    fetch: async (url, options) => {
      calls.push({ url, method: options.method || 'GET' });
      const response = responses.shift();
      assert.ok(response, 'Unexpected request');
      if (response instanceof Error) throw response;
      return {
        ok: !response.httpStatus || response.httpStatus < 400,
        status: response.httpStatus || 200,
        json: async () => response
      };
    }
  });
  vm.runInContext(clientScript, context);
  return { context, calls, storage, elapsed: () => elapsedMs };
}

function loadPage(app) {
  vm.runInContext(pageScript, app.context);
  vm.runInContext(`
    renderDocumentList = () => {};
    renderSelectedDocument = () => {};
    currentTenant = { tenantId: 'first' };
    tenantChats = [{ id: 'chat-one', title: 'New chat', messages: [] }];
    activeChatId = 'chat-one';
  `, app.context);
}

test('one async submission is polled beyond the old 14-second limit', async () => {
  const queued = Array.from({ length: 50 }, () => ({ status: 'Queued' }));
  const app = runtime([{ runId: 'run-one' }, ...queued, { status: 'Completed', answer: 'Done.' }]);
  const id = await app.context.DocIntelAskAi.submit('a/b', 'Question');
  const completed = await app.context.DocIntelAskAi.waitForRun('a/b', id);

  assert.equal(completed.answer, 'Done.');
  assert.equal(app.elapsed(), 50000);
  assert.equal(app.calls.filter(call => call.method === 'POST').length, 1);
  assert.ok(app.calls.every(call => call.url.startsWith('/api/harness/tenants/a%2Fb/ask/runs')));
});

test('submission errors are surfaced without a second execution attempt', async () => {
  const app = runtime([{ httpStatus: 400, error: 'Question is too long.' }]);

  await assert.rejects(app.context.DocIntelAskAi.submit('first', 'Question'), /too long/);
  assert.equal(app.calls.length, 1);
});

test('polling budget expiry keeps the existing run resumable instead of submitting again', async () => {
  const responses = Array.from({ length: 1800 }, () => ({ status: 'Running' }));
  const app = runtime(responses);

  await assert.rejects(app.context.DocIntelAskAi.waitForRun('first', 'existing'), error => {
    assert.equal(error.resumable, true);
    return /still processing/.test(error.message);
  });
  assert.equal(app.elapsed(), 30 * 60 * 1000);
  assert.ok(app.calls.every(call => call.method === 'GET'));
});

test('missing runs are terminal and are not automatically resubmitted', async () => {
  const app = runtime([{ httpStatus: 404, error: 'Not found.' }]);

  await assert.rejects(app.context.DocIntelAskAi.waitForRun('first', 'missing'), error => {
    assert.equal(error.resumable, false);
    return /no longer available/.test(error.message);
  });
  assert.equal(app.calls.filter(call => call.method === 'POST').length, 0);
});

test('network failures preserve the known run for polling-only recovery', async () => {
  const app = runtime([new TypeError('Network disconnected.')]);

  await assert.rejects(app.context.DocIntelAskAi.waitForRun('first', 'existing'), error => {
    assert.equal(error.resumable, true);
    return /Network disconnected/.test(error.message);
  });
  assert.equal(app.calls.length, 1);
});

test('a failed run shows its explicit error rather than a successful-looking answer', async () => {
  const app = runtime([
    { runId: 'run-one' },
    { status: 'Failed', errorMessage: 'The model is unavailable.' }
  ]);
  loadPage(app);

  await vm.runInContext("runCrossDocumentAskAi('Hi')", app.context);
  const chat = JSON.parse(app.storage.get('docintel_chats_first'))[0];

  assert.equal(chat.messages[1].text, 'The model is unavailable.');
  assert.equal(chat.messages[1].isError, true);
  assert.equal(chat.pendingAskAi, undefined);
});

test('restoring a pending chat resumes the same run without duplicating its question', async () => {
  const app = runtime([{ status: 'Completed', answer: 'Recovered.', citations: [] }]);
  loadPage(app);
  vm.runInContext(`
    tenantChats[0].messages.push({ role: 'user', text: 'Hi' });
    tenantChats[0].pendingAskAi = { question: 'Hi', runId: 'existing' };
  `, app.context);

  await vm.runInContext('runCrossDocumentAskAi(null, true)', app.context);
  const chat = JSON.parse(app.storage.get('docintel_chats_first'))[0];

  assert.equal(chat.messages.length, 2);
  assert.equal(chat.messages[1].text, 'Recovered.');
  assert.equal(app.calls.filter(call => call.method === 'POST').length, 0);
  assert.ok(app.calls[0].url.endsWith('/existing'));
});

test('switching tenants during a run cannot attach its answer to another tenant chat', async () => {
  const app = runtime();
  loadPage(app);
  let finish;
  let ready;
  const waiting = new Promise(resolve => { ready = resolve; });
  app.context.DocIntelAskAi.submit = async () => 'run-one';
  app.context.DocIntelAskAi.waitForRun = () => new Promise(resolve => { finish = resolve; ready(); });
  const running = vm.runInContext("runCrossDocumentAskAi('Hi')", app.context);
  await waiting;
  vm.runInContext(`
    currentTenant = { tenantId: 'second' };
    tenantChats = [{ id: 'chat-two', title: 'Second', messages: [] }];
    activeChatId = 'chat-two';
    saveTenantChats();
  `, app.context);

  finish({ status: 'Completed', answer: 'First tenant answer.', citations: [] });
  await running;

  const first = JSON.parse(app.storage.get('docintel_chats_first'))[0];
  const second = JSON.parse(app.storage.get('docintel_chats_second'))[0];
  assert.equal(first.messages[1].text, 'First tenant answer.');
  assert.equal(second.messages.length, 0);
});

test('deleting the original chat does not move its eventual answer into the active chat', async () => {
  const app = runtime();
  loadPage(app);
  let finish;
  let ready;
  const waiting = new Promise(resolve => { ready = resolve; });
  app.context.DocIntelAskAi.submit = async () => 'run-one';
  app.context.DocIntelAskAi.waitForRun = () => new Promise(resolve => { finish = resolve; ready(); });
  const running = vm.runInContext("runCrossDocumentAskAi('Hi')", app.context);
  await waiting;
  vm.runInContext(`
    tenantChats = [{ id: 'chat-two', title: 'New chat', messages: [] }];
    activeChatId = 'chat-two';
    saveTenantChats();
  `, app.context);

  finish({ status: 'Completed', answer: 'Old answer.', citations: [] });
  await running;

  const chats = JSON.parse(app.storage.get('docintel_chats_first'));
  assert.equal(chats.length, 1);
  assert.equal(chats[0].id, 'chat-two');
  assert.equal(chats[0].messages.length, 0);
});

test('all cited sources survive the UI instead of being truncated to eight', async () => {
  const citations = Array.from({ length: 12 }, (_, index) => ({ citationNumber: index + 1 }));
  const app = runtime([{ runId: 'one' }, { status: 'Completed', answer: 'Answer.', citations }]);
  loadPage(app);

  await vm.runInContext("runCrossDocumentAskAi('Question')", app.context);

  const chat = JSON.parse(app.storage.get('docintel_chats_first'))[0];
  assert.equal(chat.messages[1].citations.length, 12);
});
