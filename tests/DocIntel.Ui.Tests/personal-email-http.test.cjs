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
