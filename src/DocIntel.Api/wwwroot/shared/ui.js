function escapeHtml(str) {
  return String(str == null ? '' : str).replace(/[&<>"']/g,
    character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
}

function escapeJsArg(value) {
  return escapeHtml(JSON.stringify(String(value ?? '')));
}

function showToast(message) {
  const toast = document.getElementById('toast');
  toast.textContent = message;
  toast.style.display = 'block';
  setTimeout(() => { toast.style.display = 'none'; }, 2200);
}

async function copyTextValue(value) {
  if (!globalThis.navigator?.clipboard?.writeText) {
    showToast('Clipboard access is unavailable. Select and copy the value manually.');
    return;
  }
  try {
    await navigator.clipboard.writeText(String(value ?? ''));
    showToast('Copied to clipboard');
  } catch {
    showToast('The value could not be copied. Check clipboard permission or copy it manually.');
  }
}
