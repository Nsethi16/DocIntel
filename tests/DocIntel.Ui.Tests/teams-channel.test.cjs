const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

function settings() {
  const directory = crypto.randomUUID();
  return {
    'Identity:Mode': 'Entra',
    'Identity:DirectoryTenantId': directory,
    'Identity:ClientId': crypto.randomUUID(),
    'Identity:ApiClientId': crypto.randomUUID(),
    'Identity:ClientSecret': 'isolated-fixture-not-a-real-credential',
    'Identity:PublicOrigin': 'https://docintel.example',
    'Channels:Teams:Enabled': true,
    'Channels:Teams:TenantId': directory,
    'Channels:Teams:ClientId': crypto.randomUUID(),
    'Channels:Teams:ApplicationTenantId': 'contoso-legal',
    'Channels:Teams:ServiceUrl': 'https://smba.trafficmanager.net/teams/',
    'Channels:Teams:PublicOrigin': 'https://docintel.example'
  };
}

test('Teams stays absent by default and cannot enable anonymous or mismatched bot authentication',
  { timeout: 45000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Teams.Guards-'));
    let api;
    try {
      api = await startApi(directory);
      assert.equal((await fetch(api.url + '/api/messages', { method: 'POST' })).status, 404);
      await killApi(api);
      api = null;
      await assert.rejects(startApi(directory, {
        ...settings(), 'Channels:Teams:DangerouslyAllowUnauthenticatedRequests': true
      }), /Authentication bypass is not supported/);
      await assert.rejects(startApi(directory, {
        ...settings(), 'Channels:Teams:TenantId': crypto.randomUUID()
      }), /matching Entra directory/);
      await assert.rejects(startApi(directory, {
        ...settings(), 'Channels:Teams:ServiceUrl': 'https://attacker.example/'
      }), /smba.trafficmanager.net/);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('the native SDK route rejects anonymous and malformed bearer requests without replacing browser or API auth',
  { timeout: 45000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.Teams.Auth-'));
    let api;
    try {
      const root = path.join(directory, 'wwwroot');
      await fs.mkdir(root);
      const web = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
      for (const name of ['ask-run.html', 'ask-run.js', 'ask-run.css', 'identity.js']) {
        await fs.copyFile(path.join(web, name), path.join(root, name));
      }
      api = await startApi(directory, settings());
      const activity = { type: 'message', text: 'A question', channelId: 'msteams' };
      for (const authorization of [null, 'Bearer not-a-token']) {
        const headers = { 'content-type': 'application/json' };
        if (authorization) headers.authorization = authorization;
        const rejected = await fetch(api.url + '/api/messages', {
          method: 'POST', headers, body: JSON.stringify(activity)
        });
        assert.equal(rejected.status, 401);
      }
      assert.equal((await fetch(api.url + '/api/identity/me')).status, 401);
      assert.equal((await fetch(api.url + '/api/harness/tenants')).status, 401);
      const page = await fetch(api.url + '/ask-run.html?tenant=contoso-legal&run=one');
      assert.equal(page.status, 200);
      assert.match(page.headers.get('content-security-policy'), /script-src 'self'/);
      assert.match(page.headers.get('cache-control'), /no-store/);
      assert.match(await page.text(), /identity.js/);
      assert.doesNotMatch(api.getLogs(), /A question/);
      const snapshots = await fs.readdir(path.join(directory, 'App_Data', 'ask-ai-runs'), { recursive: true })
        .catch(error => { if (error.code === 'ENOENT') return []; throw error; });
      assert.equal(snapshots.filter(name => name.endsWith('.json')).length, 0);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });
