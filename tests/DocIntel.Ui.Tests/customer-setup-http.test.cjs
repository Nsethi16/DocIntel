const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const path = require('node:path');
const { DatabaseSync } = require('node:sqlite');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

const tenantId = 'contoso-legal';
const base = '/api/harness/tenants/' + tenantId;

async function fixture(documentIds = []) {
  const directory = path.join(__dirname, '.customer-http-' + crypto.randomUUID());
  await fs.mkdir(directory);
  const directoryId = crypto.randomUUID();
  const webId = crypto.randomUUID();
  const apiId = crypto.randomUUID();
  const admin = crypto.randomUUID();
  const reader = crypto.randomUUID();
  const processor = crypto.randomUUID();
  const outsider = crypto.randomUUID();
  const keys = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  const publicKey = { ...keys.publicKey.export({ format: 'jwk' }), kid: 'customer-fixture', alg: 'RS256', use: 'sig' };
  let issuer;
  let providerCalls = 0;
  const provider = http.createServer((request, response) => {
    let result;
    if (request.url === '/.well-known/openid-configuration') {
      result = { issuer, jwks_uri: issuer + '/keys', authorization_endpoint: issuer + '/authorize',
        token_endpoint: issuer + '/token', response_types_supported: ['code'], subject_types_supported: ['public'],
        id_token_signing_alg_values_supported: ['RS256'] };
    } else if (request.url === '/keys') result = { keys: [publicKey] };
    else {
      providerCalls++;
      response.writeHead(500);
      response.end('No directory, inference, mailbox or delivery calls are permitted in this fixture.');
      return;
    }
    response.writeHead(200, { 'Content-Type': 'application/json' });
    response.end(JSON.stringify(result));
  });
  provider.listen(0, '127.0.0.1');
  await once(provider, 'listening');
  issuer = `http://127.0.0.1:${provider.address().port}`;
  await fs.writeFile(path.join(directory, 'appsettings.json'), JSON.stringify({
    Identity: { Memberships: [
      { directoryTenantId: directoryId, objectId: admin, applicationTenantId: tenantId,
        role: 'Admin', allDocuments: true },
      { directoryTenantId: directoryId, objectId: reader, applicationTenantId: tenantId,
        role: 'Reader', allDocuments: false, documentIds },
      { directoryTenantId: directoryId, objectId: processor, applicationTenantId: tenantId,
        role: 'Processor', allDocuments: true }
    ] }
  }));
  const settings = {
    'Identity:Mode': 'Entra', 'Identity:DirectoryTenantId': directoryId,
    'Identity:ClientId': webId, 'Identity:ApiClientId': apiId,
    'Identity:AllowLoopbackAuthority': true, 'Identity:LoopbackAuthority': issuer,
    'Identity:ClientSecret': 'fixture-private-credential-not-for-output',
    'Identity:Directory:Enabled': true, 'Identity:Directory:DevelopmentEndpoint': issuer + '/graph/',
    'Identity:ReadRequestsPerMinute': 1000, 'Identity:WriteRequestsPerMinute': 200,
    'Channels:Teams:Enabled': false,
    'Channels:Email:ApplicationTenantId': tenantId,
    'Channels:Email:MailboxAddress': 'unconfigured-fixture@example.invalid',
    'Channels:Email:CorporateSenderProof': 'fixture-private-attestation-not-for-output'
  };
  let api;
  function token(id) {
    const now = Math.floor(Date.now() / 1000);
    const header = Buffer.from(JSON.stringify({ alg: 'RS256', typ: 'JWT', kid: publicKey.kid })).toString('base64url');
    const body = Buffer.from(JSON.stringify({
      iss: issuer, aud: apiId, tid: directoryId, oid: id, sub: id, scp: 'DocIntel.Access',
      iat: now, nbf: now - 5, exp: now + 600
    })).toString('base64url');
    return header + '.' + body + '.' + crypto.sign('RSA-SHA256', Buffer.from(header + '.' + body),
      keys.privateKey).toString('base64url');
  }
  return {
    directory, issuer, admin, reader, processor, outsider,
    providerCalls: () => providerCalls,
    async start(overrides = {}) {
      await killApi(api);
      api = await startApi(directory, { ...settings, ...overrides });
    },
    request(route, id = admin, method = 'GET', body, extraHeaders = {}) {
      return fetch(api.url + route, { method, redirect: 'manual',
        headers: { ...(id ? { Authorization: 'Bearer ' + token(id) } : {}),
          ...(body ? { 'Content-Type': 'application/json' } : {}), ...extraHeaders },
        body: body ? JSON.stringify(body) : undefined });
    },
    async stop() {
      await killApi(api);
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true, force: true });
    }
  };
}

