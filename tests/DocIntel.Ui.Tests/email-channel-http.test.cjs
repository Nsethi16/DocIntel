const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

function settings() {
  const tenant = crypto.randomUUID();
  return {
    'Identity:Mode': 'Entra', 'Identity:DirectoryTenantId': tenant,
    'Identity:ClientId': crypto.randomUUID(), 'Identity:ApiClientId': crypto.randomUUID(),
    'Identity:ClientSecret': 'isolated-fixture-not-a-real-credential',
    'Identity:PublicOrigin': 'https://docintel.example',
    'Channels:Email:Enabled': true, 'Channels:Email:TenantId': tenant,
    'Channels:Email:MailboxObjectId': crypto.randomUUID(),
    'Channels:Email:MailboxAddress': 'agent@example.com',
    'Channels:Email:ApplicationTenantId': 'contoso-legal',
    'Channels:Email:PublicOrigin': 'https://docintel.example'
  };
}

test('email stays disabled by default and refuses mismatched identities or unsafe public origins',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Email.Guards-'));
    let api;
    try {
      api = await startApi(directory);
      assert.equal((await fetch(api.url + '/api/harness/tenants/contoso-legal/ask/email/runs/missing')).status, 404);
      await killApi(api);
      api = null;
      await assert.rejects(startApi(directory, { ...settings(), 'Identity:Mode': 'LocalDevelopment' }),
        /Email delivery requires matching production Entra identity/);
      await assert.rejects(startApi(directory, { ...settings(), 'Channels:Email:TenantId': crypto.randomUUID() }),
        /Email delivery requires matching production Entra identity/);
      await assert.rejects(startApi(directory, { ...settings(), 'Channels:Email:PublicOrigin': 'http://127.0.0.1:5096' }),
        /Email requires a verified directory/);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('configured email confirmation requires a verified user and does not expose questions through anonymous links',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Email.Auth-'));
    let api;
    try {
      const root = path.join(directory, 'wwwroot');
      await fs.mkdir(root);
      const web = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
      for (const name of ['email-question.html', 'email-question.js', 'identity.js', 'ask-run.css']) {
        await fs.copyFile(path.join(web, name), path.join(root, name));
      }
      api = await startApi(directory, settings());
      const endpoint = api.url + '/api/harness/tenants/contoso-legal/ask/email/runs/one';
      for (const method of ['GET', 'POST']) {
        const response = await fetch(endpoint + (method === 'POST' ? '/confirm' : ''), { method });
        assert.equal(response.status, 401);
        assert.doesNotMatch(await response.text(), /destination|pendingEmail|questionText/);
      }
      const page = await fetch(api.url + '/email-question.html?tenant=contoso-legal&run=one');
      assert.equal(page.status, 200);
      assert.match(page.headers.get('content-security-policy'), /script-src 'self'/);
      assert.match(page.headers.get('cache-control'), /no-store/);
      assert.match(await page.text(), /Opening this page does not run the agent/);
      const snapshots = await fs.readdir(path.join(directory, 'App_Data', 'ask-ai-runs'), { recursive: true })
        .catch(error => { if (error.code === 'ENOENT') return []; throw error; });
      assert.equal(snapshots.filter(file => file.endsWith('.json')).length, 0);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });
