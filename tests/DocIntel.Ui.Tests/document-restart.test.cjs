const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

test('an uploaded document survives an API crash and resumes under a new fenced attempt',
  { timeout: 120000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.DocumentRestart.Tests-'));
    let api;
    let releaseResponses = false;
    let calls = 0;
    let entered;
    const firstRequest = new Promise(resolve => { entered = resolve; });
    const provider = http.createServer(async (request, response) => {
      const chunks = [];
      for await (const chunk of request) chunks.push(chunk);
      const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
      assert.ok(body.messages.length > 0);
      calls++;
      if (!releaseResponses) {
        entered();
        return;
      }
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify({
        id: 'chatcmpl-document-test', object: 'chat.completion',
        created: Math.floor(Date.now() / 1000), model: 'phi-4',
        choices: [{
          index: 0,
          message: {
            role: 'assistant',
            content: JSON.stringify({ identifiedDocumentName: 'Undefined', confidence: 0.77, matchedRule: null })
          },
          finish_reason: 'stop'
        }],
        usage: { prompt_tokens: 1, completion_tokens: 1, total_tokens: 2 }
      }));
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    const settings = { 'AzureAI:LocalOpenAIEndpoint': `http://127.0.0.1:${provider.address().port}/v1` };
    try {
      api = await startApi(directory, settings);
      const tenantPath = '/api/harness/tenants/contoso-legal';
      const verification = await fetch(api.url + '/api/identity/csrf');
      const csrf = (await verification.json()).token;
      const cookie = verification.headers.get('set-cookie').split(';')[0];
      const protectedHeaders = {
        'Content-Type': 'application/json', 'X-DocIntel-CSRF': csrf, Cookie: cookie
      };
      const initialBudget = await (await fetch(api.url + tenantPath + '/ocr-budget')).json();
      assert.equal((await fetch(api.url + tenantPath + '/ocr-budget', {
        method: 'PUT', headers: { 'Content-Type': 'application/json', Cookie: cookie },
        body: JSON.stringify({ ...initialBudget, monthlyPageLimit: 750 })
      })).status, 400);
      assert.equal((await fetch(api.url + tenantPath + '/ocr-budget', {
        method: 'PUT', headers: protectedHeaders,
        body: JSON.stringify({ ...initialBudget, monthlyPageLimit: 750 })
      })).status, 200);
      assert.equal((await fetch(api.url + tenantPath + '/seed-examples', {
        method: 'POST', headers: protectedHeaders, body: '{}'
      })).status, 200);
      const seeded = await (await fetch(api.url + tenantPath + '/jobs')).json();
      const pdfResponse = await fetch(api.url + tenantPath + `/jobs/${seeded[0].jobId}/slices/0/pdf`);
      assert.equal(pdfResponse.status, 200);
      const pdf = Buffer.from(await pdfResponse.arrayBuffer());
      const tenant = await (await fetch(api.url + tenantPath)).json();
      tenant.defaultModelId = 'foundry-local-phi4';
      tenant.decisionModelId = 'foundry-local-phi4';
      tenant.allowedModels = [...new Set([...tenant.allowedModels, 'foundry-local-phi4'])];
      for (const [name, capability] of Object.entries(tenant.capabilities)) {
        capability.enabled = name === 'SplitAndGroup' || name === 'IdentifyDocuments';
      }
      const configured = await fetch(api.url + tenantPath, {
        method: 'PUT', headers: protectedHeaders, body: JSON.stringify(tenant)
      });
      assert.equal(configured.status, 200);
      Object.assign(tenant, await configured.json());
      const form = new FormData();
      form.set('file', new Blob([pdf], { type: 'application/pdf' }), 'persisted-source.pdf');
      const submitted = await fetch(api.url + tenantPath + '/jobs', {
        method: 'POST', headers: { 'X-DocIntel-CSRF': csrf, Cookie: cookie }, body: form
      });
      assert.equal(submitted.status, 202);
      const accepted = await submitted.json();
      let timer;
      try {
        await Promise.race([firstRequest, new Promise((resolve, reject) => {
          timer = setTimeout(() => reject(new Error(`No native classification request.\n${api.getLogs()}`)), 10000);
        })]);
      } finally {
        clearTimeout(timer);
      }
      const running = await (await fetch(api.url + accepted.statusUrl)).json();
      assert.equal(running.status, 'Running');
      assert.equal(running.executionAttempts, 1);
      assert.equal(running.layoutAvailable, true);
      const firstLayoutResponse = await fetch(api.url + running.layoutUrl);
      assert.equal(firstLayoutResponse.status, 200);
      const firstLayout = await firstLayoutResponse.json();
      assert.ok(firstLayout.pages[0].words.length > 0);
      const changed = structuredClone(tenant);
      changed.capabilities.IdentifyDocuments.enabled = false;
      changed.decisionModelId = 'not-configured-for-this-job';
      assert.equal((await fetch(api.url + tenantPath, {
        method: 'PUT', headers: protectedHeaders, body: JSON.stringify(changed)
      })).status, 200);
      const artifactDirectory = path.join(directory, 'App_Data', 'blobs', 'tenant-contoso-legal');
      const oldSlice = path.join(artifactDirectory, `${accepted.jobId}-a1`, 'slice-1.pdf');
      assert.ok((await fs.stat(oldSlice)).size > 0);

      await killApi(api);
      releaseResponses = true;
      api = await startApi(directory, settings);
      assert.equal((await (await fetch(api.url + tenantPath + '/ocr-budget')).json()).monthlyPageLimit, 750);
      let recovered;
      for (let attempt = 0; attempt < 180; attempt++) {
        const polled = await fetch(api.url + accepted.statusUrl);
        assert.equal(polled.status, 200);
        recovered = await polled.json();
        if (recovered.status === 'Completed' || recovered.status === 'Failed') break;
        await new Promise(resolve => setTimeout(resolve, 500));
      }
      assert.equal(recovered.jobId, accepted.jobId);
      assert.equal(recovered.status, 'Completed', `${recovered.errorMessage || ''}\n${api.getLogs()}`);
      assert.equal(recovered.executionAttempts, 2);
      assert.equal(recovered.appliedHarnessVersion, accepted.appliedHarnessVersion);
      assert.equal(calls, recovered.segments.length + 1);
      assert.equal(recovered.hasDevelopmentResults, false);
      assert.equal(recovered.originalSha256, crypto.createHash('sha256').update(pdf).digest('hex').toUpperCase());
      assert.equal(recovered.layoutAvailable, true);
      const layoutResponse = await fetch(api.url + recovered.layoutUrl);
      assert.equal(layoutResponse.status, 200);
      const layout = await layoutResponse.json();
      assert.equal(layout.originalSha256, recovered.originalSha256);
      assert.equal(layout.documentId, `${recovered.jobId}-a2`);
      assert.deepEqual(layout.pages.map(page => page.words.map(word => word.id)),
        firstLayout.pages.map(page => page.words.map(word => word.id)));
      assert.ok(layout.pages.every(page => page.textSource === 'EmbeddedText' &&
        page.words.every(word => word.ocrConfidence === null)));
      assert.ok(recovered.segments.every(segment => segment.identificationConfidence === 0.77));
      const original = await fetch(api.url + tenantPath + `/jobs/${accepted.jobId}/original`);
      assert.equal(original.status, 200);
      assert.deepEqual(Buffer.from(await original.arrayBuffer()), pdf);
      const currentSlice = await fs.readFile(path.join(artifactDirectory, `${accepted.jobId}-a2`, 'slice-1.pdf'));
      await fs.writeFile(oldSlice, '%PDF-stale-owner');
      await fs.writeFile(path.join(artifactDirectory, `${accepted.jobId}-a1`, 'layout.json'), '{"stale":true}');
      const slice = await fetch(api.url + tenantPath + `/jobs/${accepted.jobId}/slices/0/pdf`);
      assert.equal(slice.status, 200);
      assert.deepEqual(Buffer.from(await slice.arrayBuffer()), currentSlice);
      assert.deepEqual(await (await fetch(api.url + recovered.layoutUrl)).json(), layout);
      assert.equal(recovered.processingOwner, undefined);
      assert.equal(recovered._etag, undefined);
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });
