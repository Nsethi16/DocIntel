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

function pdfPreviewMarkup(url, title) {
  return `<div class="pdf-preview" data-pdf-url="${escapeHtml(url)}">
    <p class="pdf-preview-status" role="status">Loading PDF...</p>
    <div class="pdf-preview-actions" hidden>
      <button type="button" class="text-link-btn">Try Again</button>
      <a class="text-link-btn" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">Open PDF in New Tab ↗</a>
    </div>
    <iframe class="pdf-full-frame" title="${escapeHtml(title)}" hidden></iframe>
  </div>`;
}

async function loadPdfPreview(preview) {
  const frame = preview.querySelector('iframe');
  const status = preview.querySelector('.pdf-preview-status');
  const actions = preview.querySelector('.pdf-preview-actions');
  const retry = actions.querySelector('button');
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 15000);
  retry.onclick = () => loadPdfPreview(preview);
  actions.hidden = true;
  frame.hidden = true;
  status.hidden = false;
  status.setAttribute('role', 'status');
  status.textContent = 'Loading PDF...';
  try {
    const response = await fetch(preview.dataset.pdfUrl, {
      headers: { Range: 'bytes=0-4' }, signal: controller.signal
    });
    await response.body?.cancel();
    if (!preview.isConnected) return;
    if (!response.ok) {
      status.textContent = response.status === 404
        ? preview.dataset.pdfUrl.endsWith('?translated=true')
          ? 'The translated PDF is not available in storage. The original is shown separately if available. ' +
            'Ask a workspace administrator to restore the translated PDF or process the original file again.'
          : 'This PDF is not available in storage. Extracted results can remain available without the PDF. ' +
            'Ask a workspace administrator to restore it or upload the original file again.'
        : response.status === 401 || response.status === 403
          ? 'Sign in again or request access to this document to view its PDF.'
          : `The PDF could not be loaded (HTTP ${response.status}). Try again or open it in a new tab.`;
      const correlation = response.headers.get('X-DocIntel-Correlation-Id');
      if (correlation && /^[A-Za-z0-9:._-]{1,160}$/.test(correlation))
        status.textContent += ` Reference: ${correlation}.`;
    } else if (!/^application\/pdf(?:;|$)/i.test(response.headers.get('Content-Type') || '')) {
      status.textContent = 'The server did not return a PDF. Try signing in again or open it in a new tab.';
    } else {
      frame.src = preview.dataset.pdfUrl;
      frame.hidden = false;
      status.hidden = true;
      return;
    }
  } catch {
    if (!preview.isConnected) return;
    status.textContent = controller.signal.aborted
      ? 'The PDF request timed out. Try again or open it in a new tab.'
      : 'The PDF request could not be completed. Check your connection and try again.';
  } finally {
    clearTimeout(timeout);
  }
  status.setAttribute('role', 'alert');
  actions.hidden = false;
}

function startPdfPreviews(body) {
  body.querySelectorAll('.pdf-preview').forEach(preview => { void loadPdfPreview(preview); });
}

function documentJobStatus(job) {
  if (job.status === 'Completed') {
    const warnings = job.hasWarnings === true ||
      (job.executionTrace || []).some(t => t.executed === true && t.succeeded === false) ||
      (job.segments || []).some(s => s.translationErrorCode || s.translationErrorMessage);
    return { label: warnings ? 'Completed with warnings' : 'Completed', kind: warnings ? 'warning' : '' };
  }
  return { label: job.status, kind: job.status === 'Failed' ? 'failed' : 'running' };
}

function featureExecutionTrace(job, executorName) {
  return (job.executionTrace || []).filter(t => t.executorName === executorName).at(-1);
}

function executionTraceStatus(trace) {
  if (!trace.executed) return 'Skipped';
  if (trace.succeeded === false) return 'Failed';
  return trace.succeeded === true ? 'Succeeded' : 'Executed (outcome unknown)';
}

function updateDocumentStatusNotice(job) {
  let notice = document.getElementById('documentStatusNotice');
  if (!notice) {
    const body = document.getElementById('docCenterBody');
    if (!body) return;
    notice = document.createElement('div');
    notice.id = 'documentStatusNotice';
    notice.className = 'document-status-notice';
    notice.setAttribute('role', 'status');
    notice.setAttribute('aria-live', 'polite');
    notice.setAttribute('aria-atomic', 'true');
    body.before(notice);
  }
  const status = job ? documentJobStatus(job) : null;
  let text = '';
  if (job?.status === 'Queued' || job?.status === 'Running') {
    const completedSteps = (job.executionTrace || []).filter(t => t.executed === true).length;
    text = `${job.status === 'Queued' ? 'Queued for processing' : 'Processing document'} ` +
      `through Agent Harness (${completedSteps} steps executed). Available results are shown as they arrive.`;
  } else if (job?.status === 'Failed') {
    text = `Processing failed: ${job.errorMessage || 'This run could not complete.'} ` +
      'Stored results, if any, remain available. Missing results may not have been generated. ' +
      'See Metadata for execution details.';
  } else if (status?.kind === 'warning') {
    const translationFailed = (job.segments || []).some(s => s.translationErrorCode || s.translationErrorMessage) ||
      (job.executionTrace || []).some(t =>
        t.executorName === 'TranslateDocumentsExecutor' && t.executed === true && t.succeeded === false);
    text = 'Completed with warnings. ' +
      (translationFailed ? 'Translation failed for one or more documents. ' : 'One or more features failed. ') +
      'Available results remain accessible. See View Document for PDF availability and Metadata for execution details.';
  }
  if (notice.textContent !== text) notice.textContent = text;
  notice.dataset.kind = status?.kind || '';
  notice.hidden = !text;
}

function unavailableResultMessage(job, executorName, featureName, missingMessage) {
  const trace = featureExecutionTrace(job, executorName);
  if (trace?.executed === false)
    return `${featureName} was skipped for this run. ${missingMessage} See Metadata for execution details.`;
  if (trace?.executed === true && trace.succeeded === false)
    return `${featureName} failed. ${missingMessage} See Metadata for execution details.`;
  if (job.status === 'Queued' || job.status === 'Running')
    return `${missingMessage} Processing is still in progress.`;
  if (trace?.executed === true && trace.succeeded === true)
    return `${featureName} succeeded. ${missingMessage}`;
  return `${missingMessage} ${job.status === 'Failed' ? 'This feature may not have run. ' : ''}` +
    'No execution outcome was recorded for this feature; see Metadata for details.';
}

function translationPreviewNote(job, slice) {
  if (!slice || slice.wasTranslated === true) return null;
  const trace = featureExecutionTrace(job, 'TranslateDocumentsExecutor');
  if (slice.translationErrorCode || slice.translationErrorMessage) {
    return { kind: 'warning', text: 'Translation unavailable. ' +
      (slice.translationErrorMessage || 'Translation failed for this document.') +
      ' The original PDF is requested below; access and storage availability are checked separately.' };
  }
  const source = String(slice.detectedLanguage || '').trim();
  const target = String(slice.targetLanguage || '').trim();
  if (source && target && source.toLowerCase() === target.toLowerCase()) {
    return { kind: '', text: `Translation not needed: the detected and target language are ${source.toUpperCase()}. ` +
      'No translated PDF was generated. The preview below requests the original PDF.' };
  }
  if (trace?.executed === true && trace.succeeded === false)
    return { kind: 'warning', text: 'Translation unavailable. Translation failed during this run. ' +
      'The original PDF is requested below; access and storage availability are checked separately.' };
  return { kind: '', text: job.status === 'Queued' || job.status === 'Running'
    ? 'No translated PDF is available yet. Processing is still in progress; the preview requests the original PDF.'
    : 'No translated PDF was generated for this document. The preview below requests the original PDF.' };
}
