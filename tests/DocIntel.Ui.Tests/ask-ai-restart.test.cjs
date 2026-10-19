const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');

const apiDll = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api',
  'bin', 'Release', 'net10.0', 'DocIntel.Api.dll');

async function startApi(directory, modelUrl) {
  const child = spawn('dotnet', [
    apiDll, '--urls', 'http://127.0.0.1:0', '--contentRoot', directory,
    '--AzureAI:LocalOpenAIEndpoint', modelUrl,
    '--AzureAI:CosmosEndpoint', '', '--AzureAI:BlobEndpoint', '',
    '--AzureAI:AzureOpenAIEndpoint', '', '--AzureAI:FoundryInferenceEndpoint', ''
  ], {
    env: { ...process.env, DOTNET_ENVIRONMENT: 'Production', ASPNETCORE_ENVIRONMENT: 'Production' },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  const url = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      child.kill('SIGKILL');
      reject(new Error(`API startup timed out: ${output}`));
    }, 20000);
    child.on('error', reject);
    child.on('exit', code => {
      clearTimeout(timeout);
      reject(new Error(`API exited (${code}): ${output}`));
    });
    child.stderr.on('data', chunk => { output += chunk; });
    child.stdout.on('data', chunk => {
      output += chunk;
      const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/);
      if (match) {
        clearTimeout(timeout);
        resolve(match[1]);
      }
    });
  });
  return { child, url };
}

async function killApi(api) {
  if (api && api.child.exitCode === null && api.child.signalCode === null) {
    const exited = once(api.child, 'exit');
    api.child.kill('SIGKILL');
    await exited;
  }
}

test('a real API process crash releases ownership and the same persisted run completes after restart',
  { timeout: 60000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Restart.Tests-'));
    let api;
    let releaseResponses = false;
    let calls = 0;
    let entered;
    const firstRequest = new Promise(resolve => { entered = resolve; });
    const provider = http.createServer(async (request, response) => {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      assert.ok(body.tools?.some(tool => tool.function?.name === 'search_documents'));
      calls++;
      if (!releaseResponses) {
        entered();
        return;
      }
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({
        id: 'chatcmpl-local-test',
        object: 'chat.completion',
        created: Math.floor(Date.now() / 1000),
        model: 'phi-4',
        choices: [{
          index: 0,
          message: {
            role: 'assistant',
            content: JSON.stringify({ kind: 'Conversation', answer: 'Hello after restart!', evidenceIds: [] })
          },
          finish_reason: 'stop'
        }],
        usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 }
      }));
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    const modelUrl = `http://127.0.0.1:${provider.address().port}/v1`;
    try {
      api = await startApi(directory, modelUrl);
      const tenantPath = '/api/harness/tenants/contoso-legal';
      const tenantResponse = await fetch(api.url + tenantPath);
      assert.equal(tenantResponse.status, 200);
      const tenant = await tenantResponse.json();
      tenant.defaultModelId = 'foundry-local-phi4';
      tenant.allowedModels = [...new Set([...tenant.allowedModels, 'foundry-local-phi4'])];
      tenant.capabilities.AskAi.modelIdOverride = 'foundry-local-phi4';
      assert.equal((await fetch(api.url + tenantPath, {
        method: 'PUT', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(tenant)
      })).status, 200);
      const submitted = await fetch(api.url + tenantPath + '/ask/runs', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ question: 'Hi' })
      });
      assert.equal(submitted.status, 202);
      const accepted = await submitted.json();
      let firstRequestTimer;
      try {
        await Promise.race([firstRequest, new Promise((resolve, reject) => {
          firstRequestTimer = setTimeout(() => reject(new Error('The API did not call the local model.')), 10000);
        })]);
      } finally {
        clearTimeout(firstRequestTimer);
      }
      const running = await (await fetch(api.url + accepted.statusUrl)).json();
      assert.equal(running.status, 'Running');
      assert.equal(running.executionAttempts, 1);

      await killApi(api);
      releaseResponses = true;
      api = await startApi(directory, modelUrl);
      let recovered;
      for (let attempt = 0; attempt < 100; attempt++) {
        recovered = await (await fetch(api.url + accepted.statusUrl)).json();
        if (recovered.status === 'Completed' || recovered.status === 'Failed') break;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      assert.equal(recovered.runId, accepted.runId);
      assert.equal(recovered.status, 'Completed', recovered.errorMessage);
      assert.equal(recovered.answer, 'Hello after restart!');
      assert.equal(recovered.executionAttempts, 2);
      assert.equal(calls, 2);
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });
