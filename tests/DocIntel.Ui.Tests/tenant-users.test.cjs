const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const webroot = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const source = fs.readFileSync(path.join(webroot, 'tenant-users.js'), 'utf8');
const id = number => `00000000-0000-4000-8000-${String(number).padStart(12, '0')}`;
const profile = (number, extra = {}) => ({
  objectId: id(number), displayName: `Employee ${number}`, email: `employee${number}@example.invalid`,
  userPrincipalName: `employee${number}@example.invalid`, jobTitle: 'Analyst', department: 'Operations',
  officeLocation: 'Seattle', member: true, accountEnabled: true, ...extra
});
const user = (number, extra = {}) => ({
  objectId: id(number), role: 'User', status: 'Active', source: 'Managed', legacyRestricted: false,
  profile: profile(number), directoryState: 'Active', ...extra
});
const snapshot = (users = [], extra = {}) => ({
  version: 4, directoryConfigured: true, directoryTenantId: id(999), users,
  total: users.length, nextOffset: null, audit: [], ...extra
});
const response = (body, status = 200) => ({ ok: status >= 200 && status < 300, status, json: async () => body });
const deferred = () => {
  let resolve;
  const promise = new Promise(done => { resolve = done; });
  return { promise, resolve };
};

function runtime() {
  const nodes = new Map();
  class Element {
    constructor(tag = 'div') {
      this.tagName = tag;
      this.children = [];
      this.attributes = {};
      this.listeners = {};
      this.value = '';
      this.hidden = false;
      this.disabled = false;
      this.open = false;
      this.text = '';
      this.classList = { toggle() {} };
    }

    set id(value) { this.elementId = value; nodes.set(value, this); }
    get id() { return this.elementId; }
    set innerHTML(value) { throw new Error(`Untrusted content became HTML: ${value}`); }
    set textContent(value) { this.text = String(value); this.children = []; }
    get textContent() { return this.text + this.children.map(child => child.textContent).join(' '); }
    append(...children) { this.children.push(...children); }
    replaceChildren(...children) { this.text = ''; this.children = children; }
    setAttribute(key, value) { this.attributes[key] = String(value); }
    getAttribute(key) { return this.attributes[key]; }
    addEventListener(name, listener) { this.listeners[name] = listener; }
    focus() { app.focused = this.id; }
    showModal() { assert.equal(this.open, false, 'A dialog must not be reopened while already open'); this.open = true; }
    close() { this.open = false; }
  }
  function node(name) {
    if (!nodes.has(name)) { const result = new Element(); result.id = name; }
    return nodes.get(name);
  }
  const actions = {};
  const timers = new Map();
  let nextTimer = 0;
  const app = {
    node, actions, requests: [], focused: null,
    identity: { refresh: async () => {} },
    handler: async () => response(snapshot()),
    async invoke(name, args = [], target = null) {
      const handler = actions[name];
      assert.ok(handler, `Registered action ${name}`);
      assert.equal(args.length, handler.arity);
      return handler.run.call(target, args);
    },
    async search(query) {
      node('tenantUsersSearch').value = query;
      await app.invoke('tenantUsersSearch');
      const callbacks = [...timers.values()];
      timers.clear();
      await Promise.all(callbacks.map(callback => callback()));
    },
    async init(data = snapshot()) {
      app.handler = async () => response(data);
      await app.users.setTenant('first');
    },
    find(root, name) {
      const found = [];
      function visit(element) {
        if (element.getAttribute('data-click') === name) found.push(element);
        for (const child of element.children) visit(child);
      }
      visit(node(root));
      return found;
    }
  };
  const context = vm.createContext({
    document: { getElementById: node, createElement: tag => new Element(tag) },
    DocIntelActions: { register: handlers => Object.assign(actions, handlers) },
    DocIntelIdentity: app.identity,
    fetch: async (url, options = {}) => {
      app.requests.push({ url, options });
      return app.handler(url, options);
    },
    setTimeout: callback => { timers.set(++nextTimer, callback); return nextTimer; },
    clearTimeout: timer => timers.delete(timer)
  });
  vm.runInContext(source, context);
  app.users = context.DocIntelTenantUsers;
  return app;
}

