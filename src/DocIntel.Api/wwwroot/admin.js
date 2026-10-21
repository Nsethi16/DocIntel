let tenants = [];
    let currentTenant = null;
    let ocrBudgetSnapshot = null;
    let ocrBudgetTenantId = null;
    let ocrBudgetRevision = 0;
    let ocrBudgetSaving = false;

    async function loadOcrUsage() {
      const tenantId = currentTenant?.tenantId;
      if (!tenantId) return;
      const revision = ++ocrBudgetRevision;
      ocrBudgetSnapshot = null;
      document.getElementById('ocrBudgetSave').disabled = true;
      document.getElementById('ocrPageLimit').disabled = true;
      document.getElementById('ocrWarningPercent').disabled = true;
      document.getElementById('ocrUsageStatus').textContent = 'Loading live OCR allowance...';
      try {
        const response = await fetch(`/api/harness/tenants/${encodeURIComponent(tenantId)}/ocr-usage`);
        const result = await response.json();
        if (!response.ok) throw new Error(result.title || result.error || 'OCR allowance could not be loaded.');
        if (currentTenant?.tenantId !== tenantId || revision !== ocrBudgetRevision) return;
        ocrBudgetSnapshot = result.budget;
        ocrBudgetTenantId = tenantId;
        document.getElementById('ocrPageLimit').value = result.budget.monthlyPageLimit;
        document.getElementById('ocrWarningPercent').value = result.budget.warningPercent;
        document.getElementById('ocrPageLimit').disabled = false;
        document.getElementById('ocrWarningPercent').disabled = false;
        document.getElementById('ocrBudgetSave').disabled = false;
        document.getElementById('ocrUsageStatus').textContent =
          `${result.period} UTC: ${result.reservedPages} reserved pages across ${result.requestCount} requests; ` +
          `${result.remainingPages} pages remain.`;
      } catch (error) {
        if (currentTenant?.tenantId === tenantId && revision === ocrBudgetRevision)
          document.getElementById('ocrUsageStatus').textContent = error.message;
      }
    }

    async function saveOcrBudget() {
      if (ocrBudgetSaving) {
        document.getElementById('ocrUsageStatus').textContent = 'An allowance save is still in progress.';
        return;
      }
      const tenantId = currentTenant?.tenantId;
      if (!ocrBudgetSnapshot || tenantId !== ocrBudgetTenantId) {
        document.getElementById('ocrUsageStatus').textContent = 'Load the current tenant allowance before saving.';
        return;
      }
      const limitText = String(document.getElementById('ocrPageLimit').value).trim();
      const warningText = String(document.getElementById('ocrWarningPercent').value).trim();
      const monthlyPageLimit = Number(limitText);
      const warningPercent = Number(warningText);
      if (!limitText || !warningText || !Number.isInteger(monthlyPageLimit) ||
          monthlyPageLimit < 0 || monthlyPageLimit > 10000000 ||
          !Number.isInteger(warningPercent) || warningPercent < 1 || warningPercent > 100) {
        document.getElementById('ocrUsageStatus').textContent = 'Enter a whole-page limit and a warning percentage from 1 to 100.';
        return;
      }
      const version = ocrBudgetSnapshot.version;
      const revision = ++ocrBudgetRevision;
      ocrBudgetSnapshot = null;
      ocrBudgetSaving = true;
      document.getElementById('ocrBudgetSave').disabled = true;
      try {
        const response = await fetch(`/api/harness/tenants/${encodeURIComponent(tenantId)}/ocr-budget`, {
          method: 'PUT', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ monthlyPageLimit, warningPercent, version })
        });
        const result = await response.json();
        if (!response.ok) throw new Error(result.title || result.error || 'Could not confirm the budget save. Reload before retrying.');
        if (currentTenant?.tenantId === tenantId && revision === ocrBudgetRevision) await loadOcrUsage();
      } catch (error) {
        if (currentTenant?.tenantId === tenantId && revision === ocrBudgetRevision)
          document.getElementById('ocrUsageStatus').textContent = `${error.message} Reload before saving again.`;
      } finally {
        ocrBudgetSaving = false;
      }
    }

    async function init() {
      const urlTenant = new URLSearchParams(window.location.search).get('tenant');
      await loadTenants(urlTenant || undefined);
    }

    async function loadTenants(selectedId) {
      const resp = await fetch('/api/harness/tenants');
      tenants = await resp.json();

      if (!selectedId && currentTenant) {
        selectedId = currentTenant.tenantId;
      }
      if (!selectedId && tenants.length > 0) {
        selectedId = tenants[0].tenantId;
      }

      currentTenant = tenants.find(t => t.tenantId === selectedId) || tenants[0] || null;
      renderSidebarTenants();
      renderMainCanvas();
    }

    function renderSidebarTenants() {
      const list = document.getElementById('tenantList');
      list.innerHTML = '';

      if (tenants.length === 0) {
        list.innerHTML = '<div class="empty-tenants">No tenants created yet</div>';
        return;
      }

      tenants.forEach(t => {
        const isActive = currentTenant && t.tenantId === currentTenant.tenantId;
        const initials = (t.displayName || t.tenantId)
          .split(' ')
          .map(w => w[0])
          .join('')
          .substring(0, 2)
          .toUpperCase();
        const row = document.createElement('div');
        row.className = 'tenant-row' + (isActive ? ' active' : '');
        row.title = `${t.displayName} (v${t.harnessVersion || 1})`;
        row.onclick = () => selectTenant(t.tenantId);
        row.innerHTML = `
          <span class="tenant-initials">${escapeHtml(initials)}</span>
          <span class="tenant-row-name">${escapeHtml(t.displayName)}</span>
          <span class="tenant-row-version">v${t.harnessVersion || 1}</span>
        `;
        list.appendChild(row);
      });
    }

    function toggleSidebar() {
      const sb = document.getElementById('leftSidebar');
      sb.classList.toggle('collapsed');
    }

    function expandAndOpenNewTenant() {
      const sb = document.getElementById('leftSidebar');
      sb.classList.remove('collapsed');
      toggleNewTenantPopover(true);
    }

    let savedSnapshotJson = '';
    let harnessSaveInFlight = false;
    let harnessSaveBlocked = false;

    function selectTenant(tenantId) {
      currentTenant = tenants.find(t => t.tenantId === tenantId) || null;
      renderSidebarTenants();
      renderMainCanvas();
    }

    const expandedCards = new Set();
    let workingDataExtractionProfiles = [];
    let activeExtractionDocName = '';
    let workingPromptColumns = [];
    let knownEntityNames = [
      'Contract Number',
      'MBA/MBSA Number',
      'Customer Name',
      'Quote Number',
      'Billing Currency',
      'Total Deal Value',
      'Tenant ID',
      'Document Heading',
      'Document Name'
    ];

    function toggleFeatureDrawer(key) {
      if (expandedCards.has(key)) {
        expandedCards.delete(key);
      } else {
        expandedCards.add(key);
      }
      renderExpandedState();
    }

    function renderExpandedState() {
      ['split', 'identify', 'translation', 'insight', 'entity', 'dataExtraction'].forEach(key => {
        const drawer = document.getElementById(key + 'Drawer');
        const chevron = document.getElementById(key + 'Chevron');
        const isOpen = expandedCards.has(key);
        if (drawer) drawer.classList.toggle('open', isOpen);
        if (chevron) chevron.classList.toggle('expanded', isOpen);
      });
    }

    function syncActiveDocProfileFromDom() {
      if (!activeExtractionDocName) return;
      let profile = workingDataExtractionProfiles.find(p => p.documentName === activeExtractionDocName);
      if (!profile) {
        profile = { documentName: activeExtractionDocName, fields: [], tables: [] };
        workingDataExtractionProfiles.push(profile);
      }

      const fields = [];
      document.querySelectorAll('#dataExtractionFieldsList .rule-item').forEach(row => {
        const fName = row.querySelector('[data-field="fieldName"]').value.trim();
        const instr = row.querySelector('[data-field="instruction"]').value.trim();
        if (fName) {
          fields.push({ fieldName: fName, instruction: instr });
        }
      });
      profile.fields = fields;

      const tables = [];
      document.querySelectorAll('#dataExtractionTablesList .table-schema-box').forEach(box => {
        const tName = box.querySelector('[data-field="tableName"]').value.trim();
        const tInstr = box.querySelector('[data-field="tableInstruction"]').value.trim();
        if (!tName) return;
        const cols = [];
        box.querySelectorAll('.col-row').forEach(cr => {
          const cName = cr.querySelector('[data-field="colName"]').value.trim();
          const cInstr = cr.querySelector('[data-field="colInstruction"]').value.trim();
          if (cName) {
            cols.push({ columnName: cName, instruction: cInstr });
          }
        });
        tables.push({ tableName: tName, instruction: tInstr, columns: cols });
      });
      profile.tables = tables;
    }

    function renderDataExtractionDocDropdown(definedDocs) {
      const sel = document.getElementById('dataExtractionDocSelect');
      const docNames = [...new Set([
        ...definedDocs,
        ...workingDataExtractionProfiles.map(p => p.documentName)
      ])].filter(Boolean);

      if (docNames.length === 0) {
        docNames.push('Program Signature Form', 'Customer Price Sheet');
      }

      if (!activeExtractionDocName || !docNames.includes(activeExtractionDocName)) {
        activeExtractionDocName = docNames[0];
      }

      sel.innerHTML = '';
      docNames.forEach(name => {
        const opt = document.createElement('option');
        opt.value = name;
        opt.textContent = name;
        sel.appendChild(opt);
      });
      sel.value = activeExtractionDocName;
      renderActiveDocExtractionSchema();
    }

    function onSelectedExtractionDocChanged() {
      syncActiveDocProfileFromDom();
      activeExtractionDocName = document.getElementById('dataExtractionDocSelect').value;
      renderActiveDocExtractionSchema();
    }

    function renderActiveDocExtractionSchema() {
      const profile = workingDataExtractionProfiles.find(p => p.documentName === activeExtractionDocName)
        || { documentName: activeExtractionDocName, fields: [], tables: [] };

      const fieldsList = document.getElementById('dataExtractionFieldsList');
      fieldsList.innerHTML = '';
      (profile.fields || []).forEach(f => appendDataFieldRow(f.fieldName || '', f.instruction || ''));

      const tablesList = document.getElementById('dataExtractionTablesList');
      tablesList.innerHTML = '';
      (profile.tables || []).forEach(t => appendDataTableBlock(t.tableName || '', t.instruction || '', t.columns || []));
    }

    function appendDataFieldRow(fieldName, instruction) {
      const container = document.getElementById('dataExtractionFieldsList');
      const div = document.createElement('div');
      div.className = 'rule-item';
      div.innerHTML = `
        <input type="text" class="clean-input" data-field="fieldName" placeholder="Field name (e.g. QuoteNumber)" value="${escapeHtml(fieldName)}" data-input="onDataExtractionSchemaEdited" />
        <input type="text" class="clean-input" data-field="instruction" placeholder="Instruction on how to extract this field..." value="${escapeHtml(instruction)}" data-input="onDataExtractionSchemaEdited" />
        <button class="btn-mini" type="button" data-click="removeExtractionField">✕</button>
      `;
      container.appendChild(div);
    }

    function addDataExtractionFieldRow() {
      appendDataFieldRow('', '');
      onDataExtractionSchemaEdited();
    }

    function appendDataTableBlock(tableName, instruction, columns) {
      const container = document.getElementById('dataExtractionTablesList');
      const box = document.createElement('div');
      box.className = 'table-schema-box';
      box.innerHTML = `
        <div class="rule-item">
          <input type="text" class="clean-input" data-field="tableName" placeholder="Table name (e.g. SubscriptionPricing)" value="${escapeHtml(tableName)}" data-input="onDataExtractionSchemaEdited" />
          <input type="text" class="clean-input" data-field="tableInstruction" placeholder="Table extraction instruction..." value="${escapeHtml(instruction)}" data-input="onDataExtractionSchemaEdited" />
          <button class="btn-mini" type="button" data-click="removeExtractionTable">Remove table</button>
        </div>
        <div style="padding-left:14px;display:flex;flex-direction:column;gap:6px;" class="cols-container"></div>
        <div style="padding-left:14px;">
          <button class="btn-mini" type="button" data-click="addExtractionColumn">+ Add column</button>
        </div>
      `;
      container.appendChild(box);
      const colsContainer = box.querySelector('.cols-container');
      (columns || []).forEach(c => appendColumnRow(colsContainer, c.columnName || '', c.instruction || ''));
    }

    function appendColumnRow(colsContainer, colName, colInstruction) {
      const row = document.createElement('div');
      row.className = 'rule-item col-row';
      row.innerHTML = `
        <input type="text" class="clean-input" data-field="colName" placeholder="Column name (e.g. PartNumber)" value="${escapeHtml(colName)}" data-input="onDataExtractionSchemaEdited" />
        <input type="text" class="clean-input" data-field="colInstruction" placeholder="Column extraction instruction..." value="${escapeHtml(colInstruction)}" data-input="onDataExtractionSchemaEdited" />
        <button class="btn-mini" type="button" data-click="removeExtractionColumn">✕</button>
      `;
      colsContainer.appendChild(row);
    }

    function addColumnToTableBox(box) {
      const colsContainer = box.querySelector('.cols-container');
      appendColumnRow(colsContainer, '', '');
      onDataExtractionSchemaEdited();
    }

    function addDataExtractionTableBlock() {
      appendDataTableBlock('', '', [{ columnName: '', instruction: '' }]);
      onDataExtractionSchemaEdited();
    }

    function onDataExtractionSchemaEdited() {
      syncActiveDocProfileFromDom();
      onHarnessChanged();
    }

    function buildPayloadFromUi() {
      if (!currentTenant) return null;

      const updated = JSON.parse(JSON.stringify(currentTenant));
      const decisionModel = document.getElementById('harnessDecisionModelSelect').value || 'decision-1';
      const languageModel = document.getElementById('harnessLanguageModelSelect').value || 'phi-4';

      updated.decisionModelId = decisionModel;
      updated.defaultModelId = languageModel;
      updated.ocr = { ...(updated.ocr || {}), mode: document.getElementById('ocrModeSelect').value };
      updated.reviewPolicy = { enabled: document.getElementById('reviewEnabled').checked,
        requiresDualReview: document.getElementById('reviewDual').checked,
        dueHours: Number(document.getElementById('reviewDueHours').value) };
      updated.capabilities = updated.capabilities || {};

      // 1. SplitAndGroup
      updated.capabilities.SplitAndGroup = updated.capabilities.SplitAndGroup || { splitAndGroup: {} };
      updated.capabilities.SplitAndGroup.enabled = document.getElementById('splitFeatureToggle').checked;
      updated.capabilities.SplitAndGroup.splitAndGroup = updated.capabilities.SplitAndGroup.splitAndGroup || {};
      updated.capabilities.SplitAndGroup.splitAndGroup.footersToIgnore = document.getElementById('footersToIgnoreInput').value
        .split(',').map(s => s.trim()).filter(Boolean);
      updated.capabilities.SplitAndGroup.splitAndGroup.amendmentIndicators = document.getElementById('amendmentIndicatorsInput').value
        .split(',').map(s => s.trim()).filter(Boolean);

      // 2. IdentifyDocuments
      const idRules = [];
      document.querySelectorAll('#identificationRulesList .rule-item').forEach(row => {
        const docName = row.querySelector('[data-field="docName"]').value.trim();
        const prompt = row.querySelector('[data-field="prompt"]').value.trim();
        if (docName && prompt) {
          idRules.push({ documentName: docName, identificationPrompt: prompt, isActive: true });
        }
      });
      updated.capabilities.IdentifyDocuments = {
        enabled: document.getElementById('identifyFeatureToggle').checked,
        modelIdOverride: decisionModel,
        identifyDocuments: {
          decisionModelId: decisionModel,
          rules: idRules
        }
      };

      // 3. DocumentTranslation
      updated.capabilities.DocumentTranslation = {
        enabled: document.getElementById('translationFeatureToggle').checked,
        documentTranslation: {
          targetLanguage: document.getElementById('translationTargetLang').value || 'en',
          doNotTranslateTerms: document.getElementById('doNotTranslateInput').value
            .split(',')
            .map(s => s.trim())
            .filter(Boolean)
        }
      };

      // 4. DocumentInsight
      const insightPrompt = document.getElementById('insightInstructionsInput').value.trim();
      updated.capabilities.DocumentInsight = {
        enabled: document.getElementById('insightFeatureToggle').checked,
        modelIdOverride: languageModel,
        documentInsight: {
          summaryInstructions: insightPrompt
        }
      };

      // 5. EntityExtraction
      const entityPrompt = document.getElementById('entityPromptInput').value.trim();
      updated.capabilities.EntityExtraction = {
        enabled: document.getElementById('entityFeatureToggle').checked,
        modelIdOverride: languageModel,
        entityExtraction: {
          extractionPrompt: entityPrompt
        }
      };

      // 6. DataExtraction
      syncActiveDocProfileFromDom();
      updated.capabilities.DataExtraction = {
        enabled: document.getElementById('dataExtractionFeatureToggle').checked,
        modelIdOverride: languageModel,
        dataExtraction: {
          profiles: JSON.parse(JSON.stringify(workingDataExtractionProfiles))
        }
      };

      // 7. PromptColumns
      const promptColsToggle = document.getElementById('promptColumnsFeatureToggle');
      updated.capabilities.PromptColumns = {
        enabled: promptColsToggle ? promptColsToggle.checked : true,
        modelIdOverride: languageModel,
        promptColumns: {
          columns: JSON.parse(JSON.stringify(workingPromptColumns))
        }
      };

      // 8. DocumentGraph
      const docGraphToggle = document.getElementById('documentGraphFeatureToggle');
      updated.capabilities.DocumentGraph = {
        enabled: docGraphToggle ? docGraphToggle.checked : true
      };

      // 9. AskAi
      const askAiToggle = document.getElementById('askAiFeatureToggle');
      updated.capabilities.AskAi = {
        enabled: askAiToggle ? askAiToggle.checked : true,
        modelIdOverride: languageModel
      };

      return updated;
    }

    function updateDrawersAndCount() {
      const splitOn = document.getElementById('splitFeatureToggle').checked;
      const idOn = document.getElementById('identifyFeatureToggle').checked;
      const transOn = document.getElementById('translationFeatureToggle').checked;
      const insightOn = document.getElementById('insightFeatureToggle').checked;
      const entityOn = document.getElementById('entityFeatureToggle').checked;
      const dataExtOn = document.getElementById('dataExtractionFeatureToggle').checked;
      const promptColsToggle = document.getElementById('promptColumnsFeatureToggle');
      const promptColsOn = promptColsToggle ? promptColsToggle.checked : true;
      const docGraphToggle = document.getElementById('documentGraphFeatureToggle');
      const docGraphOn = docGraphToggle ? docGraphToggle.checked : true;
      const askAiToggle = document.getElementById('askAiFeatureToggle');
      const askAiOn = askAiToggle ? askAiToggle.checked : true;

      renderExpandedState();

      const enabledCount = [splitOn, idOn, transOn, insightOn, entityOn, dataExtOn, promptColsOn, docGraphOn, askAiOn].filter(Boolean).length;
      document.getElementById('featureStatusCount').textContent = `${enabledCount} of 9 enabled`;
    }

    function updateDirtyState() {
      const currentPayload = buildPayloadFromUi();
      const currentJson = currentPayload
        ? JSON.stringify({
            decisionModelId: currentPayload.decisionModelId,
            defaultModelId: currentPayload.defaultModelId,
            ocr: currentPayload.ocr,
            reviewPolicy: currentPayload.reviewPolicy,
            capabilities: currentPayload.capabilities
          })
        : '';
      const dirty = currentTenant ? (currentJson !== savedSnapshotJson) : false;

      document.getElementById('btnCancelHarness').disabled = !dirty;
      document.getElementById('btnSaveHarness').disabled = !dirty || harnessSaveInFlight || harnessSaveBlocked;
      document.getElementById('unsavedBadge').classList.toggle('visible', dirty);
    }

    function renderIdentificationRules(rules) {
      const container = document.getElementById('identificationRulesList');
      container.innerHTML = '';
      (rules || []).forEach(r => appendRuleRow(r.documentName || '', r.identificationPrompt || ''));
    }

    function appendRuleRow(docName, prompt) {
      const container = document.getElementById('identificationRulesList');
      const div = document.createElement('div');
      div.className = 'rule-item';
      div.innerHTML = `
        <input type="text" class="clean-input" data-field="docName" placeholder="Document name (e.g. Customer Price Sheet)" value="${escapeHtml(docName)}" data-input="onHarnessChanged" />
        <input type="text" class="clean-input" data-field="prompt" placeholder="If footer contains..., or title contains..." value="${escapeHtml(prompt)}" data-input="onHarnessChanged" />
        <button class="btn-mini" type="button" title="Remove rule" data-click="removeIdentificationRule">✕</button>
      `;
      container.appendChild(div);
    }

    function addIdentificationRuleRow() {
      appendRuleRow('', '');
      onHarnessChanged();
    }

    function renderMainCanvas() {
      if (!currentTenant) return;
      harnessSaveBlocked = false;
      document.getElementById('harnessSaveNotice').hidden = true;
      document.getElementById('harnessDraft').textContent = '';
      document.getElementById('harnessServer').textContent = '';
      ocrBudgetRevision++;
      ocrBudgetSnapshot = null;
      ocrBudgetTenantId = null;
      document.getElementById('ocrBudgetSave').disabled = true;
      document.getElementById('ocrPageLimit').disabled = true;
      document.getElementById('ocrWarningPercent').disabled = true;
      document.getElementById('ocrUsageStatus').textContent = 'Load the current monthly allowance and reserved request pages.';

      document.getElementById('harnessVersionChip').textContent = `Harness v${currentTenant.harnessVersion || 1}`;
      document.getElementById('tenantHeading').textContent = `${currentTenant.displayName}`;
      document.getElementById('tenantIdDisplay').textContent = `Tenant ID: ${currentTenant.tenantId}`;

      document.getElementById('harnessDecisionModelSelect').value = currentTenant.decisionModelId || 'decision-1';
      document.getElementById('harnessLanguageModelSelect').value =
        (currentTenant.defaultModelId && currentTenant.defaultModelId !== 'decision-1')
          ? currentTenant.defaultModelId
          : 'phi-4';

      const caps = currentTenant.capabilities || {};

      // 1. Split
      const splitCap = caps.SplitAndGroup || { enabled: true, splitAndGroup: {} };
      const splitCfg = splitCap.splitAndGroup || {};
      document.getElementById('ocrModeSelect').value = currentTenant.ocr?.mode || 'Auto';
      document.getElementById('reviewEnabled').checked = currentTenant.reviewPolicy?.enabled !== false;
      document.getElementById('reviewDual').checked = currentTenant.reviewPolicy?.requiresDualReview === true;
      document.getElementById('reviewDueHours').value = currentTenant.reviewPolicy?.dueHours || 24;
      document.getElementById('splitFeatureToggle').checked = splitCap.enabled !== false;
      document.getElementById('footersToIgnoreInput').value = (splitCfg.footersToIgnore || ['Confidential', 'Microsoft Confidential', 'Page']).join(', ');
      document.getElementById('amendmentIndicatorsInput').value = (splitCfg.amendmentIndicators || ['amendment', 'avenant', 'enmienda', 'nachtrag', 'ctm']).join(', ');

      // 2. Identify Documents
      const idCap = caps.IdentifyDocuments || { enabled: true, identifyDocuments: {} };
      const idCfg = idCap.identifyDocuments || {};
      document.getElementById('identifyFeatureToggle').checked = idCap.enabled !== false;
      renderIdentificationRules(idCfg.rules || []);

      // 3. Document Translation
      const transCap = caps.DocumentTranslation || { enabled: true, documentTranslation: {} };
      const transCfg = transCap.documentTranslation || {};
      document.getElementById('translationFeatureToggle').checked = transCap.enabled !== false;
      document.getElementById('translationTargetLang').value = transCfg.targetLanguage || 'en';
      document.getElementById('doNotTranslateInput').value = (transCfg.doNotTranslateTerms || ['Microsoft', 'MBSA', 'OXXO']).join(', ');

      // 4. Document Insight
      const inCap = caps.DocumentInsight || { enabled: true, documentInsight: {} };
      const inCfg = inCap.documentInsight || {};
      document.getElementById('insightFeatureToggle').checked = inCap.enabled !== false;
      document.getElementById('insightInstructionsInput').value =
        inCfg.summaryInstructions || 'Summarize the document concisely, highlighting the customer entity, agreement/quote identifiers, key commercial terms, and total financial value.';

      // 5. Entity Extraction
      const exCap = caps.EntityExtraction || { enabled: true, entityExtraction: {} };
      const exCfg = exCap.entityExtraction || {};
      document.getElementById('entityFeatureToggle').checked = exCap.enabled !== false;
      document.getElementById('entityPromptInput').value =
        exCfg.extractionPrompt || 'Extract Customer Name, MBA/MBSA Number, Contract/Agreement Number, Quote Number, Billing Currency, Tenant ID, and Total Deal Value.';

      // 6. Data Extraction
      const deCap = caps.DataExtraction || { enabled: true, dataExtraction: {} };
      const deCfg = deCap.dataExtraction || {};
      document.getElementById('dataExtractionFeatureToggle').checked = deCap.enabled !== false;
      workingDataExtractionProfiles = JSON.parse(JSON.stringify(deCfg.profiles || []));
      activeExtractionDocName = '';
      const definedDocNames = (idCfg.rules || []).map(r => r.documentName).filter(Boolean);
      renderDataExtractionDocDropdown(definedDocNames);

      // 7. Prompt Columns
      const pcCap = caps.PromptColumns || { enabled: true, promptColumns: { columns: [] } };
      const pcCfg = pcCap.promptColumns || { columns: [] };
      const pcToggle = document.getElementById('promptColumnsFeatureToggle');
      if (pcToggle) pcToggle.checked = pcCap.enabled !== false;
      workingPromptColumns = JSON.parse(JSON.stringify(pcCfg.columns || []));
      refreshKnownEntitiesFromTenant();
      renderSavedPromptsList();
      clearPromptColumnForm();

      // 8. Document Graph & 9. Ask AI
      const dgCap = caps.DocumentGraph || { enabled: true };
      const dgToggle = document.getElementById('documentGraphFeatureToggle');
      if (dgToggle) dgToggle.checked = dgCap.enabled !== false;

      const askCap = caps.AskAi || { enabled: true };
      const askToggle = document.getElementById('askAiFeatureToggle');
      if (askToggle) askToggle.checked = askCap.enabled !== false;

      updateDrawersAndCount();
      const initialPayload = buildPayloadFromUi();
      savedSnapshotJson = initialPayload
        ? JSON.stringify({
            decisionModelId: initialPayload.decisionModelId,
            defaultModelId: initialPayload.defaultModelId,
            ocr: initialPayload.ocr,
            reviewPolicy: initialPayload.reviewPolicy,
            capabilities: initialPayload.capabilities
          })
        : '';
      updateDirtyState();
    }

    function onHarnessChanged() {
      updateDrawersAndCount();
      updateDirtyState();
    }

    function cancelTenantChanges() {
      renderMainCanvas();
      showToast('Reverted unsaved changes');
    }

    async function refreshKnownEntitiesFromTenant() {
      const defaults = [
        'Contract Number',
        'MBA/MBSA Number',
        'Customer Name',
        'Quote Number',
        'Billing Currency',
        'Total Deal Value',
        'Tenant ID',
        'Document Heading',
        'Document Name'
      ];
      const promptVal = (document.getElementById('entityPromptInput')?.value || '')
        .replace(/^Extract\s+/i, '')
        .replace(/\.$/, '');
      const parsedFromPrompt = promptVal
        .split(/,|and\s+/i)
        .map(s => s.trim())
        .filter(s => s.length >= 2 && s.length <= 40);

      const combined = new Set([...defaults, ...parsedFromPrompt]);

      if (currentTenant) {
        try {
          const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(currentTenant.tenantId)}/jobs`);
          if (resp.ok) {
            const jobs = await resp.json();
            (jobs || []).forEach(j => {
              (j.extractedEntities || []).forEach(e => {
                if (e && e.name) combined.add(e.name);
              });
            });
          }
        } catch {}
      }

      knownEntityNames = [...combined];
      const dl = document.getElementById('existingEntitiesDatalist');
      if (dl) {
        dl.innerHTML = knownEntityNames.map(n => `<option value="${escapeHtml(n)}"></option>`).join('');
      }
    }

    function applyQuickPromptTemplate(kind) {
      const nameInput = document.getElementById('promptColumnNameInput');
      const instrInput = document.getElementById('promptInstructionsInput');
      const filterSelect = document.getElementById('promptFilterModeSelect');
      const rulesList = document.getElementById('promptFilterRulesList');

      if (kind === 'summary') {
        nameInput.value = 'Summary';
        instrInput.value = 'Summarize the contents of {{Document.Content}} in a clear and concise manner. Focus on key points, main arguments, and important conclusions.';
        filterSelect.value = 'All';
        rulesList.innerHTML = '';
        onPromptFilterModeChanged();
      } else if (kind === 'highlights') {
        nameInput.value = 'Highlights';
        instrInput.value = 'Extract the most important commercial, financial, and operational highlights from {{Document.Content}} as concise bullet points.';
        filterSelect.value = 'All';
        rulesList.innerHTML = '';
        onPromptFilterModeChanged();
      } else if (kind === 'language') {
        nameInput.value = 'Language';
        instrInput.value = 'Identify the primary source language and tone of {{Document.Content}}, noting any bilingual or translated clauses.';
        filterSelect.value = 'All';
        rulesList.innerHTML = '';
        onPromptFilterModeChanged();
      } else if (kind === 'contract') {
        nameInput.value = 'Contract Check';
        instrInput.value = 'Verify contract identifiers, MBA/MBSA reference numbers, legal customer entity names, and formalization clauses in {{Document.Content}}.';
        filterSelect.value = 'Or';
        rulesList.innerHTML = '';
        onPromptFilterModeChanged();
        addPromptEntityFilterRow('Contract Number', 'Exists', '');
        addPromptEntityFilterRow('MBA/MBSA Number', 'Exists', '');
      }
    }

    function onPromptFilterModeChanged() {
      const mode = document.getElementById('promptFilterModeSelect').value;
      const container = document.getElementById('promptFilterRulesContainer');
      const rulesList = document.getElementById('promptFilterRulesList');
      if (mode === 'All') {
        container.style.display = 'none';
      } else {
        container.style.display = 'flex';
        if (rulesList.children.length === 0) {
          addPromptEntityFilterRow('Contract Number', 'Exists', '');
        }
      }
    }

    function addPromptEntityFilterRow(entityName = 'Contract Number', operator = 'Exists', expectedValue = '') {
      const rulesList = document.getElementById('promptFilterRulesList');
      const row = document.createElement('div');
      row.className = 'entity-filter-rule-row';
      const showVal = operator !== 'Exists';
      row.innerHTML = `
        <input type="text" list="existingEntitiesDatalist" class="clean-input" data-filter="entityName" placeholder="Select or type Entity Name" value="${escapeHtml(entityName)}" />
        <select class="clean-select" data-filter="operator" data-change="setFilterVisibility">
          <option value="Exists" ${operator === 'Exists' ? 'selected' : ''}>Exists</option>
          <option value="Contains" ${operator === 'Contains' ? 'selected' : ''}>Contains</option>
          <option value="Equals" ${operator === 'Equals' ? 'selected' : ''}>Equals</option>
          <option value="StartsWith" ${operator === 'StartsWith' ? 'selected' : ''}>Starts With</option>
          <option value="NotEquals" ${operator === 'NotEquals' ? 'selected' : ''}>Not Equals</option>
        </select>
        <input type="text" class="clean-input" data-filter="expectedValue" placeholder="Expected value..." value="${escapeHtml(expectedValue || '')}" style="visibility:${showVal ? 'visible' : 'hidden'}" />
        <button type="button" class="btn-mini" title="Remove filter condition" data-click="removePromptFilter">✕</button>
      `;
      rulesList.appendChild(row);
    }

    function clearPromptColumnForm() {
      document.getElementById('editingPromptColumnId').value = '';
      document.getElementById('promptFormHeading').textContent = 'Create New Prompt Column';
      document.getElementById('btnSavePromptLabel').textContent = 'Save Prompt';
      document.getElementById('promptColumnNameInput').value = '';
      document.getElementById('promptInstructionsInput').value = 'Summarize the contents of {{Document.Content}} in a clear and concise manner. Focus on key points, main arguments, and important conclusions.';
      document.getElementById('promptFilterModeSelect').value = 'All';
      document.getElementById('promptFilterRulesList').innerHTML = '';
      onPromptFilterModeChanged();
    }

    function formatPromptFilterBadgeText(col) {
      const mode = col.filterMode || 'All';
      const rules = col.entityFilters || [];
      if (mode === 'All' || rules.length === 0) {
        return 'Filter: All Documents';
      }
      const joiner = mode === 'Or' ? ' OR ' : ' AND ';
      const desc = rules.map(r => r.operator === 'Exists' ? `${r.entityName} Exists` : `${r.entityName} ${r.operator} "${r.expectedValue || ''}"`).join(joiner);
      return `Entity Filter: ${desc}`;
    }

    function renderSavedPromptsList() {
      const list = document.getElementById('savedPromptsList');
      const countSub = document.getElementById('savedPromptsCountSub');
      if (!list) return;

      countSub.textContent = `${workingPromptColumns.length} prompt${workingPromptColumns.length === 1 ? '' : 's'} configured`;
      list.innerHTML = '';

      if (workingPromptColumns.length === 0) {
        list.innerHTML = `
          <div style="padding:36px 18px;text-align:center;color:var(--text-secondary);font-size:12.5px;">
            No saved prompts yet.<br/>Create your first prompt column on the left to start analyzing documents as PDF/Base64.
          </div>
        `;
        return;
      }

      workingPromptColumns.forEach((col, idx) => {
        const card = document.createElement('div');
        card.className = 'saved-prompt-item';
        const filterText = formatPromptFilterBadgeText(col);
        card.innerHTML = `
          <div class="saved-prompt-top">
            <div class="saved-prompt-name">
              <span>✦ ${escapeHtml(col.columnName)}</span>
            </div>
            <div style="display:flex;align-items:center;gap:6px;">
              <button type="button" class="btn-mini" data-click="editPromptColumn_arg" data-args="[${idx}]">Edit</button>
              <button type="button" class="btn-mini" data-click="deletePromptColumn_arg" data-args="[${idx}]">✕</button>
            </div>
          </div>
          <div class="prompt-filter-badge">${escapeHtml(filterText)}</div>
          <div class="saved-prompt-instr">${escapeHtml(col.promptInstructions)}</div>
        `;
        list.appendChild(card);
      });
    }

    function editPromptColumn(idx) {
      const col = workingPromptColumns[idx];
      if (!col) return;
      document.getElementById('editingPromptColumnId').value = col.columnId || `col-${idx}`;
      document.getElementById('promptFormHeading').textContent = `Edit Prompt Column: ${col.columnName}`;
      document.getElementById('btnSavePromptLabel').textContent = 'Update Prompt';
      document.getElementById('promptColumnNameInput').value = col.columnName || '';
      document.getElementById('promptInstructionsInput').value = col.promptInstructions || '';
      document.getElementById('promptFilterModeSelect').value = col.filterMode || 'All';
      const rulesList = document.getElementById('promptFilterRulesList');
      rulesList.innerHTML = '';
      (col.entityFilters || []).forEach(r => addPromptEntityFilterRow(r.entityName || '', r.operator || 'Exists', r.expectedValue || ''));
      onPromptFilterModeChanged();
    }

    async function deletePromptColumn(idx) {
      workingPromptColumns.splice(idx, 1);
      renderSavedPromptsList();
      onHarnessChanged();
      await saveTenantHarness();
    }

    async function savePromptColumnFromForm() {
      const name = document.getElementById('promptColumnNameInput').value.trim();
      const instructions = document.getElementById('promptInstructionsInput').value.trim();
      if (!name || !instructions) {
        showToast('Please enter both Column Name and Prompt Instructions');
        return;
      }

      const filterMode = document.getElementById('promptFilterModeSelect').value || 'All';
      const entityFilters = [];
      if (filterMode !== 'All') {
        document.querySelectorAll('#promptFilterRulesList .entity-filter-rule-row').forEach(r => {
          const eName = r.querySelector('[data-filter="entityName"]').value.trim();
          const op = r.querySelector('[data-filter="operator"]').value;
          const val = r.querySelector('[data-filter="expectedValue"]').value.trim();
          if (eName) {
            entityFilters.push({ entityName: eName, operator: op, expectedValue: op === 'Exists' ? null : val });
          }
        });
      }

      const editingId = document.getElementById('editingPromptColumnId').value;
      if (editingId) {
        const existingIdx = workingPromptColumns.findIndex(c => c.columnId === editingId);
        if (existingIdx >= 0) {
          workingPromptColumns[existingIdx] = {
            columnId: editingId,
            columnName: name,
            promptInstructions: instructions,
            filterMode,
            entityFilters,
            isActive: true
          };
        }
      } else {
        workingPromptColumns.push({
          columnId: 'col-' + Math.random().toString(36).substring(2, 8),
          columnName: name,
          promptInstructions: instructions,
          filterMode,
          entityFilters,
          isActive: true
        });
      }

      renderSavedPromptsList();
      clearPromptColumnForm();
      onHarnessChanged();
      await saveTenantHarness();
    }

    async function saveTenantHarness() {
      if (!currentTenant || harnessSaveInFlight || harnessSaveBlocked) return;
      const startedTenant = currentTenant;
      const updated = buildPayloadFromUi();
      harnessSaveInFlight = true;
      updateDirtyState();
      try {
        const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(updated.tenantId)}`, {
          method: 'PUT',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(updated)
        });
        if (currentTenant !== startedTenant) return;
        if (!resp.ok) {
          const conflict = resp.status === 409 ? await resp.json() : null;
          if (currentTenant !== startedTenant) return;
          showHarnessSaveFailure(conflict?.current, resp.status === 409
            ? 'Another administrator saved this harness. Your draft is preserved. Compare the configurations, copy any changes you need, then reload.'
            : 'The save was not confirmed. Your draft is preserved. Reload the server configuration before saving again.');
          return;
        }
        const saved = await resp.json();
        if (currentTenant !== startedTenant) return;
        const editedDuringSave = JSON.stringify(buildPayloadFromUi()) !== JSON.stringify(updated);
        currentTenant = saved;
        tenants = tenants.map(tenant => tenant.tenantId === saved.tenantId ? saved : tenant);
        renderSidebarTenants();
        if (editedDuringSave) {
          savedSnapshotJson = JSON.stringify({
            decisionModelId: updated.decisionModelId, defaultModelId: updated.defaultModelId,
            ocr: updated.ocr, reviewPolicy: updated.reviewPolicy, capabilities: updated.capabilities
          });
          document.getElementById('harnessVersionChip').textContent = `Harness v${saved.harnessVersion}`;
        } else {
          renderMainCanvas();
        }
        showToast(`Saved ${saved.displayName} (v${saved.harnessVersion})`);
      } catch {
        if (currentTenant === startedTenant) showHarnessSaveFailure(null,
          'The save outcome is unknown. Your draft is preserved. Reload the server configuration; do not repeat the save blindly.');
      } finally {
        harnessSaveInFlight = false;
        updateDirtyState();
      }
    }

    function showHarnessSaveFailure(server, message) {
      harnessSaveBlocked = true;
      document.getElementById('harnessSaveNotice').hidden = false;
      document.getElementById('harnessSaveMessage').textContent = message;
      document.getElementById('harnessDraft').textContent = JSON.stringify(buildPayloadFromUi(), null, 2);
      document.getElementById('harnessServer').textContent = server
        ? JSON.stringify(server, null, 2) : 'Reload to retrieve the current server configuration.';
    }

    async function reloadHarnessAfterConflict() {
      if (!currentTenant || harnessSaveInFlight ||
          !window.confirm('Discard the unsaved draft and load the latest server configuration? Copy any needed changes first.')) return;
      const selected = currentTenant;
      try {
        const response = await fetch(`/api/harness/tenants/${encodeURIComponent(selected.tenantId)}`);
        if (!response.ok) throw new Error('Reload failed');
        const server = await response.json();
        if (currentTenant !== selected) return;
        currentTenant = server;
        tenants = tenants.map(tenant => tenant.tenantId === server.tenantId ? server : tenant);
        renderSidebarTenants();
        renderMainCanvas();
      } catch {
        if (currentTenant === selected) showHarnessSaveFailure(null, 'Reload failed. Your draft is still preserved.');
      }
    }

    function toggleNewTenantPopover(show) {
      const pop = document.getElementById('newTenantPopover');
      pop.style.display = show ? 'flex' : 'none';
      if (show) {
        document.getElementById('newTenantName').value = '';
        document.getElementById('newTenantId').value = '';
        document.getElementById('newTenantName').focus();
      }
    }

    document.getElementById('newTenantName').addEventListener('input', (e) => {
      const slug = e.target.value
        .toLowerCase()
        .replace(/[^a-z0-9]+/g, '-')
        .replace(/^-|-$/g, '');
      document.getElementById('newTenantId').value = slug;
    });

    async function createTenant() {
      const displayName = document.getElementById('newTenantName').value.trim();
      const tenantId = document.getElementById('newTenantId').value.trim();
      if (!displayName || !tenantId) return;

      const payload = {
        tenantId,
        displayName,
        harnessVersion: 0,
        isActive: true,
        defaultModelId: 'gpt-5-mini',
        allowedModels: ['gpt-5-mini', 'gpt-4o', 'o3-mini'],
        capabilities: {
          SplitAndGroup: {
            enabled: true,
            splitAndGroup: {
              strategy: 'Hybrid',
              enableFooterNormalization: true,
              footerNormalizationThreshold: 0.85,
              footerBoundarySimilarityThreshold: 0.90,
              inferNameWhenNotInCatalog: true,
              produceSplitPdfBytes: true
            }
          }
        }
      };

      const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(tenantId)}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(payload)
      });

      if (resp.ok) {
        toggleNewTenantPopover(false);
        await loadTenants(tenantId);
        showToast(`Created ${displayName}`);
      } else {
        showToast(resp.status === 409 ? 'This tenant already exists. Choose another ID or open the existing tenant.'
          : 'Tenant creation was not confirmed. Check the tenant list before retrying.');
      }
    }




    // =========================================================================
    // Document Intelligence Results Workspace (3-Pane View by Tenant)
    // =========================================================================
    let activeWorkspaceView = 'documents'; // 'documents' | 'harness'
    let tenantJobs = [];
    let selectedJobId = null;
    let selectedSliceIndex = -1; // -1 = All Slices
    let docResultTab = 'insights'; // 'insights' | 'document' | 'entities' | 'structured' | 'metadata'
    let docStageViewMode = 'split'; // 'pdf' | 'split' | 'text'
    let selectedEntityCategory = 'ALL';
    let jobPollTimer = null;

    function toggleDocListPane() {
      const pane = document.getElementById('docListPane');
      const expandHeaderBtn = document.getElementById('btnHeaderExpandDocList');
      if (!pane) return;
      const isCollapsed = pane.classList.toggle('collapsed');
      if (expandHeaderBtn) {
        expandHeaderBtn.style.display = isCollapsed ? 'inline-flex' : 'none';
      }
    }

    function jumpToSlicePdf(sliceIndex) {
      selectedSliceIndex = sliceIndex;
      setDocResultTab('document');
    }


    function setEntityCategoryFilter(cat) {
      selectedEntityCategory = cat;
      renderSelectedDocument();
    }

    function exportSelectedJobJson() {
      const job = tenantJobs.find(j => j.jobId === selectedJobId);
      if (!job) return;
      const blob = new Blob([JSON.stringify(job, null, 2)], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `${(job.fileName || 'document').replace(/\.pdf$/i, '')}-docintel-result.json`;
      a.click();
      URL.revokeObjectURL(url);
      showToast('Exported JSON result');
    }

    function switchWorkspaceView(view) {
      activeWorkspaceView = view;
      document.getElementById('docIntelWorkspace').classList.toggle('active-view', view === 'documents');
      document.getElementById('agentHarnessCanvas').classList.toggle('hidden-view', view === 'documents');
      document.getElementById('navDocumentIntel').classList.toggle('active-nav', view === 'documents');
      document.getElementById('navAgentHarness').classList.toggle('active-nav', view === 'harness');
      if (view === 'documents') {
        loadTenantJobs();
      }
    }

    async function loadTenantJobs(preferredJobId) {
      if (!currentTenant) return;
      document.getElementById('docListTenantBadge').textContent = currentTenant.displayName;
      document.getElementById('docListTenantBadge').title = currentTenant.displayName;

      const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(currentTenant.tenantId)}/jobs`);
      if (!resp.ok) return;
      tenantJobs = await resp.json();

      if (preferredJobId) {
        selectedJobId = preferredJobId;
      } else if (!selectedJobId || !tenantJobs.some(j => j.jobId === selectedJobId)) {
        selectedJobId = tenantJobs.length > 0 ? tenantJobs[0].jobId : null;
        selectedSliceIndex = -1;
      }

      renderDocumentList();
      renderSelectedDocument();
      scheduleJobPollingIfNeeded();
    }

    function scheduleJobPollingIfNeeded() {
      if (jobPollTimer) {
        clearTimeout(jobPollTimer);
        jobPollTimer = null;
      }
      if (tenantJobs.some(j => j.status === 'Queued' || j.status === 'Running')) {
        jobPollTimer = setTimeout(() => loadTenantJobs(selectedJobId), 2000);
      }
    }

    function renderDocumentList() {
      const container = document.getElementById('docItemsList');
      const query = (document.getElementById('docSearchInput').value || '').trim().toLowerCase();

      const filtered = tenantJobs.filter(j => {
        if (!query) return true;
        const hay = `${j.fileName} ${j.jobId} ${(j.segments || []).map(s => s.identifiedDocumentName || s.documentTypeName || '').join(' ')}`.toLowerCase();
        return hay.includes(query);
      });

      document.getElementById('docListCountLabel').textContent = `${filtered.length} of ${tenantJobs.length}`;
      container.innerHTML = '';

      if (filtered.length === 0) {
        container.innerHTML = `
          <div style="padding:28px 14px;text-align:center;color:var(--text-secondary);font-size:12.5px;line-height:1.5;">
            No documents processed for <strong>${escapeHtml(currentTenant ? currentTenant.displayName : 'this tenant')}</strong> yet.<br/><br/>
            Click <strong>Upload File</strong> below to process a document.
          </div>
        `;
        return;
      }

      filtered.forEach(job => {
        const isSelected = job.jobId === selectedJobId;
        const dt = job.createdAtUtc ? new Date(job.createdAtUtc) : new Date();
        const dateStr = dt.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
        const segs = job.segments || [];
        const langs = [...new Set(segs.map(s => s.detectedLanguage).filter(Boolean))];
        const langLabel = langs.length > 0
          ? langs.map(l => l === 'es' ? 'Spanish' : l === 'en' ? 'English' : l.toUpperCase()).join(', ')
          : 'English';

        const statusCls = job.status === 'Completed' ? '' : (job.status === 'Failed' ? 'failed' : 'running');

        const card = document.createElement('div');
        card.className = 'doc-card-item' + (isSelected ? ' selected' : '');
        card.title = `${job.fileName} (${segs.length} slices · ${job.status})`;
        card.onclick = () => {
          selectedJobId = job.jobId;
          selectedSliceIndex = -1;
          renderDocumentList();
          renderSelectedDocument();
        };

        card.innerHTML = `
          <div class="doc-card-icon">
            <svg width="18" height="18" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.6">
              <path stroke-linecap="round" stroke-linejoin="round" d="M5.5 2.5h5.8l3.7 3.7v10.3a1.5 1.5 0 01-1.5 1.5h-8A1.5 1.5 0 014 16.5v-12A1.5 1.5 0 015.5 2.5z" />
              <path stroke-linecap="round" d="M7 11h6M7 14h4" />
            </svg>
          </div>
          <div class="doc-card-body">
            <div class="doc-card-name" title="${escapeHtml(job.fileName)}">${escapeHtml(job.fileName)}</div>
            <div class="doc-card-sub">
              <span>${escapeHtml(dateStr)}</span>
              <span>·</span>
              <span>${escapeHtml(langLabel)}</span>
              ${segs.length > 1 ? `<span>· ${segs.length} slices</span>` : ''}
            </div>
            <div style="margin-top:5px;">
              <span class="status-pill-mini ${statusCls}">${escapeHtml(job.status)}</span>
            </div>
          </div>
        `;
        container.appendChild(card);
      });
    }

    async function uploadAndRunTenantDocument(inputEl) {
      if (!currentTenant || !inputEl.files || !inputEl.files[0]) return;
      const file = inputEl.files[0];
      const lbl = document.getElementById('uploadBtnLabel');
      lbl.textContent = 'Uploading...';

      try {
        const fd = new FormData();
        fd.append('file', file);
        const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(currentTenant.tenantId)}/jobs`, {
          method: 'POST',
          body: fd
        });
        if (resp.ok) {
          const accepted = await resp.json();
          showToast(`Submitted ${file.name}`);
          await loadTenantJobs(accepted.jobId);
        } else {
          showToast('Upload failed');
        }
      } finally {
        lbl.textContent = 'Upload File';
        inputEl.value = '';
      }
    }

    async function submitSampleDocumentJob() {
      if (!currentTenant) return;
      const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(currentTenant.tenantId)}/jobs`, {
        method: 'POST'
      });
      if (resp.ok) {
        const accepted = await resp.json();
        showToast('Submitted sample 5-page contract packet');
        await loadTenantJobs(accepted.jobId);
      }
    }

    function setDocResultTab(tab) {
      docResultTab = tab;
      document.querySelectorAll('#insightsTabBar .result-tab-btn').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.tab === tab);
      });
      renderSelectedDocument();
    }

    function setDocStageViewMode(mode) {
      docStageViewMode = mode;
      renderSelectedDocument();
    }

    function selectSliceInViewer(idx) {
      selectedSliceIndex = idx;
      renderSelectedDocument();
    }

    function renderSelectedDocument() {
      const body = document.getElementById('docCenterBody');
      const sliceRow = document.getElementById('sliceSelectorRow');
      const job = tenantJobs.find(j => j.jobId === selectedJobId);

      if (!job) {
        document.getElementById('selectedDocTitle').textContent = 'No document selected';
        document.getElementById('selectedDocHarnessBadge').textContent = currentTenant ? `Harness v${currentTenant.harnessVersion || 1}` : 'Harness';
        document.getElementById('selectedDocStatusBadge').textContent = 'Idle';
        sliceRow.innerHTML = '';
        body.innerHTML = `
          <div class="result-card" style="text-align:center;padding:48px 24px;color:var(--text-secondary);">
            <div style="font-size:15px;font-weight:600;color:var(--text-primary);margin-bottom:6px;">No document selected</div>
            <div style="font-size:13px;">Upload a PDF using <strong>Upload File</strong> on the left to inspect results for this tenant.</div>
          </div>
        `;
        return;
      }

      document.getElementById('selectedDocTitle').textContent = `${job.fileName}`;
      document.getElementById('selectedDocHarnessBadge').textContent = `Harness v${job.appliedHarnessVersion || 1}`;
      const statusBadge = document.getElementById('selectedDocStatusBadge');
      statusBadge.textContent = `${job.status} · ${job.totalPages || 0} pages`;
      statusBadge.className = 'status-pill-mini ' + (job.status === 'Completed' ? '' : (job.status === 'Failed' ? 'failed' : 'running'));

      const segs = job.segments || [];
      const totalEntitiesCount = segs.reduce((acc, s) => acc + ((s.extractedEntities || []).length), 0);
      const totalStructuredCount = segs.reduce((acc, s) => {
        const de = s.dataExtraction;
        if (!de) return acc;
        return acc + ((de.fields || []).length) + ((de.tables || []).length);
      }, 0);

      // Update tab button labels with live count badges
      const tabButtons = document.querySelectorAll('#insightsTabBar .result-tab-btn');
      tabButtons.forEach(btn => {
        const t = btn.dataset.tab;
        if (t === 'insights') btn.innerHTML = `<span>✦ Insights</span>`;
        else if (t === 'document') btn.innerHTML = `<span>📄 View Document</span>`;
        else if (t === 'entities') btn.innerHTML = `<span>🏷 Entities</span>${totalEntitiesCount > 0 ? `<span class="tab-count-pill">${totalEntitiesCount}</span>` : ''}`;
        else if (t === 'structured') btn.innerHTML = `<span>📊 Structured Data</span>${totalStructuredCount > 0 ? `<span class="tab-count-pill">${totalStructuredCount}</span>` : ''}`;
        else if (t === 'metadata') btn.innerHTML = `<span>⚙ Metadata</span>`;
      });

      sliceRow.innerHTML = '';
      if (segs.length > 0) {
        const group = document.createElement('div');
        group.className = 'slice-switcher-group';

        const prevBtn = document.createElement('button');
        prevBtn.type = 'button';
        prevBtn.className = 'slice-step-btn';
        prevBtn.title = 'Previous slice';
        prevBtn.textContent = '◀';
        const effectiveIdx = selectedSliceIndex === -1 ? 0 : selectedSliceIndex;
        prevBtn.disabled = segs.length <= 1 || effectiveIdx <= 0;
        prevBtn.onclick = () => {
          selectedSliceIndex = Math.max(0, effectiveIdx - 1);
          renderSelectedDocument();
        };

        const select = document.createElement('select');
        select.className = 'slice-select-modern';
        if (docResultTab !== 'document') {
          const allOpt = document.createElement('option');
          allOpt.value = '-1';
          allOpt.textContent = `All Slices (${segs.length})`;
          if (selectedSliceIndex === -1) allOpt.selected = true;
          select.appendChild(allOpt);
        }

        const activePdfSliceIdx = (docResultTab === 'document' && selectedSliceIndex === -1) ? 0 : selectedSliceIndex;

        segs.forEach(s => {
          const opt = document.createElement('option');
          opt.value = String(s.segmentIndex);
          const labelName = s.identifiedDocumentName || s.documentTypeName || `Slice ${s.segmentIndex + 1}`;
          opt.textContent = `Slice ${s.segmentIndex + 1} of ${segs.length} · ${labelName} (p.${s.startPage}–${s.endPage})`;
          if (activePdfSliceIdx === s.segmentIndex) opt.selected = true;
          select.appendChild(opt);
        });

        select.onchange = (e) => {
          selectedSliceIndex = parseInt(e.target.value, 10);
          renderSelectedDocument();
        };

        const nextBtn = document.createElement('button');
        nextBtn.type = 'button';
        nextBtn.className = 'slice-step-btn';
        nextBtn.title = 'Next slice';
        nextBtn.textContent = '▶';
        nextBtn.disabled = segs.length <= 1 || effectiveIdx >= segs.length - 1;
        nextBtn.onclick = () => {
          selectedSliceIndex = Math.min(segs.length - 1, (selectedSliceIndex === -1 ? 0 : selectedSliceIndex) + 1);
          renderSelectedDocument();
        };

        group.appendChild(prevBtn);
        group.appendChild(select);
        group.appendChild(nextBtn);
        sliceRow.appendChild(group);

        if (docResultTab === 'document') {
          const focusedSeg = (selectedSliceIndex >= 0 ? segs.find(s => s.segmentIndex === selectedSliceIndex) : segs[0]) || segs[0];
          const pdfUrl = `/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/slices/${focusedSeg.segmentIndex}/pdf`;
          const openLink = document.createElement('a');
          openLink.href = pdfUrl;
          openLink.target = '_blank';
          openLink.className = 'btn-mini';
          openLink.style.cssText = 'text-decoration:none;font-size:12px;padding:6px 10px;border-radius:8px;';
          openLink.textContent = '↗ Open PDF';
          sliceRow.appendChild(openLink);
        }
      }

      if (job.status === 'Queued' || job.status === 'Running') {
        body.innerHTML = `
          <div class="result-card">
            <div class="result-card-header">
              <h3 class="result-card-title">Processing Document through Agent Harness...</h3>
              <span class="status-pill-mini running">${escapeHtml(job.status)}</span>
            </div>
            <div style="font-size:13px;color:var(--text-secondary);">
              Completed steps so far: ${(job.executionTrace || []).length}
            </div>
          </div>
        `;
        return;
      }

      if (job.status === 'Failed') {
        body.innerHTML = `
          <div class="result-card" style="border-color:#fca5a5;background:#fef2f2;">
            <h3 class="result-card-title" style="color:#991b1b;margin-bottom:8px;">Processing Failed</h3>
            <div style="font-size:12.5px;color:#7f1d1d;white-space:pre-wrap;">${escapeHtml(job.errorMessage || 'Unknown error')}</div>
          </div>
        `;
        return;
      }

      const activeSegs = selectedSliceIndex === -1
        ? segs
        : segs.filter(s => s.segmentIndex === selectedSliceIndex);

      if (docResultTab === 'document') {
        if (segs.length === 0) {
          body.innerHTML = `<div class="result-card">No PDF available.</div>`;
          return;
        }

        const focusedSeg = (selectedSliceIndex >= 0 ? segs.find(s => s.segmentIndex === selectedSliceIndex) : segs[0]) || segs[0];
        const pdfUrl = `/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/slices/${focusedSeg.segmentIndex}/pdf`;

        body.innerHTML = `
          <iframe src="${pdfUrl}" style="width:100%;height:calc(100vh - 185px);min-height:580px;border:1px solid var(--card-border);border-radius:14px;background:#f4f4f4;display:block;box-shadow:0 4px 16px rgba(0,0,0,0.04);" title="PDF Preview"></iframe>
        `;
        return;
      }

      if (docResultTab === 'insights') {
        const distinctTypes = [...new Set(segs.map(s => s.identifiedDocumentName || s.documentTypeName || 'Undefined'))];
        const distinctLangs = [...new Set(segs.map(s => (s.detectedLanguage || 'en').toUpperCase()))];
        const anyTranslated = segs.some(s => s.wasTranslated);

        const kpiStripHtml = `
          <div class="packet-kpi-strip">
            <div class="packet-kpi-card">
              <span class="packet-kpi-label">Packet Structure</span>
              <span class="packet-kpi-value">${segs.length} Slice${segs.length === 1 ? '' : 's'} · ${job.totalPages || 0} Pages</span>
              <span class="packet-kpi-sub">Deterministic layout &amp; footer split</span>
            </div>
            <div class="packet-kpi-card">
              <span class="packet-kpi-label">Identified Document Types</span>
              <span class="packet-kpi-value">${distinctTypes.filter(t => t !== 'Undefined').length} Matched / ${distinctTypes.length} Total</span>
              <span class="packet-kpi-sub" title="${escapeHtml(distinctTypes.join(', '))}">${escapeHtml(distinctTypes.join(', '))}</span>
            </div>
            <div class="packet-kpi-card">
              <span class="packet-kpi-label">Language &amp; Translation</span>
              <span class="packet-kpi-value">${escapeHtml(distinctLangs.join(', '))}${anyTranslated ? ' → EN' : ''}</span>
              <span class="packet-kpi-sub">${anyTranslated ? 'Translated via Azure AI Translator' : 'Native source language'}</span>
            </div>
            <div class="packet-kpi-card">
              <span class="packet-kpi-label">Extracted Intelligence</span>
              <span class="packet-kpi-value">${totalEntitiesCount} Entities · ${totalStructuredCount} Schema Items</span>
              <span class="packet-kpi-sub">Model: ${escapeHtml(job.modelIdUsed || 'gpt-5-mini')}</span>
            </div>
          </div>
        `;

        body.innerHTML = kpiStripHtml + activeSegs.map(s => `
          <div class="result-card">
            <div class="result-card-header">
              <div style="display:flex;align-items:center;gap:10px;flex-wrap:wrap;">
                <span class="version-chip" style="background:var(--accent-sand);color:#7c2816;border-color:#e5c5b8;">Slice #${s.segmentIndex + 1} · p.${s.startPage}–${s.endPage}</span>
                <h3 class="result-card-title" style="font-size:15px;">${escapeHtml(s.identifiedDocumentName || s.documentTypeName || 'Document')}</h3>
              </div>
              <div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">
                <span class="version-chip">${escapeHtml((s.detectedLanguage || 'en').toUpperCase())}${s.wasTranslated ? ` → ${escapeHtml((s.targetLanguage || 'en').toUpperCase())}` : ''}</span>
                <span class="conf-badge">${s.identifiedDocumentName && s.identifiedDocumentName !== 'Undefined' ? 'Matched Rule' : 'Identified'}</span>
                <button type="button" class="copy-chip-btn" data-click="jumpToSlicePdf_arg" data-args="[${s.segmentIndex}]" title="Open this slice in View Document">📄 View Slice PDF</button>
              </div>
            </div>
            <div class="insight-summary-box">
              ${escapeHtml(s.insightSummary || 'No insight summary generated (Document Insights capability was disabled for this run).')}
            </div>
            <details>
              <summary style="cursor:pointer;font-size:12px;color:var(--text-secondary);font-weight:600;user-select:none;">View Extracted / Translated Source Text (${(s.translatedText || s.bodyText || '').length.toLocaleString()} chars)</summary>
              <pre style="background:#faf9f7;padding:14px;border-radius:10px;border:1px solid var(--border);font-size:12px;line-height:1.55;white-space:pre-wrap;margin-top:8px;max-height:240px;overflow:auto;">${escapeHtml(s.translatedText || s.bodyText)}</pre>
            </details>
          </div>
        `).join('');
      } else if (docResultTab === 'entities') {
        const allEntities = activeSegs.flatMap(s => (s.extractedEntities || []).map(e => ({ ...e, sliceIndex: s.segmentIndex, sliceNum: s.segmentIndex + 1, docType: s.identifiedDocumentName || s.documentTypeName })));
        if (allEntities.length === 0) {
          body.innerHTML = `
            <div class="result-card" style="text-align:center;padding:42px 20px;">
              <div style="font-size:14.5px;font-weight:600;color:var(--text-primary);margin-bottom:4px;">Entity extraction artifact unavailable</div>
              <div style="font-size:12.5px;color:var(--text-secondary);">This document has no extracted entities or Entity Extraction was disabled.</div>
            </div>
          `;
          return;
        }
        const categories = ['ALL', ...new Set(allEntities.map(e => (e.category || 'Entity').toUpperCase()))];
        if (!categories.includes(selectedEntityCategory)) selectedEntityCategory = 'ALL';
        const filteredEntities = selectedEntityCategory === 'ALL'
          ? allEntities
          : allEntities.filter(e => (e.category || 'Entity').toUpperCase() === selectedEntityCategory);

        body.innerHTML = `
          <div class="result-card">
            <div class="result-card-header">
              <h3 class="result-card-title">Extracted Entities (${filteredEntities.length} of ${allEntities.length})</h3>
              <span class="version-chip">Model: ${escapeHtml(job.modelIdUsed || 'gpt-5-mini')}</span>
            </div>
            <div class="entity-filter-bar">
              <div class="entity-cat-pills">
                ${categories.map(cat => `
                  <button type="button" class="entity-cat-pill ${selectedEntityCategory === cat ? 'active' : ''}" data-click="setEntityCategoryFilter_arg" data-args="[${escapeJsArg(cat)}]">
                    ${cat === 'ALL' ? `All (${allEntities.length})` : escapeHtml(cat)}
                  </button>
                `).join('')}
              </div>
            </div>
            <div class="entity-grid">
              ${filteredEntities.map(e => {
                const pct = Math.round((e.confidence <= 1 ? e.confidence * 100 : e.confidence));
                return `
                  <div class="entity-chip-card">
                    <div class="entity-chip-top">
                      <span>${escapeHtml(e.category || 'Entity')} · <a href="javascript:void(0)" data-click="jumpToSlicePdf_arg" data-args="[${e.sliceIndex}]" style="color:#c25138;text-decoration:none;">Slice #${e.sliceNum} ↗</a></span>
                      <span class="conf-badge">${pct}%</span>
                    </div>
                    <div class="entity-chip-name">${escapeHtml(e.name)}</div>
                    <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;">
                      <div class="entity-chip-val">${escapeHtml(e.value)}</div>
                      <button type="button" class="copy-chip-btn" data-click="copyTextValue_arg" data-args="[${escapeJsArg(e.value)}]" title="Copy value">Copy</button>
                    </div>
                    <div style="height:4px;background:#eeeae2;border-radius:99px;overflow:hidden;margin-top:6px;">
                      <div style="height:100%;width:${Math.min(100, Math.max(10, pct))}%;background:#16a34a;border-radius:99px;"></div>
                    </div>
                  </div>
                `;
              }).join('')}
            </div>
          </div>
        `;
      } else if (docResultTab === 'structured') {
        const slicesWithData = activeSegs.filter(s => s.dataExtraction && ((s.dataExtraction.fields && s.dataExtraction.fields.length > 0) || (s.dataExtraction.tables && s.dataExtraction.tables.length > 0)));
        if (slicesWithData.length === 0) {
          body.innerHTML = `
            <div class="result-card" style="text-align:center;padding:42px 20px;">
              <div style="font-size:14.5px;font-weight:600;color:var(--text-primary);margin-bottom:4px;">Structured data extraction unavailable</div>
              <div style="font-size:12.5px;color:var(--text-secondary);">Configure key-value pairs and tables for "${escapeHtml((activeSegs[0] && activeSegs[0].identifiedDocumentName) || 'your document type')}" in Agent Harness → Data Extraction.</div>
            </div>
          `;
          return;
        }
        body.innerHTML = slicesWithData.map(s => {
          const de = s.dataExtraction;
          const fieldsHtml = (de.fields && de.fields.length > 0) ? `
            <div class="kv-modern-grid">
              ${de.fields.map(f => `
                <div class="kv-modern-card">
                  <div style="display:flex;align-items:center;justify-content:space-between;gap:6px;">
                    <span style="font-size:11.5px;font-weight:600;color:var(--text-secondary);">${escapeHtml(f.fieldName)}</span>
                    <span class="conf-badge">${Math.round(f.confidenceValue)}%</span>
                  </div>
                  <div style="font-size:14px;font-weight:600;color:var(--text-primary);word-break:break-word;">${escapeHtml(f.fieldValue || '—')}</div>
                </div>
              `).join('')}
            </div>
          ` : '';

          const tablesHtml = (de.tables || []).map(t => {
            const rows = t.rows || [];
            const colNames = rows.length > 0 ? (rows[0].fields || []).map(c => c.fieldName) : [];
            return `
              <div style="margin-top:12px;">
                <div style="font-size:12.5px;font-weight:600;color:var(--text-secondary);margin-bottom:8px;">Table: ${escapeHtml(t.tableName)} (${rows.length} row(s))</div>
                ${rows.length > 0 ? `
                  <table class="data-table">
                    <thead>
                      <tr>${colNames.map(cn => `<th>${escapeHtml(cn)}</th>`).join('')}</tr>
                    </thead>
                    <tbody>
                      ${rows.map(r => `
                        <tr>${(r.fields || []).map(cell => `<td>${escapeHtml(cell.fieldValue)}</td>`).join('')}</tr>
                      `).join('')}
                    </tbody>
                  </table>
                ` : '<div style="font-size:12px;color:var(--text-muted);">No rows matched in this slice.</div>'}
              </div>
            `;
          }).join('');

          return `
            <div class="result-card">
              <div class="result-card-header">
                <h3 class="result-card-title">Slice #${s.segmentIndex + 1} · ${escapeHtml(de.documentName)} (Pages ${escapeHtml(de.pageRange)})</h3>
                <span class="version-chip">Footer: ${escapeHtml(de.footerId || 'none')}</span>
              </div>
              ${fieldsHtml}
              ${tablesHtml}
            </div>
          `;
        }).join('');
      } else if (docResultTab === 'metadata') {
        const trace = job.executionTrace || [];
        body.innerHTML = `
          <div class="result-card">
            <div class="result-card-header">
              <h3 class="result-card-title">Document Slices &amp; Classification Metadata</h3>
              <span class="version-chip">Job ID: ${escapeHtml(job.jobId)}</span>
            </div>
            <table class="data-table">
              <thead>
                <tr><th>Slice</th><th>Pages</th><th>Identified Document</th><th>Matched Rule</th><th>Footers</th><th>Blob Artifact</th></tr>
              </thead>
              <tbody>
                ${activeSegs.map(s => `
                  <tr>
                    <td>#${s.segmentIndex + 1}</td>
                    <td>${s.startPage}–${s.endPage}</td>
                    <td><strong>${escapeHtml(s.identifiedDocumentName || s.documentTypeName || 'Undefined')}</strong></td>
                    <td style="font-size:11.5px;color:var(--text-secondary);">${escapeHtml(s.matchedIdentificationRule || '—')}</td>
                    <td style="font-size:11.5px;font-family:monospace;">${escapeHtml((s.footers || []).join(', ') || '—')}</td>
                    <td style="font-size:11.5px;">${s.splitPdfBlobUri ? `<a href="/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/slices/${s.segmentIndex}/pdf" target="_blank" style="color:#c25138;font-weight:600;">Open PDF</a>` : '—'}</td>
                  </tr>
                `).join('')}
              </tbody>
            </table>
          </div>

          <div class="result-card">
            <div class="result-card-header">
              <h3 class="result-card-title">Microsoft Agent Framework Execution Trace</h3>
              <span class="version-chip">Applied Harness v${job.appliedHarnessVersion}</span>
            </div>
            <table class="data-table">
              <thead>
                <tr><th>Executor Step</th><th>Executed</th><th>Model / Service</th><th>Duration</th><th>Notes</th></tr>
              </thead>
              <tbody>
                ${trace.map(t => `
                  <tr>
                    <td style="font-weight:600;">${escapeHtml(t.executorName)}</td>
                    <td>${t.executed ? '✓ Yes' : 'Skipped'}</td>
                    <td>${escapeHtml(t.modelIdUsed || 'Deterministic')}</td>
                    <td>${t.durationMs ? t.durationMs.toFixed(1) + ' ms' : '0 ms'}</td>
                    <td style="color:var(--text-secondary);">${escapeHtml(t.notes || '')}</td>
                  </tr>
                `).join('')}
              </tbody>
            </table>
          </div>
        `;
      }
    }

    DocIntelActions.register({
      addDataExtractionFieldRow: { arity: 0, run: function(args, event) { return addDataExtractionFieldRow(); } },
      addDataExtractionTableBlock: { arity: 0, run: function(args, event) { return addDataExtractionTableBlock(); } },
      addExtractionColumn: { arity: 0, run: function(args, event) { return addColumnToTableBox(this.closest('.table-schema-box')); } },
      addIdentificationRuleRow: { arity: 0, run: function(args, event) { return addIdentificationRuleRow(); } },
      addPromptEntityFilterRow: { arity: 0, run: function(args, event) { return addPromptEntityFilterRow(); } },
      applyQuickPromptTemplate_arg: { arity: 1, run: function(args, event) { return applyQuickPromptTemplate(args[0]); } },
      cancelTenantChanges: { arity: 0, run: function(args, event) { return cancelTenantChanges(); } },
      clearPromptColumnForm: { arity: 0, run: function(args, event) { return clearPromptColumnForm(); } },
      copyTextValue_arg: { arity: 1, run: function(args, event) { return copyTextValue(args[0]); } },
      createTenant: { arity: 0, run: function(args, event) { return createTenant(); } },
      deletePromptColumn_arg: { arity: 1, run: function(args, event) { return deletePromptColumn(args[0]); } },
      editPromptColumn_arg: { arity: 1, run: function(args, event) { return editPromptColumn(args[0]); } },
      expandAndOpenNewTenant: { arity: 0, run: function(args, event) { return expandAndOpenNewTenant(); } },
      jumpToSlicePdf_arg: { arity: 1, run: function(args, event) { return jumpToSlicePdf(args[0]); } },
      loadOcrUsage: { arity: 0, run: function(args, event) { return loadOcrUsage(); } },
      onDataExtractionSchemaEdited: { arity: 0, run: function(args, event) { return onDataExtractionSchemaEdited(); } },
      onHarnessChanged: { arity: 0, run: function(args, event) { return onHarnessChanged(); } },
      onPromptFilterModeChanged: { arity: 0, run: function(args, event) { return onPromptFilterModeChanged(); } },
      onSelectedExtractionDocChanged: { arity: 0, run: function(args, event) { return onSelectedExtractionDocChanged(); } },
      openWorkspaceForTenant: { arity: 0, run: function(args, event) { window.location.href='/?tenant=' + encodeURIComponent(currentTenant ? currentTenant.tenantId : ''); } },
      reloadHarnessAfterConflict: { arity: 0, run: function(args, event) { return reloadHarnessAfterConflict(); } },
      removeExtractionColumn: { arity: 0, run: function(args, event) { this.closest('.col-row').remove(); onDataExtractionSchemaEdited(); } },
      removeExtractionField: { arity: 0, run: function(args, event) { this.closest('.rule-item').remove(); onDataExtractionSchemaEdited(); } },
      removeExtractionTable: { arity: 0, run: function(args, event) { this.closest('.table-schema-box').remove(); onDataExtractionSchemaEdited(); } },
      removeIdentificationRule: { arity: 0, run: function(args, event) { this.closest('.rule-item').remove(); onHarnessChanged(); } },
      removePromptFilter: { arity: 0, run: function(args, event) { this.closest('.entity-filter-rule-row').remove(); } },
      saveOcrBudget: { arity: 0, run: function(args, event) { return saveOcrBudget(); } },
      savePromptColumnFromForm: { arity: 0, run: function(args, event) { return savePromptColumnFromForm(); } },
      saveTenantHarness: { arity: 0, run: function(args, event) { return saveTenantHarness(); } },
      setEntityCategoryFilter_arg: { arity: 1, run: function(args, event) { return setEntityCategoryFilter(args[0]); } },
      setFilterVisibility: { arity: 0, run: function(args, event) { this.nextElementSibling.style.visibility = this.value === 'Exists' ? 'hidden' : 'visible'; } },
      signOut: { arity: 0, run: function(args, event) { return DocIntelIdentity.signOut(); } },
      toggleFeatureDrawer_arg: { arity: 1, run: function(args, event) { return toggleFeatureDrawer(args[0]); } },
      toggleNewTenantPopover_arg: { arity: 1, run: function(args, event) { return toggleNewTenantPopover(args[0]); } },
      toggleSidebar: { arity: 0, run: function(args, event) { return toggleSidebar(); } }
    });

    init();
