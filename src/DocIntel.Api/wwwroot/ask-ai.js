(() => {
  const pollIntervalMs = 1000;
  const maxWaitMs = 30 * 60 * 1000;

  function runUrl(tenantId, runId) {
    const base = `/api/harness/tenants/${encodeURIComponent(tenantId)}/ask/runs`;
    return runId ? `${base}/${encodeURIComponent(runId)}` : base;
  }

  async function requestJson(url, options = {}) {
    const response = await fetch(url, { ...options, signal: AbortSignal.timeout(30000) });
    let data;
    try {
      data = await response.json();
    } catch {
      throw new Error('Ask AI returned an unreadable response.');
    }
    if (!response.ok) {
      const error = new Error(response.status === 404 && options.method !== 'POST'
        ? 'This Ask AI run is no longer available. It may have been lost when the server restarted.'
        : data.error || data.title || `Ask AI request failed (${response.status}).`);
      error.status = response.status;
      throw error;
    }
    return data;
  }

  async function submit(tenantId, question) {
    const data = await requestJson(runUrl(tenantId), {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ question, replyChannel: { channelType: 'Web' } })
    });
    if (typeof data.runId !== 'string' || !data.runId) {
      throw new Error('Ask AI did not return a run identifier. The question was not automatically retried.');
    }
    return data.runId;
  }

  async function waitForRun(tenantId, runId, onProgress) {
    const startedAt = Date.now();
    try {
      while (Date.now() - startedAt < maxWaitMs) {
        const run = await requestJson(runUrl(tenantId, runId));
        if (run.status === 'Completed' || run.status === 'Failed') {
          if (run.status === 'Completed' && (typeof run.answer !== 'string' || !run.answer.trim())) {
            throw new Error('Ask AI finished without a usable answer.');
          }
          return run;
        }
        if (run.status !== 'Queued' && run.status !== 'Running') {
          throw new Error('Ask AI returned an unrecognized run status.');
        }
        if (onProgress) onProgress(run);
        await new Promise(resolve => setTimeout(resolve, pollIntervalMs));
      }
      throw new Error('This run is still processing. Reopen the chat to check it without submitting again.');
    } catch (error) {
      error.resumable = error.status !== 404;
      throw error;
    }
  }

  globalThis.DocIntelAskAi = { submit, waitForRun };
})();
