function createDocument() {
  const ids = new Map();
  function element(tagName, namespaceURI = null) {
    const attributes = new Map();
    const listeners = new Map();
    const classes = new Set();
    let content = '';
    return {
      tagName, namespaceURI, children: [], style: {},
      get textContent() { return content + this.children.map(child => child.textContent).join(''); },
      set textContent(value) { content = String(value); this.children = []; },
      set innerHTML(value) { throw new Error(`Unsafe HTML used in ${tagName}: ${value}`); },
      className: '',
      classList: { add(name) { classes.add(name); }, contains(name) { return classes.has(name); } },
      setAttribute(name, value) { attributes.set(name, String(value)); },
      getAttribute(name) { return attributes.get(name) ?? null; },
      appendChild(child) { this.children.push(child); return child; },
      replaceChildren(...children) { content = ''; this.children = children; },
      addEventListener(name, callback) { listeners.set(name, callback); },
      dispatch(name, key) {
        const event = { key, prevented: false, preventDefault() { this.prevented = true; } };
        listeners.get(name)?.(event);
        return event;
      },
      click() { return this.dispatch('click'); }
    };
  }
  return {
    createElement: tag => element(tag),
    createElementNS: (namespace, tag) => element(tag, namespace),
    getElementById(id) {
      if (!ids.has(id)) ids.set(id, element('div'));
      return ids.get(id);
    }
  };
}

function all(node, predicate = () => true) {
  return [node, ...node.children.flatMap(child => all(child))]
    .filter(predicate);
}

module.exports = { createDocument, all };
