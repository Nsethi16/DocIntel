const assert = require('node:assert/strict');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

async function prepare(directory, { ocr = 'Never', processing = false } = {}) {
  await fs.mkdir(path.join(directory, 'App_Data'));
  const capabilities = Object.fromEntries([
    'SplitAndGroup', 'IdentifyDocuments', 'DocumentTranslation', 'DocumentInsight',
    'EntityExtraction', 'DataExtraction', 'PromptColumns', 'DocumentGraph', 'AskAi'
  ].map(name => [name, { enabled: name === 'AskAi' || (processing && name === 'SplitAndGroup'),
    modelIdOverride: 'foundry-local-phi4' }]));
  await fs.writeFile(path.join(directory, 'App_Data', 'agent-harness-settings.json'), JSON.stringify([{
    tenantId: 'ready-test', displayName: 'Readiness fixture', harnessVersion: 1, isActive: true,
    defaultModelId: 'foundry-local-phi4', decisionModelId: 'foundry-local-phi4',
    allowedModels: ['foundry-local-phi4'], ocr: { mode: ocr }, capabilities
  }]));
}

async function settled(api) {
  for (let attempt = 0; attempt < 150; attempt++) {
    const response = await fetch(api.url + '/health/ready');
    const body = await response.json();
    if (!body.checks.some(check => check.description?.includes('in progress'))) return { response, body };
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error(`Readiness never settled.\n${api.getLogs()}`);
}

test('missing model configuration blocks readiness without breaking liveness or exposing diagnostics',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Readiness.Missing-'));
    let api;
    try {
      await prepare(directory);
      api = await startApi(directory);
      assert.equal((await fetch(api.url + '/health/live')).status, 200);
      assert.equal((await fetch(api.url + '/alive')).status, 200);
      const { response, body } = await settled(api);
      assert.equal(response.status, 503);
      assert.equal(body.status, 'Unhealthy');
      assert.doesNotMatch(JSON.stringify(body), /foundry-local|127\.0\.0\.1|StackTrace|App_Data/);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('opted-in startup probes make exactly one bounded model request and health polling never repeats inference',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Readiness.Model-'));
    const requests = [];
    const provider = http.createServer(async (request, response) => {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      requests.push(JSON.parse(Buffer.concat(chunks)));
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({
        id: 'startup-fixture', object: 'chat.completion', created: 1, model: 'phi-4',
        choices: [{ index: 0, message: { role: 'assistant', content: 'OK' }, finish_reason: 'stop' }],
        usage: { prompt_tokens: 2, completion_tokens: 1, total_tokens: 3 }
      }));
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    let api;
    try {
      await prepare(directory);
      api = await startApi(directory, {
        'AzureAI:LocalOpenAIEndpoint': `http://127.0.0.1:${provider.address().port}/v1`,
        'Readiness:ProbeModelsOnStartup': true
      });
      const { response, body } = await settled(api);
      assert.equal(response.status, 200, JSON.stringify(body));
      assert.equal(requests.length, 1);
      assert.equal(Object.hasOwn(requests[0], 'max_completion_tokens'), false);
      assert.equal(Object.hasOwn(requests[0], 'max_tokens'), false);
      assert.equal(requests[0].messages[0].content, 'Reply OK.');
      for (let i = 0; i < 4; i++) assert.equal((await fetch(api.url + '/health/ready')).status, 200);
      assert.equal(requests.length, 1);
      assert.ok(body.checks.some(check => check.description?.includes('inference were verified')));
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });

test('configured but unprobed models are labeled honestly and do not incur a hidden startup generation',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Readiness.NoInference-'));
    let calls = 0;
    const provider = http.createServer((request, response) => { calls++; response.writeHead(500); response.end(); });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    let api;
    try {
      await prepare(directory);
      api = await startApi(directory, {
        'AzureAI:LocalOpenAIEndpoint': `http://127.0.0.1:${provider.address().port}/v1`
      });
      const { response, body } = await settled(api);
      assert.equal(response.status, 200);
      assert.equal(calls, 0);
      assert.ok(body.checks.some(check => check.description?.includes('inference probing is disabled')));
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });

test('enabled scanned-page processing requires OCR configuration and startup probes have a hard deadline',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Readiness.Ocr-'));
    let api;
    const provider = http.createServer(() => {});
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    const model = `http://127.0.0.1:${provider.address().port}/v1`;
    try {
      await prepare(directory, { ocr: 'Auto', processing: true });
      api = await startApi(directory, { 'AzureAI:LocalOpenAIEndpoint': model });
      assert.equal((await settled(api)).response.status, 503);
      await killApi(api);
      api = null;
      const tenantFile = path.join(directory, 'App_Data', 'agent-harness-settings.json');
      const tenants = JSON.parse(await fs.readFile(tenantFile, 'utf8'));
      tenants[0].ocr.mode = 'Never';
      tenants[0].capabilities.SplitAndGroup.enabled = false;
      await fs.writeFile(tenantFile, JSON.stringify(tenants));
      api = await startApi(directory, { 'AzureAI:LocalOpenAIEndpoint': model,
        'Readiness:ProbeModelsOnStartup': true, 'Readiness:TimeoutSeconds': 1 });
      const { response, body } = await settled(api);
      assert.equal(response.status, 503);
      assert.ok(body.checks.some(check => check.description?.includes('deadline')));
      assert.equal((await fetch(api.url + '/health/live')).status, 200);
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });

test('a failed authenticated model probe keeps readiness closed without exposing the provider response',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Readiness.Rejected-'));
    const marker = 'PRIVATE_PROVIDER_DIAGNOSTIC_91027';
    const provider = http.createServer((request, response) => {
      request.resume();
      response.writeHead(401, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({ error: { message: marker, type: 'authentication_error' } }));
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    let api;
    try {
      await prepare(directory);
      api = await startApi(directory, {
        'AzureAI:LocalOpenAIEndpoint': `http://127.0.0.1:${provider.address().port}/v1`,
        'Readiness:ProbeModelsOnStartup': true
      });
      const { response, body } = await settled(api);
      assert.equal(response.status, 503);
      assert.doesNotMatch(JSON.stringify(body), new RegExp(marker));
      assert.doesNotMatch(api.getLogs(), new RegExp(marker));
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });

test('classification with no active rules does not require a model that the workflow would never call',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Readiness.NoRules-'));
    let api;
    try {
      await prepare(directory);
      const file = path.join(directory, 'App_Data', 'agent-harness-settings.json');
      const tenants = JSON.parse(await fs.readFile(file, 'utf8'));
      tenants[0].capabilities.AskAi.enabled = false;
      tenants[0].capabilities.IdentifyDocuments = { enabled: true, identifyDocuments: { rules: [] } };
      await fs.writeFile(file, JSON.stringify(tenants));
      api = await startApi(directory);
      assert.equal((await settled(api)).response.status, 200);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });
