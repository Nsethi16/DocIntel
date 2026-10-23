const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { spawnSync } = require('node:child_process');

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

test('every project and supported Aspire host platform has a content-hashed dependency lockfile', () => {
  const files = ['src/DocIntel.Api', 'src/DocIntel.Core',
    'src/DocIntel.ServiceDefaults', 'tests/DocIntel.Tests'].map(project => path.join(project, 'packages.lock.json'));
  files.push(...['linux-x64', 'win-x64'].map(rid => path.join('src', 'DocIntel.AppHost', `packages.${rid}.lock.json`)));
  for (const file of files) {
    const lock = JSON.parse(read(file));
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

test('Aspire locks vary only by native host tools and are selected by the SDK runtime identifier', () => {
  assert.match(read(path.join('src', 'DocIntel.AppHost', 'DocIntel.AppHost.csproj')),
    /<NuGetLockFilePath>\$\(MSBuildThisFileDirectory\)packages\.\$\(NETCoreSdkRuntimeIdentifier\)\.lock\.json<\/NuGetLockFilePath>/);
  const common = [];
  for (const rid of ['win-x64', 'linux-x64']) {
    const lock = JSON.parse(read(path.join('src', 'DocIntel.AppHost', `packages.${rid}.lock.json`)));
    for (const prefix of ['Aspire.Dashboard.Sdk', 'Aspire.Hosting.Orchestration']) {
      const key = `${prefix}.${rid}`;
      assert.equal(lock.dependencies['net10.0'][key].resolved, '13.1.0');
      delete lock.dependencies['net10.0'][key];
      assert.ok(!Object.keys(lock.dependencies['net10.0']).some(name => name.startsWith(prefix + '.')));
    }
    common.push(lock);
  }
  assert.deepEqual(common[0], common[1], 'Shared package versions and hashes must not drift between operating systems');
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

test('personal subscription identities and resource names are configurable without breaking the existing deployment', () => {
  for (const name of ['CLIENT_ID', 'TENANT_ID', 'SUBSCRIPTION_ID']) {
    assert.ok(workflow.includes(`secrets.AZURE_${name} || secrets.CADOCINTELDEV_AZURE_${name}`));
  }
  for (const [name, fallback] of [
    ['AZURE_CONTAINER_REGISTRY', 'acrdocinteldevokjs3fevazuto'],
    ['AZURE_CONTAINER_APP_NAME', 'ca-docintel-dev'], ['AZURE_RESOURCE_GROUP', 'Docintel']
  ]) assert.ok(workflow.includes(`vars.${name} || '${fallback}'`));
  assert.match(workflow, /az acr login --name "\$AZURE_CONTAINER_REGISTRY"/);
  assert.match(workflow, /az containerapp update \\\r?\n\s+--name "\$AZURE_CONTAINER_APP_NAME" \\\r?\n\s+--resource-group "\$AZURE_RESOURCE_GROUP" \\/);
  assert.ok(workflow.includes('${AZURE_CONTAINER_REGISTRY}.azurecr.io/${AZURE_CONTAINER_APP_NAME}:${GITHUB_SHA}'));
});

function runBash(script, env) {
  const executable = process.platform === 'win32'
    ? path.join(process.env.ProgramFiles || 'C:\\Program Files', 'Git', 'bin', 'bash.exe') : 'bash';
  const result = spawnSync(executable, ['--noprofile', '--norc', '-c', script], {
    env: { ...process.env, ...env }, encoding: 'utf8', windowsHide: true, timeout: 10000
  });
  assert.ifError(result.error);
  return result;
}

test('the actual identity preflight accepts complete personal or legacy settings but rejects every mixed combination', () => {
  const script = workflow.match(/name: Prevent mixed personal and legacy Azure identities[\s\S]*?run: \|\r?\n([\s\S]*?)\r?\n      - name: Sign in/)[1]
    .replace(/^ {10}/gm, '');
  for (let mask = 0; mask < 8; mask++) {
    const result = runBash(script, {
      PERSONAL_CLIENT_ID: mask & 1 ? 'fixture-client' : '',
      PERSONAL_TENANT_ID: mask & 2 ? 'fixture-directory' : '',
      PERSONAL_SUBSCRIPTION_ID: mask & 4 ? 'fixture-subscription' : ''
    });
    assert.equal(result.status, mask === 0 || mask === 7 ? 0 : 1);
  }
});

test('the actual deployment command passes the selected subscription resources as quoted CLI arguments', () => {
  const script = workflow.match(/name: Deploy the validated image\r?\n\s+run: \|\r?\n([\s\S]*)$/)[1]
    .replace(/^ {10}/gm, '');
  const result = runBash('az() { printf "%s\\n" "$@"; }\n' + script, {
    AZURE_CONTAINER_REGISTRY: 'personalregistry', AZURE_CONTAINER_APP_NAME: 'personal-docintel',
    AZURE_RESOURCE_GROUP: 'personal-rg', GITHUB_SHA: 'fixture-sha'
  });
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(result.stdout.trim().split(/\r?\n/), [
    'containerapp', 'update', '--name', 'personal-docintel', '--resource-group', 'personal-rg',
    '--image', 'personalregistry.azurecr.io/personal-docintel:fixture-sha'
  ]);
});

test('feature-branch validation builds the Linux image without registry or Azure access', () => {
  const validation = workflow.split(/\r?\n  deploy:/)[0];
  assert.doesNotMatch(validation, /secrets\.|azure\/login|az containerapp update|id-token: write/);
  assert.match(validation, /Build the container without publishing/);
  assert.match(validation, /docker\/build-push-action@v6/);
  assert.match(validation, /push: false/);
  assert.match(validation, /tags: docintel:validation/);
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
