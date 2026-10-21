const fs = require('node:fs');
const path = require('node:path');

function loadApplication(page) {
  const root = path.join(__dirname, '..', '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
  const app = page === 'index' ? 'workspace.js' : 'admin.js';
  return 'globalThis.DocIntelActions ??= { register: actions => { globalThis.registeredActions = actions; } };\n' +
    [path.join('shared', 'ui.js'), app].map(name => fs.readFileSync(path.join(root, name), 'utf8'))
    .join('\n').replace(/\n\s*init\(\);\s*$/, '');
}

module.exports = { loadApplication };
