const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

async function copyPages(directory) {
  const root = path.join(directory, 'wwwroot');
  await fs.mkdir(root);
  const source = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
  for (const name of ['index.html', 'admin.html', 'review.html', 'ask-run.html', 'identity.js']) {
    await fs.copyFile(path.join(source, name), path.join(root, name));
  }
}

function assertHeaders(response) {
  assert.equal(response.headers.get('x-content-type-options'), 'nosniff');
  assert.equal(response.headers.get('x-frame-options'), 'DENY');
  assert.equal(response.headers.get('referrer-policy'), 'no-referrer');
  assert.match(response.headers.get('permissions-policy'), /camera=\(\)/);
  assert.match(response.headers.get('content-security-policy'), /frame-ancestors 'none'/);
  assert.ok(response.headers.get('x-docintel-correlation-id'));
}

function oversizedHeaders(endpoint, headers) {
  return new Promise((resolve, reject) => {
    const request = http.request(endpoint, { method: 'POST', headers: {
      ...headers, 'Content-Length': 26 * 1024 * 1024 + 1, Expect: '100-continue', Connection: 'close'
    } });
    request.on('error', reject);
    request.setTimeout(10000, () => request.destroy(new Error('Oversized request was not rejected promptly.')));
    request.on('continue', () =>
      request.destroy(new Error('The server accepted an oversized body before enforcing its limit.')));
    request.on('response', response => {
      const chunks = [];
      response.on('data', chunk => chunks.push(chunk));
      response.on('error', reject);
      response.on('end', () => {
        resolve({ status: response.statusCode, headers: new Headers(response.headers),
          body: Buffer.concat(chunks).toString('utf8') });
        request.destroy();
      });
    });
    request.flushHeaders();
  });
}

test('browser policies cover static pages, API results and missing resources without breaking legacy scripts',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Http.Headers-'));
    let api;
    try {
      await copyPages(directory);
      api = await startApi(directory);
      for (const route of ['/', '/index.html', '/admin.html', '/review.html', '/ask-run.html',
        '/api/harness/tenants', '/not-a-real-route']) {
        const response = await fetch(api.url + route);
        assertHeaders(response);
        assert.equal(response.headers.get('strict-transport-security'), null);
        const policy = response.headers.get('content-security-policy');
        if (['/', '/index.html', '/admin.html'].includes(route)) {
          assert.match(policy, /script-src 'self' 'unsafe-inline'/);
          assert.match(policy, /frame-src 'self' blob:/);
          assert.match(policy, /https:\/\/fonts\.googleapis\.com/);
        } else {
          assert.doesNotMatch(policy, /unsafe-inline|unsafe-eval/);
        }
        if (route === '/review.html') {
          assert.match(policy, /script-src 'self' https:\/\/cdnjs\.cloudflare\.com/);
          assert.match(policy, /worker-src 'self' blob: https:\/\/cdnjs\.cloudflare\.com/);
        }
      }
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('unexpected failures return only safe ProblemDetails and a generated correlation ID, including in Development',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Http.Errors-'));
    let api;
    try {
      api = await startApi(directory);
      const url = api.url + '/api/harness/tenants';
      assert.equal((await fetch(url)).status, 200);
      const secretMarker = 'PRIVATE_PROVIDER_PAYLOAD_71903';
      await fs.writeFile(path.join(directory, 'App_Data', 'agent-harness-settings.json'),
        `{"${secretMarker}": "http://private-internal-provider/token", "incomplete":`);
      const response = await fetch(url, { headers: { 'X-DocIntel-Correlation-Id': secretMarker } });
      assert.equal(response.status, 500);
      assertHeaders(response);
      assert.match(response.headers.get('content-type'), /application\/problem\+json/);
      assert.match(response.headers.get('cache-control'), /no-store/);
      const problem = await response.json();
      assert.equal(problem.status, 500);
      assert.equal(problem.traceId, response.headers.get('x-docintel-correlation-id'));
      assert.notEqual(problem.traceId, secretMarker);
      assert.match(problem.detail, /correlation ID/);
      assert.doesNotMatch(JSON.stringify(problem), /PRIVATE_PROVIDER|private-internal|JsonException|StackTrace|App_Data/);
      assert.doesNotMatch(api.getLogs(), /PRIVATE_PROVIDER|private-internal/);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('malformed JSON and oversized request bodies are rejected without provider details or creating work',
  { timeout: 45000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Http.Limits-'));
    let api;
    try {
      api = await startApi(directory);
      const verification = await fetch(api.url + '/api/identity/csrf');
      const csrf = (await verification.json()).token;
      const cookie = verification.headers.get('set-cookie').split(';')[0];
      const headers = { 'Content-Type': 'application/json', Cookie: cookie, 'X-DocIntel-CSRF': csrf };
      const csrfDeletion = await fetch(api.url + '/api/harness/tenants/contoso-legal/jobs/not-created', {
        method: 'DELETE', headers: { Cookie: cookie, 'X-DocIntel-Confirm-Delete': 'not-created' }
      });
      assert.equal(csrfDeletion.status, 400);
      assert.match((await csrfDeletion.json()).error, /verification token/);
      const endpoint = api.url + '/api/harness/tenants/contoso-legal/ask/runs';
      const invalid = await fetch(endpoint, {
        method: 'POST', headers, body: '{"question": "PRIVATE_REQUEST_MARKER",'
      });
      assert.equal(invalid.status, 400);
      assertHeaders(invalid);
      assert.doesNotMatch(await invalid.text(), /PRIVATE_REQUEST_MARKER|JsonException|BytePosition|StackTrace/);
      const large = await oversizedHeaders(endpoint, headers);
      assert.equal(large.status, 413);
      assertHeaders(large);
      assert.equal(JSON.parse(large.body).status, 413);
      const snapshots = await fs.readdir(path.join(directory, 'App_Data', 'ask-ai-runs'), { recursive: true })
        .catch(error => { if (error.code === 'ENOENT') return []; throw error; });
      assert.equal(snapshots.filter(name => name.endsWith('.json')).length, 0);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('production responses advertise HTTPS transport policy without extending it to unrelated subdomains',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Http.Production-'));
    let api;
    try {
      await copyPages(directory);
      api = await startApi(directory, {
        'Identity:Mode': 'Entra', 'Identity:DirectoryTenantId': crypto.randomUUID(),
        'Identity:ClientId': crypto.randomUUID(), 'Identity:ApiClientId': crypto.randomUUID(),
        'Identity:PublicOrigin': 'https://docintel.example',
        'Identity:ClientSecret': 'isolated-fixture-not-a-real-credential',
        'Identity:DataProtectionDirectory': path.join(directory, 'identity-keys'),
        'Models:AllowDevelopmentSimulation': false
      }, { environment: 'Production' });
      const response = await fetch(api.url + '/ask-run.html');
      assert.equal(response.status, 200);
      assertHeaders(response);
      assert.equal(response.headers.get('strict-transport-security'), 'max-age=31536000');
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });
