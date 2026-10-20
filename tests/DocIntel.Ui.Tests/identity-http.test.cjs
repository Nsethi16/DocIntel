const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

const directoryId = crypto.randomUUID();
const webClientId = crypto.randomUUID();
const apiClientId = crypto.randomUUID();
const readerId = crypto.randomUUID();
const processorId = crypto.randomUUID();
const administratorId = crypto.randomUUID();
const serviceId = crypto.randomUUID();
const keyPair = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
const publicKey = { ...keyPair.publicKey.export({ format: 'jwk' }), kid: 'local-test', use: 'sig', alg: 'RS256' };
const tenantPath = '/api/harness/tenants/contoso-legal';

function signToken(issuer, audience, claims = {}) {
  const now = Math.floor(Date.now() / 1000);
  const header = Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: publicKey.kid })).toString('base64url');
  const body = Buffer.from(JSON.stringify({
    iss: issuer, aud: audience, iat: now, nbf: now - 5, exp: now + 600,
    tid: directoryId, oid: readerId, sub: readerId, ...claims
  })).toString('base64url');
  const signature = crypto.sign('RSA-SHA256', Buffer.from(`${header}.${body}`), keyPair.privateKey)
    .toString('base64url');
  return `${header}.${body}.${signature}`;
}

function cookies(response) {
  return response.headers.getSetCookie ? response.headers.getSetCookie()
    : (response.headers.get('set-cookie') || '').split(/,(?=\s*[^;,]+=)/).filter(Boolean);
}

async function writeRun(directory, id, actor, citations = []) {
  const hash = value => crypto.createHash('sha256').update(value).digest('hex').toUpperCase();
  const folder = path.join(directory, 'App_Data', 'ask-ai-runs', hash('contoso-legal'));
  await fs.mkdir(folder, { recursive: true });
  await fs.writeFile(path.join(folder, `${hash(id)}.json`), JSON.stringify({
    schemaVersion: 1, tenantId: 'contoso-legal', runId: id, question: 'Test question',
    actor: { directoryTenantId: directoryId, objectId: actor },
    status: 'Completed', answer: 'A test-only answer.', citations,
    createdAtUtc: new Date().toISOString()
  }));
}

test('production cannot start with loopback development identity or missing Entra configuration',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Identity.Guards-'));
    try {
      await assert.rejects(startApi(directory, {}, { environment: 'Production' }),
        /LocalDevelopment identity requires Development/);
      await assert.rejects(startApi(directory, { 'Identity:Mode': 'Entra' }, { environment: 'Production' }),
        /Configure the Entra directory tenant/);
      await assert.rejects(startApi(directory, { urls: 'http://0.0.0.0:0' }),
        /explicit loopback-only listening URLs/);
      await assert.rejects(startApi(directory, {
        'AskAi:RunStorageDirectory': path.join(directory, 'wwwroot', 'runs')
      }), /outside the web root/);
    } finally {
      await fs.rm(directory, { recursive: true });
    }
  });

