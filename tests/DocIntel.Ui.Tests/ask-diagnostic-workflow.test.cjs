const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const root = path.join(__dirname, '..', '..');
const workflow = fs.readFileSync(path.join(root, '.github', 'workflows', 'diagnose-ask-ai.yml'), 'utf8');
const program = fs.readFileSync(path.join(root, 'src', 'DocIntel.Api', 'Program.cs'), 'utf8');

test('saved-run diagnostics are manual, main-only and inspect-only by default', () => {
  assert.match(workflow, /workflow_dispatch:/);
  assert.doesNotMatch(workflow, /^\s+(push|schedule|pull_request):/m);
  assert.match(workflow, /default: Inspect/);
  assert.match(workflow, /if: github.ref == 'refs\/heads\/main'/);
  assert.match(workflow, /environment: docintel-dev/);
  assert.match(workflow, /requires the exact confirmation REPLAY/);
  assert.match(workflow, /Model usage can incur charges/);
});

test('runtime execution uses the selected revision and built-in command without publishing raw output', () => {
  assert.match(workflow, /"--revision", revision/);
  assert.match(workflow, /"--replica", replicas\[0\]\["name"\]/);
  assert.match(workflow, /"--container", selected\[0\]\["name"\]/);
  assert.match(workflow, /"--diagnose-ask-run", os.environ\["DIAGNOSTIC_RUN"\]/);
  assert.match(workflow, /"--diagnose-tenant", os.environ\["DIAGNOSTIC_TENANT"\]/);
  assert.match(workflow, /shlex.join\(command\)/);
  assert.match(workflow, /capture_output=True/);
  assert.match(workflow, /timeout=180/);
  assert.doesNotMatch(workflow, /print\(result\.(stdout|stderr)\)|set-env-vars|role assignment|ad app/);
  assert.match(workflow, /Raw process output was not published/);
});

test('recent failure references can only be inspected and cannot authorize a replay', () => {
  assert.match(workflow, /\$env:DIAGNOSTIC_MODE -eq 'Inspect' -and \$env:DIAGNOSTIC_RUN -ceq 'recent'/);
  assert.match(workflow, /"runId", "createdAtUtc"/);
  assert.match(workflow, /"completedAtUtc", "lastStep", "analyticalAttempts"/);
  assert.doesNotMatch(workflow, /"question",|"actor",|"answer",/);
});

test('contract probing is a distinct document-free mode and does not weaken saved-question replay approval', () => {
  assert.match(workflow, /options: \[Inspect, Replay, Contract, Synthetic\]/);
  assert.match(workflow, /Contract checks do not accept saved run identifiers/);
  assert.match(workflow, /"--diagnose-ask-contract", "true"/);
  assert.match(workflow, /"--diagnose-ask-synthetic", "true"/);
  assert.match(workflow, /Replay requires the exact confirmation REPLAY/);
  assert.match(workflow, /"httpStatus", "providerCode"/);
  assert.match(program, /startupReadiness\.ProbeModelsOnStartup/);
  const contract = program.indexOf('if (askContractMode)', program.indexOf('var app = builder.Build()'));
  assert.ok(contract > 0 && contract < program.indexOf('app.UseDocIntelResponseHeaders()'));
});

test('the diagnostic CLI exits before web hosting and background workers start', () => {
  const diagnostic = program.indexOf('if (diagnosticMode)', program.indexOf('var app = builder.Build()'));
  assert.ok(diagnostic > 0);
  const diagnosticEnd = program.indexOf('app.UseDocIntelResponseHeaders()', diagnostic);
  assert.match(program.slice(diagnostic, diagnosticEnd), /AskAiRunDiagnostic.ExecuteAsync/);
  assert.match(program.slice(diagnostic, diagnosticEnd), /await app.DisposeAsync\(\);\s*return;/);
  assert.ok(program.indexOf('app.Run();') > diagnosticEnd);
});
