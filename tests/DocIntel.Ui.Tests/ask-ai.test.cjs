const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const webRoot = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const clientScript = fs.readFileSync(path.join(webRoot, 'ask-ai.js'), 'utf8');
const { loadApplication } = require('./helpers/application.cjs');
const pageScript = loadApplication('index');

function runtime(responses = []) {
  let elapsedMs = 0;
  const calls = [];
  const storage = new Map();
  const context = vm.createContext({
    console,
    DocIntelIdentity: { chatPartition: 'test-user' },
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
      calls.push({ url, method: options.method || 'GET', body: options.body ? JSON.parse(options.body) : undefined });
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
    loadedChatStorageKey = getChatsStorageKey();
  `, app.context);
}

test('human-reviewed citations route to review evidence rather than pretending to quote the original PDF', () => {
  const app = runtime();
  loadPage(app);
  app.context.citation = { jobId: 'reviewed-job', sliceIndex: 0, evidenceKind: 'HumanReviewedField' };
  const action = vm.runInContext('citationAction(citation)', app.context);
  assert.match(action, /data-click="jumpToHumanReview_arg"/);
  app.context.citation = { jobId: 'source-job', sliceIndex: 2, evidenceKind: 'OriginalSourceText' };
  assert.match(vm.runInContext('citationAction(citation)', app.context), /data-click="jumpFromGraphToSlice_arg_arg"/);
});

test('untrusted cached citation indices cannot become executable actions', () => {
  const app = runtime();
  loadPage(app);
  app.context.citation = { jobId: 'source-job', sliceIndex: '0);alert(1)//' };
  const action = vm.runInContext('citationAction(citation)', app.context);
  assert.ok(!action.includes('alert'));
  assert.match(action, /,0\]"/);
});

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
  const chat = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];

  assert.equal(chat.messages[1].text, 'The model is unavailable.');
  assert.equal(chat.messages[1].isError, true);
  assert.equal(chat.messages[1].runId, 'run-one');
  assert.equal(chat.messages[1].tenantId, 'first');
  assert.equal(chat.pendingAskAi, undefined);
});

test('saved failures retain an escaped diagnostic link and timestamp but never become follow-up context', () => {
  const app = runtime();
  loadPage(app);
  app.context.failure = {
    role: 'assistant', isError: true, runId: 'failed-run', tenantId: 'first',
    timestamp: '2026-10-28T20:00:00Z', errorCode: '<unsafe>',
    providerFailure: { httpStatus: 429 }
  };
  const html = vm.runInContext('askFailureDetails(failure)', app.context);
  assert.match(html, /Saved failure/);
  assert.match(html, /datetime="2026-10-28T20:00:00.000Z"/);
  assert.match(html, /&lt;unsafe&gt;/);
  assert.match(html, /HTTP 429/);
  assert.match(html, /ask-run\.html\?tenant=first&amp;run=failed-run/);
  assert.match(html, /rel="noopener noreferrer"/);
  vm.runInContext('tenantChats[0].messages=[failure];', app.context);
  assert.equal(vm.runInContext('previousAskAiRunId(tenantChats[0])', app.context), null);
});

test('restoring a pending chat resumes the same run without duplicating its question', async () => {
  const app = runtime([{ status: 'Completed', answer: 'Recovered.', citations: [] }]);
  loadPage(app);
  vm.runInContext(`
    tenantChats[0].messages.push({ role: 'user', text: 'Hi' });
    tenantChats[0].pendingAskAi = { question: 'Hi', runId: 'existing' };
  `, app.context);

  await vm.runInContext('runCrossDocumentAskAi(null, true)', app.context);
  const chat = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];

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

  const first = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];
  const second = JSON.parse(app.storage.get('docintel_chats_v2_test-user_second'))[0];
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

  const chats = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'));
  assert.equal(chats.length, 1);
  assert.equal(chats[0].id, 'chat-two');
  assert.equal(chats[0].messages.length, 0);
});

test('all cited sources survive the UI instead of being truncated to eight', async () => {
  const citations = Array.from({ length: 12 }, (_, index) => ({ citationNumber: index + 1 }));
  const app = runtime([{ runId: 'one' }, { status: 'Completed', answer: 'Answer.', citations }]);
  loadPage(app);

  await vm.runInContext("runCrossDocumentAskAi('Question')", app.context);

  const chat = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];
  assert.equal(chat.messages[1].citations.length, 12);
});

test('another account never loads the previous account chat history for the same tenant', () => {
  const app = runtime();
  loadPage(app);
  vm.runInContext(`
    tenantChats[0].messages.push({ role: 'assistant', text: 'Private to the first account.' });
    saveTenantChats();
    DocIntelIdentity.chatPartition = 'another-account';
    tenantChats = [];
    activeChatId = null;
    loadTenantChats(true);
  `, app.context);

  const current = JSON.parse(app.storage.get('docintel_chats_v2_another-account_first'));
  assert.equal(current[0].messages.length, 0);
  assert.ok(app.storage.get('docintel_chats_v2_test-user_first').includes('Private to the first account.'));
});

test('legacy unscoped browser history is not silently assigned to a newly signed-in account', () => {
  const app = runtime();
  loadPage(app);
  app.storage.set('docintel_chats_first', JSON.stringify([
    { id: 'old', title: 'Unverified owner', messages: [{ role: 'assistant', text: 'Legacy private answer.' }] }
  ]));

  vm.runInContext('tenantChats = []; activeChatId = null; loadTenantChats(true);', app.context);

  const current = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'));
  assert.equal(current[0].messages.length, 0);
});

test('action arguments remain JSON data after browser entity decoding', () => {
  const app = runtime();
  loadPage(app);
  const payloads = [
    "');globalThis.injected=true;//",
    '&quot;);globalThis.injected=true;//',
    '\\";globalThis.injected=true;//',
    '<img src=x onerror=globalThis.injected=true>'
  ];
  const entities = { '&amp;': '&', '&lt;': '<', '&gt;': '>', '&quot;': '"', '&#39;': "'" };
  for (const payload of payloads) {
    app.context.inputValue = payload;
    const escaped = vm.runInContext('escapeJsArg(inputValue)', app.context);
    const decoded = escaped.replace(/&(?:amp|lt|gt|quot|#39);/g, entity => entities[entity]);
    const result = JSON.parse(decoded);
    assert.equal(result, payload);
    assert.equal(app.context.injected, undefined);
  }
});

test('submission sends only the optional server run reference, never trusted browser history', async () => {
  const app = runtime([{ runId: 'one' }, { runId: 'two' }]);
  await app.context.DocIntelAskAi.submit('first', 'Question');
  await app.context.DocIntelAskAi.submit('first', 'Follow-up', 'one');
  assert.deepEqual(app.calls[0].body, { question: 'Question', replyChannel: { channelType: 'Web' } });
  assert.deepEqual(app.calls[1].body, {
    question: 'Follow-up', replyChannel: { channelType: 'Web' }, previousRunId: 'one'
  });
});

test('completed analyses and run IDs persist, reload and continue only the active conversation', async () => {
  const analyses = [{ analysisId: 'a1', title: 'Spend', visualization: 'table', rows: [] }];
  const app = runtime([
    { runId: 'first-run' }, { status: 'Completed', answer: 'First.', analyses },
    { runId: 'second-run' }, { status: 'Completed', answer: 'Second.', analyses: [] }
  ]);
  loadPage(app);
  await vm.runInContext("runCrossDocumentAskAi('Analyze spend')", app.context);
  assert.equal(app.calls[0].body.previousRunId, undefined);
  const first = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];
  assert.equal(first.messages[1].runId, 'first-run');
  assert.deepEqual(first.messages[1].analyses, analyses);
  vm.runInContext('tenantChats = []; activeChatId = null; loadTenantChats(true);', app.context);
  assert.equal(vm.runInContext('getActiveChat().messages[1].analyses[0].title', app.context), 'Spend');
  await vm.runInContext("runCrossDocumentAskAi('Why the difference?')", app.context);
  const secondPost = app.calls.filter(call => call.method === 'POST')[1];
  assert.equal(secondPost.body.previousRunId, 'first-run');
  assert.equal(secondPost.body.messages, undefined);
  const second = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];
  assert.equal(second.messages[3].runId, 'second-run');
});

test('follow-up context skips failures but never crosses chat, tenant or account boundaries', async () => {
  const app = runtime();
  loadPage(app);
  const submitted = [];
  app.context.DocIntelAskAi.submit = async (tenant, question, previousRunId) => {
    submitted.push({ tenant, question, previousRunId });
    return `new-${submitted.length}`;
  };
  app.context.DocIntelAskAi.waitForRun = async () => ({ status: 'Completed', answer: 'Done.', analyses: [] });
  vm.runInContext(`
    tenantChats[0].messages = [
      { role: 'assistant', text: 'Previous answer', runId: 'same-chat-success', analyses: [] },
      { role: 'assistant', text: 'Failure', runId: 'failed-run', isError: true }
    ];
  `, app.context);
  await vm.runInContext("runCrossDocumentAskAi('Continue')", app.context);
  assert.equal(submitted[0].previousRunId, 'same-chat-success');
  vm.runInContext(`
    tenantChats.push({ id: 'empty', title: 'New chat', messages: [] });
    activeChatId = 'empty';
  `, app.context);
  await vm.runInContext("runCrossDocumentAskAi('Unrelated question')", app.context);
  assert.equal(submitted[1].previousRunId, null);
  vm.runInContext(`
    currentTenant = { tenantId: 'second' };
    loadTenantChats(true);
  `, app.context);
  await vm.runInContext("runCrossDocumentAskAi('Different tenant')", app.context);
  assert.equal(submitted[2].tenant, 'second');
  assert.equal(submitted[2].previousRunId, null);
  vm.runInContext(`
    DocIntelIdentity.chatPartition = 'another-account';
    loadTenantChats(true);
  `, app.context);
  await vm.runInContext("runCrossDocumentAskAi('Different account')", app.context);
  assert.equal(submitted[3].previousRunId, null);
});

test('legacy text-only chats keep rendering and start without a fabricated previous run', async () => {
  const app = runtime([{ runId: 'fresh' }, { status: 'Completed', answer: 'New answer.' }]);
  loadPage(app);
  vm.runInContext(`
    tenantChats[0].messages.push({ role: 'assistant', text: 'Legacy answer.' });
    globalThis.body = { innerHTML: '' };
    renderCrossDocumentAskTab(body);
  `, app.context);
  assert.match(app.context.body.innerHTML, /Legacy answer/);
  assert.doesNotMatch(app.context.body.innerHTML, /data-ask-analysis/);
  await vm.runInContext("runCrossDocumentAskAi('New question')", app.context);
  assert.equal(app.calls[0].body.previousRunId, undefined);
});

test('resuming a pending analytical run persists its original ID and analyses without resubmitting', async () => {
  const analyses = [{ title: 'Recovered analysis', visualization: 'table', rows: [] }];
  const app = runtime([{ status: 'Completed', answer: 'Recovered.', analyses }]);
  loadPage(app);
  vm.runInContext(`
    tenantChats[0].messages.push({ role: 'user', text: 'Hi' });
    tenantChats[0].pendingAskAi = { question: 'Hi', runId: 'original-run' };
  `, app.context);
  await vm.runInContext('runCrossDocumentAskAi(null, true)', app.context);
  const chat = JSON.parse(app.storage.get('docintel_chats_v2_test-user_first'))[0];
  assert.equal(app.calls.filter(call => call.method === 'POST').length, 0);
  assert.equal(chat.messages[1].runId, 'original-run');
  assert.deepEqual(chat.messages[1].analyses, analyses);
});

test('rich conversation cards use existing citation actions and offer context only on the latest successful answer', () => {
  const { createDocument, all } = require('./helpers/analysis-dom.cjs');
  const app = runtime();
  loadPage(app);
  app.context.document = createDocument();
  const hosts = new Map();
  app.context.body = {
    innerHTML: '',
    querySelector(selector) {
      if (!hosts.has(selector)) hosts.set(selector, app.context.document.createElement('div'));
      return hosts.get(selector);
    }
  };
  app.context.analysis = {
    title: 'Totals', visualization: 'bar', xColumn: 'name', yColumn: 'amount',
    columns: [{ key: 'name', label: 'Name', kind: 'text' }, { key: 'amount', label: 'Amount', kind: 'number' }],
    rows: [{ cells: { name: 'Vendor', amount: 2, unit: 'USD' }, citationNumbers: [1, 2] }]
  };
  app.context.citations = [
    { citationNumber: 1, jobId: 'source', sliceIndex: 3 },
    { citationNumber: 2, jobId: 'review', sliceIndex: 0, evidenceKind: 'HumanReviewedField' }
  ];
  const opened = [];
  const followUps = [];
  app.context.recordCitation = (...args) => opened.push(args);
  app.context.recordQuestion = question => followUps.push(question);
  vm.runInContext(`
    jumpFromGraphToSlice = (job, slice) => recordCitation(job, slice);
    jumpToHumanReview = job => recordCitation(job);
    tenantChats[0].messages = [
      { role: 'assistant', text: 'Old', runId: 'old', analyses: [analysis], citations },
      { role: 'assistant', text: 'Latest [1]', runId: 'latest', analyses: [analysis], citations }
    ];
    renderCrossDocumentAskTab(body);
    runCrossDocumentAskAi = question => recordQuestion(question);
  `, app.context);
  const old = hosts.get('[data-ask-analysis="0"]');
  const latest = hosts.get('[data-ask-analysis="1"]');
  const buttons = host => all(host, item => item.tagName === 'button');
  assert.equal(buttons(old).filter(item => item.className === 'ask-analysis-followup').length, 0);
  const sources = buttons(latest).filter(item => item.className === 'ask-analysis-source');
  sources[0].click();
  sources[1].click();
  assert.deepEqual(opened, [['source', 3], ['review']]);
  buttons(latest).find(item => item.className === 'ask-analysis-followup').click();
  assert.match(followUps[0], /Totals/);
  assert.match(app.context.body.innerHTML, /insight-prose/);
  assert.match(app.context.body.innerHTML, /ask-pill-bar/);
});
