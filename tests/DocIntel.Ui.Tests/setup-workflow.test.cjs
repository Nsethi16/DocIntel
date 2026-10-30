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
  assert.match(setup, /reviewed_plan_hash:[\s\S]*?type: string/);
  assert.match(setup, /Apply requires plan_hash from the Preview run you reviewed/);
  assert.match(setup, /-ExpectedPlanHash \$env:REVIEWED_PLAN_HASH/);
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

test('queue discovery uses the paginated management API without storage keys or data-plane access', () => {
  assert.doesNotMatch(script, /queue-rm|storage['", ]+queue['", ]+list|--account-key/);
  assert.match(script, /management\.azure\.com[\s\S]*?queueServices\/default\/queues\?api-version=2023-05-01/);
  assert.match(script, /'rest', '--method', 'GET', '--url', \$queueUrl/);
  assert.match(script, /\$queuePage\.nextLink/);
  assert.match(script, /outside the selected account management endpoint/);
});

test('browser deployment preflight no longer demands a conflicting same-valued API application role', () => {
  const legacy = read('Deploy-DocIntelAzure.ps1');
  assert.match(legacy, /oauth2PermissionScopes/);
  assert.doesNotMatch(legacy, /\$api\.appRoles/);
  assert.match(legacy, /An app role is not required for browser deployment/);
});

test('translation setup is separately approved and never applied by an ordinary image release', () => {
  const translation = read(path.join('scripts', 'Sync-DocIntelTranslation.ps1'));
  const module = read(path.join('infra', 'translation-connection.bicep'));
  const main = read(path.join('infra', 'main.bicep'));
  assert.match(setup, /translation_only:[\s\S]*?default: true/);
  assert.match(setup, /replace_translation_endpoint:[\s\S]*?default: false/);
  assert.equal([...setup.matchAll(/-ReplaceExistingEndpoint:/g)].length, 2);
  assert.match(setup, /Sync-DocIntelTranslation\.ps1 -Mode Apply -Confirmation TRANSLATE/);
  assert.match(translation, /ExpectedPlanHash -cne \$planHash/);
  assert.match(translation, /EndpointReplacementRequired/);
  assert.match(translation, /NetworkReviewRequired/);
  assert.doesNotMatch(translation, /--replace-env-vars|listKeys|--account-key|--sas-token|graph\.microsoft\.com/);
  assert.match(module, /kind: 'TextTranslation'/);
  assert.match(module, /name: 'S1'/);
  assert.match(module, /type: 'SystemAssigned'/);
  assert.match(module, /scope: source/);
  assert.match(module, /scope: target/);
  assert.doesNotMatch(module, /publicNetworkAccess|accounts\/deployments/);
  assert.match(module, /managementPolicies@2023-05-01' = if \(provisionStagingAccount\)/);
  assert.match(module, /param provisionStagingAccount bool = true/);
  assert.match(module, /name: 'Standard_LRS'/);
  assert.match(module, /allowSharedKeyAccess: false/);
  assert.match(module, /'docintel-owner-app': toLower\(applicationResourceId\)/);
  assert.match(main, /stagingAccountName: translationStagingName/);
  assert.match(module, /translation-source\/docintel-translation-v1\//);
  assert.match(module, /translation-target\/docintel-translation-v1\//);
  assert.match(module, /daysAfterModificationGreaterThan: 1/);
  assert.match(translation, /provisionStagingAccount = \$provisionStaging/);
  assert.match(translation, /OriginalStoragePolicy = 'Not read or written'/);
  assert.match(translation, /DocumentTranslation__StagingBlobEndpoint=\$stagingEndpoint/);
  assert.match(translation, /Lifecycle = \$lifecycle/);
  assert.match(release, /preview\.Plan\.Lifecycle\.Status -notin @\('Ready', 'CreateWithNewStagingAccount'\)/);
  assert.match(main, /module translation '\.\/translation-connection\.bicep'/);
  assert.doesNotMatch(main, /effectiveTranslatorEndpoint[\s\S]{0,300}foundrySubdomain/);
  assert.match(release, /Sync-DocIntelTranslation\.Tests\.ps1/);
  assert.match(release, /preview\.Plan\.RequiresApply/);
  assert.doesNotMatch(release, /Sync-DocIntelTranslation\.ps1[^\n]*-Mode Apply/);
});
