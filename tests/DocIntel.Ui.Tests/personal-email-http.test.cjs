const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs/promises');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const http = require('node:http');
const { once } = require('node:events');
const { startApi, killApi } = require('./helpers/api.cjs');

function settings() {
  const tenant = crypto.randomUUID();
  return {
    'Identity:Mode': 'Entra', 'Identity:DirectoryTenantId': tenant,
    'Identity:ClientId': crypto.randomUUID(), 'Identity:ApiClientId': crypto.randomUUID(),
    'Identity:ClientSecret': 'isolated-fixture-not-a-real-credential',
    'Identity:PublicOrigin': 'https://docintel.example',
    'Channels:Email:Enabled': true, 'Channels:Email:Provider': 'OutlookCom',
    'Channels:Email:TenantId': tenant, 'Channels:Email:MailboxAddress': 'vault-test@outlook.com',
    'Channels:Email:ApplicationTenantId': 'contoso-legal',
    'Channels:Email:PublicOrigin': 'https://docintel.example',
    'Channels:Email:Personal:ClientId': crypto.randomUUID(),
    'Channels:Email:Personal:ClientSecret': 'isolated-personal-fixture-not-a-real-credential'
  };
}

test('personal mailbox requires a separate OAuth registration and explicitly rejects trusted internal mode',
  { timeout: 45000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.PersonalMail.Guards-'));
    let api;
    try {
      const configuration = settings();
      await assert.rejects(startApi(directory, {
        ...configuration, 'Channels:Email:Personal:ClientId': configuration['Identity:ClientId']
      }), /separate Microsoft OAuth registration/);
      await assert.rejects(startApi(directory, {
        ...configuration, 'Channels:Email:AuthorizationMode': 'TrustedInternal',
        'Channels:Email:CorporateSenderProof': 'x'.repeat(43)
      }), /Email requires a verified directory/);
      await assert.rejects(startApi(directory, {
        ...configuration, 'Channels:Email:Personal:ClientSecret': ''
      }), /Email requires a verified directory/);
      api = await startApi(directory, configuration);
      assert.equal((await fetch(api.url + '/health/live')).status, 200);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('personal connection endpoints require DocIntel authentication and invalid callbacks expose no tokens',
  { timeout: 30000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.PersonalMail.Auth-'));
    let api;
    try {
      api = await startApi(directory, settings());
      const prefix = api.url + '/api/harness/tenants/contoso-legal/email/personal';
      for (const purpose of ['mailbox', 'sender']) {
        for (const [suffix, method] of [['', 'GET'], ['/connect', 'POST'], ['/disconnect', 'POST']]) {
          const response = await fetch(prefix + '/' + purpose + suffix, { method, redirect: 'manual' });
          assert.equal(response.status, 401);
          assert.doesNotMatch(await response.text(), /accessToken|tokenCache|homeAccountId|sender-test/);
        }
      }
      const start = await fetch(api.url + '/auth/email/personal/start?request=' + 'A'.repeat(64),
        { redirect: 'manual' });
      assert.equal(start.status, 401);
      const callback = await fetch(api.url + '/auth/email/personal/callback?code=invalid-fixture&state=invalid-fixture',
        { redirect: 'manual' });
      assert.equal(callback.status, 401);
      assert.match((await callback.json()).error, /not completed/);
      assert.doesNotMatch(callback.headers.get('set-cookie') || '', /\.DocIntel\.Session=/);
      const files = await fs.readdir(path.join(directory, 'App_Data', 'personal-email'))
        .catch(error => { if (error.code === 'ENOENT') return []; throw error; });
      assert.equal(files.filter(file => file.endsWith('.protected')).length, 0);
    } finally {
      await killApi(api);
      await fs.rm(directory, { recursive: true });
    }
  });

test('reader sender operations use current read access while mailbox operations remain administrator-only',
  { timeout: 60000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.PersonalMail.Roles-'));
    const configuration = settings();
    const reader = crypto.randomUUID();
    const admin = crypto.randomUUID();
    const keys = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    const publicKey = { ...keys.publicKey.export({ format: 'jwk' }), kid: 'email-fixture', use: 'sig', alg: 'RS256' };
    let issuer, api;
    const provider = http.createServer((request, response) => {
      const url = new URL(request.url, issuer);
      const result = url.pathname === '/keys' ? { keys: [publicKey] } : {
        issuer, jwks_uri: issuer + '/keys', authorization_endpoint: issuer + '/authorize',
        token_endpoint: issuer + '/token', response_types_supported: ['code'], subject_types_supported: ['public'],
        id_token_signing_alg_values_supported: ['RS256']
      };
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify(result));
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    issuer = `http://127.0.0.1:${provider.address().port}`;
    function token(objectId) {
      const now = Math.floor(Date.now() / 1000);
      const header = Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: publicKey.kid })).toString('base64url');
      const body = Buffer.from(JSON.stringify({
        iss: issuer, aud: configuration['Identity:ApiClientId'],
        tid: configuration['Identity:DirectoryTenantId'], oid: objectId, sub: objectId,
        scp: 'DocIntel.Access', iat: now, nbf: now - 5, exp: now + 600
      })).toString('base64url');
      return `${header}.${body}.` +
        crypto.sign('RSA-SHA256', Buffer.from(`${header}.${body}`), keys.privateKey).toString('base64url');
    }
    async function request(route, objectId, method = 'GET') {
      return fetch(api.url + route, { method, redirect: 'manual',
        headers: { Authorization: `Bearer ${token(objectId)}` } });
    }
    try {
      await fs.writeFile(path.join(directory, 'appsettings.json'), JSON.stringify({
        Identity: { Memberships: [reader, admin].map((objectId, index) => ({
          directoryTenantId: configuration['Identity:DirectoryTenantId'], objectId,
          applicationTenantId: 'contoso-legal', role: index === 0 ? 'Reader' : 'Admin', allDocuments: true
        })) }
      }));
      api = await startApi(directory, {
        ...configuration, 'Identity:AllowLoopbackAuthority': true, 'Identity:LoopbackAuthority': issuer
      });
      const prefix = '/api/harness/tenants/contoso-legal/email/personal';
      const status = await request(prefix + '/sender', reader);
      assert.equal(status.status, 200);
      assert.equal((await status.json()).state, 'NotConnected');
      const connect = await request(prefix + '/sender/connect', reader, 'POST');
      assert.equal(connect.status, 200);
      const result = await connect.json();
      assert.match(result.redirectUrl, /^\/auth\/email\/personal\/start\?request=[A-F0-9]{64}$/);
      const cookieRequired = await request(result.redirectUrl, reader);
      assert.equal(cookieRequired.status, 401, 'the start route requires the original DocIntel browser session');
      assert.equal((await request(prefix + '/mailbox', reader)).status, 403);
      assert.equal((await request(prefix + '/mailbox/connect', reader, 'POST')).status, 403);
      assert.equal((await request(prefix + '/mailbox', admin)).status, 200);
      assert.equal((await request(prefix + '/sender/disconnect', reader, 'POST')).status, 200);
      assert.equal((await request('/api/harness/tenants/another/email/personal/sender', reader)).status, 403);
      const body = await (await request(prefix + '/sender', reader)).json();
      assert.deepEqual(Object.keys(body).sort(), ['address', 'available', 'connected', 'state']);
    } finally {
      await killApi(api);
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });
