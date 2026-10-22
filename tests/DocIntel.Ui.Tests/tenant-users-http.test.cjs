const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

test('directory-backed tenant membership controls HTML, APIs, roles and revocation across restarts',
  { timeout: 120000 }, async t => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.TenantUsers-'));
    const tenantId = crypto.randomUUID();
    const apiId = crypto.randomUUID();
    const webId = crypto.randomUUID();
    const admin = crypto.randomUUID();
    const user = crypto.randomUUID();
    const secondAdmin = crypto.randomUUID();
    const outsider = crypto.randomUUID();
    const keys = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
    const publicKey = { ...keys.publicKey.export({ format: 'jwk' }), kid: 'directory-fixture', alg: 'RS256', use: 'sig' };
    const people = [admin, user, secondAdmin].map((id, index) => ({
      id, displayName: index === 1 ? '<img src=x onerror=bad()> Employee' : `Employee ${index}`,
      mail: `employee${index}@example.com`, userPrincipalName: `employee${index}@example.com`,
      jobTitle: 'Analyst', department: 'Operations', officeLocation: 'Office',
      accountEnabled: true, userType: 'Member'
    }));
    let issuer;
    let api;
    let failDirectory = false;
    const codes = new Map();
    const providerErrors = [];
    const provider = http.createServer(async (request, response) => {
      try {
      const url = new URL(request.url, issuer);
      let result;
      if (url.pathname === '/.well-known/openid-configuration')
        result = { issuer, jwks_uri: issuer + '/keys', authorization_endpoint: issuer + '/authorize',
          token_endpoint: issuer + '/token', response_types_supported: ['code'], subject_types_supported: ['public'],
          id_token_signing_alg_values_supported: ['RS256'] };
      else if (url.pathname === '/keys') result = { keys: [publicKey] };
      else if (url.pathname === '/authorize') {
        const code = crypto.randomUUID();
        codes.set(code, { nonce: url.searchParams.get('nonce'), challenge: url.searchParams.get('code_challenge'),
          redirectUri: url.searchParams.get('redirect_uri') });
        const callback = new URL(url.searchParams.get('redirect_uri'));
        callback.searchParams.set('code', code);
        callback.searchParams.set('state', url.searchParams.get('state'));
        response.writeHead(302, { Location: callback.href }); response.end(); return;
      } else if (url.pathname === '/token') {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const form = new URLSearchParams(Buffer.concat(chunks).toString('utf8'));
        const code = codes.get(form.get('code'));
        assert.ok(code);
        assert.equal(crypto.createHash('sha256').update(form.get('code_verifier')).digest('base64url'), code.challenge);
        assert.equal(form.get('redirect_uri'), code.redirectUri);
        result = { token_type: 'Bearer', expires_in: 600, access_token: token(admin),
          id_token: token(admin, webId, { nonce: code.nonce }) };
      }
      else if (url.pathname.startsWith('/graph/users')) {
        if (failDirectory) { response.writeHead(503); response.end('PRIVATE_DIRECTORY_DIAGNOSTIC'); return; }
        if (url.pathname === '/graph/users') {
          result = { value: people.filter(person => person.accountEnabled).slice(0, 20) };
        } else {
          const id = url.pathname.split('/')[3];
          const person = people.find(person => person.id === id);
          if (!person) { response.writeHead(404); response.end(); return; }
          if (url.pathname.endsWith('/$value')) {
            response.writeHead(200, { 'Content-Type': 'image/png' });
            response.end(Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j2ioAAAAASUVORK5CYII=', 'base64'));
            return;
          }
          result = person;
        }
      } else { response.writeHead(404); response.end(); return; }
      response.writeHead(200, { 'Content-Type': 'application/json' });
      response.end(JSON.stringify(result));
      } catch (error) {
        providerErrors.push(error.message);
        response.writeHead(500); response.end('Local directory fixture failed.');
      }
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    issuer = `http://127.0.0.1:${provider.address().port}`;
    function token(id, audience = apiId, extra = {}) {
      const now = Math.floor(Date.now() / 1000);
      const header = Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: publicKey.kid })).toString('base64url');
      const body = Buffer.from(JSON.stringify({
        iss: issuer, aud: audience, tid: tenantId, oid: id, sub: id, scp: 'DocIntel.Access',
        iat: now, nbf: now - 5, exp: now + 600, ...extra
      })).toString('base64url');
      return header + '.' + body + '.' + crypto.sign('RSA-SHA256', Buffer.from(header + '.' + body),
        keys.privateKey).toString('base64url');
    }
    const base = '/api/harness/tenants/contoso-legal';
    function request(route, id = admin, method = 'GET', body) {
      return fetch(api.url + route, { method, redirect: 'manual',
        headers: { ...(id ? { Authorization: `Bearer ${token(id)}` } : {}),
          ...(body ? { 'Content-Type': 'application/json' } : {}) },
        body: body ? JSON.stringify(body) : undefined });
    }
    const settings = {
      'Identity:Mode': 'Entra', 'Identity:DirectoryTenantId': tenantId,
      'Identity:ClientId': webId, 'Identity:ApiClientId': apiId,
      'Identity:AllowLoopbackAuthority': true, 'Identity:LoopbackAuthority': issuer,
      'Identity:ClientSecret': 'isolated-fixture-not-a-real-credential',
      'Identity:Directory:Enabled': true, 'Identity:Directory:DevelopmentEndpoint': issuer + '/graph/',
      'Identity:WriteRequestsPerMinute': 200, 'Identity:ReadRequestsPerMinute': 1000
    };
    let version = 0;
    const jar = new Map();
    async function browser(url, options = {}) {
      const headers = new Headers(options.headers);
      if (url.startsWith(api.url) && jar.size)
        headers.set('Cookie', [...jar].map(([name, value]) => `${name}=${value}`).join('; '));
      const response = await fetch(url, { ...options, headers, redirect: 'manual' });
      if (url.startsWith(api.url)) {
        const cookies = response.headers.getSetCookie?.() ||
          (response.headers.get('set-cookie') || '').split(/,(?=\s*[^;,]+=)/).filter(Boolean);
        for (const cookie of cookies) {
          const [name, ...value] = cookie.split(';')[0].split('=');
          jar.set(name.trim(), value.join('='));
        }
      }
      return response;
    }
    async function change(id, changes, expected = version) {
      const response = await request(base + '/users', id, 'PUT', { version: expected, changes });
      const result = await response.json();
      if (response.ok) version = result.version;
      return { response, result };
    }
    try {
      await fs.writeFile(path.join(directory, 'appsettings.json'), JSON.stringify({
        Identity: { Memberships: [{
          directoryTenantId: tenantId, objectId: admin, applicationTenantId: 'contoso-legal',
          role: 'Admin', allDocuments: true
        }] }
      }));
      const web = path.join(directory, 'wwwroot');
      await fs.mkdir(web);
      await fs.writeFile(path.join(web, 'index.html'), '<html><body>Workspace fixture</body></html>');
      await fs.writeFile(path.join(web, 'admin.html'), '<html><body>Admin fixture</body></html>');
      api = await startApi(directory, settings);

      await t.test('unassigned employees cannot enter either UI or call tenant APIs', async () => {
        assert.equal((await request(base + '/users', null)).status, 401);
        for (const route of ['/?tenant=contoso-legal', '/admin.html?tenant=contoso-legal', base + '/jobs', base + '/users']) {
          assert.equal((await request(route, outsider)).status, 403, route);
        }
        assert.equal((await (await request('/api/identity/me', outsider)).json()).hasMembership, false);
        assert.equal((await request('/admin.html?tenant=contoso-legal', null)).status, 302);
      });
      await t.test('people and photos are directory-backed and management is tenant-admin-only', async () => {
        const initial = await (await request(base + '/users')).json();
        assert.equal(initial.version, 0);
        assert.equal(initial.directoryConfigured, true);
        assert.equal(initial.users[0].profile.objectId, admin);
        const found = await (await request(base + '/users/directory?q=Employee')).json();
        assert.deepEqual(new Set(found.people.map(person => person.objectId)), new Set([user, secondAdmin]));
        const photo = await request(base + '/users/photo/' + user);
        assert.equal(photo.status, 200);
        assert.equal(photo.headers.get('content-type'), 'image/png');
        assert.match(photo.headers.get('cache-control'), /no-store/);
        assert.equal((await request('/api/harness/tenants/fabrikam-finance/users')).status, 403);
        assert.equal((await request(base + '/users/directory?q=x')).status, 400);
      });
      await t.test('assignment grants tenant-wide reading and Ask AI but no administration or processing', async () => {
        const saved = await change(admin, [
          { objectId: user, role: 'User', status: 'Active' },
          { objectId: secondAdmin, role: 'TenantAdmin', status: 'Active' }
        ]);
        assert.equal(saved.response.status, 200);
        assert.equal(version, 1);
        assert.equal((await request('/?tenant=contoso-legal', user)).status, 200);
        assert.equal((await request(base + '/jobs', user)).status, 200);
        for (const route of ['/admin.html?tenant=contoso-legal', base + '/users',
          base + '/users/directory?q=Employee', base + '/users/photo/' + admin]) {
          assert.equal((await request(route, user)).status, 403, route);
        }
        assert.equal((await request(base, user, 'PUT', {})).status, 403);
        assert.equal((await request(base + '/run-split', user, 'POST', {})).status, 403);
        const me = await (await request('/api/identity/me', user)).json();
        assert.equal(me.hasMembership, true);
        assert.deepEqual(me.tenants, [{ tenantId: 'contoso-legal', canAdmin: false, canProcess: false }]);
        assert.equal((await request('/admin.html?tenant=contoso-legal', secondAdmin)).status, 200);
        const lookup = await (await request(base + '/users/directory?q=Employee')).json();
        assert.equal(lookup.people.length, 0, 'assigned users are excluded even outside the visible page');
        assert.equal((await change(admin, [{ objectId: user, role: 'User', status: 'Disabled' }], 0)).response.status, 409);
      });
      await t.test('role changes affect the same token and cannot remove every active admin', async () => {
        assert.equal((await change(admin, [{ objectId: user, role: 'TenantAdmin', status: 'Active' }])).response.status, 200);
        assert.equal((await request('/admin.html?tenant=contoso-legal', user)).status, 200);
        assert.equal((await change(admin, [{ objectId: user, role: 'User', status: 'Active' }])).response.status, 200);
        assert.equal((await request('/admin.html?tenant=contoso-legal', user)).status, 403);
        const denied = await change(admin, [
          { objectId: admin, role: 'TenantAdmin', status: 'Removed' },
          { objectId: secondAdmin, role: 'User', status: 'Active' }
        ]);
        assert.equal(denied.response.status, 400);
        assert.match(denied.result.error, /at least one active tenant admin/);
        assert.equal((await (await request(base + '/users')).json()).version, version);
      });
      await t.test('tenant-admin browser sessions require CSRF for membership edits and never retry a rejected edit', async () => {
        let redirect = await browser(api.url + '/auth/login?returnUrl=%2Fadmin.html');
        assert.equal(redirect.status, 302);
        redirect = await browser(redirect.headers.get('location'));
        assert.equal(redirect.status, 302);
        const signedIn = await browser(redirect.headers.get('location'));
        assert.equal(signedIn.status, 302);
        assert.equal((await browser(api.url + base + '/users')).status, 200);
        const body = JSON.stringify({ version, changes: [{ objectId: user, role: 'User', status: 'Active' }] });
        assert.equal((await browser(api.url + base + '/users', {
          method: 'PUT', headers: { 'Content-Type': 'application/json' }, body
        })).status, 400);
        assert.equal((await (await request(base + '/users')).json()).version, version);
        const verification = await (await browser(api.url + '/api/identity/csrf')).json();
        const saved = await browser(api.url + base + '/users', {
          method: 'PUT', headers: { 'Content-Type': 'application/json', 'X-DocIntel-CSRF': verification.token }, body
        });
        assert.equal(saved.status, 200);
        version = (await saved.json()).version;
      });
      await t.test('revocation overrides bootstrap config and remains effective after restart', async () => {
        assert.equal((await change(secondAdmin, [{ objectId: admin, role: 'TenantAdmin', status: 'Removed' }])).response.status, 200);
        assert.equal((await request(base + '/users', admin)).status, 403);
        assert.equal((await request('/admin.html?tenant=contoso-legal', admin)).status, 403);
        assert.equal((await browser(api.url + base + '/users')).status, 403);
        assert.equal((await browser(api.url + '/admin.html?tenant=contoso-legal')).status, 403);
        assert.equal((await change(secondAdmin, [{ objectId: user, role: 'User', status: 'Disabled' }])).response.status, 200);
        assert.equal((await request(base + '/jobs', user)).status, 403);
        await killApi(api);
        api = await startApi(directory, settings);
        assert.equal((await request(base + '/jobs', user)).status, 403);
        assert.equal((await request(base + '/users', admin)).status, 403);
        const current = await (await request(base + '/users', secondAdmin)).json();
        assert.equal(current.version, version);
        assert.equal(current.audit.length, version);
        assert.ok(current.audit.every(entry => entry.actor.directoryTenantId === tenantId));
      });
      await t.test('directory outage exposes no provider diagnostics and never grants a new person', async () => {
        failDirectory = true;
        const failed = await request(base + '/users/directory?q=Unknown', secondAdmin);
        assert.equal(failed.status, 503);
        assert.doesNotMatch(await failed.text(), /PRIVATE_DIRECTORY/);
        failDirectory = false;
        assert.equal((await (await request(base + '/users', secondAdmin)).json()).version, version);
      });
      assert.deepEqual(providerErrors, []);
    } finally {
      await killApi(api);
      provider.close();
      await once(provider, 'close');
      await fs.rm(directory, { recursive: true });
    }
  });
