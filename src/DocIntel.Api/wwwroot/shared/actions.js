(function (root) {
  'use strict';
  const handlers = new Map();
  const attributes = { click: 'data-click', input: 'data-input', change: 'data-change', keydown: 'data-enter' };

  function reportFailure(event) {
    event.preventDefault();
    event.stopPropagation();
    root.console.error('The interface action could not be completed.');
    if (typeof root.showToast === 'function') root.showToast('This action could not be completed. Refresh and try again.');
  }

  function dispatch(event) {
    if (event.type === 'keydown' && event.key !== 'Enter') return;
    const attribute = attributes[event.type];
    const selector = `[${attribute}]`;
    const origin = typeof event.target?.closest === 'function' ? event.target : event.target?.parentElement;
    let element = origin?.closest(selector);
    while (element) {
      const parent = element.parentElement?.closest(selector);
      if (element.disabled || element.getAttribute('aria-disabled') === 'true') {
        event.preventDefault();
        return;
      }
      try {
        const handler = handlers.get(element.getAttribute(attribute));
        if (!handler) throw new Error('Unknown action.');
        const args = JSON.parse(element.getAttribute('data-args') || '[]');
        if (!Array.isArray(args) || args.length !== handler.arity ||
            args.some(value => value !== null && !['string', 'number', 'boolean'].includes(typeof value)) ||
            args.some(value => typeof value === 'number' && !Number.isFinite(value))) {
          throw new Error('Invalid action arguments.');
        }
        const result = handler.run.call(element, args, event);
        if (result === false) {
          event.preventDefault();
          event.stopPropagation();
        } else if (result && typeof result.catch === 'function') {
          result.catch(() => reportFailure(event));
        }
      } catch {
        reportFailure(event);
      }
      if (event.cancelBubble) return;
      element = parent;
    }
  }

  root.DocIntelActions = Object.freeze({
    register(actions) {
      for (const [name, handler] of Object.entries(actions)) {
        if (handlers.has(name) || typeof handler.run !== 'function' ||
            !Number.isSafeInteger(handler.arity) || handler.arity < 0) {
          throw new Error('Invalid or duplicate interface action registration.');
        }
        handlers.set(name, Object.freeze({ run: handler.run, arity: handler.arity }));
      }
    }
  });
  for (const name of Object.keys(attributes)) document.addEventListener(name, dispatch);
})(globalThis);
