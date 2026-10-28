(function (root) {
  'use strict';
  const node = id => document.getElementById(id);
  const roles = Object.freeze([['User', 'User'], ['TenantAdmin', 'Workspace Admin']]);
  const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
  const dialogIds = {
    add: 'tenantUsersAddDialog', details: 'tenantUsersDetailsDialog', confirm: 'tenantUsersConfirmDialog'
  };
  let state = null;
  let searchRevision = 0;
  let searchTimer;
  let results = [];
  let selected = new Map();
  let detailId = null;
  let pending = null;

  const endpoint = owner => `/api/harness/tenants/${encodeURIComponent(owner.tenant)}/users`;
  const current = owner => owner === state;
  const editable = () => !!state?.snapshot && !state.loading && !state.paging && !state.busy && !state.blocked;
  const roleName = role => roles.find(item => item[0] === role)?.[1] || 'Operator-managed';
  const personName = person => person?.displayName || person?.email || person?.userPrincipalName || person?.objectId;

  function element(tag, className, text) {
    const result = document.createElement(tag);
    if (className) result.className = className;
    if (text !== undefined) result.textContent = String(text);
    return result;
  }
  function action(text, name, args = [], disabled = false) {
    const button = element('button', 'btn-action', text);
    button.type = 'button';
    button.setAttribute('data-click', name);
    button.setAttribute('data-args', JSON.stringify(args));
    button.disabled = disabled;
    return button;
  }
  function notice(message, error = false) {
    node('tenantUsersStatus').textContent = message;
    node('tenantUsersStatus').setAttribute('data-error', String(error));
  }
  function controls() {
    node('tenantUsersAdd').disabled = !editable() || !state.snapshot.directoryConfigured;
    node('tenantUsersSearch').disabled = !editable() || !state.snapshot.directoryConfigured;
    node('tenantUsersReload').disabled = !state?.tenant || state.loading || state.paging || state.busy;
    node('tenantUsersList').setAttribute('aria-busy', String(!!(state?.loading || state?.paging || state?.busy)));
    node('tenantUsersMore').hidden = !Number.isSafeInteger(state?.snapshot?.nextOffset);
    node('tenantUsersMore').disabled = !editable();
    node('tenantUsersReviewAdd').disabled = !editable() || selected.size === 0;
    node('tenantUsersConfirmSave').disabled = !editable() || !pending;
    node('tenantUsersConfirmCancel').disabled = !!state?.busy;
  }
  function closeDialog(name) {
    if (state?.busy) return;
    const dialog = node(dialogIds[name]);
    if (!dialog) return;
    dialog.close();
    if (name === 'confirm') pending = null;
    if (name === 'add') {
      clearTimeout(searchTimer);
      searchRevision++;
    }
  }
  function closeDialogs() {
    clearTimeout(searchTimer);
    searchRevision++;
    for (const id of Object.values(dialogIds)) if (node(id).open) node(id).close();
    pending = null;
    detailId = null;
    results = [];
    selected = new Map();
  }
  for (const [name, id] of Object.entries(dialogIds)) {
    node(id).addEventListener('cancel', event => {
      event.preventDefault();
      closeDialog(name);
    });
  }

  function avatar(profile, objectId) {
    const holder = element('span', 'tenant-users-avatar');
    holder.setAttribute('aria-hidden', 'true');
    holder.append(element('span', '', (profile?.displayName || '?').trim().split(/\s+/)
      .slice(0, 2).map(word => word[0] || '').join('').toUpperCase()));
    if (profile && guid.test(objectId) && state?.snapshot?.directoryConfigured) {
      const image = element('img');
      image.alt = '';
      image.hidden = true;
      image.loading = 'lazy';
      image.addEventListener('load', () => { image.hidden = false; }, { once: true });
      image.addEventListener('error', () => { image.hidden = true; }, { once: true });
      image.src = `${endpoint(state)}/photo/${encodeURIComponent(objectId)}`;
      holder.append(image);
    }
    return holder;
  }
  function identity(profile, objectId) {
    const info = element('div', 'tenant-users-person');
    info.append(element('strong', '', profile ? personName(profile) : 'Directory profile unavailable'));
    info.append(element('small', '', profile?.email || profile?.userPrincipalName || 'No work email available'));
    if (profile?.jobTitle || profile?.department)
      info.append(element('small', '', [profile.jobTitle, profile.department].filter(Boolean).join(' · ')));
    if (!profile) info.append(element('small', '', `Object ID: ${objectId}`));
    return info;
  }
  function renderList() {
    const list = node('tenantUsersList');
    list.replaceChildren();
    const snapshot = state?.snapshot;
    node('tenantUsersCount').textContent = snapshot ? `(${snapshot.users.length} of ${snapshot.total})` : '';
    node('tenantUsersVersion').textContent = snapshot ? `Access v${snapshot.version}` : '';
    node('tenantUsersDirectory').textContent = !snapshot ? '' : snapshot.directoryConfigured
      ? 'Connected to the enterprise directory. Only existing, enabled directory members can be assigned.'
      : 'Enterprise directory not connected. Ask your operator to configure the organization directory connection. ' +
        'Adding and searching people are unavailable; existing memberships below are not a directory listing.';
    if (!snapshot?.users.length) {
      list.append(element('div', 'tenant-users-empty', state?.loading ? 'Loading assigned people…'
        : snapshot ? 'No people assigned yet.' : 'Load workspace access to view assigned people.'));
    } else {
      for (const user of snapshot.users) {
        const row = element('div', 'tenant-users-row');
        row.append(avatar(user.profile, user.objectId));
        const info = identity(user.profile, user.objectId);
        const badges = element('div', 'tenant-users-badges');
        badges.append(element('span', 'tenant-users-badge', user.status));
        badges.append(element('span', user.legacyRestricted ? 'tenant-users-badge restricted' : 'tenant-users-badge',
          user.legacyRestricted ? 'Operator-managed restricted access' : roleName(user.role)));
        if (user.directoryState !== 'Active')
          badges.append(element('span', 'tenant-users-badge', directoryLabel(user.directoryState)));
        info.append(badges);
        const manage = action('Manage', 'tenantUsersDetails_arg', [user.objectId], !editable());
        manage.className = 'chevron-btn';
        manage.setAttribute('aria-label', `Manage access for ${personName(user.profile) || user.objectId}`);
        row.append(info, manage);
        list.append(row);
      }
    }
    controls();
  }
  function directoryLabel(value) {
    return {
      Active: 'Directory account active', Disabled: 'Directory account disabled',
      NotFound: 'Not found in directory', NotConnected: 'Directory not connected',
      Unavailable: 'Directory temporarily unavailable'
    }[value] || 'Directory state unknown';
  }
  async function errorMessage(response) {
    const fallback = {
      400: 'The access change was rejected. At least one active workspace admin must remain.',
      403: 'You are not authorized to manage this workspace.',
      409: 'Access changed on the server.',
      503: 'The enterprise directory is unavailable.'
    }[response.status] || 'The access request could not be completed.';
    try {
      const body = await response.json();
      return typeof body.error === 'string' && body.error.trim() ? body.error : fallback;
    } catch {
      return fallback;
    }
  }
  async function load(owner, saved = false) {
    if (!current(owner)) return;
    owner.loading = true;
    owner.snapshot = null;
    closeDialogs();
    notice(saved ? 'Access changes saved. Refreshing assigned people…' : 'Loading workspace access…');
    renderList();
    try {
      const response = await fetch(`${endpoint(owner)}?offset=0&limit=50`);
      if (!response.ok) throw new Error(await errorMessage(response));
      const snapshot = await response.json();
      if (!current(owner)) return;
      validateSnapshot(snapshot);
      owner.snapshot = snapshot;
      owner.blocked = false;
      notice(saved ? 'Access changes saved.' : 'Access is up to date.');
    } catch (error) {
      if (!current(owner)) return;
      owner.blocked = true;
      notice(`${saved ? 'The change was saved, but the list could not be refreshed. ' : ''}` +
        `${error.message} Reload access before making changes.`, true);
    } finally {
      if (current(owner)) {
        owner.loading = false;
        renderList();
      }
    }
  }
  function setTenant(tenant) {
    if (state?.tenant === tenant) return;
    closeDialogs();
    state = tenant ? { tenant, snapshot: null, loading: false, paging: false, busy: false, blocked: false } : null;
    notice(tenant ? '' : 'Select a workspace you administer to manage people.');
    renderList();
    if (state) return load(state);
  }
  function reload() {
    if (!state || state.loading || state.paging || state.busy) return;
    return load(state);
  }
  function validateSnapshot(snapshot) {
    if (!Number.isSafeInteger(snapshot.version) || snapshot.version < 0 || !Array.isArray(snapshot.users) ||
        !Number.isSafeInteger(snapshot.total) || snapshot.total < 0 ||
        (snapshot.nextOffset !== null && (!Number.isSafeInteger(snapshot.nextOffset) || snapshot.nextOffset < 0)))
      throw new Error('The server returned an invalid access snapshot.');
  }
  async function more() {
    if (!editable() || !Number.isSafeInteger(state.snapshot.nextOffset)) return;
    const owner = state;
    const offset = owner.snapshot.nextOffset;
    owner.paging = true;
    notice('Loading more assigned people…');
    renderList();
    try {
      const response = await fetch(`${endpoint(owner)}?offset=${offset}&limit=50`);
      if (!response.ok) throw new Error(await errorMessage(response));
      const snapshot = await response.json();
      if (!current(owner)) return;
      validateSnapshot(snapshot);
      if (snapshot.version !== owner.snapshot.version)
        throw new Error('Access changed while loading people. Reload access to view a consistent list.');
      if (snapshot.nextOffset !== null && snapshot.nextOffset <= offset)
        throw new Error('The server did not advance the access page. Reload access.');
      const seen = new Set(owner.snapshot.users.map(user => user.objectId.toLowerCase()));
      for (const user of snapshot.users) {
        if (!seen.has(user.objectId.toLowerCase())) owner.snapshot.users.push(user);
        seen.add(user.objectId.toLowerCase());
      }
      owner.snapshot.nextOffset = snapshot.nextOffset;
      owner.snapshot.total = snapshot.total;
      notice('Access is up to date.');
    } catch (error) {
      if (!current(owner)) return;
      owner.blocked = true;
      notice(`${error.message} Reload access before making changes.`, true);
    } finally {
      if (current(owner)) {
        owner.paging = false;
        renderList();
      }
    }
  }

  function eligible(person) {
    return guid.test(person?.objectId) && person.member === true && person.accountEnabled === true &&
      !state.snapshot.users.some(user => user.objectId.toLowerCase() === person.objectId.toLowerCase() &&
        (user.status !== 'Removed' || user.legacyRestricted));
  }
  function renderResults() {
    const list = node('tenantUsersSearchResults');
    list.replaceChildren();
    for (const person of results) {
      const row = element('div', 'tenant-users-row');
      const picked = selected.has(person.objectId.toLowerCase());
      row.append(avatar(person, person.objectId), identity(person, person.objectId),
        action(picked ? 'Selected' : 'Select', 'tenantUsersSelect_arg', [person.objectId],
          !editable() || picked || selected.size >= 25));
      list.append(row);
    }
  }
  function roleSelect(id, value) {
    const select = element('select', 'clean-select');
    select.id = id;
    for (const [role, label] of roles) {
      const option = element('option', '', label);
      option.value = role;
      select.append(option);
    }
    select.value = value;
    return select;
  }
  function renderSelected() {
    node('tenantUsersSelected').replaceChildren();
    node('tenantUsersSelectedCount').textContent = `(${selected.size}/25)`;
    for (const { person, role } of selected.values()) {
      const row = element('div', 'tenant-users-row');
      const label = element('label', '', 'Role');
      const select = roleSelect(`tenant-user-role-${person.objectId}`, role);
      label.htmlFor = select.id;
      select.setAttribute('data-change', 'tenantUsersSelectedRole_arg');
      select.setAttribute('data-args', JSON.stringify([person.objectId]));
      label.append(select);
      row.append(avatar(person, person.objectId), identity(person, person.objectId), label,
        action('Remove selection', 'tenantUsersUnselect_arg', [person.objectId], !editable()));
      node('tenantUsersSelected').append(row);
    }
    controls();
  }
  function openAdd() {
    if (!editable() || !state.snapshot.directoryConfigured) return;
    selected = new Map();
    results = [];
    searchRevision++;
    node('tenantUsersSearch').value = '';
    node('tenantUsersSearch').disabled = false;
    node('tenantUsersSearchStatus').textContent = 'Enter at least 2 characters. Search uses your connected directory.';
    renderResults();
    renderSelected();
    node(dialogIds.add).showModal();
    node('tenantUsersSearch').focus();
  }
  function search() {
    clearTimeout(searchTimer);
    const revision = ++searchRevision;
    results = [];
    renderResults();
    if (!editable() || !state.snapshot.directoryConfigured || !node(dialogIds.add).open) return;
    const query = node('tenantUsersSearch').value.trim();
    if (query.length < 2 || query.length > 80) {
      node('tenantUsersSearchStatus').textContent = 'Enter between 2 and 80 characters.';
      return;
    }
    node('tenantUsersSearchStatus').textContent = 'Searching enterprise directory…';
    const owner = state;
    searchTimer = setTimeout(() => searchDirectory(owner, revision, query), 250);
  }
  async function searchDirectory(owner, revision, query) {
    const valid = () => current(owner) && revision === searchRevision && node(dialogIds.add).open;
    if (!valid()) return;
    try {
      const response = await fetch(`${endpoint(owner)}/directory?q=${encodeURIComponent(query)}`);
      if (!response.ok) throw new Error(await errorMessage(response));
      const body = await response.json();
      if (!valid()) return;
      results = (Array.isArray(body.people) ? body.people : []).filter(eligible).slice(0, 20);
      renderResults();
      node('tenantUsersSearchStatus').textContent = results.length
        ? `${results.length} matching people. Already assigned, disabled and non-member accounts are excluded.`
        : 'No eligible employees found. Try another name or work email. You cannot create employees here.';
    } catch (error) {
      if (!valid()) return;
      results = [];
      renderResults();
      node('tenantUsersSearchStatus').textContent = `${error.message} No directory results were loaded.`;
    }
  }
  function selectPerson(objectId) {
    if (!editable() || !node(dialogIds.add).open || selected.size >= 25) return;
    const person = results.find(item => item.objectId === objectId);
    if (!person || !eligible(person)) return;
    selected.set(objectId.toLowerCase(), { person, role: 'User' });
    renderSelected();
    renderResults();
  }
  function unselect(objectId) {
    if (!editable()) return;
    selected.delete(objectId.toLowerCase());
    renderSelected();
    renderResults();
  }
  function selectedRole(objectId, value) {
    if (!editable() || !roles.some(role => role[0] === value)) return;
    const item = selected.get(objectId.toLowerCase());
    if (item) item.role = value;
  }
  function review(changes, title, message) {
    if (!editable() || !changes.length || changes.length > 25) return;
    pending = { owner: state, version: state.snapshot.version, changes };
    node('tenantUsersConfirmTitle').textContent = title;
    node('tenantUsersConfirmText').textContent = message;
    node('tenantUsersConfirmStatus').textContent = '';
    controls();
    node(dialogIds.confirm).showModal();
    node('tenantUsersConfirmCancel').focus();
  }
  function reviewAdd() {
    if (!editable() || !state.snapshot.directoryConfigured) return;
    const items = [...selected.values()];
    if (!items.length || items.some(item => !eligible(item.person))) return;
    const changes = items.map(item => ({ objectId: item.person.objectId, role: item.role, status: 'Active' }));
    const admins = changes.filter(change => change.role === 'TenantAdmin').length;
    review(changes, 'Confirm New Assignments',
      `Assign ${changes.length} people with workspace-wide documents? ${admins} will be workspace admins with full ` +
      'processing, settings and people-management access. Membership applies across web, email and Teams.');
  }
  function selectedUser() {
    return state?.snapshot?.users.find(user => user.objectId === detailId);
  }
  function canActivate(user) {
    return !user.legacyRestricted && state.snapshot.directoryConfigured && user.directoryState === 'Active';
  }
  function openDetails(objectId) {
    if (!editable()) return;
    const user = state.snapshot.users.find(item => item.objectId === objectId);
    if (!user) return;
    detailId = objectId;
    const container = node('tenantUsersDetails');
    container.replaceChildren();
    const heading = element('div', 'tenant-users-row');
    heading.append(avatar(user.profile, objectId), identity(user.profile, objectId));
    container.append(heading);
    const details = element('dl', 'tenant-users-details-grid');
    for (const [label, value] of [
      ['Object ID', objectId], ['Work Email', user.profile?.email || user.profile?.userPrincipalName || 'Not available'],
      ['Job Title', user.profile?.jobTitle || 'Not available'], ['Department', user.profile?.department || 'Not available'],
      ['Office', user.profile?.officeLocation || 'Not available'], ['Directory', directoryLabel(user.directoryState)],
      ['Status', user.status], ['Source', user.source], ['Role', user.legacyRestricted
        ? 'Operator-managed restricted access' : roleName(user.role)]
    ]) details.append(element('dt', '', label), element('dd', '', value));
    container.append(details);
    const management = node('tenantUsersDetailControls');
    management.replaceChildren();
    if (user.legacyRestricted) {
      management.append(element('p', '', 'This imported membership has operator-managed restricted access. ' +
        'Only disabling or removing it is supported here. Its role and document access cannot be broadened.'));
    } else if (user.status !== 'Removed') {
      const label = element('label', '', 'Workspace Role');
      label.htmlFor = 'tenantUsersDetailRole';
      const select = roleSelect(label.htmlFor, user.role);
      management.append(label, select, element('p', '', 'User: read and Ask AI. Workspace Admin: full workspace management.'));
      management.append(action('Review Role Change', 'tenantUsersReviewRole'));
    }
    const buttons = element('div', 'tenant-users-dialog-actions');
    if (user.status === 'Active')
      buttons.append(action('Disable Access', 'tenantUsersReviewStatus_arg', ['Disabled']));
    if (user.status !== 'Active' && canActivate(user))
      buttons.append(action(user.status === 'Removed' ? 'Restore Access' : 'Enable Access',
        'tenantUsersReviewStatus_arg', ['Active']));
    if (user.status !== 'Removed')
      buttons.append(action('Remove Access', 'tenantUsersReviewStatus_arg', ['Removed']));
    management.append(buttons);
    node(dialogIds.details).showModal();
  }
  function reviewRole() {
    const user = selectedUser();
    if (!editable() || !user || user.legacyRestricted || user.status === 'Removed') return;
    const role = node('tenantUsersDetailRole').value;
    if (!roles.some(item => item[0] === role) || role === user.role) return;
    if (role === 'TenantAdmin' && !canActivate(user)) {
      managementNotice('Cannot grant workspace admin while the directory account is not confirmed active.');
      return;
    }
    review([{ objectId: user.objectId, role, status: user.status }], 'Confirm Role Change',
      `Change ${personName(user.profile) || user.objectId} from ${roleName(user.role)} to ${roleName(role)}? ` +
      (role === 'TenantAdmin' ? 'Workspace admins can process documents and manage workspace settings and people.'
        : 'Users can read documents and Ask AI, but cannot process documents or manage settings and people.'));
  }
  function managementNotice(message) {
    const target = node('tenantUsersDetailControls');
    const text = element('p', 'tenant-users-status', message);
    text.setAttribute('role', 'alert');
    target.append(text);
  }
  function reviewStatus(status) {
    const user = selectedUser();
    if (!editable() || !user || !['Active', 'Disabled', 'Removed'].includes(status) || status === user.status) return;
    if (user.legacyRestricted && (status === 'Active' || user.status === 'Removed')) return;
    if (status === 'Active' && !canActivate(user)) return;
    review([{ objectId: user.objectId, role: user.role, status }], `Confirm ${status} Access`,
      `Set access for ${personName(user.profile) || user.objectId} to ${status}? ` +
      (status === 'Active' ? 'This restores membership across the workspace, web, email and Teams.'
        : 'This revokes membership across the workspace, Admin Studio, web, email and Teams. ' +
          'At least one active workspace admin must remain.'));
  }
  async function confirm() {
    const operation = pending;
    if (!editable() || !operation || operation.owner !== state ||
        operation.version !== state.snapshot.version) return;
    const owner = state;
    owner.busy = true;
    node('tenantUsersConfirmStatus').textContent = 'Saving access change. Do not submit again…';
    clearTimeout(searchTimer);
    searchRevision++;
    controls();
    renderList();
    try {
      const response = await fetch(endpoint(owner), {
        method: 'PUT', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ version: operation.version, changes: operation.changes })
      });
      if (!response.ok) {
        const message = await errorMessage(response);
        throw new Error(`${message} ${response.status === 409
          ? 'Version conflict: reload access and review the latest memberships before making a new change.'
          : 'Reload access before making another change.'}`);
      }
      if (!current(owner)) return;
      try {
        await root.DocIntelIdentity?.refresh?.();
      } catch {
        if (current(owner)) {
          owner.blocked = true;
          closeDialogs();
          notice('Access changes saved. Your current permissions could not be refreshed. ' +
            'Reload the page before making more changes.', true);
        }
        return;
      }
      if (!current(owner)) return;
      owner.busy = false;
      await load(owner, true);
    } catch (error) {
      if (!current(owner)) return;
      owner.blocked = true;
      closeDialogs();
      notice(`${error.message} No automatic retry was made. If the outcome is uncertain, reload access ` +
        'to confirm it before submitting anything else.', true);
    } finally {
      if (current(owner)) {
        owner.busy = false;
        renderList();
        if (!node(dialogIds.confirm).open) node('tenantUsersReload').focus();
      }
    }
  }

  root.DocIntelTenantUsers = Object.freeze({ setTenant, closeDialogs });
  root.DocIntelActions.register({
    tenantUsersOpenAdd: { arity: 0, run: openAdd },
    tenantUsersReload: { arity: 0, run: reload },
    tenantUsersMore: { arity: 0, run: more },
    tenantUsersSearch: { arity: 0, run: search },
    tenantUsersSelect_arg: { arity: 1, run: args => selectPerson(args[0]) },
    tenantUsersUnselect_arg: { arity: 1, run: args => unselect(args[0]) },
    tenantUsersSelectedRole_arg: { arity: 1, run: function (args) { selectedRole(args[0], this.value); } },
    tenantUsersReviewAdd: { arity: 0, run: reviewAdd },
    tenantUsersDetails_arg: { arity: 1, run: args => openDetails(args[0]) },
    tenantUsersReviewRole: { arity: 0, run: reviewRole },
    tenantUsersReviewStatus_arg: { arity: 1, run: args => reviewStatus(args[0]) },
    tenantUsersConfirm: { arity: 0, run: confirm },
    tenantUsersClose_arg: { arity: 1, run: args => closeDialog(args[0]) }
  });
})(globalThis);