test('the neutral Manage control opens existing access details without saving or changing permissions', async () => {
  const app = runtime();
  await app.init(snapshot([user(1)]));
  const [manage] = app.find('tenantUsersList', 'tenantUsersDetails_arg');
  assert.equal(manage.textContent, 'Manage');
  assert.equal(manage.className, 'chevron-btn');
  assert.equal(manage.getAttribute('aria-label'), 'Manage access for Employee 1');
  const before = app.requests.length;
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  assert.equal(app.node('tenantUsersDetailsDialog').open, true);
  assert.equal(app.node('tenantUsersDetailRole').value, 'User');
  assert.equal(app.requests.length, before);
  assert.equal(app.requests.filter(request => request.options.method === 'PUT').length, 0);
});

test('real markup integrates a third tab, same-origin scripts and separately saved access', () => {
  const html = fs.readFileSync(path.join(webroot, 'admin.html'), 'utf8');
  const admin = fs.readFileSync(path.join(webroot, 'admin.js'), 'utf8');
  assert.match(html, /id="tenantUsersTab"[^>]+role="tab"/);
  assert.match(html, /id="harnessHeaderActions"/);
  assert.match(html, /src="\/tenant-users\.js"/);
  assert.match(html, /href="\/tenant-users\.css"/);
  assert.match(admin, /fetch\('\/api\/harness\/tenants\?adminOnly=true'\)/);
  assert.match(admin, /function renderMainCanvas\(\) \{\s+if \(globalThis.DocIntelTenantUsers\)/);
  assert.doesNotMatch(source, /innerHTML|onclick|onerror|Authorization|access_token/i);
  assert.doesNotMatch(html.slice(html.indexOf('id="tenantUsersPanel"'), html.indexOf('id="agentSettingsPanel"')),
    /\bon(?:click|input|change)\s*=/i);
});

test('assigned names and work details stay text; photos are same-origin and fall back to initials', async () => {
  const app = runtime();
  await app.init(snapshot([user(1, { profile: profile(1, { displayName: '<img src=x onerror=evil()>' }) })]));
  const text = app.node('tenantUsersList').textContent;
  assert.match(text, /<img src=x onerror=evil\(\)>/);
  assert.match(text, /Analyst · Operations/);
  const avatar = app.node('tenantUsersList').children[0].children[0];
  const image = avatar.children.find(child => child.tagName === 'img');
  assert.equal(image.src, `/api/harness/tenants/first/users/photo/${id(1)}`);
  image.listeners.load();
  assert.equal(image.hidden, false);
  image.listeners.error();
  assert.equal(image.hidden, true);
  assert.ok(avatar.children[0].textContent);
});

test('disconnected directory disables Add and search without fabricating imported profiles', async () => {
  const app = runtime();
  await app.init(snapshot([user(1, { profile: null, source: 'Operator', directoryState: 'NotConnected' })],
    { directoryConfigured: false }));
  assert.equal(app.node('tenantUsersAdd').disabled, true);
  assert.equal(app.node('tenantUsersSearch').disabled, true);
  assert.match(app.node('tenantUsersDirectory').textContent, /not connected.*operator/);
  assert.match(app.node('tenantUsersList').textContent, new RegExp(id(1)));
  assert.match(app.node('tenantUsersList').textContent, /Directory profile unavailable/);
  await app.invoke('tenantUsersOpenAdd');
  await app.search('employee');
  assert.equal(app.node('tenantUsersAddDialog').open, false);
  assert.equal(app.requests.length, 1);
});

test('directory search is bounded, excludes ineligible people and never accepts arbitrary addresses', async () => {
  const app = runtime();
  await app.init(snapshot([user(1), user(7, { status: 'Removed', legacyRestricted: true })]));
  await app.invoke('tenantUsersOpenAdd');
  app.handler = async () => response({ people: [
    profile(1), profile(2, { accountEnabled: false }), profile(3, { member: false }),
    profile(4, { objectId: 'arbitrary@example.invalid' }), profile(5), profile(7)
  ] });
  await app.search('a');
  await app.search('a'.repeat(81));
  assert.equal(app.requests.length, 1);
  await app.search('A & B');
  assert.match(app.requests.at(-1).url, /directory\?q=A%20%26%20B$/);
  assert.equal(app.node('tenantUsersSearchResults').children.length, 1);
  await app.invoke('tenantUsersSelect_arg', ['arbitrary@example.invalid']);
  assert.equal(app.node('tenantUsersSelected').children.length, 0);
  await app.invoke('tenantUsersSelect_arg', [id(5)]);
  const select = app.node(`tenant-user-role-${id(5)}`);
  assert.deepEqual(select.children.map(option => option.value), ['User', 'TenantAdmin']);
  assert.deepEqual(select.children.map(option => option.textContent), ['User', 'Workspace Admin']);
  assert.equal(select.value, 'User');
  assert.equal(app.node('tenantUsersReviewAdd').disabled, false);
});

test('multi-person assignment is atomic, defaults User and requires explicit confirmation before PUT', async () => {
  const app = runtime();
  await app.init();
  await app.invoke('tenantUsersOpenAdd');
  app.handler = async () => response({ people: [profile(1), profile(2)] });
  await app.search('employee');
  for (const number of [1, 2]) await app.invoke('tenantUsersSelect_arg', [id(number)]);
  await app.invoke('tenantUsersSelectedRole_arg', [id(2)], { value: 'TenantAdmin' });
  await app.invoke('tenantUsersReviewAdd');
  assert.equal(app.requests.filter(item => item.options.method === 'PUT').length, 0);
  assert.match(app.node('tenantUsersConfirmText').textContent, /2 people.*1 will be workspace admins/);
  app.handler = async (url, options) => options.method === 'PUT'
    ? response({ version: 5 }) : response(snapshot([user(1), user(2, { role: 'TenantAdmin' })], { version: 5 }));
  await app.invoke('tenantUsersConfirm');
  const puts = app.requests.filter(item => item.options.method === 'PUT');
  assert.equal(puts.length, 1);
  assert.equal(puts[0].url, '/api/harness/tenants/first/users');
  assert.deepEqual(JSON.parse(puts[0].options.body), {
    version: 4, changes: [
      { objectId: id(1), role: 'User', status: 'Active' },
      { objectId: id(2), role: 'TenantAdmin', status: 'Active' }
    ]
  });
  assert.equal(app.requests.at(-1).url, '/api/harness/tenants/first/users?offset=0&limit=50');
  assert.match(app.node('tenantUsersVersion').textContent, /v5/);
  assert.equal(app.node('tenantUsersAddDialog').open, false);
});

test('selection caps at 25 and duplicate selections cannot bypass the atomic change limit', async () => {
  const app = runtime();
  await app.init();
  await app.invoke('tenantUsersOpenAdd');
  app.handler = async url => response({
    people: Array.from({ length: 20 }, (_, index) => profile(index + (url.includes('second') ? 21 : 1)))
  });
  await app.search('first');
  for (let number = 1; number <= 20; number++) await app.invoke('tenantUsersSelect_arg', [id(number)]);
  await app.search('second');
  for (let number = 21; number <= 40; number++) await app.invoke('tenantUsersSelect_arg', [id(number)]);
  assert.equal(app.node('tenantUsersSelected').children.length, 25);
  assert.equal(app.find('tenantUsersSearchResults', 'tenantUsersSelect_arg').every(button => button.disabled), true);
  await app.invoke('tenantUsersReviewAdd');
  assert.match(app.node('tenantUsersConfirmText').textContent, /Assign 25 people/);
});

test('roles and removals require confirmation; cancelled changes never mutate membership', async () => {
  const app = runtime();
  await app.init(snapshot([user(1)]));
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  assert.match(app.node('tenantUsersDetails').textContent, /Seattle/);
  app.node('tenantUsersDetailRole').value = 'TenantAdmin';
  await app.invoke('tenantUsersReviewRole');
  assert.equal(app.node('tenantUsersConfirmDialog').open, true);
  assert.match(app.node('tenantUsersConfirmText').textContent, /from User to Workspace Admin/);
  await app.invoke('tenantUsersClose_arg', ['confirm']);
  await app.invoke('tenantUsersConfirm');
  assert.equal(app.requests.length, 1);
  await app.invoke('tenantUsersReviewStatus_arg', ['Removed']);
  assert.match(app.node('tenantUsersConfirmText').textContent, /Removed.*revokes membership/);
  assert.equal(app.requests.length, 1);
});

test('restricted imported entries offer only disable/remove, never role changes or restoration', async () => {
  const app = runtime();
  await app.init(snapshot([user(1, { source: 'Operator', legacyRestricted: true })]));
  assert.match(app.node('tenantUsersList').textContent, /Operator-managed restricted access/);
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  assert.equal(app.find('tenantUsersDetailControls', 'tenantUsersReviewRole').length, 0);
  const buttons = app.find('tenantUsersDetailControls', 'tenantUsersReviewStatus_arg');
  assert.deepEqual(buttons.map(button => JSON.parse(button.getAttribute('data-args'))[0]), ['Disabled', 'Removed']);
  app.node('tenantUsersDetailRole').value = 'TenantAdmin';
  await app.invoke('tenantUsersReviewRole');
  await app.invoke('tenantUsersReviewStatus_arg', ['Active']);
  assert.equal(app.node('tenantUsersConfirmDialog').open, false);
  await app.invoke('tenantUsersReviewStatus_arg', ['Disabled']);
  app.handler = async (_, options) => options.method === 'PUT'
    ? response({ version: 5 }) : response(snapshot([user(1, { legacyRestricted: true, status: 'Disabled' })]));
  await app.invoke('tenantUsersConfirm');
  const change = JSON.parse(app.requests.find(item => item.options.method === 'PUT').options.body).changes[0];
  assert.deepEqual(change, { objectId: id(1), role: 'User', status: 'Disabled' });
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  assert.deepEqual(app.find('tenantUsersDetailControls', 'tenantUsersReviewStatus_arg')
    .map(button => JSON.parse(button.getAttribute('data-args'))[0]), ['Removed']);
});

test('disabled directory accounts cannot be enabled or promoted but can lose access', async () => {
  const app = runtime();
  await app.init(snapshot([user(1, { status: 'Disabled', directoryState: 'Disabled' })]));
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  assert.deepEqual(app.find('tenantUsersDetailControls', 'tenantUsersReviewStatus_arg')
    .map(button => JSON.parse(button.getAttribute('data-args'))[0]), ['Removed']);
  app.node('tenantUsersDetailRole').value = 'TenantAdmin';
  await app.invoke('tenantUsersReviewRole');
  assert.match(app.node('tenantUsersDetailControls').textContent, /not confirmed active/);
  assert.equal(app.node('tenantUsersConfirmDialog').open, false);
});

test('last-admin rejection and version conflicts surface verbatim, block writes and never retry', async () => {
  for (const status of [400, 409, 403, 503]) {
    const app = runtime();
    await app.init(snapshot([user(1, { role: 'TenantAdmin' })]));
    await app.invoke('tenantUsersDetails_arg', [id(1)]);
    await app.invoke('tenantUsersReviewStatus_arg', ['Removed']);
    const message = status === 400 ? 'Cannot remove the last active tenant administrator.' : `Server error ${status}`;
    app.handler = async () => response({ error: message }, status);
    await app.invoke('tenantUsersConfirm');
    await app.invoke('tenantUsersConfirm');
    assert.equal(app.requests.filter(item => item.options.method === 'PUT').length, 1);
    assert.ok(app.node('tenantUsersStatus').textContent.includes(message));
    assert.match(app.node('tenantUsersStatus').textContent, /reload access/i);
    if (status === 409) assert.match(app.node('tenantUsersStatus').textContent, /Version conflict/);
    assert.equal(app.node('tenantUsersAdd').disabled, true);
    assert.equal(app.node('tenantUsersReload').disabled, false);
    assert.equal(app.find('tenantUsersList', 'tenantUsersDetails_arg')[0].disabled, true);
    app.handler = async () => response(snapshot([user(1)]));
    await app.invoke('tenantUsersReload');
    assert.equal(app.node('tenantUsersAdd').disabled, false);
  }
});

test('uncertain saves do not retry, double submit is guarded and refresh failure stays explicit', async () => {
  const app = runtime();
  await app.init(snapshot([user(1)]));
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  await app.invoke('tenantUsersReviewStatus_arg', ['Disabled']);
  const saving = deferred();
  app.handler = () => saving.promise;
  const operation = app.invoke('tenantUsersConfirm');
  await app.invoke('tenantUsersConfirm');
  assert.equal(app.requests.filter(item => item.options.method === 'PUT').length, 1);
  assert.equal(app.node('tenantUsersConfirmSave').disabled, true);
  assert.equal(app.node('tenantUsersConfirmCancel').disabled, true);
  app.handler = async () => { throw new Error('Network disconnected'); };
  saving.resolve(response({ version: 5 }));
  await operation;
  assert.match(app.node('tenantUsersStatus').textContent, /change was saved.*list could not be refreshed/);
  assert.equal(app.node('tenantUsersAdd').disabled, true);
});

test('stale tenant list, directory and mutation responses cannot overwrite the selected tenant', async () => {
  const app = runtime();
  const oldList = deferred();
  app.handler = () => oldList.promise;
  const oldLoad = app.users.setTenant('old-list');
  app.handler = async () => response(snapshot([user(2)]));
  await app.users.setTenant('new-list');
  oldList.resolve(response(snapshot([user(1)])));
  await oldLoad;
  assert.match(app.node('tenantUsersList').textContent, /Employee 2/);
  assert.doesNotMatch(app.node('tenantUsersList').textContent, /Employee 1/);

  await app.invoke('tenantUsersOpenAdd');
  const oldSearch = deferred();
  app.handler = () => oldSearch.promise;
  const searching = app.search('employee');
  app.handler = async () => response(snapshot([user(3)]));
  await app.users.setTenant('third');
  oldSearch.resolve(response({ people: [profile(1)] }));
  await searching;
  assert.equal(app.node('tenantUsersSearchResults').children.length, 0);

  await app.invoke('tenantUsersDetails_arg', [id(3)]);
  await app.invoke('tenantUsersReviewStatus_arg', ['Removed']);
  const oldSave = deferred();
  app.handler = () => oldSave.promise;
  const saving = app.invoke('tenantUsersConfirm');
  app.handler = async () => response(snapshot([user(4)], { version: 17 }));
  await app.users.setTenant('fourth');
  const count = app.requests.length;
  oldSave.resolve(response({ error: 'Old tenant error must not appear' }, 409));
  await saving;
  assert.equal(app.requests.length, count);
  assert.match(app.node('tenantUsersList').textContent, /Employee 4/);
  assert.match(app.node('tenantUsersVersion').textContent, /v17/);
  assert.doesNotMatch(app.node('tenantUsersStatus').textContent, /Old tenant error/);
  assert.equal(app.node('tenantUsersAdd').disabled, false);
});

test('out-of-order searches and closed dialog results are discarded', async () => {
  const app = runtime();
  await app.init();
  await app.invoke('tenantUsersOpenAdd');
  const old = deferred();
  app.handler = () => old.promise;
  const search = app.search('old');
  app.handler = async () => response({ people: [profile(2)] });
  await app.search('new');
  old.resolve(response({ people: [profile(1)] }));
  await search;
  assert.match(app.node('tenantUsersSearchResults').textContent, /Employee 2/);
  assert.doesNotMatch(app.node('tenantUsersSearchResults').textContent, /Employee 1/);
  const closed = deferred();
  app.handler = () => closed.promise;
  const closing = app.search('closed');
  await app.invoke('tenantUsersClose_arg', ['add']);
  closed.resolve(response({ people: [profile(3)] }));
  await closing;
  assert.equal(app.node('tenantUsersSearchResults').children.length, 0);
});

test('pagination appends deduplicated people and resets on tenant or mutation refresh', async () => {
  const app = runtime();
  await app.init(snapshot([user(1)], { total: 3, nextOffset: 1 }));
  assert.equal(app.node('tenantUsersMore').hidden, false);
  app.handler = async () => response(snapshot([user(1), user(2)], { total: 3, nextOffset: 2 }));
  await app.invoke('tenantUsersMore');
  assert.equal(app.requests.at(-1).url, '/api/harness/tenants/first/users?offset=1&limit=50');
  assert.equal(app.node('tenantUsersList').children.length, 2);
  assert.match(app.node('tenantUsersCount').textContent, /2 of 3/);
  app.handler = async () => response(snapshot([user(3)], { total: 3, nextOffset: null }));
  await app.invoke('tenantUsersMore');
  assert.equal(app.node('tenantUsersList').children.length, 3);
  assert.equal(app.node('tenantUsersMore').hidden, true);
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  await app.invoke('tenantUsersReviewStatus_arg', ['Removed']);
  app.handler = async (_, options) => options.method === 'PUT' ? response({ version: 5 })
    : response(snapshot([user(2)], { total: 2, nextOffset: 1, version: 5 }));
  await app.invoke('tenantUsersConfirm');
  assert.match(app.requests.at(-1).url, /\?offset=0&limit=50$/);
  assert.equal(app.node('tenantUsersList').children.length, 1);
  assert.match(app.node('tenantUsersList').textContent, /Employee 2/);
  app.handler = async () => response(snapshot([user(9)]));
  await app.users.setTenant('another');
  assert.equal(app.node('tenantUsersMore').hidden, true);
  assert.match(app.node('tenantUsersList').textContent, /Employee 9/);
});

test('pagination never merges different versions or a stale tenant page', async () => {
  const app = runtime();
  await app.init(snapshot([user(1)], { total: 2, nextOffset: 1 }));
  app.handler = async () => response(snapshot([user(2)], { version: 5, total: 2 }));
  await app.invoke('tenantUsersMore');
  assert.match(app.node('tenantUsersStatus').textContent, /Access changed.*Reload access/);
  assert.equal(app.node('tenantUsersList').children.length, 1);
  assert.equal(app.node('tenantUsersAdd').disabled, true);
  app.handler = async () => response(snapshot([user(1)], { total: 2, nextOffset: 1 }));
  await app.invoke('tenantUsersReload');
  const page = deferred();
  app.handler = () => page.promise;
  const loading = app.invoke('tenantUsersMore');
  app.handler = async () => response(snapshot([user(3)]));
  await app.users.setTenant('new');
  page.resolve(response(snapshot([user(2)])));
  await loading;
  assert.match(app.node('tenantUsersList').textContent, /Employee 3/);
  assert.doesNotMatch(app.node('tenantUsersList').textContent, /Employee 2/);
});

test('successful membership mutations refresh caller identity before reloading assigned people', async () => {
  const app = runtime();
  await app.init(snapshot([user(1, { role: 'TenantAdmin' })]));
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  app.node('tenantUsersDetailRole').value = 'User';
  await app.invoke('tenantUsersReviewRole');
  const events = [];
  app.identity.refresh = async () => { events.push('refresh identity'); };
  app.handler = async (_, options) => {
    events.push(options.method === 'PUT' ? 'save' : 'reload people');
    return options.method === 'PUT' ? response({ version: 5 })
      : response(snapshot([user(1)], { version: 5 }));
  };
  await app.invoke('tenantUsersConfirm');
  assert.deepEqual(events, ['save', 'refresh identity', 'reload people']);
  assert.match(app.node('tenantUsersStatus').textContent, /Access changes saved/);
});

test('forbidden identity refresh cannot relabel a successful mutation as a failed save or retry it', async () => {
  const app = runtime();
  await app.init(snapshot([user(1, { role: 'TenantAdmin' })]));
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  app.node('tenantUsersDetailRole').value = 'User';
  await app.invoke('tenantUsersReviewRole');
  let refreshes = 0;
  app.identity.refresh = async () => {
    refreshes++;
    throw new Error('Forbidden');
  };
  app.handler = async () => response({ version: 5 });
  await app.invoke('tenantUsersConfirm');
  await app.invoke('tenantUsersConfirm');
  assert.equal(refreshes, 1);
  assert.equal(app.requests.length, 2, 'Only the initial load and confirmed PUT should run');
  assert.match(app.node('tenantUsersStatus').textContent, /^Access changes saved\./);
  assert.match(app.node('tenantUsersStatus').textContent, /permissions could not be refreshed/);
  assert.doesNotMatch(app.node('tenantUsersStatus').textContent, /outcome is uncertain|save.*failed/i);
  assert.equal(app.node('tenantUsersAdd').disabled, true);
  assert.equal(app.node('tenantUsersConfirmDialog').open, false);
});

test('a late identity refresh failure after a tenant switch cannot replace the selected tenant status', async () => {
  const app = runtime();
  await app.init(snapshot([user(1)]));
  await app.invoke('tenantUsersDetails_arg', [id(1)]);
  await app.invoke('tenantUsersReviewStatus_arg', ['Disabled']);
  const refresh = deferred();
  const refreshing = deferred();
  app.identity.refresh = async () => {
    refreshing.resolve();
    await refresh.promise;
    throw new Error('Old identity refresh failure');
  };
  app.handler = async () => response({ version: 5 });
  const saving = app.invoke('tenantUsersConfirm');
  await refreshing.promise;
  app.handler = async () => response(snapshot([user(2)]));
  await app.users.setTenant('second');
  refresh.resolve();
  await saving;
  assert.match(app.node('tenantUsersList').textContent, /Employee 2/);
  assert.equal(app.node('tenantUsersStatus').textContent, 'Access is up to date.');
  assert.equal(app.node('tenantUsersAdd').disabled, false);
});
