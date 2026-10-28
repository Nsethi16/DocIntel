(() => {
  const nativeFetch = globalThis.fetch.bind(globalThis);
  let csrf;
  let identityReady;
  let redirecting = false;
  const identityChannel = typeof BroadcastChannel === 'undefined' ? null : new BroadcastChannel('docintel-identity');
  if (identityChannel) identityChannel.onmessage = () => location.assign('/auth/signed-out');

  function requireSignIn() {
    if (!redirecting) {
      redirecting = true;
      location.assign(`/auth/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`);
    }
    throw new Error('Sign-in is required.');
  }

  function showAccessError() {
    if (document.getElementById('identityAccessError')) return;
    const notice = document.createElement('div');
    notice.id = 'identityAccessError';
    notice.setAttribute('role', 'alert');
    notice.textContent = 'Your account does not have permission for this operation. Contact your administrator.';
    notice.style.cssText = 'position:fixed;top:12px;left:20%;right:20%;padding:16px;background:var(--error-bg);' +
      'color:var(--error);border:1px solid var(--error);border-radius:8px;z-index:1000;';
    document.body.appendChild(notice);
  }

  async function requestToken() {
    if (!csrf) {
      csrf = nativeFetch('/api/identity/csrf', { credentials: 'same-origin', cache: 'no-store' })
        .then(async response => {
          if (response.status === 401) requireSignIn();
          if (!response.ok) throw new Error('Unable to verify this request.');
          const token = (await response.json()).token;
          if (typeof token !== 'string' || !token) throw new Error('Unable to verify this request.');
          return token;
        }).catch(error => { csrf = null; throw error; });
    }
    return csrf;
  }

  function ensureIdentity() {
    if (!identityReady) {
      identityReady = nativeFetch('/api/identity/me', { credentials: 'same-origin', cache: 'no-store' })
        .then(async response => {
          if (response.status === 401) requireSignIn();
          if (!response.ok) throw new Error('Unable to verify the signed-in account.');
          const identity = await response.json();
          if (!identity.chatPartition) throw new Error('Unable to verify the signed-in account.');
          if (identity.hasMembership === false) {
            showAccessError();
            throw new Error('Your account is not assigned to a DocIntel workspace.');
          }
          globalThis.DocIntelIdentity.chatPartition = identity.chatPartition;
          return identity;
        }).catch(error => { identityReady = null; throw error; });
    }
    return identityReady;
  }

  globalThis.fetch = async (input, options = {}) => {
    const url = new URL(input instanceof Request ? input.url : input, location.href);
    if (url.origin !== location.origin || !url.pathname.startsWith('/api/harness')) {
      return nativeFetch(input, options);
    }
    await ensureIdentity();
    const method = (options.method || (input instanceof Request ? input.method : 'GET')).toUpperCase();
    const headers = new Headers(input instanceof Request ? input.headers : undefined);
    new Headers(options.headers).forEach((value, name) => headers.set(name, value));
    if (!['GET', 'HEAD', 'OPTIONS'].includes(method)) {
      headers.set('X-DocIntel-CSRF', await requestToken());
    }
    const response = await nativeFetch(input, { ...options, headers, credentials: 'same-origin', cache: 'no-store' });
    if (response.status === 401) requireSignIn();
    const tenantPath = url.pathname.match(/^\/api\/harness\/tenants\/([^/]+)(?:\/|$)/);
    const selected = document.getElementById('tenantSelectDropdown')?.value ||
      new URL(location.href).searchParams.get('tenant');
    const canAdmin = response.headers.get('X-DocIntel-Can-Admin');
    if (tenantPath && decodeURIComponent(tenantPath[1]) === selected && canAdmin !== null) {
      const adminLink = document.getElementById('adminStudioSwitchLink');
      if (adminLink) adminLink.style.display = canAdmin === 'true' ? '' : 'none';
      const upload = document.getElementById('drawerUploadLabel');
      if (upload) upload.style.display = response.headers.get('X-DocIntel-Can-Process') === 'true' ? '' : 'none';
    }
    const partition = response.headers.get('X-DocIntel-Identity');
    if (partition && partition !== globalThis.DocIntelIdentity.chatPartition) {
      location.reload();
      throw new Error('The signed-in account changed. Reloading the workspace.');
    }
    if (response.status === 403) showAccessError();
    return response;
  };

  globalThis.DocIntelIdentity = {
    async refresh() {
      identityReady = null;
      return syncIdentityControls();
    },
    async signOut() {
      const response = await nativeFetch('/auth/logout', {
        method: 'POST', credentials: 'same-origin',
        headers: { 'X-DocIntel-CSRF': await requestToken() }
      });
      if (!response.ok) throw new Error('Sign-out could not be completed.');
      csrf = null;
      if (identityChannel) identityChannel.postMessage('signed-out');
      location.assign('/auth/signed-out');
    }
  };

  async function syncIdentityControls() {
    const identity = await ensureIdentity();
    const tenantId = document.getElementById('tenantSelectDropdown')?.value ||
      new URL(location.href).searchParams.get('tenant');
    const tenant = identity.tenants?.find(entry => entry.tenantId === tenantId);
    const canAdminister = identity.platformAdministrator ||
      (tenantId && identity.tenants ? !!tenant?.canAdmin : identity.canAdministerAnyTenant);
    const adminLink = document.getElementById('adminStudioSwitchLink');
    if (adminLink) adminLink.style.display = canAdminister ? '' : 'none';
    const upload = document.getElementById('drawerUploadLabel');
    if (upload && tenant) upload.style.display = tenant.canProcess ? '' : 'none';
    for (const id of ['platformNewTenant', 'platformCreateTenant']) {
      const control = document.getElementById(id);
      if (control) control.style.display = identity.platformAdministrator ? '' : 'none';
    }
    const signOut = document.getElementById('identitySignOut');
    if (signOut) {
      signOut.hidden = identity.localDevelopment;
      signOut.style.display = identity.localDevelopment ? 'none' : '';
    }
    if (location.pathname === '/admin.html' && !canAdminister) location.replace('/');
    return identity;
  }
  document.addEventListener('DOMContentLoaded', () => {
    syncIdentityControls().catch(() => { if (!redirecting) showAccessError(); });
  });
})();
