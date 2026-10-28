(function (root) {
  'use strict';
  const document = root.document;
  const themeKey = 'docintel_appearance_v1';
  const widthKey = 'docintel_sidebar_width_v1';
  const choices = ['system', 'light', 'dark'];
  const scheme = root.matchMedia('(prefers-color-scheme: dark)');
  const narrow = root.matchMedia('(max-width: 700px)');
  const controls = [];
  let notice = '';
  let sidebar;
  let divider;
  let drag;

  function report(message) {
    notice = message;
    if (!document.body) return;
    let node = document.getElementById('shellPreferenceNotice');
    if (!node) {
      node = document.createElement('p');
      node.id = 'shellPreferenceNotice';
      node.className = 'shell-notice';
      node.setAttribute('role', 'status');
      document.body.appendChild(node);
    }
    node.textContent = message;
  }

  function storageError(error) {
    if (!['SecurityError', 'QuotaExceededError', 'NS_ERROR_DOM_QUOTA_REACHED'].includes(error?.name)) throw error;
    report('Browser preferences are unavailable. Appearance and sidebar changes apply only to this page.');
  }

  function read(key) {
    try { return root.localStorage.getItem(key); }
    catch (error) { storageError(error); return null; }
  }

  function write(key, value) {
    try {
      if (value === null) root.localStorage.removeItem(key);
      else root.localStorage.setItem(key, String(value));
      document.getElementById('shellPreferenceNotice')?.remove();
      notice = '';
    } catch (error) { storageError(error); }
  }

  function parseTheme(value) {
    if (value === null) return 'system';
    if (choices.includes(value)) return value;
    report('The saved appearance preference is invalid. System appearance is being used.');
    return 'system';
  }

  function parseWidth(value) {
    if (value === null) return null;
    const number = Number(value);
    if (Number.isInteger(number) && number >= 200 && number <= 480) return number;
    report('The saved sidebar width is invalid. The default width is being used.');
    return null;
  }

  let preference = parseTheme(read(themeKey));
  let desiredWidth = parseWidth(read(widthKey));

  function applyTheme() {
    const theme = preference === 'system' ? (scheme.matches ? 'dark' : 'light') : preference;
    document.documentElement.setAttribute('data-theme', theme);
    document.documentElement.setAttribute('data-appearance', preference);
    for (const control of controls) control.value = preference;
    document.dispatchEvent(new root.CustomEvent('docintel:appearance-change', { detail: { theme } }));
  }

  function bounds() {
    const maximum = narrow.matches ? Math.min(480, Math.floor(root.innerWidth * 0.85))
      : Math.max(200, Math.min(480, root.innerWidth - 360));
    return { minimum: Math.min(200, maximum), maximum };
  }

  function applyWidth() {
    if (!sidebar) return;
    const { minimum, maximum } = bounds();
    const width = Math.round(Math.max(minimum,
      Math.min(maximum, desiredWidth ?? Number(sidebar.dataset.sidebarDefault))));
    document.documentElement.style.setProperty('--sidebar-width', `${width}px`);
    divider.setAttribute('aria-valuemin', String(minimum));
    divider.setAttribute('aria-valuemax', String(maximum));
    divider.setAttribute('aria-valuenow', String(width));
    divider.setAttribute('aria-valuetext', `${width} pixels`);
  }

  function changeWidth(value, persist) {
    const { minimum, maximum } = bounds();
    desiredWidth = Math.round(Math.max(minimum, Math.min(maximum, value)));
    applyWidth();
    if (persist) write(widthKey, desiredWidth);
  }

  function endDrag(cancelled) {
    if (!drag) return;
    const previous = drag;
    drag = null;
    try {
      if (cancelled) {
        desiredWidth = previous.desired;
        applyWidth();
      } else write(widthKey, desiredWidth);
    } finally {
      document.documentElement.classList.remove('sidebar-resizing');
      if (divider.hasPointerCapture(previous.id)) divider.releasePointerCapture(previous.id);
    }
  }

  function initialize() {
    for (const host of document.querySelectorAll('[data-appearance-host]')) {
      const label = document.createElement('label');
      label.textContent = 'Appearance';
      const select = document.createElement('select');
      select.id = 'appearanceSelect';
      label.htmlFor = select.id;
      select.setAttribute('aria-label', 'Appearance');
      for (const value of choices) {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = value[0].toUpperCase() + value.slice(1);
        select.appendChild(option);
      }
      select.value = preference;
      select.addEventListener('change', () => {
        if (!choices.includes(select.value)) {
          report('Choose System, Light or Dark appearance.');
          select.value = preference;
          return;
        }
        preference = select.value;
        write(themeKey, preference);
        applyTheme();
      });
      controls.push(select);
      host.append(label, select);
    }

    sidebar = document.querySelector('[data-resizable-sidebar]');
    divider = sidebar?.querySelector('.sidebar-resizer');
    if (sidebar && divider) {
      applyWidth();
      divider.addEventListener('pointerdown', event => {
        if (event.button !== 0 || drag || narrow.matches || sidebar.classList.contains('collapsed')) return;
        event.preventDefault();
        divider.focus();
        drag = {
          id: event.pointerId, desired: desiredWidth, startX: event.clientX,
          width: Number(divider.getAttribute('aria-valuenow'))
        };
        divider.setPointerCapture(event.pointerId);
        document.documentElement.classList.add('sidebar-resizing');
      });
      divider.addEventListener('pointermove', event => {
        if (drag?.id !== event.pointerId) return;
        changeWidth(drag.width + event.clientX - drag.startX, false);
      });
      divider.addEventListener('pointerup', event => { if (drag?.id === event.pointerId) endDrag(false); });
      divider.addEventListener('pointercancel', () => endDrag(true));
      divider.addEventListener('lostpointercapture', () => endDrag(true));
      divider.addEventListener('dblclick', () => {
        desiredWidth = null;
        write(widthKey, null);
        applyWidth();
      });
      divider.addEventListener('keydown', event => {
        if (event.key === 'Escape' && drag) { event.preventDefault(); endDrag(true); return; }
        if (narrow.matches || sidebar.classList.contains('collapsed')) return;
        const { minimum, maximum } = bounds();
        const current = Number(divider.getAttribute('aria-valuenow'));
        const step = event.shiftKey ? 30 : 10;
        const next = { ArrowLeft: current - step, ArrowRight: current + step, Home: minimum, End: maximum }[event.key];
        if (next === undefined) return;
        event.preventDefault();
        changeWidth(next, true);
      });
      root.addEventListener('resize', applyWidth);
    }
    if (notice) report(notice);
  }

  applyTheme();
  scheme.addEventListener('change', () => { if (preference === 'system') applyTheme(); });
  root.addEventListener('storage', event => {
    if (event.key === themeKey || event.key === null) {
      preference = parseTheme(event.key === null ? read(themeKey) : event.newValue);
      applyTheme();
    }
    if (event.key === widthKey || event.key === null) {
      desiredWidth = parseWidth(event.key === null ? read(widthKey) : event.newValue);
      applyWidth();
    }
  });
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', initialize, { once: true });
  else initialize();
})(globalThis);
