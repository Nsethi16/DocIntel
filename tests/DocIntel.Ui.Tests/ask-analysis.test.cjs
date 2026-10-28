const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');
const { createDocument, all } = require('./helpers/analysis-dom.cjs');
const root = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const source = fs.readFileSync(path.join(root, 'ask-analysis.js'), 'utf8');

function dataset(values = [1250.25, -50.5, 0, null]) {
  return {
    analysisId: 'a1', title: 'Spend by supplier', operation: 'sum', visualization: 'bar',
    xColumn: 'category', yColumn: 'value',
    columns: [
      { key: 'category', label: 'Supplier', kind: 'text' }, { key: 'unit', label: 'Unit', kind: 'text' },
      { key: 'value', label: 'Total', kind: 'number' }, { key: 'sharePercent', label: 'Share %', kind: 'number' }
    ],
    rows: values.map((value, index) => ({
      cells: { category: `Vendor ${index + 1}`, unit: 'USD', value, sharePercent: value === null ? null : 12.5 },
      evidenceIds: ['e1'], citationNumbers: [1, 2]
    })),
    coverage: {
      scope: 'InspectedEvidence', recordsIncluded: 4, missingValues: 1, duplicatesExcluded: 2,
      documentsMatched: 3, documentsInspected: 2, hasMoreDocuments: true
    },
    notes: ['Only cited, inspected evidence is covered.']
  };
}

const citations = [
  { citationNumber: 1, jobId: 'doc/a', sliceIndex: 2, fileName: 'Supplier.pdf', pageRange: '1–2' },
  { citationNumber: 2, jobId: 'doc/b', sliceIndex: 0, cleanName: 'Invoice', evidenceKind: 'HumanReviewedField' }
];

function runtime() {
  const document = createDocument();
  const context = vm.createContext({ document, Intl, Date });
  vm.runInContext(source, context);
  const host = document.createElement('div');
  const opened = [];
  const prompts = [];
  return {
    host, context, opened, prompts,
    render(analysis, options = {}) {
      context.DocIntelAnalyses.render(host, Array.isArray(analysis) ? analysis : [analysis], citations, {
        onCitation: citation => opened.push(citation), onFollowUp: question => prompts.push(question), ...options
      });
      return host;
    }
  };
}

const tagged = (host, tag) => all(host, item => item.tagName === tag);
const classified = (host, name) => all(host, item =>
  item.className === name || item.getAttribute('class') === name || item.classList.contains(name));

test('analyses render generic semantic data tables, exact numbers, missing values, coverage and follow-ups', () => {
  const app = runtime();
  const host = app.render(dataset());
  const rows = tagged(host, 'tbody')[0].children;
  assert.equal(tagged(host, 'table').length, 1);
  assert.match(tagged(host, 'caption')[0].textContent, /data and cited sources/);
  assert.ok(tagged(host, 'th').every(item => item.getAttribute('scope') === 'col'));
  assert.equal(rows[0].children[2].textContent, '1,250.25');
  assert.equal(rows[1].children[2].textContent, '-50.5');
  assert.equal(rows[2].children[2].textContent, '0');
  assert.equal(rows[3].children[2].textContent, 'Missing');
  assert.equal(rows[0].children[3].textContent, '12.5');
  for (const phrase of ['inspected evidence only', 'not complete-workspace totals', 'Missing values: 1',
    'Duplicates excluded: 2', 'Documents matched: 3', 'Documents inspected: 2', 'More matching documents exist'])
    assert.ok(host.textContent.includes(phrase), phrase);
  classified(host, 'ask-analysis-followup')[0].click();
  assert.match(app.prompts[0], /Spend by supplier/);
});