test('customer setup is admin-only, configuration-only, secret-free and read-only',
  { timeout: 120000 }, async t => {
    // Arrange: only an isolated loopback identity issuer is used; every provider call is counted.
    const f = await fixture();
    try {
      await f.start();
      await t.test('authentication and tenant administration are checked before reporting configuration', async () => {
        assert.equal((await f.request(base + '/setup', null)).status, 401);
        for (const id of [f.reader, f.processor, f.outsider]) {
          assert.equal((await f.request(base + '/setup', id)).status, 403);
        }
        assert.equal((await f.request('/api/harness/tenants/fabrikam-finance/setup')).status, 403);
        const response = await f.request(base + '/setup');
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        const report = await response.json();
        assert.deepEqual(Object.keys(report).sort(), ['channels', 'checks', 'directory', 'models', 'workspace']);
        assert.equal(report.workspace.tenantId, tenantId);
        assert.equal(report.directory.configured, true);
        assert.equal(report.models.some(model => model.configured), false);
        assert.ok(report.models.filter(model => model.selected).every(model => model.state === 'DevelopmentOnly'));
        assert.equal(report.channels.email.configured, false);
        assert.equal(report.channels.email.enabled, false);
        assert.equal(report.channels.email.address, null);
        assert.equal(report.channels.email.verified, false);
        assert.equal(report.channels.teams.verified, false);
        assert.ok(report.channels.email.setupSteps.length > 0);
        assert.deepEqual(report.checks.map(check => check.id),
          ['models', 'content-understanding', 'decision-rules', 'extraction', 'people', 'data', 'channels']);
        assert.ok(report.checks.every(check => ['Configured', 'ActionRequired', 'NotStarted'].includes(check.state)));
        assert.equal(report.checks.find(check => check.id === 'data').state, 'NotStarted');
        assert.equal(report.checks.find(check => check.id === 'people').state, 'NotStarted');
        assert.doesNotMatch(JSON.stringify(report), /fixture-private|unconfigured-fixture|App_Data|storageLocation/);
        assert.doesNotMatch(JSON.stringify(report), /127\.0\.0\.1|modelDescriptors|deploymentName|endpoint"\s*:/i);
      });

      await t.test('configured endpoints never imply deployed models, verified inference or channel delivery', async () => {
        await f.start({
          'AzureAI:FoundryInferenceEndpoint': f.issuer + '/models',
          'AzureAI:AzureOpenAIEndpoint': f.issuer,
          'AzureAI:DecisionEndpoint': f.issuer,
          'AzureAI:LocalOpenAIEndpoint': f.issuer + '/v1',
          'AzureAI:ContentUnderstandingEndpoint': f.issuer + '/private-layout'
        });
        const before = await (await f.request(base)).json();
        const response = await f.request(base + '/setup');
        assert.equal(response.status, 200);
        const report = await response.json();
        assert.ok(report.models.filter(model => model.configured)
          .every(model => model.state === 'EndpointConfiguredUnverified'));
        assert.ok(report.models.filter(model => model.selected).every(model => model.configured));
        assert.equal(report.models.find(model => model.modelId === 'decision-1').purpose, 'decision');
        assert.equal(report.models.find(model => model.modelId === 'decision-1').supportsPdfInputs, false);
        assert.equal(report.models.find(model => model.modelId === 'gpt-5-mini').supportsPdfInputs, true);
        assert.equal(report.models.find(model => model.modelId === 'gpt-4o').state, 'EndpointConfiguredUnverified');
        assert.match(report.checks.find(check => check.id === 'models').description, /does not prove any deployment/);
        assert.equal(report.checks.find(check => check.id === 'content-understanding').state, 'Configured');
        assert.equal(report.channels.email.configured, false);
        assert.equal(report.channels.teams.configured, false);
        assert.doesNotMatch(JSON.stringify(report), /private-layout|127\.0\.0\.1|fixture-private|App_Data/);
        assert.equal((await (await f.request(base)).json()).harnessVersion, before.harnessVersion);
        assert.equal(f.providerCalls(), 0, 'reporting performs no directory, inference or delivery probes');
      });

      await t.test('legacy chat-as-decision and unknown injected selections are retained but actionable', async () => {
        const tenant = await (await f.request(base)).json();
        tenant.defaultModelId = 'legacy-injected-selection';
        tenant.decisionModelId = 'gpt-5-mini';
        tenant.agent.emailEnabled = true;
        tenant.agent.teamsEnabled = true;
        assert.equal((await f.request(base, f.admin, 'PUT', tenant)).status, 200);
        const report = await (await f.request(base + '/setup')).json();
        const unknown = report.models.find(model => model.modelId === 'legacy-injected-selection');
        assert.equal(unknown.selected, true);
        assert.equal(unknown.configured, false);
        assert.equal(unknown.state, 'UnknownSelection');
        const legacy = report.models.find(model => model.modelId === 'gpt-5-mini' && model.purpose === 'decision');
        assert.equal(legacy.selected, true);
        assert.equal(legacy.configured, false);
        assert.equal(legacy.state, 'NativeDecisionRequired');
        assert.equal(report.checks.find(check => check.id === 'models').state, 'ActionRequired');
        for (const channel of [report.channels.email, report.channels.teams]) {
          assert.equal(channel.enabled, true);
          assert.equal(channel.configured, false);
          assert.equal(channel.state, 'ActionRequired');
          assert.equal(channel.verified, false);
        }
        const saved = await (await f.request(base)).json();
        assert.equal(saved.defaultModelId, 'legacy-injected-selection');
        assert.equal(saved.decisionModelId, 'gpt-5-mini');
        assert.equal(f.providerCalls(), 0);
      });

      await t.test('unknown selections resembling connection strings are actionable without echoing them', async () => {
        const tenant = await (await f.request(base)).json();
        tenant.defaultModelId = 'http://user:fixture-private@localhost/path?secret=fixture-private';
        assert.equal((await f.request(base, f.admin, 'PUT', tenant)).status, 200);
        const report = await (await f.request(base + '/setup')).json();
        const selected = report.models.find(model => model.selected && model.purpose === 'language');
        assert.equal(selected.state, 'UnknownSelection');
        assert.equal(selected.configured, false);
        assert.equal(selected.modelId, 'unknown-language-selection');
        assert.doesNotMatch(JSON.stringify(report), /fixture-private|localhost|secret=/);
      });

      await t.test('invalid connection schemes, native chat paths and credential-bearing URLs are not configured',
        async () => {
          await f.start({
            'AzureAI:AzureOpenAIEndpoint': 'http://example.invalid/',
            'AzureAI:FoundryInferenceEndpoint': 'https://user:fixture-private@example.invalid/models',
            'AzureAI:DecisionEndpoint': f.issuer + '/models'
          });
          const report = await (await f.request(base + '/setup')).json();
          for (const id of ['decision-1', 'gpt-5-mini', 'gpt-4o', 'phi-4']) {
            assert.equal(report.models.find(model => model.modelId === id).configured, false, id);
          }
          assert.equal(report.models.find(model => model.modelId === 'decision-1').state, 'InvalidConfiguration');
          assert.doesNotMatch(JSON.stringify(report), /fixture-private|example\.invalid|127\.0\.0\.1/);
          assert.equal(f.providerCalls(), 0);
        });
    } finally { await f.stop(); }
  });

async function seedDocuments(directory) {
  const data = path.join(directory, 'App_Data');
  await fs.mkdir(data, { recursive: true });
  const database = new DatabaseSync(path.join(data, 'document-jobs.sqlite'));
  try {
    database.exec(`CREATE TABLE document_jobs (
      tenant TEXT NOT NULL, id TEXT NOT NULL, body BLOB NOT NULL,
      status INTEGER NOT NULL, version INTEGER NOT NULL, created INTEGER NOT NULL,
      owner TEXT NULL, expires INTEGER NULL, attempts INTEGER NOT NULL DEFAULT 0,
      PRIMARY KEY (tenant,id));`);
    const insert = database.prepare(
      'INSERT INTO document_jobs(tenant,id,body,status,version,created) VALUES(?,?,?,?,1,?)');
    function add(index, tenant = tenantId, status = 'Completed') {
      const id = 'doc-' + String(index).padStart(4, '0');
      const created = Date.UTC(2026, 0, 1) + index * 1000;
      const job = {
        jobId: id, tenantId: tenant, TenantDisplayName: 'Fixture customer', AppliedHarnessVersion: 1,
        FileName: `case-${String(index).padStart(4, '0')}.pdf`, Status: status,
        TotalPages: 2, CreatedAtUtc: new Date(created).toISOString(), HasDevelopmentResults: true,
        ErrorMessage: status === 'Failed' ? 'Fixture processing failed.' : null,
        OriginalPdfLocation: 'C:\\private-resource-path\\original.pdf',
        PacketSummary: index === 1 ? 'packet-summary-needle' : '',
        ExtractedEntities: [{ name: 'Customer Name', value: 'Contoso', category: 'Organization' }],
        ExecutionTrace: [{ ExecutorName: 'Extract', Executed: true, Succeeded: true,
          ModelIdUsed: 'fixture', DurationMs: 1, Notes: 'SOURCE_BODY_NOT_FOR_SUMMARIES' }],
        Segments: [{
          segmentIndex: 0, startPage: 1, endPage: 2, documentTypeName: 'Agreement',
          identifiedDocumentName: 'Agreement',
          bodyText: index === 0 ? 'body-query-needle SOURCE_BODY_NOT_FOR_SUMMARIES' : '',
          markdown: index === 3 ? 'markdown-query-needle' : '',
          translatedText: index === 4 ? 'translation-query-needle' : '',
          insightSummary: index === 5 ? 'insight-query-needle' : '',
          footers: index === 6 ? ['footer-query-needle'] : [],
          extractedEntities: [
            { name: 'Customer Name', value: 'Contoso', category: 'Organization' },
            { name: 'customer name', value: 'contoso', category: 'Organization' },
            { name: 'Contract ID', value: index === 7 ? 'entity-query-needle' : 'C-100', category: 'Identifier' }
          ],
          dataExtraction: { documentName: 'Agreement', footerId: 'form', pageRange: '1-2',
            fields: [{ fieldName: 'Reference', fieldValue: index === 8 ? 'field-query-needle' : '' }],
            tables: [{ tableName: 'Lines', rows: [{ fields: [
              { fieldName: 'Product', fieldValue: index === 9 ? 'table-query-needle' : '' }
            ] }] }] },
          promptColumnResults: [{
            columnId: 'custom', columnName: 'Custom', promptInstructions: 'not-searchable-configuration',
            filterSummary: '', filterMatched: true, matchedEntities: [], status: 'Completed',
            resultText: index === 2 ? 'custom-query-needle' : '', processedAsFileBase64: false,
            fileName: 'fixture.pdf', fileSizeBytes: 0, base64Preview: '',
            modelIdUsed: 'fixture', segmentIndex: 0, documentName: 'Agreement', pageRange: '1-2',
            evaluatedAtUtc: '2026-01-01T00:00:00Z'
          }]
        }]
      };
      insert.run(tenant, id, Buffer.from(JSON.stringify(job)),
        status === 'Completed' ? 2 : status === 'Failed' ? 3 : 4, created);
    }
    database.exec('BEGIN');
    for (let index = 0; index < 1005; index++) add(index);
    add(1005, tenantId, 'Failed');
    add(1006, tenantId, 'Deleted');
    add(1007, 'fabrikam-finance');
    database.exec('COMMIT');
  } finally { database.close(); }
}

test('customer document summaries authorize and search before paging without returning source content',
  { timeout: 120000 }, async t => {
    // Arrange: real local persistence, scoped membership, deleted records and another customer's records.
    const f = await fixture(['doc-0000', 'doc-0002', 'doc-1006', 'doc-1007']);
    try {
      await seedDocuments(f.directory);
      await f.start();
      await t.test('default and explicit pagination return only lightweight stable summaries', async () => {
        const response = await f.request(base + '/documents');
        assert.equal(response.status, 200);
        assert.equal(response.headers.get('cache-control'), 'no-store');
        const page = await response.json();
        assert.equal(page.total, 1006);
        assert.equal(page.items.length, 50);
        assert.equal(page.nextOffset, 50);
        assert.equal(page.items[0].jobId, 'doc-1005');
        assert.equal(page.items[0].status, 'Failed');
        assert.equal(page.items[0].errorMessage, 'Fixture processing failed.');
        assert.equal(page.items[1].segmentCount, 1);
        assert.equal(page.items[1].hasDevelopmentResults, true);
        assert.ok(page.items.every(item => item.tenantId === tenantId));
        assert.deepEqual(Object.keys(page.items[0]).sort(), [
          'createdAtUtc', 'errorMessage', 'executionTrace', 'fileName', 'hasDevelopmentResults',
          'jobId', 'segmentCount', 'status', 'tenantId', 'totalPages'
        ]);
        assert.doesNotMatch(JSON.stringify(page), /SOURCE_BODY|private-resource|bodyText|markdown|segments|extractedEntities/);
        const end = await (await f.request(base + '/documents?offset=1000&limit=100')).json();
        assert.equal(end.items.length, 6);
        assert.equal(end.nextOffset, null);
        const beyond = await (await f.request(base + '/documents?offset=2147483647')).json();
        assert.equal(beyond.total, 1006);
        assert.deepEqual(beyond.items, []);
        assert.equal(beyond.nextOffset, null);
      });

      await t.test('scoped authorization and soft deletion apply before total and pagination', async () => {
        assert.equal((await f.request(base + '/documents', null)).status, 401);
        assert.equal((await f.request(base + '/documents', f.outsider)).status, 403);
        assert.equal((await f.request('/api/harness/tenants/fabrikam-finance/documents', f.reader)).status, 403);
        const first = await (await f.request(base + '/documents?limit=1', f.reader)).json();
        assert.equal(first.total, 2);
        assert.equal(first.items[0].jobId, 'doc-0002');
        assert.equal(first.nextOffset, 1);
        const second = await (await f.request(base + '/documents?offset=1&limit=1', f.reader)).json();
        assert.equal(second.total, 2);
        assert.equal(second.items[0].jobId, 'doc-0000');
        assert.equal(second.nextOffset, null);
        const unauthorizedSearch = await (await f.request(base + '/documents?q=case-1004', f.reader)).json();
        assert.equal(unauthorizedSearch.total, 0);
        assert.deepEqual(unauthorizedSearch.items, []);
        const deleted = await (await f.request(base + '/documents?status=Deleted')).json();
        assert.equal(deleted.total, 0);
        const failed = await (await f.request(base + '/documents?status=failed&limit=1')).json();
        assert.equal(failed.total, 1);
        assert.equal(failed.items[0].status, 'Failed');
      });

      await t.test('content, metadata, entities and custom extraction search run before pagination', async () => {
        const needles = [
          ['body-query-needle', 'doc-0000'], ['packet-summary-needle', 'doc-0001'],
          ['custom-query-needle', 'doc-0002'], ['markdown-query-needle', 'doc-0003'],
          ['translation-query-needle', 'doc-0004'], ['insight-query-needle', 'doc-0005'],
          ['footer-query-needle', 'doc-0006'], ['entity-query-needle', 'doc-0007'],
          ['field-query-needle', 'doc-0008'], ['table-query-needle', 'doc-0009'],
          ['CASE-0000.PDF', 'doc-0000'], ['doc-0000', 'doc-0000']
        ];
        for (const [needle, id] of needles) {
          const page = await (await f.request(base + '/documents?limit=1&status=Completed&q=' +
            encodeURIComponent(needle))).json();
          assert.equal(page.total, 1, needle);
          assert.equal(page.items[0].jobId, id, needle);
          assert.equal(page.nextOffset, null);
          assert.doesNotMatch(JSON.stringify(page), /query-needle|SOURCE_BODY|private-resource/);
        }
        const empty = await (await f.request(base + '/documents?offset=1&q=body-query-needle')).json();
        assert.equal(empty.total, 1);
        assert.deepEqual(empty.items, []);
        const scoped = await (await f.request(base + '/documents?q=custom-query-needle', f.reader)).json();
        assert.equal(scoped.items[0].jobId, 'doc-0002');
      });

      await t.test('invalid query ranges, status values, duplicates and oversized search return 400', async () => {
        const invalid = [
          'offset=-1', 'offset=oops', 'offset=2147483648', 'limit=0', 'limit=-1', 'limit=101', 'limit=oops',
          'status=Unknown', 'status=2', 'status=Completed,Failed', 'q=' + 'x'.repeat(101),
          'q=%00', 'q=%0A', 'offset=0&offset=1', 'limit=1&limit=2', 'q=a&q=b', 'status=Failed&status=Completed'
        ];
        for (const query of invalid) {
          assert.equal((await f.request(base + '/documents?' + query)).status, 400, query);
        }
        assert.equal((await f.request(base + '/documents?q=' + 'x'.repeat(100))).status, 200);
      });

      await t.test('graph adds bounded actual metadata while retaining the existing nodes and edges contract', async () => {
        const graph = await (await f.request(base + '/graph?maxDocs=1000')).json();
        assert.equal(graph.totalDocumentsInTenant, 1005);
        assert.equal(graph.documents.length, 1000);
        assert.equal(graph.truncated, true);
        assert.ok(Array.isArray(graph.docNodes));
        assert.ok(Array.isArray(graph.entityNodes));
        assert.ok(Array.isArray(graph.edges));
        assert.equal(graph.documents[0].jobId, 'doc-1004');
        assert.deepEqual(graph.documents[0].documentTypes, ['Agreement']);
        assert.equal(graph.documents[0].entities.length, 2, 'case-insensitive actual pairs are deduplicated');
        assert.equal(graph.documents[0].segmentCount, 1);
        assert.doesNotMatch(JSON.stringify(graph), /SOURCE_BODY|private-resource|bodyText|markdown|insightSummary/);
        const scoped = await (await f.request(base + '/graph?maxDocs=1000', f.reader)).json();
        assert.equal(scoped.documents.length, 2);
        assert.equal(scoped.truncated, false);
        assert.deepEqual(new Set(scoped.documents.map(document => document.jobId)), new Set(['doc-0000', 'doc-0002']));
        const cap = await (await f.request(base + '/graph?maxDocs=10')).json();
        assert.equal(cap.documents.length, 10);
        assert.equal(cap.truncated, true);
      });

      await t.test('existing full job reads remain backward compatible and inactive tenants stay unavailable', async () => {
        const jobs = await (await f.request(base + '/jobs', f.reader)).json();
        assert.equal(jobs.length, 2);
        assert.ok(jobs[0].segments.length > 0);
        assert.equal(jobs[1].segments[0].bodyText, 'body-query-needle SOURCE_BODY_NOT_FOR_SUMMARIES');
        const selected = await (await f.request(base + '/jobs/doc-0000', f.reader)).json();
        assert.equal(selected.segments[0].bodyText, 'body-query-needle SOURCE_BODY_NOT_FOR_SUMMARIES');
        assert.equal((await f.request(base + '/jobs/doc-1004', f.reader)).status, 404);
        assert.equal((await f.request(base + '/jobs/doc-1006')).status, 404);
        const deleted = await f.request(base, f.admin, 'DELETE', undefined,
          { 'X-DocIntel-Confirm-Delete': tenantId });
        assert.equal(deleted.status, 200);
        for (const suffix of ['/documents', '/setup', '/graph']) {
          assert.equal((await f.request(base + suffix)).status, 404, suffix);
        }
        assert.equal(f.providerCalls(), 0);
      });
    } finally { await f.stop(); }
  });
