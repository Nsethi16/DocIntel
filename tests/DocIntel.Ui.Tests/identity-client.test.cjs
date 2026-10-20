const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const script = fs.readFileSync(path.join(__dirname, '..', '..', 'src',
  'DocIntel.Api', 'wwwroot', 'identity.js'), 'utf8');
const identity = { chatPartition: 'account-one', localDevelopment: false,
  canAdministerAnyTenant: false, platformAdministrator: false };

function runtime(responses) {
  const requests = [];
  const navigations = [];
  const nodes = [];
  const context = vm.createContext({
    URL, Request, Headers,
    location: {
      href: 'https://app.example.invalid/?tenant=one', origin: 'https://app.example.invalid',
      pathname: '/', search: '?tenant=one',
      assign: value => navigations.push(value), replace: value => navigations.push(value),
      reload: () => navigations.push('reload')
    },
    document: {
      addEventListener: () => {},
      getElementById: id => nodes.find(node => node.id === id),
      createElement: () => ({ style: {}, setAttribute() {} }),
      body: { appendChild: node => nodes.push(node) }
    },
    fetch: async (input, options) => {
      requests.push({ input, options });
      const response = responses.shift();
      assert.ok(response, 'Unexpected request');
      if (response instanceof Error) throw response;
      return {
        ok: !response.status || response.status < 400, status: response.status || 200,
        headers: new Headers(response.headers),
        json: async () => response.data || {}
      };
    }
  });
  vm.runInContext(script, context);
  return { context, requests, navigations, nodes };
}

test('browser mutations acquire one CSRF token and preserve the original request body', async () => {
  const app = runtime([
    { data: identity }, { data: { token: 'test-request-token' } }, { data: {} }, { data: {} }
  ]);

  await app.context.fetch('/api/harness/tenants/one/ask/runs', { method: 'POST', body: 'first' });
  await app.context.fetch('/api/harness/tenants/one/ask/runs', { method: 'POST', body: 'second' });

  assert.equal(app.requests.filter(request => request.input === '/api/identity/csrf').length, 1);
  assert.equal(app.requests[2].options.headers.get('X-DocIntel-CSRF'), 'test-request-token');
  assert.equal(app.requests[2].options.body, 'first');
  assert.equal(app.requests[3].options.body, 'second');
  assert.equal(app.context.DocIntelIdentity.chatPartition, 'account-one');
});

test('unauthenticated users navigate to sign-in without retrying an execution', async () => {
  const app = runtime([{ status: 401 }]);

  await assert.rejects(app.context.fetch('/api/harness/tenants/one/ask/runs', { method: 'POST' }), /Sign-in/);

  assert.equal(app.requests.length, 1);
  assert.ok(app.navigations[0].startsWith('/auth/login?returnUrl='));
});

test('account changes in another tab reload before returning data under the old chat identity', async () => {
  const app = runtime([
    { data: identity }, { headers: { 'X-DocIntel-Identity': 'account-two' }, data: { secret: 'not returned' } }
  ]);

  await assert.rejects(app.context.fetch('/api/harness/tenants'), /account changed/);

  assert.deepEqual(app.navigations, ['reload']);
});

test('external requests are not modified and receive no CSRF or identity headers', async () => {
  const app = runtime([{ data: {} }]);

  await app.context.fetch('https://public.example.invalid/library.js', { method: 'GET' });

  assert.equal(app.requests.length, 1);
  assert.equal(app.requests[0].options.headers, undefined);
});

test('network errors do not cause automatic mutation resubmission', async () => {
  const app = runtime([
    { data: identity }, { data: { token: 'test-request-token' } }, new TypeError('Network disconnected.')
  ]);

  await assert.rejects(app.context.fetch('/api/harness/tenants/one/ask/runs', { method: 'POST' }), /Network/);

  assert.equal(app.requests.length, 3);
});

test('sign-out clears the application session and shows a signed-out page instead of signing in again', async () => {
  const app = runtime([{ data: { token: 'test-request-token' } }, { data: { signedOut: true } }]);

  await app.context.DocIntelIdentity.signOut();

  assert.equal(app.requests[1].input, '/auth/logout');
  assert.equal(app.requests[1].options.headers['X-DocIntel-CSRF'], 'test-request-token');
  assert.deepEqual(app.navigations, ['/auth/signed-out']);
});
