const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const root = path.join(__dirname, '..', '..');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const setup = read(path.join('.github', 'workflows', 'setup-azure.yml'));
const release = read(path.join('.github', 'workflows',
  'ca-docintel-dev-AutoDeployTrigger-aecf934f-2618-4144-b094-760dd25b50b2.yml'));
const script = read(path.join('scripts', 'Setup-DocIntelGitHub.ps1'));

test('infrastructure setup is manual, main-only, preview-first and explicitly confirmed before apply', () => {
  assert.match(setup, /workflow_dispatch:/);
  assert.doesNotMatch(setup, /^\s+(?:push|pull_request|schedule):/m);
  assert.match(setup, /default: Preview/);
  assert.match(setup, /needs: preview/);
  assert.match(setup, /inputs\.mode == 'Apply' && inputs\.confirmation == 'APPLY'/);
  assert.equal([...setup.matchAll(/if: github\.ref == 'refs\/heads\/main'/g)].length, 2);
  assert.equal([...setup.matchAll(/environment: docintel-dev/g)].length, 2);
  assert.match(setup, /-ExpectedPlanHash \$env:EXPECTED_PLAN_HASH/);
  assert.match(setup, /needs\.preview\.outputs\.plan_hash/);
  assert.match(setup, /actions\/upload-artifact@v4/);
  assert.match(setup, /retention-days: 7/);
});

test('setup and release serialize changes to the same Azure target', () => {
  const group = workflow => workflow.match(/^\s+group: (.+)$/m)[1];
  assert.equal(group(setup), group(release));
  assert.match(setup, /cancel-in-progress: false/);
  assert.match(release, /cancel-in-progress: false/);
});

test('directory administration is explicit and no cloud or directory privilege is self-granted', () => {
  assert.match(setup, /configure_directory:[\s\S]*?default: false/);
  assert.doesNotMatch(script, /az role assignment create|app-role-assignment|Directory\.ReadWrite\.All|Application\.ReadWrite\.All/);
  assert.match(script, /if \(\$ConfigureDirectoryApplications\)/);
  assert.match(script, /existing.*?scopes|oauth2PermissionScopes/);
  assert.match(script, /app role conflicts with delegated DocIntel\.Access/);
  assert.doesNotMatch(script, /api\.appRoles\s*=/);
  assert.match(script, /if \(\$Mode -eq 'Preview'\)[\s\S]*?return[\s\S]*?if \(\$web\.Planned\)/);
});

test('setup manages existing resources without silently replacing data, images or ingress', () => {
  assert.match(script, /deployApplication = \$false/);
  assert.match(script, /existingEnvironmentId = \$environmentId/);
  assert.match(script, /Refusing to move/);
  assert.match(script, /different partition key/);
  assert.match(script, /The app changed during setup/);
  assert.match(script, /--set-env-vars/);
  assert.doesNotMatch(script, /--replace-env-vars|--remove-all|--delete/);
  assert.match(script, /bootstrapAdminRequired/);
});

test('normal validation executes setup and readiness guards without requiring Azure credentials', () => {
  const validate = release.split(/\r?\n  deploy:/)[0];
  for (const name of ['Setup-DocIntelGitHub.Tests.ps1', 'Wait-DocIntelReady.Tests.ps1'])
    assert.ok(validate.includes(name));
  assert.doesNotMatch(validate, /azure\/login|secrets\./);
  assert.match(release, /Verify the released image and revision readiness/);
  assert.match(release, /Wait-DocIntelReady\.ps1/);
});

test('browser deployment preflight no longer demands a conflicting same-valued API application role', () => {
  const legacy = read('Deploy-DocIntelAzure.ps1');
  assert.match(legacy, /oauth2PermissionScopes/);
  assert.doesNotMatch(legacy, /\$api\.appRoles/);
  assert.match(legacy, /An app role is not required for browser deployment/);
});
