const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const root = path.join(__dirname, '..', '..', 'src', 'DocIntel.Api', 'wwwroot');
const context = vm.createContext({});
vm.runInContext(fs.readFileSync(path.join(root, 'shared', 'ui.js'), 'utf8'), context);
const render = value => context.processingResultMarkup(value);

test('processing briefs render readable bullets and labels without dropping details', () => {
  const markup = render('- **What matters:** Customer offer.\n- **Key terms:** USD 158,400.\n' +
    '- **Watch out:** Approval pending.\n- **Next action:** Confirm with Procurement.');
  assert.equal([...markup.matchAll(/<li>/g)].length, 4);
  assert.match(markup, /<ul class="processing-result-fields" role="list">/);
  assert.match(markup, /<strong>Key terms:<\/strong> USD 158,400\./);
  assert.match(markup, /Approval pending/);
  assert.match(markup, /Confirm with Procurement/);
  assert.doesNotMatch(markup, /\*\*/);
});

test('privacy decisions have an explicit text verdict and theme-aware styling', () => {
  for (const [label, status] of [
    ['HOLD', 'hold'], ['REVIEW', 'review'], ['READY', 'ready'], ['NOT APPLICABLE', 'not-applicable']
  ]) {
    const markup = render(`- **Decision:** ${label}\n- **Why:** Document-specific evidence.`);
    assert.match(markup, new RegExp(`data-status="${status}">${label}</strong>`));
    assert.match(markup, /result-verdict-label">Decision/);
    const css = fs.readFileSync(path.join(root, 'shared', 'shell.css'), 'utf8');
    assert.match(css, new RegExp(`\\.result-verdict\\[data-status="${status}"\\]`));
  }
  assert.doesNotMatch(render('- **Decision:** Probably compliant'), /data-status=/);
  assert.doesNotMatch(render('A quotation says Decision: READY but approval is absent.'), /data-status=/);
});

test('untrusted result text stays escaped and cannot create links, markup or attributes', () => {
  const markup = render('- **Why:** <img src=x onerror=alert(1)> & "quoted"\n' +
    '- **Evidence:** [source](javascript:alert(1))\n<script>alert(2)</script>');
  assert.match(markup, /&lt;img src=x onerror=alert\(1\)&gt; &amp; &quot;quoted&quot;/);
  assert.match(markup, /&lt;script&gt;alert\(2\)&lt;\/script&gt;/);
  assert.doesNotMatch(markup, /<img|<script|<a\s|href=/);
});

test('legacy paragraphs, line breaks and long results remain complete and readable', () => {
  const tail = 'Important final obligation. '.repeat(120);
  const markup = render(`First line.\r\nSecond line.\r\n\r\n${tail}`);
  assert.match(markup, /<p>First line\.<br>Second line\.<\/p>/);
  assert.ok(markup.includes(tail.trim()));
  assert.equal(render(null), '<div class="processing-result"></div>');
});

test('only complete labeled briefs use the responsive field layout', () => {
  const privacy = render('- **Decision:** HOLD\n- **Why:** Approval pending.\n' +
    '- **Evidence:** "Privacy clearance is outstanding."\n- **Next:** Ask Privacy to confirm.');
  assert.match(privacy, /class="processing-result-fields"/);
  assert.match(privacy, /data-status="hold">HOLD/);
  for (const text of [
    '- First step.\n- Second step.\n- Third step.\n- Fourth step.',
    '- **What matters:** Offer.\n- **Key terms:** USD 158,400.',
    '- **Next action:** Confirm.\n- **Watch out:** Pending.\n- **Key terms:** USD 158,400.\n- **What matters:** Offer.'
  ]) {
    assert.doesNotMatch(render(text), /class="processing-result-fields"/);
  }
});

test('processing results fill their available width and adapt to the result container rather than the viewport', () => {
  const css = fs.readFileSync(path.join(root, 'shared', 'shell.css'), 'utf8');
  const result = css.match(/\.processing-result\s*\{([^}]+)\}/)[1];
  assert.match(result, /width:100%/);
  assert.match(result, /min-width:0/);
  assert.match(result, /max-width:none/);
  assert.match(result, /container-type:inline-size/);
  assert.match(result, /font-size:16px/);
  assert.doesNotMatch(result, /max-width:\s*\d+ch/);
  assert.match(css, /@container \(min-width:860px\)\s*\{\s*\.processing-result \.processing-result-fields\s*\{[^}]*repeat\(2, minmax\(0, 1fr\)\)/);
});