test('HTTP request limits reject excess requests instead of queueing unbounded work',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.RateLimit.Tests-'));
    let api;
    try {
      api = await startApi(directory, { 'Identity:ReadRequestsPerMinute': 2 });
      const url = api.url + '/api/harness/tenants';
      assert.equal((await fetch(url)).status, 200);
      assert.equal((await fetch(url)).status, 200);
      const rejected = await fetch(url);
      assert.equal(rejected.status, 429);
      assert.ok(Number(rejected.headers.get('retry-after')) > 0);
      assert.match((await rejected.json()).error, /request limit/i);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('verified bearer and browser identities enforce tenant, document, run, and CSRF boundaries',
  { timeout: 120000 }, async t => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Identity.Tests-'));
    const codes = new Map();
    const providerErrors = [];
    let api;
    let issuer;
    let allowedDocumentId;
    let documentSearches = 0;
    let revokeAfterSearch = false;
    let readerToken;
    const memberships = [
      { directoryTenantId: directoryId, objectId: readerId, applicationTenantId: 'contoso-legal',
        role: 'Reader', allDocuments: false, documentIds: [] },
      { directoryTenantId: directoryId, objectId: processorId, applicationTenantId: 'contoso-legal',
        role: 'Processor', allDocuments: true },
      { directoryTenantId: directoryId, objectId: administratorId, applicationTenantId: 'contoso-legal',
        role: 'Admin', allDocuments: true },
      { directoryTenantId: directoryId, objectId: serviceId, applicationTenantId: 'contoso-legal',
        role: 'Processor', allDocuments: true, servicePrincipal: true }
    ];
    async function saveMemberships() {
      await fs.writeFile(path.join(directory, 'appsettings.json'),
        JSON.stringify({ Identity: { Memberships: memberships } }));
    }
    const provider = http.createServer(async (request, response) => {
      try {
        const url = new URL(request.url, issuer);
        let result;
        if (url.pathname === '/.well-known/openid-configuration') {
          result = {
            issuer, authorization_endpoint: `${issuer}/authorize`, token_endpoint: `${issuer}/token`,
            jwks_uri: `${issuer}/keys`, response_types_supported: ['code'], subject_types_supported: ['public'],
            id_token_signing_alg_values_supported: ['RS256'],
            token_endpoint_auth_methods_supported: ['client_secret_post', 'client_secret_basic'],
            code_challenge_methods_supported: ['S256']
          };
        } else if (url.pathname === '/keys') {
          result = { keys: [publicKey] };
        } else if (url.pathname === '/authorize') {
          const code = crypto.randomUUID();
          codes.set(code, {
            nonce: url.searchParams.get('nonce'), challenge: url.searchParams.get('code_challenge'),
            redirectUri: url.searchParams.get('redirect_uri')
          });
          const callback = new URL(url.searchParams.get('redirect_uri'));
          callback.searchParams.set('code', code);
          callback.searchParams.set('state', url.searchParams.get('state'));
          response.writeHead(302, { Location: callback.href });
          response.end();
          return;
        } else {
          const chunks = [];
          for await (const chunk of request) chunks.push(chunk);
          const text = Buffer.concat(chunks).toString('utf8');
          if (url.pathname === '/token') {
            const form = new URLSearchParams(text);
            const issued = codes.get(form.get('code'));
            assert.ok(issued);
            assert.equal(crypto.createHash('sha256').update(form.get('code_verifier')).digest('base64url'),
              issued.challenge);
            assert.equal(form.get('redirect_uri'), issued.redirectUri);
            result = {
              token_type: 'Bearer', expires_in: 600,
              access_token: signToken(issuer, apiClientId, { scp: 'DocIntel.Access' }),
              id_token: signToken(issuer, webClientId, { nonce: issued.nonce })
            };
          } else if (url.pathname === '/model/v1/chat/completions') {
            const body = JSON.parse(text);
            const question = body.messages.find(message => message.role === 'user')?.content || '';
            const toolResults = body.messages.filter(message => message.role === 'tool');
            let message;
            if (toolResults.some(result => String(result.content).startsWith('Error:'))) {
              message = { role: 'assistant', content: JSON.stringify({
                kind: 'NoEvidence', answer: 'Supporting evidence is not available.', evidenceIds: []
              }) };
            } else if (!question.includes('accessible document')) {
              message = { role: 'assistant', content: JSON.stringify({
                kind: 'Conversation', answer: 'Hello from the local identity fixture.', evidenceIds: []
              }) };
            } else if (toolResults.length === 0) {
              documentSearches++;
              message = { role: 'assistant', tool_calls: [{
                id: 'search', type: 'function', function: { name: 'search_documents',
                  arguments: JSON.stringify({ query: { pageSize: 20, authorizedJobIds: ['not-authorized'] } }) }
              }] };
            } else if (toolResults.length === 1) {
              const page = JSON.parse(toolResults[0].content);
              const documents = page.documents || page.Documents;
              assert.equal(documents.length, 1);
              assert.equal(documents[0].jobId || documents[0].JobId, allowedDocumentId);
              if (revokeAfterSearch) {
                memberships[0].documentIds = [];
                await saveMemberships();
                for (let attempt = 0; attempt < 60; attempt++) {
                  const visible = await (await fetch(api.url + tenantPath + '/jobs', {
                    headers: { Authorization: `Bearer ${readerToken}` }
                  })).json();
                  if (visible.length === 0) break;
                  await new Promise(resolve => setTimeout(resolve, 100));
                }
              }
              message = { role: 'assistant', tool_calls: [{
                id: 'inspect', type: 'function', function: { name: 'inspect_document_slice',
                  arguments: JSON.stringify({ jobId: allowedDocumentId, sliceIndex: 0, textOffset: 0 }) }
              }] };
            } else {
              const evidence = JSON.parse(toolResults[1].content);
              message = { role: 'assistant', content: JSON.stringify({
                kind: 'DocumentAnswer', answer: 'This accessible document is supported by its source [1].',
                evidenceIds: [evidence.evidenceId]
              }) };
            }
            result = {
              id: 'chatcmpl-local-identity', object: 'chat.completion', created: Math.floor(Date.now() / 1000),
              model: 'phi-4', choices: [{ index: 0, message,
                finish_reason: message.tool_calls ? 'tool_calls' : 'stop' }]
            };
          } else {
            response.writeHead(404); response.end(); return;
          }
        }
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify(result));
      } catch (error) {
        providerErrors.push(error.message);
        response.writeHead(500); response.end('Local fixture failed.');
      }
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    issuer = `http://127.0.0.1:${provider.address().port}`;
    try {
      await saveMemberships();
      api = await startApi(directory, {
        'Identity:Mode': 'Entra', 'Identity:DirectoryTenantId': directoryId,
        'Identity:ClientId': webClientId, 'Identity:ApiClientId': apiClientId,
        'Identity:AllowLoopbackAuthority': true, 'Identity:LoopbackAuthority': issuer,
        'AzureAI:LocalOpenAIEndpoint': `${issuer}/model/v1`
      }, { env: { Identity__ClientSecret: crypto.randomBytes(48).toString('base64url') } });
      const reader = signToken(issuer, apiClientId, { scp: 'DocIntel.Access' });
      readerToken = reader;
      const processor = signToken(issuer, apiClientId, { oid: processorId, sub: processorId, scp: 'DocIntel.Access' });
      const administrator = signToken(issuer, apiClientId, {
        oid: administratorId, sub: administratorId, scp: 'DocIntel.Access'
      });
      const request = (pathname, token, method = 'GET', body) => fetch(api.url + pathname, {
        method, headers: { ...(token ? { Authorization: `Bearer ${token}` } : {}),
          ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}) },
        body: body === undefined ? undefined : JSON.stringify(body), redirect: 'manual'
      });

      await t.test('anonymous and malformed, expired, wrong-audience or wrong-directory tokens are rejected', async () => {
        assert.equal((await request('/api/harness/tenants')).status, 401);
        for (const token of [
          'not-a-token',
          signToken(issuer, webClientId, { scp: 'DocIntel.Access' }),
          signToken(issuer, apiClientId, { scp: 'DocIntel.Access', exp: Math.floor(Date.now() / 1000) - 120 }),
          signToken(issuer, apiClientId, { scp: 'DocIntel.Access', tid: crypto.randomUUID() }),
          signToken(issuer, apiClientId, { roles: ['DocIntel.Access'] }),
          signToken(issuer, apiClientId, { scp: 'Unrelated.Permission' })
        ]) {
          assert.equal((await request('/api/harness/tenants', token)).status, 401);
        }
        const forged = reader.split('.');
        const payload = JSON.parse(Buffer.from(forged[1], 'base64url').toString('utf8'));
        payload.oid = administratorId;
        forged[1] = Buffer.from(JSON.stringify(payload)).toString('base64url');
        assert.equal((await request('/api/harness/tenants', forged.join('.'))).status, 401);
        assert.equal((await fetch(api.url + '/api/harness/tenants', {
          headers: { 'X-MS-CLIENT-PRINCIPAL': Buffer.from(JSON.stringify({
            tid: directoryId, oid: administratorId, roles: ['Admin']
          })).toString('base64') }
        })).status, 401);
      });
      await t.test('directory identity maps to explicit app memberships, not the tid GUID or asserted Admin role', async () => {
        const tenants = await (await request('/api/harness/tenants', reader)).json();
        assert.deepEqual(tenants.map(tenant => tenant.tenantId), ['contoso-legal']);
        assert.equal((await request('/api/harness/tenants/fabrikam-finance', reader)).status, 403);
        assert.equal((await request(`/api/harness/tenants/${directoryId}`, reader)).status, 403);
        const claimedAdministrator = signToken(issuer, apiClientId, { scp: 'DocIntel.Access', roles: ['Admin'] });
        assert.equal((await request(tenantPath, claimedAdministrator, 'PUT', {})).status, 403);
        assert.equal((await request('/api/harness/catalog', reader)).status, 403);
        assert.equal((await request(tenantPath, administrator, 'PUT', {})).status, 400);
      });
      let tenant = await (await request(tenantPath, administrator)).json();
      tenant.defaultModelId = 'foundry-local-phi4';
      tenant.allowedModels = [...new Set([...tenant.allowedModels, 'foundry-local-phi4'])];
      tenant.capabilities.AskAi.modelIdOverride = 'foundry-local-phi4';
      assert.equal((await request(tenantPath, administrator, 'PUT', tenant)).status, 200);
      assert.equal((await request(tenantPath + '/seed-examples', processor, 'POST', {})).status, 200);
      const jobs = await (await request(tenantPath + '/jobs', processor)).json();
      assert.ok(jobs.length >= 2);
      allowedDocumentId = jobs[0].jobId;
      const forbiddenDocumentId = jobs[1].jobId;

      await t.test('empty document grants yield no list or graph data and cannot upload or inspect documents', async () => {
        assert.deepEqual(await (await request(tenantPath + '/jobs', reader)).json(), []);
        assert.equal((await (await request(tenantPath + '/graph', reader)).json()).totalDocumentsInTenant, 0);
        assert.equal((await request(tenantPath + '/jobs', reader, 'POST', {})).status, 403);
        assert.equal((await request(`/api/harness/jobs/${allowedDocumentId}`, reader)).status, 404);
        assert.equal((await request(`${tenantPath}/jobs/${allowedDocumentId}/slices/0/pdf`, reader)).status, 404);
      });
      memberships[0].documentIds = [allowedDocumentId];
      await saveMemberships();
      for (let attempt = 0; attempt < 60; attempt++) {
        const visible = await (await request(tenantPath + '/jobs', reader)).json();
        if (visible.length === 1) break;
        await new Promise(resolve => setTimeout(resolve, 100));
      }
      await t.test('allowed document lists, graph, point lookup and PDF access share the same boundaries', async () => {
        const visible = await (await request(tenantPath + '/jobs', reader)).json();
        assert.deepEqual(visible.map(job => job.jobId), [allowedDocumentId]);
        const graph = await (await request(tenantPath + '/graph', reader)).json();
        assert.equal(graph.totalDocumentsInTenant, 1);
        assert.ok(graph.docNodes.every(node => node.jobId === allowedDocumentId));
        assert.equal((await request(`/api/harness/jobs/${allowedDocumentId}`, reader)).status, 200);
        assert.equal((await request(`/api/harness/jobs/${forbiddenDocumentId}`, reader)).status, 404);
        assert.equal((await request(`${tenantPath}/jobs/${allowedDocumentId}/slices/0/pdf`, reader)).status, 200);
        assert.equal((await request(`${tenantPath}/jobs/${forbiddenDocumentId}/slices/0/pdf`, reader)).status, 404);
      });
      await t.test('model arguments cannot broaden the server-owned document search scope', async () => {
        const response = await request(tenantPath + '/ask', reader, 'POST', {
          question: 'Find an accessible document.'
        });
        const answer = await response.json();
        assert.equal(response.status, 200, JSON.stringify(answer));
        assert.equal(answer.citations.length, 1);
        assert.equal(answer.citations[0].jobId, allowedDocumentId);
        assert.ok(answer.executedCosmosQueries[0].includes('@authorizedJobIds'));
        assert.equal(documentSearches, 1);
      });
      await t.test('run polling protects ownership and reevaluates document permissions', async () => {
        await writeRun(directory, 'owned-by-another-user', processorId);
        await writeRun(directory, 'owned-by-reader', readerId);
        await writeRun(directory, 'now-forbidden-citation', readerId, [{ jobId: forbiddenDocumentId }]);
        assert.equal((await request(tenantPath + '/ask/runs/owned-by-another-user', reader)).status, 404);
        assert.equal((await request(tenantPath + '/ask/runs/owned-by-reader', reader)).status, 200);
        assert.equal((await request(tenantPath + '/ask/runs/now-forbidden-citation', reader)).status, 404);
        assert.equal((await request(tenantPath + '/ask/runs/owned-by-another-user', administrator)).status, 200);
      });
      await t.test('only an explicitly mapped service principal can use app-only access grants', async () => {
        const service = signToken(issuer, apiClientId, { oid: serviceId, sub: serviceId, roles: ['DocIntel.Access'] });
        assert.equal((await request(tenantPath + '/ask/webhooks/email', service, 'POST',
          { body: 'Hi', from: 'untrusted-sender@example.invalid' })).status, 200);
        const unknown = signToken(issuer, apiClientId, {
          oid: crypto.randomUUID(), roles: ['DocIntel.Access']
        });
        assert.equal((await request(tenantPath + '/ask/webhooks/email', unknown, 'POST',
          { body: 'Hi' })).status, 401);
        assert.equal((await request(tenantPath + '/ask/webhooks/email', processor, 'POST',
          { body: 'Hi', from: 'not-a-verified-identity@example.invalid' })).status, 403);
      });
      await t.test('document access revoked between search and inspection stops the answer', async () => {
        revokeAfterSearch = true;
        const response = await request(tenantPath + '/ask', reader, 'POST', {
          question: 'Find another accessible document.'
        });
        assert.equal(response.status, 502);
        const failure = await response.json();
        const run = await (await request(tenantPath + '/ask/runs/' + failure.runId, reader)).json();
        assert.equal(run.status, 'Failed');
        assert.equal(run.errorCode, 'AccessRevoked');
        assert.equal(run.answer, null);
        assert.deepEqual(run.citations, []);
        revokeAfterSearch = false;
        memberships[0].documentIds = [allowedDocumentId];
        await saveMemberships();
      });
      await t.test('browser code flow uses PKCE, an HttpOnly session, CSRF protection and safe sign-out', async () => {
        const jar = new Map();
        async function browser(url, options = {}) {
          const headers = new Headers(options.headers);
          if (url.startsWith(api.url) && jar.size) headers.set('Cookie',
            [...jar].map(([name, value]) => `${name}=${value}`).join('; '));
          const response = await fetch(url, { ...options, headers, redirect: 'manual' });
          if (url.startsWith(api.url)) {
            for (const cookie of cookies(response)) {
              const [name, ...value] = cookie.split(';')[0].split('=');
              jar.set(name.trim(), value.join('='));
            }
          }
          return response;
        }
        assert.equal((await browser(api.url + '/auth/login?returnUrl=https://example.invalid')).status, 400);
        let redirect = await browser(api.url + '/auth/login?returnUrl=%2F');
        assert.equal(redirect.status, 302);
        redirect = await browser(redirect.headers.get('location'));
        assert.equal(redirect.status, 302);
        const signedIn = await browser(redirect.headers.get('location'));
        assert.equal(signedIn.status, 302);
        assert.ok(cookies(signedIn).some(cookie => cookie.startsWith('.DocIntel.Session=') && /httponly/i.test(cookie)));
        assert.equal((await browser(api.url + tenantPath)).status, 200);
        assert.equal((await browser(api.url + tenantPath + '/ask/runs', {
          method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ question: 'Hi' })
        })).status, 400);
        const verification = await browser(api.url + '/api/identity/csrf');
        const token = (await verification.json()).token;
        assert.equal((await browser(api.url + tenantPath + '/ask/runs', {
          method: 'POST', headers: { 'Content-Type': 'application/json', 'X-DocIntel-CSRF': token },
          body: JSON.stringify({ question: 'Hi' })
        })).status, 202);
        assert.equal((await browser(api.url + '/auth/logout', { method: 'POST' })).status, 400);
        assert.equal((await browser(api.url + '/auth/logout', {
          method: 'POST', headers: { 'X-DocIntel-CSRF': token }
        })).status, 200);
        assert.equal((await browser(api.url + tenantPath)).status, 401);
        assert.equal((await browser(api.url + '/auth/signed-out')).status, 200);
      });
      await t.test('reads and harness saves do not fabricate new prompt results', async () => {
        tenant = await (await request(tenantPath, administrator)).json();
        tenant.capabilities.PromptColumns.promptColumns.columns.push({
          columnId: 'must-not-run-on-read', columnName: 'No implicit evaluation',
          promptInstructions: 'Summarize this document.', isActive: true
        });
        assert.equal((await request(tenantPath, administrator, 'PUT', tenant)).status, 200);
        const after = await (await request(tenantPath + '/jobs', processor)).json();
        assert.ok(after.every(job => !(job.promptColumnResults || [])
          .some(result => result.columnId === 'must-not-run-on-read')));
      });
      assert.deepEqual(providerErrors, []);
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });
