const { spawn } = require('node:child_process');
const { once } = require('node:events');
const path = require('node:path');

const apiDll = path.join(__dirname, '..', '..', '..', 'src', 'DocIntel.Api',
  'bin', 'Release', 'net10.0', 'DocIntel.Api.dll');

async function startApi(directory, settings = {}, { environment = 'Development', env = {} } = {}) {
  const configuration = {
    urls: 'http://127.0.0.1:0',
    contentRoot: directory,
    'Identity:Mode': 'LocalDevelopment',
    'Identity:UseManagedIdentityAssertion': false,
    'Models:AllowDevelopmentSimulation': true,
    'AzureAI:CosmosEndpoint': '',
    'AzureAI:BlobEndpoint': '',
    'AzureAI:AzureOpenAIEndpoint': '',
    'AzureAI:FoundryInferenceEndpoint': '',
    'AzureAI:LocalOpenAIEndpoint': '',
    'AzureAI:TranslatorEndpoint': '',
    ...settings
  };
  const child = spawn('dotnet', [apiDll,
    ...Object.entries(configuration).flatMap(([name, value]) => [`--${name}`, String(value)])
  ], {
    env: {
      ...process.env, ...env, DOTNET_ENVIRONMENT: environment, ASPNETCORE_ENVIRONMENT: environment,
      OTEL_SDK_DISABLED: 'true', OTEL_EXPORTER_OTLP_ENDPOINT: '', APPLICATIONINSIGHTS_CONNECTION_STRING: ''
    },
    stdio: ['ignore', 'pipe', 'pipe']
  });
  let output = '';
  const url = await new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      child.kill('SIGKILL');
      reject(new Error(`API startup timed out: ${output}`));
    }, 20000);
    child.on('error', error => { clearTimeout(timeout); reject(error); });
    child.on('exit', code => {
      clearTimeout(timeout);
      reject(new Error(`API exited (${code}): ${output}`));
    });
    child.stderr.on('data', chunk => { output += chunk; });
    child.stdout.on('data', chunk => {
      output += chunk;
      const match = output.match(/Now listening on:\s+(http:\/\/127\.0\.0\.1:\d+)/);
      if (match) {
        clearTimeout(timeout);
        resolve(match[1]);
      }
    });
  });
  return { child, url };
}

async function killApi(api) {
  if (api && api.child.exitCode === null && api.child.signalCode === null) {
    const exited = once(api.child, 'exit');
    api.child.kill('SIGKILL');
    await exited;
  }
}

module.exports = { startApi, killApi };
