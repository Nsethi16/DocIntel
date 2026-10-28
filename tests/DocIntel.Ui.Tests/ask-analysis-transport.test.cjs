const assert = require('node:assert/strict');
const { once } = require('node:events');
const fs = require('node:fs/promises');
const http = require('node:http');
const os = require('node:os');
const path = require('node:path');
const test = require('node:test');
const { startApi, killApi } = require('./helpers/api.cjs');

test('analytical requests satisfy provider metadata limits and round-trip real SDK tool calls',
  { timeout: 90000 }, async () => {
    const directory = await fs.mkdtemp(path.join(os.tmpdir(), 'DocIntel.AnalysisTransport-'));
    const providerErrors = [];
    let api;
    let calls = 0;
    let selectedJobId;
    let inspected;
    const provider = http.createServer(async (request, response) => {
      try {
        const chunks = [];
        for await (const chunk of request) chunks.push(chunk);
        const body = JSON.parse(Buffer.concat(chunks).toString('utf8'));
        calls++;
        const tools = body.tools || [];
        assert.ok(tools.some(tool => tool.function?.name === 'analyze_evidence'));
        const oversized = tools.find(tool => (tool.function?.description || '').length > 1024);
        if (oversized) {
          providerErrors.push('Tool description exceeds the provider limit.');
          response.writeHead(400, { 'Content-Type': 'application/json' });
          response.end(JSON.stringify({ error: {
            code: 'string_above_max_length', type: 'invalid_request_error',
            param: 'tools.function.description', message: 'Tool description exceeds 1024 characters.'
          } }));
          return;
        }
        const instructions = body.messages.filter(message => ['system', 'developer'].includes(message.role))
          .map(message => typeof message.content === 'string' ? message.content : JSON.stringify(message.content)).join('\n');
        assert.match(instructions, /analyze_evidence request rules:/);
        assert.match(instructions, /dateBucket is none\/month\/quarter\/year/);
        const results = body.messages.filter(message => message.role === 'tool');
        let message;
        const call = (name, args) => ({ role: 'assistant', tool_calls: [{
          id: `step-${results.length}`, type: 'function',
          function: { name, arguments: JSON.stringify(args) }
        }] });
        if (results.length === 0) {
          message = call('browse_documents', { query: {
            searchText: 'Contoso-Cloud-Price-Sheet', documentType: null, entities: [],
            pageSize: 10, continuationToken: null
          } });
        } else if (results.length === 1) {
          const page = JSON.parse(results[0].content);
          selectedJobId = page.documents[0].jobId;
          message = call('inspect_document_slice', { jobId: selectedJobId, sliceIndex: 0, textOffset: 0 });
        } else if (results.length === 2) {
          inspected = JSON.parse(results[1].content);
          assert.match(inspected.sourceText, /410,000\.00/);
          assert.ok(inspected.sourceText.trim().length <= 2000);
          message = call('analyze_evidence', { request: {
            title: 'Source-backed USD quotes', operation: 'sum', visualization: 'bar',
            groupBy: 'customer', valueColumn: 'amount', dateBucket: 'none',
            columns: [
              { key: 'customer', label: 'Customer', kind: 'text',
                numberFormat: 'dot-decimal', dateFormat: 'yyyy-MM-dd' },
              { key: 'quote_id', label: 'Quote Number', kind: 'text',
                numberFormat: 'dot-decimal', dateFormat: 'yyyy-MM-dd' },
              { key: 'amount', label: 'Deal Value', kind: 'number',
                numberFormat: 'dot-decimal', dateFormat: 'yyyy-MM-dd' }
            ],
            rows: [{
              evidenceId: inspected.evidenceId, sourceQuote: inspected.sourceText.trim(), unit: 'USD',
              cells: { customer: 'Contoso Legal', quote_id: '3037534.001', amount: '410,000.00' }
            }]
          } });
        } else {
          const analysis = JSON.parse(results[2].content);
          assert.equal(analysis.analysisId, 'a1');
          assert.equal(analysis.rows[0].cells.value, 410000);
          message = { role: 'assistant', content: JSON.stringify({
            kind: 'DocumentAnswer', answer: 'The inspected quote totals 410,000.00 USD [1].',
            evidenceIds: [inspected.evidenceId], analysisIds: [analysis.analysisId]
          }) };
        }
        response.writeHead(200, { 'Content-Type': 'application/json' });
        response.end(JSON.stringify({
          id: 'chatcmpl-analysis-transport', object: 'chat.completion', model: 'phi-4',
          created: Math.floor(Date.now() / 1000),
          choices: [{ index: 0, message, finish_reason: message.tool_calls ? 'tool_calls' : 'stop' }]
        }));
      } catch (error) {
        providerErrors.push(error.message);
        response.writeHead(500);
        response.end('Synthetic provider contract failure.');
      }
    });
    provider.listen(0, '127.0.0.1');
    await once(provider, 'listening');
    try {
      api = await startApi(directory, {
        'AzureAI:LocalOpenAIEndpoint': `http://127.0.0.1:${provider.address().port}/v1`
      });
      const base = api.url + '/api/harness/tenants/contoso-legal';
      const verification = await fetch(api.url + '/api/identity/csrf');
      const token = (await verification.json()).token;
      const headers = { 'Content-Type': 'application/json',
        Cookie: verification.headers.get('set-cookie').split(';')[0], 'X-DocIntel-CSRF': token };
      const tenant = await (await fetch(base)).json();
      tenant.defaultModelId = 'foundry-local-phi4';
      tenant.allowedModels = [...new Set([...tenant.allowedModels, 'foundry-local-phi4'])];
      tenant.capabilities.AskAi.modelIdOverride = 'foundry-local-phi4';
      assert.equal((await fetch(base, { method: 'PUT', headers, body: JSON.stringify(tenant) })).status, 200);
      assert.equal((await fetch(base + '/seed-examples', { method: 'POST', headers, body: '{}' })).status, 200);
      const result = await fetch(base + '/ask', { method: 'POST', headers, body: JSON.stringify({
        question: 'Compare USD quote values by customer, show a bar chart and cite every amount.'
      }) });
      const data = await result.json();
      assert.equal(result.status, 200, `${JSON.stringify(data)}\n${providerErrors.join('\n')}\n${api.getLogs()}`);
      assert.equal(calls, 4);
      assert.deepEqual(providerErrors, []);
      assert.equal(data.analyses.length, 1);
      assert.equal(data.analyses[0].visualization, 'bar');
      assert.equal(data.analyses[0].rows[0].cells.value, 410000);
      assert.equal(data.analyses[0].rows[0].cells.sharePercent, 100);
      assert.deepEqual(data.analyses[0].rows[0].citationNumbers, [1]);
      assert.equal(data.citations[0].jobId, selectedJobId);
      const saved = await (await fetch(base + '/ask/runs/' + data.runId)).json();
      assert.deepEqual(saved.analyses, data.analyses);
    } finally {
      await killApi(api);
      provider.closeAllConnections();
      await new Promise(resolve => provider.close(resolve));
      await fs.rm(directory, { recursive: true });
    }
  });
