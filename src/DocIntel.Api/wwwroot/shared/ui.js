function escapeHtml(str) {
  return String(str == null ? '' : str).replace(/[&<>"']/g,
    character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
}

function escapeJsArg(value) {
  return escapeHtml(JSON.stringify(String(value ?? '')));
}

let docIntelToastTimer;

function showToast(message, options = {}) {
  if (globalThis.DocIntelUX) {
    return globalThis.DocIntelUX.inform(message, { kind: 'success', ...options });
  }
  let toast = document.getElementById('toast');
  if (!toast) {
    toast = document.createElement('div');
    toast.id = 'toast';
    toast.className = 'toast';
    document.body.appendChild(toast);
  }
  clearTimeout(docIntelToastTimer);
  const isError = options.kind === 'error';
  toast.setAttribute('role', isError ? 'alert' : 'status');
  toast.setAttribute('aria-live', isError ? 'assertive' : 'polite');
  toast.setAttribute('aria-atomic', 'true');
  toast.replaceChildren();
  const text = document.createElement('span');
  text.textContent = typeof message === 'string' ? message : 'The operation could not be completed.';
  const dismiss = document.createElement('button');
  dismiss.type = 'button';
  dismiss.textContent = 'Dismiss';
  dismiss.addEventListener('click', () => {
    clearTimeout(docIntelToastTimer);
    toast.style.display = 'none';
  });
  toast.appendChild(text);
  toast.appendChild(dismiss);
  toast.style.display = 'block';
  if (!isError && options.kind !== 'warning' && options.persistent !== true) {
    docIntelToastTimer = setTimeout(() => { toast.style.display = 'none'; }, 5000);
  }
}

async function copyTextValue(value) {
  if (!globalThis.navigator?.clipboard?.writeText) {
    showToast('Clipboard access is unavailable. Select and copy the value manually.', { kind: 'error' });
    return;
  }
  try {
    await navigator.clipboard.writeText(String(value ?? ''));
    showToast('Copied to clipboard');
  } catch {
    showToast('The value could not be copied. Check clipboard permission or copy it manually.', { kind: 'error' });
  }
}