test('model and cached content remain inert text, never HTML, URLs, CSS or executable SVG', () => {
  const app = runtime();
  const analysis = dataset([10]);
  const payload = '<img src=x onerror=globalThis.attacked=true><script>alert(1)</script>';
  analysis.title = payload;
  analysis.columns[0].label = payload;
  analysis.rows[0].cells.category = payload;
  analysis.notes = [payload];
  analysis.rows[0].citationNumbers.push('1);evil()', { code: 'evil' });
  analysis.rows[0].cells.value = '10;alert(1)';
  analysis.visualization = '<svg onload=evil()>';
  const host = app.render(analysis);
  assert.ok(host.textContent.includes(payload));
  assert.ok(host.textContent.includes('Invalid number'));
  assert.equal(tagged(host, 'script').length + tagged(host, 'img').length + tagged(host, 'svg').length, 0);
  assert.equal(app.context.attacked, undefined);
  assert.doesNotMatch(source, /innerHTML|outerHTML|insertAdjacentHTML|\beval\s*\(|new Function/);
  assert.equal(classified(host, 'ask-analysis-source').length, 2);
  analysis.visualization = 'bar';
  analysis.rows[0].cells.value = 10;
  app.render(analysis);
  assert.equal(tagged(host, 'svg').length, 1);
  assert.equal(tagged(host, 'script').length + tagged(host, 'img').length, 0);
  assert.ok(tagged(host, 'title').some(item => item.textContent === payload));
  assert.ok(tagged(host, 'svg').every(item => item.namespaceURI === 'http://www.w3.org/2000/svg'));
});

test('negative, positive, decimal, zero and missing marks share one honest axis and accessible sources', () => {
  const app = runtime();
  const host = app.render(dataset());
  const bars = tagged(host, 'rect');
  const zero = classified(host, 'ask-analysis-zero')[0];
  const zeroX = Number(zero.getAttribute('x1'));
  assert.equal(bars.length, 2);
  assert.equal(Number(bars[0].getAttribute('x')), zeroX);
  assert.ok(Number(bars[1].getAttribute('x')) < zeroX);
  assert.ok(Math.abs(Number(bars[1].getAttribute('x')) + Number(bars[1].getAttribute('width')) - zeroX) < 1e-8);
  assert.ok(Math.abs(Number(bars[0].getAttribute('width')) /
    Number(bars[1].getAttribute('width')) - 1250.25 / 50.5) < 1e-8);
  assert.equal(tagged(host, 'circle').length, 1, 'zero is a point, not an invented nonzero bar');
  const points = all(host, item => item.classList.contains('ask-analysis-point'));
  assert.equal(points.length, 3, 'missing values never become chart marks');
  assert.equal(points[0].getAttribute('role'), 'link');
  assert.equal(points[0].getAttribute('tabindex'), '0');
  assert.match(points[0].getAttribute('aria-label'), /Vendor 1.*1,250.25.*Open source/);
  assert.equal(points[0].dispatch('keydown', 'Enter').prevented, true);
  assert.equal(points[1].dispatch('keydown', ' ').prevented, true);
  assert.equal(points[1].dispatch('keydown', 'ArrowDown').prevented, false);
  points[0].click();
  assert.equal(app.opened.length, 3);
  classified(host, 'ask-analysis-source')[1].click();
  assert.equal(app.opened.at(-1), citations[1], 'each table source remains reachable, not just the first');
  assert.equal(tagged(host, 'svg')[0].getAttribute('role'), 'group');
  assert.ok(tagged(host, 'svg')[0].getAttribute('aria-labelledby'));
});

test('zero-only, tiny decimals, negative-only and extreme finite data never fabricate values or invalid geometry', () => {
  for (const values of [[0, 0], [1e-20, -1e-20, null], [-5, -2.25], [Number.MAX_VALUE, -Number.MAX_VALUE]]) {
    const app = runtime();
    const host = app.render(dataset(values));
    const shapes = [...tagged(host, 'rect'), ...tagged(host, 'circle'), ...tagged(host, 'line')];
    for (const shape of shapes) {
      for (const name of ['x', 'y', 'x1', 'x2', 'y1', 'y2', 'cx', 'cy', 'width', 'height']) {
        const value = shape.getAttribute(name);
        if (value !== null) assert.ok(Number.isFinite(Number(value)), `${name} = ${value}`);
      }
    }
    if (values.every(value => value === 0)) {
      assert.equal(tagged(host, 'rect').length, 0);
      assert.match(host.textContent, /All available values are zero/);
    }
  }
});

test('line charts use real chronological distances, sort dates and break at missing values', () => {
  const app = runtime();
  const analysis = dataset([4, 1, 2, null, 6]);
  analysis.visualization = 'line';
  analysis.xColumn = 'day';
  analysis.columns[0] = { key: 'day', label: 'Invoice date', kind: 'date' };
  const dates = ['2026-01-11', '2026-01-01', '2026-01-02', '2026-01-12', '2026-01-13'];
  analysis.rows.forEach((row, index) => { row.cells.day = dates[index]; });
  const host = app.render(analysis);
  const points = tagged(host, 'circle');
  assert.equal(points.length, 4);
  const x = points.map(point => Number(point.getAttribute('cx')));
  assert.ok(Math.abs((x[2] - x[1]) / (x[1] - x[0]) - 9) < 1e-8, 'unequal dates must not have equal spacing');
  assert.equal(tagged(host, 'polyline').length, 1, 'a missing row breaks the segment');
  assert.equal(tagged(host, 'polyline')[0].getAttribute('points').split(' ').length, 3);
  assert.match(host.textContent, /actual time intervals/);
  assert.equal(tagged(host, 'tbody')[0].children[0].children[0].textContent, dates[0], 'table preserves source order');
});

test('line dates with offsets represent actual instants and invalid or duplicate dates fall back to a table', () => {
  const app = runtime();
  const analysis = dataset([1, 2]);
  analysis.visualization = 'line';
  analysis.xColumn = 'day';
  analysis.columns[0] = { key: 'day', label: 'Date', kind: 'date' };
  for (const dates of [
    ['2026-02-30', '2026-03-01'], ['not a date', '2026-03-01'],
    ['2026-01-01', '2026-01-01'], [null, '2026-01-01'],
    ['2026-01-01T10:00:00Z', '2026-01-01T12:00:00+02:00']
  ]) {
    analysis.rows.forEach((row, index) => { row.cells.day = dates[index]; });
    const host = app.render(analysis);
    assert.equal(tagged(host, 'svg').length, 0);
    assert.match(host.textContent, /Table only.*distinct, valid ISO dates/);
    assert.equal(tagged(host, 'table').length, 1);
  }
  analysis.rows[0].cells.day = '2026-01-01T10:00:00Z';
  analysis.rows[1].cells.day = '2026-01-01T13:00:00+02:00';
  assert.equal(tagged(app.render(analysis), 'svg').length, 1);
});

test('mixed units, excessive charts, non-numbers and missing columns disclose a table-only presentation', () => {
  const app = runtime();
  const mixed = dataset([10, 20]);
  mixed.rows[1].cells.unit = 'EUR';
  const undeclaredMixed = structuredClone(mixed);
  undeclaredMixed.columns = undeclaredMixed.columns.filter(column => column.key !== 'unit');
  const mixedLine = structuredClone(mixed);
  mixedLine.visualization = 'line';
  mixedLine.columns[0].kind = 'date';
  mixedLine.rows[0].cells.category = '2026-01-01';
  mixedLine.rows[1].cells.category = '2026-01-02';
  const tooLarge = dataset(Array.from({ length: 41 }, (_, index) => index));
  const invalid = dataset(['5']);
  const allMissing = dataset([null, null]);
  const badSpec = dataset();
  badSpec.yColumn = 'script';
  for (const [analysis, reason] of [[mixed, /mixed or invalid units/], [undeclaredMixed, /mixed or invalid units/],
    [mixedLine, /mixed or invalid units/],
    [tooLarge, /limited to 40 rows/],
    [invalid, /finite numbers/], [allMissing, /all chart values are missing/], [badSpec, /columns are missing or invalid/]]) {
    const host = app.render(analysis);
    assert.equal(tagged(host, 'svg').length, 0);
    assert.equal(tagged(host, 'table').length, 1);
    assert.match(host.textContent, reason);
  }
});

test('cached malformed or oversized datasets are bounded and null never silently becomes zero', () => {
  const app = runtime();
  const analysis = dataset(Array.from({ length: 101 }, () => null));
  analysis.rows[1] = null;
  analysis.rows[2] = { cells: { value: Infinity }, citationNumbers: [999] };
  analysis.columns.push(null, { key: 'value', kind: 'number' });
  const host = app.render([null, analysis]);
  assert.equal(tagged(host, 'tbody')[0].children.length, 100);
  assert.match(host.textContent, /first 100 rows/);
  assert.match(host.textContent, /declared columns are invalid/);
  assert.match(host.textContent, /Source unavailable/);
  assert.match(host.textContent, /Invalid number/);
  assert.doesNotThrow(() => app.render({ title: 'Broken', columns: {}, rows: {}, coverage: {} }));
  assert.match(app.host.textContent, /not verified/);
  assert.match(app.host.textContent, /Not reported/);
});

test('table-only and legacy text-only messages do not invent charts, dataset scope or follow-up context', () => {
  const app = runtime();
  app.render(dataset());
  app.context.DocIntelAnalyses.render(app.host, undefined);
  assert.equal(app.host.children.length, 0);
  const analysis = dataset();
  analysis.visualization = 'table';
  const host = app.render(analysis, { onFollowUp: undefined });
  assert.equal(tagged(host, 'svg').length, 0);
  assert.equal(classified(host, 'ask-analysis-followup').length, 0);
  assert.equal(tagged(host, 'table').length, 1);
});

test('labels retain full accessible values while responsive charts and tables scroll within semantic theme tokens', () => {
  const app = runtime();
  const analysis = dataset([2]);
  analysis.rows[0].cells.category = 'A supplier with a very long name which must not be hidden from accessible values';
  const host = app.render(analysis);
  assert.match(tagged(host, 'circle')[0]?.getAttribute('aria-label') ||
    tagged(host, 'rect')[0].getAttribute('aria-label'), /must not be hidden/);
  assert.ok(tagged(host, 'title').some(item => item.textContent === analysis.rows[0].cells.category));
  assert.equal(tagged(host, 'td')[0].textContent, analysis.rows[0].cells.category);
  const css = fs.readFileSync(path.join(root, 'ask-analysis.css'), 'utf8');
  for (const token of ['--bg', '--border', '--text-primary', '--text-secondary', '--graph-entity',
    '--focus-ring', '--font-sans', '--warning-bg']) assert.ok(css.includes(`var(${token})`), token);
  assert.doesNotMatch(css, /#[\da-f]{3,8}\b|https?:|@import/);
  assert.match(css, /max-width:\s*100%/);
  assert.match(css, /overflow-x:\s*auto/);
  assert.match(css, /max-width:\s*700px/);
  for (const page of ['index.html', 'ask-run.html']) {
    const html = fs.readFileSync(path.join(root, page), 'utf8');
    assert.match(html, /href="\/ask-analysis.css"/);
    assert.match(html, /src="\/ask-analysis.js"/);
    assert.ok(html.indexOf('src="/ask-analysis.js"') < html.indexOf(`src="/${page === 'index.html' ? 'workspace' : 'ask-run'}.js"`));
  }
});
