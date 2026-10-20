(function (root) {
  'use strict';
  async function read(url) {
    const response = await fetch(url);
    const body = await response.json();
    if (!response.ok) {
      const error = new Error(body.title || body.error || 'The review could not be loaded.');
      error.status = response.status;
      error.canReview = response.headers.get('X-DocIntel-Can-Review') === 'true';
      throw error;
    }
    return { body, tag: response.headers.get('ETag'),
      canReview: response.headers.get('X-DocIntel-Can-Review') === 'true' };
  }
  async function change(base, action, command, tag) {
    if (action !== 'start' && !/^"review-[1-9]\d*"$/.test(tag || ''))
      throw new Error('Reload the review before changing it.');
    let response;
    try {
      response = await fetch(`${base}/${encodeURIComponent(action)}`, {
        method: 'POST', headers: { 'Content-Type': 'application/json', ...(tag ? { 'If-Match': tag } : {}) },
        body: JSON.stringify(command || {})
      });
    } catch {
      throw new Error('The review action could not be confirmed. Reload before trying again.');
    }
    const body = await response.json();
    if (!response.ok) throw new Error(body.title || body.error || 'The review action failed. Reload before trying again.');
    return { body, tag: response.headers.get('ETag') };
  }
  function contains(polygon, x, y) {
    if (!Array.isArray(polygon) || polygon.length !== 8 || !polygon.every(Number.isFinite)) return false;
    let inside = false;
    for (let current = 0, previous = 3; current < 4; previous = current++) {
      const ax = polygon[current * 2], ay = polygon[current * 2 + 1];
      const bx = polygon[previous * 2], by = polygon[previous * 2 + 1];
      if ((ay > y) !== (by > y) && x < (bx - ax) * (y - ay) / (by - ay) + ax) inside = !inside;
    }
    return inside;
  }
  root.DocIntelReview = Object.freeze({ read, change, contains });
})(globalThis);
