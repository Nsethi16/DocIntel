const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const webRoot = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const context = vm.createContext({});
vm.runInContext(fs.readFileSync(path.join(webRoot, 'evidence.js'), 'utf8'), context);
const describe = context.DocIntelEvidence.describe;
const exact = () => ({
  status: 'ExactTextMatch', grounded: true, wordIds: ['w_2_0'],
  boxes: [{ page: 2, polygon: [0.1, 0.1, 0.2, 0.1, 0.2, 0.2, 0.1, 0.2] }]
});

test('source presence does not inflate or calibrate a zero model score', () => {
  const info = describe({ confidenceSignals: { modelScore: 0, groundingScore: 1, calibrated: false },
    provenance: exact() }, 1);
  assert.equal(info.modelPercent, 0);
  assert.equal(info.status, 'Exact source text');
  assert.deepEqual(Array.from(info.pages), [2]);
  assert.match(info.tooltip, /uncalibrated/);
  assert.match(info.tooltip, /not semantic verification or review approval/);
});

test('unknown and invalid model scores remain unknown rather than showing NaN or inflated percentages', () => {
  assert.equal(describe({ confidenceSignals: { modelScore: null }, confidence: 0.99 }, 1).modelPercent, null);
  assert.equal(describe({ confidenceValue: 85 }, 100).modelPercent, 85);
  assert.equal(describe({ confidence: 1.5 }, 1).modelPercent, null);
  assert.equal(describe({ confidence: NaN }, 1).modelPercent, null);
});

test('ambiguous or absent evidence never offers a fabricated exact source page', () => {
  for (const status of ['Ambiguous', 'NotFound', 'Unavailable', 'ReferenceLimitExceeded']) {
    const info = describe({ provenance: { ...exact(), status } }, 1);
    assert.equal(info.pages.length, 0);
    assert.notEqual(info.status, 'Exact source text');
  }
});

test('malformed or incomplete geometry cannot enable an exact source link', () => {
  const polygon = exact();
  polygon.boxes[0].polygon[0] = Infinity;
  assert.equal(describe({ provenance: polygon }, 1).pages.length, 0);
  const page = exact();
  page.boxes[0].page = -1;
  assert.equal(describe({ provenance: page }, 1).pages.length, 0);
  const ids = exact();
  ids.wordIds = [];
  assert.equal(describe({ provenance: ids }, 1).pages.length, 0);
});
