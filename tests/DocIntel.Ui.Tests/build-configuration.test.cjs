const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');

const root = path.join(__dirname, '..', '..');
const read = name => fs.readFileSync(path.join(root, name), 'utf8');
const workflow = read(path.join('.github', 'workflows',
  'ca-docintel-dev-AutoDeployTrigger-aecf934f-2618-4144-b094-760dd25b50b2.yml'));

test('standalone container restore uses the configured public source without package credentials', () => {
  const docker = read('Dockerfile');
  assert.doesNotMatch(docker, /rm\s+-f\s+NuGet|ARG\s+.*(?:SECRET|PASSWORD|TOKEN|CREDENTIAL)/i);
  assert.doesNotMatch(docker, /--mount=type=secret|NuGetPackageSourceCredentials|nuget_credentials|OneITVSO/);
  assert.match(docker, /dotnet restore.*--configfile NuGet\.config --locked-mode/);
  assert.match(docker, /--no-restore/);
  assert.match(docker, /USER \$APP_UID/);
  const ignored = read('.dockerignore').split(/\r?\n/);
  for (const entry of ['.git', '**/App_Data', '**/.env', '**/*.pfx']) assert.ok(ignored.includes(entry));
  assert.ok(!ignored.includes('NuGet.config'));
});

test('NuGet source mapping deliberately uses only NuGet.org and keeps locked restore', () => {
  const config = read('NuGet.config');
  const sources = [...config.matchAll(/<add key="([^"]+)" value="(https:[^"]+)"/g)];
  assert.equal(sources.length, 1);
  assert.equal(sources[0][1], 'nuget.org');
  assert.equal(sources[0][2], 'https://api.nuget.org/v3/index.json');
  assert.doesNotMatch(config, /OneITVSO|visualstudio\.com|pkgs\.dev\.azure\.com|<packageSourceCredentials>/i);
  assert.match(config, /<clear\s*\/>/);
  assert.match(config, /<packageSource key="nuget\.org">/);
  assert.match(config, /<package pattern="\*"\s*\/>/);
  assert.doesNotMatch(config, /disableTLSCertificateValidation|allowInsecureConnections/i);
  const props = read('Directory.Build.props');
  assert.match(props, /<RestorePackagesWithLockFile>true/);
  assert.match(props, /<RestoreLockedMode Condition=.*ContinuousIntegrationBuild/);
  assert.match(read('README.md'), /public NuGet\.org only/);
  assert.doesNotMatch(read('README.md'), /DOCINTEL_NUGET_CREDENTIALS|nuget_credentials|OneITVSO/);
});

test('all five projects have content-hashed dependency lockfiles without changing pinned package versions', () => {
  for (const project of ['src/DocIntel.Api', 'src/DocIntel.AppHost', 'src/DocIntel.Core',
    'src/DocIntel.ServiceDefaults', 'tests/DocIntel.Tests']) {
    const lock = JSON.parse(read(path.join(project, 'packages.lock.json')));
    assert.ok([1, 2].includes(lock.version));
    for (const framework of Object.values(lock.dependencies)) {
      for (const dependency of Object.values(framework)) {
        if (dependency.type.toLowerCase() === 'project') continue;
        assert.equal(typeof dependency.resolved, 'string');
        assert.equal(typeof dependency.contentHash, 'string');
        assert.equal(Buffer.from(dependency.contentHash, 'base64').length, 64);
      }
    }
  }
});

test('deployment uses federation and registry identity rather than registry account passwords', () => {
  assert.match(workflow, /actions\/checkout@v4/);
  assert.doesNotMatch(workflow, /checkout@v2|registryUsername|registryPassword|REGISTRY_PASSWORD|REGISTRY_USERNAME/);
  assert.match(workflow, /id-token: write/);
  assert.match(workflow, /az acr login --name/);
  assert.match(workflow, /needs: validate/);
  assert.match(workflow, /DOCINTEL_DEPLOY_ENABLED == 'true'/);
  assert.match(workflow, /github\.event_name != 'pull_request'/);
  assert.match(workflow, /environment: docintel-dev/);
  assert.doesNotMatch(workflow, /DOCINTEL_NUGET_CREDENTIALS|NuGetPackageSourceCredentials|nuget_credentials|OneITVSO/);
  assert.match(workflow, /Restore locked dependencies from NuGet\.org/);
});

test('docs-only changes do not match deployment paths and pull requests get validation', () => {
  assert.doesNotMatch(workflow, /^\s+- ['"]\*\*['"]\s*$/m);
  assert.match(workflow, /pull_request:/);
  assert.match(workflow, /- 'src\/\*\*'/);
  assert.match(workflow, /- 'tests\/\*\*'/);
  assert.doesNotMatch(workflow, /- ['"](?:README\.md|docs\/\*\*)['"]/);
  assert.match(workflow, /--locked-mode/);
  assert.match(workflow, /-warnaserror/);
  assert.match(workflow, /node --test/);
  for (const content of [workflow, read('Dockerfile'), read('Directory.Build.props')])
    assert.doesNotMatch(content, /ignore-failed-sources|NuGetAudit>\s*false|NuGetAudit=false|NoWarn.*NU19|disableTLSCertificateValidation/i);
});
