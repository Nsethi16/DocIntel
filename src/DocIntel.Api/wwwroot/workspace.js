let tenants = [];
    let currentTenant = null;
    let tenantJobs = [];
    let selectedJobId = null;
    let selectedSliceIndex = -1; // -1 = All Slices
    let workspaceMode = 'documents'; // 'documents' | 'graph' (Cross-document tenant-level graph)
    let docResultTab = 'insights'; // 'insights' | 'document' | 'entities' | 'structured' | 'metadata' | 'prompt:<id>'
    let selectedEntityCategory = 'ALL';
    let selectedPromptFilterView = 'ALL'; // 'ALL' | 'MATCHED' | 'SKIPPED'
    let graphSelectedEntityNames = new Set(); // empty = All entity names
    let graphSelectedEntityValues = new Set(); // Set of 'EntityName||EntityValue' pairs (multi-select!)
    let graphSelectedEntityValuePair = 'ALL';
    let graphEntityDropdownOpen = false;
    let graphEntityDropdownSearch = '';
    let graphEntitySearchQuery = '';
    let graphFocusedNodeId = null;
    let graphDetailsOpen = false;
    let graphHoveredNodeId = null;
    let graphDepthMode = 'GLOBAL'; // 'GLOBAL' | 'HOP1' | 'HOP2'
    let graphZoom = 1;
    let graphPanX = 0;
    let graphPanY = 0;
    let graphSimNodes = [];
    let graphSimEdges = [];
    let graphSimAnimFrame = null;
    let graphCanvasDisposer = null;
    let graphCanvasRefresh = null;
    let graphCanvasNodeIds = new Set();
    const maximumGraphCanvasNodes = 500;
    const maximumGraphCanvasEdges = 2000;
    const maximumGraphInspectorLinks = 100;
    const maximumGraphEntityOptions = 200;
    const graphCanvasDescription = 'Document and entity connections. Use arrow keys to focus a node, Enter to select it, and Escape to clear selection. Documents provides an alternative without the canvas.';
    let graphLayoutSize = null;
    let graphCustomNodePositions = {};
    let askAiQuestionInput = '';
    let askAiLoading = false;
    let askAiLoadingChatId = null;
    let askAiLoadingTenantId = null;
    let askAiProgressText = '';
    let tenantChats = [];
    let loadedChatStorageKey = null;
    let activeChatId = null;
    let jobPollTimer = null;

    function getChatsStorageKey(tenantId = currentTenant?.tenantId || 'default',
      partition = globalThis.DocIntelIdentity?.chatPartition) {
      if (!partition) throw new Error('A verified chat identity is required.');
      return `docintel_chats_v2_${partition}_${tenantId}`;
    }

    function loadTenantChats(forceReload = false) {
      const storageKey = getChatsStorageKey();
      if (loadedChatStorageKey !== storageKey) {
        tenantChats = [];
        activeChatId = null;
        const search = document.getElementById('chatSearchInput');
        if (search) search.value = '';
      }
      loadedChatStorageKey = storageKey;
      if (!forceReload && Array.isArray(tenantChats) && tenantChats.length > 0) {
        if (!activeChatId || !tenantChats.some(c => c.id === activeChatId)) {
          activeChatId = tenantChats[0].id;
        }
        return;
      }
      try {
        const raw = localStorage.getItem(storageKey);
        tenantChats = raw ? JSON.parse(raw) : [];
      } catch {
        console.error('Unable to read browser chat history.');
        showToast('Chat history could not be loaded on this device.');
        tenantChats = [];
      }
      if (!Array.isArray(tenantChats)) tenantChats = [];
      if (tenantChats.length === 0) {
        const initialChat = {
          id: 'chat-' + Date.now(),
          title: 'New chat',
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          messages: []
        };
        tenantChats.push(initialChat);
        activeChatId = initialChat.id;
        saveTenantChats();
      } else if (!activeChatId || !tenantChats.some(c => c.id === activeChatId)) {
        activeChatId = tenantChats[0].id;
      }
    }

    function saveTenantChats() {
      try {
        localStorage.setItem(getChatsStorageKey(), JSON.stringify(tenantChats));
      } catch {
        console.error('Unable to save browser chat history.');
        showToast('Chat history could not be saved. Keep this tab open while your question is processing.');
      }
    }

    function getActiveChat() {
      if (!activeChatId || !tenantChats.some(c => c.id === activeChatId)) {
        loadTenantChats();
      }
      return tenantChats.find(c => c.id === activeChatId) || tenantChats[0];
    }

    function startNewAskAiChat() {
      loadTenantChats();
      document.getElementById('chatSearchInput').value = '';
      // Reuse existing empty chat if the top chat has 0 messages
      const existingEmpty = tenantChats.find(c => (c.messages || []).length === 0);
      if (existingEmpty) {
        activeChatId = existingEmpty.id;
      } else {
        const newChat = {
          id: 'chat-' + Date.now() + '-' + Math.random().toString(36).slice(2, 6),
          title: 'New chat',
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          messages: []
        };
        tenantChats.unshift(newChat);
        activeChatId = newChat.id;
        saveTenantChats();
      }
      askAiQuestionInput = '';
      renderDocumentList();
      renderSelectedDocument();
      const inp = document.getElementById('askAiMainInputEl');
      if (inp) inp.focus();
    }

    function startNewAskAiChatAndOpen() {
      workspaceMode = 'ask';
      startNewAskAiChat();
      setWorkspaceMode('ask');
    }

    function selectAskAiChat(chatId) {
      loadTenantChats();
      if (!tenantChats.some(chat => chat.id === chatId)) {
        showToast('This chat is no longer available in this workspace.');
        renderDocumentList();
        return;
      }
      activeChatId = chatId;
      askAiQuestionInput = '';
      if (workspaceMode !== 'ask') {
        setWorkspaceMode('ask');
        return;
      }
      renderDocumentList();
      renderSelectedDocument();

      const chat = getActiveChat();
      if (!askAiLoading && chat?.pendingAskAi?.runId) {
        runCrossDocumentAskAi(null, true);
      }
    }

    function deleteAskAiChat(chatId, evt) {
      if (evt) evt.stopPropagation();
      loadTenantChats();
      if (!tenantChats.some(chat => chat.id === chatId)) {
        showToast('This chat is no longer available in this workspace.');
        renderDocumentList();
        return;
      }
      tenantChats = tenantChats.filter(c => c.id !== chatId);
      if (tenantChats.length === 0) {
        const fresh = {
          id: 'chat-' + Date.now(),
          title: 'New chat',
          createdAt: new Date().toISOString(),
          updatedAt: new Date().toISOString(),
          messages: []
        };
        tenantChats.push(fresh);
        activeChatId = fresh.id;
      } else if (activeChatId === chatId) {
        activeChatId = tenantChats[0].id;
      }
      saveTenantChats();
      renderDocumentList();
      renderSelectedDocument();
    }

    function isGraphCapabilityEnabled() {
      return currentTenant?.capabilities?.DocumentGraph?.enabled !== false;
    }

    function isAskAiCapabilityEnabled() {
      return currentTenant?.capabilities?.AskAi?.enabled !== false;
    }

    function syncTenantWorkspaceCapabilities() {
      const graphOn = isGraphCapabilityEnabled();
      const askOn = isAskAiCapabilityEnabled();
      const switcher = document.querySelector('#docListPane .workspace-mode-switcher');
      const btnDocs = document.getElementById('btnModeDocuments');
      const btnGraph = document.getElementById('btnModeGraph');
      const btnAsk = document.getElementById('btnModeAsk');

      if (btnGraph) btnGraph.style.display = graphOn ? 'inline-flex' : 'none';
      if (btnAsk) btnAsk.style.display = askOn ? 'inline-flex' : 'none';

      if (switcher) switcher.style.display = 'grid';

      if ((workspaceMode === 'graph' && !graphOn) || (workspaceMode === 'ask' && !askOn)) {
        workspaceMode = 'documents';
      }
      const asking = workspaceMode === 'ask';
      document.getElementById('drawerUploadLabel').hidden = asking;
      document.getElementById('drawerNewChatButton').hidden = !asking;
      document.getElementById('docSearchBoxWrap').hidden = asking;
      document.getElementById('chatSearchBoxWrap').hidden = !asking;
      document.getElementById('sidebarCollectionTitle').textContent = asking ? 'Past Chats' : 'Documents';

      for (const [button, mode] of [[btnDocs, 'documents'], [btnGraph, 'graph'], [btnAsk, 'ask']]) {
        if (!button) continue;
        button.classList.toggle('active', workspaceMode === mode);
        button.setAttribute('aria-pressed', String(workspaceMode === mode));
      }
    }

    function setWorkspaceMode(mode) {
      if (mode === 'graph' && !isGraphCapabilityEnabled()) mode = 'documents';
      if (mode === 'ask' && !isAskAiCapabilityEnabled()) mode = 'documents';
      workspaceMode = mode;
      syncTenantWorkspaceCapabilities();
      if (mode === 'ask') {
        loadTenantChats();
        const chat = getActiveChat();
        if (!askAiLoading && chat?.pendingAskAi?.runId) {
          runCrossDocumentAskAi(null, true);
        }
      }
      renderDocumentList();
      renderSelectedDocument();
    }

    function getMonogram(name) {
      return String(name || 'DI')
        .split(/[\s&-]+/)
        .filter(Boolean)
        .map(w => w[0])
        .join('')
        .substring(0, 2)
        .toUpperCase() || 'DI';
    }

    function cleanInsightText(raw) {
      if (!raw) return '';
      return String(raw)
        .replace(/^\[[^\]]*Insight\]\s*/i, '')
        .replace(/\s*\(Guided by:[^)]*\)\s*$/i, '')
        .trim();
    }



    function formatModelScore(value, scale) {
      const info = DocIntelEvidence.describe(value, scale);
      return info.modelPercent === null ? 'Unknown model score' : `Model score ${info.modelPercent}%`;
    }

    function renderValueProvenance(value, job, scale) {
      const info = DocIntelEvidence.describe(value, scale);
      const source = job?.tenantId && job?.jobId
        ? `/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/original`
        : null;
      const links = source ? info.pages.map(page =>
        `<a class="text-link-btn" href="${escapeHtml(source)}#page=${page}" target="_blank" rel="noopener noreferrer">Original p.${page}</a>`
      ).join(' ') : '';
      return `<div style="font-size:11px;color:var(--text-muted);" title="${escapeHtml(info.tooltip)}">${escapeHtml(info.status)}${links ? ' · ' + links : ''}</div>`;
    }


    function toggleDocListPane() {
      const pane = document.getElementById('docListPane');
      const expandBtn = document.getElementById('btnHeaderExpandDocList');
      if (!pane) return;
      const isCollapsed = pane.classList.toggle('collapsed');
      pane.inert = isCollapsed;
      pane.setAttribute('aria-hidden', String(isCollapsed));
      if (expandBtn) {
        expandBtn.style.display = isCollapsed ? 'inline-flex' : 'none';
      }
    }

    async function init() {
      document.addEventListener('docintel:appearance-change', () => graphCanvasRefresh?.());
      document.addEventListener('keydown', event => {
        if (event.key === 'Escape' && workspaceMode === 'graph' && graphEntityDropdownOpen) {
          event.preventDefault();
          closeGraphDropdown();
          document.getElementById('entityMultiSelectBox')?.focus();
        }
      });
      const resp = await fetch('/api/harness/tenants');
      tenants = await resp.json();
      const urlTenant = new URLSearchParams(window.location.search).get('tenant');
      currentTenant = tenants.find(t => t.tenantId === urlTenant) || tenants[0] || null;
      renderTenantSelect();
      if (globalThis.matchMedia?.('(max-width: 700px)').matches) toggleDocListPane();
      await loadTenantJobs();
    }

    function renderTenantSelect() {
      const sel = document.getElementById('tenantSelectDropdown');
      sel.innerHTML = '';
      tenants.forEach(t => {
        const opt = document.createElement('option');
        opt.value = t.tenantId;
        opt.textContent = t.displayName;
        if (currentTenant && currentTenant.tenantId === t.tenantId) opt.selected = true;
        sel.appendChild(opt);
      });
      syncTenantWorkspaceCapabilities();
    }

    async function onTenantSwitched(tenantId) {
      stopGraphCanvas();
      resetGraphState();
      currentTenant = tenants.find(t => t.tenantId === tenantId) || tenants[0] || null;
      selectedJobId = null;
      selectedSliceIndex = -1;
      activeChatId = null;
      askAiQuestionInput = '';
      loadTenantChats(true);
      renderTenantSelect();
      await loadTenantJobs();
    }

    async function loadTenantJobs(preferredJobId) {
      if (!currentTenant) return;
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

    function buildJobSearchCorpus(job) {
      const segs = job.segments || [];
      const markdownAndContent = segs.map(s => [
        s.identifiedDocumentName || '',
        s.documentTypeName || '',
        s.markdown || '',
        s.bodyText || '',
        s.translatedText || '',
        s.insightSummary || '',
        (s.footers || []).join(' ')
      ].join('\n')).join('\n');

      const entitiesText = (job.extractedEntities || [])
        .map(e => `${e.name || ''} ${e.value || ''}`)
        .join(' ');

      return `${job.fileName || ''}\n${job.jobId || ''}\n${job.packetSummary || ''}\n${entitiesText}\n${markdownAndContent}`;
    }

    function extractContentMatchSnippet(job, rawQuery) {
      if (!rawQuery) return '';
      const q = rawQuery.toLowerCase();
      if ((job.fileName || '').toLowerCase().includes(q)) return '';

      for (const s of (job.segments || [])) {
        const md = String(s.markdown || s.bodyText || s.translatedText || '')
          .replace(/<!--[\s\S]*?-->/g, ' ')
          .replace(/\s+/g, ' ')
          .trim();
        const idx = md.toLowerCase().indexOf(q);
        if (idx >= 0) {
          const start = Math.max(0, idx - 22);
          const end = Math.min(md.length, idx + rawQuery.length + 34);
          const prefix = start > 0 ? '…' : '';
          const suffix = end < md.length ? '…' : '';
          return `p.${s.startPage}: "${prefix}${md.slice(start, end)}${suffix}"`;
        }
      }
      return '';
    }

    function renderChatList() {
      loadTenantChats();
      const container = document.getElementById('docItemsList');
      const query = document.getElementById('chatSearchInput').value.trim().toLowerCase();
      const history = tenantChats.filter(chat => Array.isArray(chat.messages) && chat.messages.length > 0);
      const filtered = history.filter(chat => !query ||
        [chat.title, ...chat.messages.map(message => message?.text)].join('\n').toLowerCase().includes(query));
      document.getElementById('docListCountLabel').textContent =
        `${filtered.length} chat${filtered.length === 1 ? '' : 's'}`;
      container.replaceChildren();
      if (!filtered.length) {
        const empty = document.createElement('div');
        empty.className = 'sidebar-list-empty';
        empty.textContent = query ? 'No matching chats. Try another search.'
          : 'No past chats yet. Ask a question to start your first conversation.';
        container.appendChild(empty);
        return;
      }
      for (const chat of filtered) {
        const selected = chat.id === activeChatId;
        const row = document.createElement('div');
        row.className = 'chat-history-item' + (selected ? ' selected' : '');
        const open = document.createElement('button');
        open.type = 'button';
        open.className = 'doc-row';
        open.title = String(chat.title || 'Chat');
        open.setAttribute('aria-pressed', String(selected));
        open.onclick = () => selectAskAiChat(chat.id);
        open.innerHTML = `
          <svg class="doc-row-icon" viewBox="0 0 20 24" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><path d="M3 4h14v12H8l-5 4zM6 8h8M6 12h5"/></svg>
          <span class="doc-row-main"><span class="doc-row-name">${escapeHtml(chat.title || 'Chat')}</span>
            <span class="doc-row-meta">${chat.messages.length} message${chat.messages.length === 1 ? '' : 's'}</span></span>`;
        const remove = document.createElement('button');
        remove.type = 'button';
        remove.className = 'chat-delete-btn';
        remove.title = 'Delete Chat';
        remove.setAttribute('aria-label', `Delete chat: ${chat.title || 'Chat'}`);
        remove.textContent = '\u00d7';
        remove.onclick = event => deleteAskAiChat(chat.id, event);
        row.append(open, remove);
        container.appendChild(row);
      }
    }

    function renderDocumentList() {
      if (workspaceMode === 'ask') {
        renderChatList();
        return;
      }
      const container = document.getElementById('docItemsList');
      const rawQuery = (document.getElementById('docSearchInput').value || '').trim();
      const query = rawQuery.toLowerCase();

      const filtered = tenantJobs.filter(j => {
        if (!query) return true;
        const hay = buildJobSearchCorpus(j).toLowerCase();
        return hay.includes(query);
      });

      document.getElementById('docListCountLabel').textContent = `${filtered.length} document${filtered.length === 1 ? '' : 's'}`;
      container.innerHTML = '';

      if (filtered.length === 0) {
        container.innerHTML = `
          <div style="padding:28px 12px;text-align:center;color:var(--text-secondary);font-size:12.5px;line-height:1.5;">
            No documents found.<br/>${query ? 'Try another name or content search.'
              : 'Your accessible documents will appear here.'}
          </div>
        `;
        return;
      }

      filtered.forEach(job => {
        const isSelected = job.jobId === selectedJobId;
        const dt = job.createdAtUtc ? new Date(job.createdAtUtc) : new Date();
        const dateStr = dt.toLocaleDateString('en-US', { month: 'short', day: 'numeric' });
        const segs = job.segments || [];
        const status = documentJobStatus(job);
        const snippet = extractContentMatchSnippet(job, rawQuery);

        const row = document.createElement('button');
        row.type = 'button';
        row.className = 'doc-row' + (isSelected ? ' selected' : '');
        row.setAttribute('aria-pressed', String(isSelected));
        row.setAttribute('aria-label', `${job.fileName} · ${status.label} · ${job.totalPages || 0} pages`);
        row.title = `${job.fileName} (${status.label})`;
        row.onclick = () => {
          selectedJobId = job.jobId;
          selectedSliceIndex = -1;
          if (workspaceMode !== 'documents') {
            setWorkspaceMode('documents');
            return;
          }
          renderDocumentList();
          renderSelectedDocument();
        };

        row.innerHTML = `
          <svg class="doc-row-icon" viewBox="0 0 20 24" fill="none" stroke="currentColor" stroke-width="1.4" aria-hidden="true"><path d="M4 2h8l4 4v16H4zM12 2v5h4M7 12h6M7 16h6"/></svg>
          <span class="doc-row-main">
            <span class="doc-row-name">${escapeHtml(job.fileName)}</span>
            <span class="doc-row-meta">${escapeHtml(dateStr)} · ${job.totalPages || 0} pp${segs.length > 1 ? ` · ${segs.length} slices` : ''}</span>
            ${status.kind === 'warning' ? '<span class="doc-row-warning">Completed with warnings</span>' : ''}
            ${snippet ? `<span class="doc-row-snippet">${escapeHtml(snippet)}</span>` : ''}
          </span>
          <span class="status-dot ${status.kind}" title="${escapeHtml(status.label)}" aria-hidden="true"></span>
        `;
        container.appendChild(row);
      });
    }

    async function uploadAndRunTenantDocument(inputEl) {
      if (!currentTenant || !inputEl.files || !inputEl.files[0]) return;
      const file = inputEl.files[0];
      const tenantId = currentTenant.tenantId;
      const lbl = document.getElementById('uploadBtnLabel');
      lbl.textContent = 'Uploading...';
      inputEl.disabled = true;

      try {
        const fd = new FormData();
        fd.append('file', file);
        const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(tenantId)}/jobs`, {
          method: 'POST',
          body: fd
        });
        if (resp.ok) {
          const accepted = await resp.json();
          showToast(`Submitted ${file.name}`);
          if (currentTenant?.tenantId === tenantId) await loadTenantJobs(accepted.jobId);
        } else {
          const messages = {
            400: 'Upload rejected. Select one valid PDF up to 25 MiB, or refresh to renew request verification.',
            401: 'Sign in again before uploading.',
            403: 'Your account does not have permission to upload to this workspace.',
            413: 'The PDF is too large. The maximum file size is 25 MiB.',
            429: 'The upload allowance was exceeded. Wait before trying again.'
          };
          const correlation = resp.headers.get('X-DocIntel-Correlation-Id');
          const reference = correlation && /^[A-Za-z0-9:._-]{1,160}$/.test(correlation)
            ? ` Reference: ${correlation}.` : '';
          showToast((messages[resp.status] || `Upload failed (HTTP ${resp.status}). Contact your administrator.`) + reference);
        }
      } catch {
        showToast('The upload could not be confirmed. Check your connection and sign-in, then refresh the document list before retrying.');
      } finally {
        lbl.textContent = 'Upload PDF';
        inputEl.disabled = false;
        inputEl.value = '';
      }
    }

    function setDocResultTab(tab) {
      docResultTab = tab;
      document.querySelectorAll('#insightsTabBar .meridian-tab').forEach(btn => {
        btn.classList.toggle('active', btn.dataset.tab === tab);
      });
      renderSelectedDocument();
    }

    function jumpToSlicePdf(sliceIndex, evt) {
      if (evt) evt.stopPropagation();
      selectedSliceIndex = sliceIndex;
      setDocResultTab('document');
    }

    function toggleSliceCard(headEl) {
      const card = headEl.closest('.slice-section');
      if (card) {
        card.classList.toggle('expanded');
      }
    }

    function setEntityCategoryFilter(cat) {
      selectedEntityCategory = cat;
      renderSelectedDocument();
    }

    function setPromptFilterView(mode) {
      selectedPromptFilterView = mode;
      renderSelectedDocument();
    }

    function toggleGraphEntityNameFilter(entityName, evt) {
      if (evt) evt.stopPropagation();
      if (entityName === 'ALL') {
        graphSelectedEntityNames.clear();
        graphSelectedEntityValues.clear();
      } else {
        if (graphSelectedEntityNames.has(entityName)) {
          graphSelectedEntityNames.delete(entityName);
        } else {
          graphSelectedEntityNames.add(entityName);
        }
      }
      graphFocusedNodeId = null;
      renderSelectedDocument();
      if (graphEntityDropdownOpen) document.getElementById('entityPopoverSearchInput')?.focus();
    }

    function toggleGraphEntityValueSelection(pairKey, evt) {
      if (evt) evt.stopPropagation();
      if (graphSelectedEntityValues.has(pairKey)) {
        graphSelectedEntityValues.delete(pairKey);
      } else {
        graphSelectedEntityValues.add(pairKey);
      }
      graphFocusedNodeId = null;
      renderSelectedDocument();
      if (graphEntityDropdownOpen) document.getElementById('entityPopoverSearchInput')?.focus();
    }

    function toggleEntityMultiSelectDropdown(evt) {
      if (evt) evt.stopPropagation();
      graphEntityDropdownOpen = !graphEntityDropdownOpen;
      const pop = document.getElementById('entityMultiSelectPopover');
      const box = document.getElementById('entityMultiSelectBox');
      if (pop) pop.classList.toggle('open', graphEntityDropdownOpen);
      if (box) box.classList.toggle('open', graphEntityDropdownOpen);
      if (box) box.setAttribute('aria-expanded', String(graphEntityDropdownOpen));
      if (graphEntityDropdownOpen) {
        const inp = document.getElementById('entityPopoverSearchInput');
        if (inp) inp.focus();
      }
    }

    function closeGraphDropdown() {
      graphEntityDropdownOpen = false;
      document.getElementById('entityMultiSelectPopover')?.classList.remove('open');
      const button = document.getElementById('entityMultiSelectBox');
      button?.classList.remove('open');
      button?.setAttribute('aria-expanded', 'false');
    }

    function filterEntityPopoverOptions(query) {
      graphEntityDropdownSearch = query || '';
      const list = document.getElementById('entityPopoverList');
      if (list) list.innerHTML = renderGraphEntityOptions(computeFilteredGraphData().allEntityValuePairs);
    }

    function onGraphEntityValueFilterChanged(val) {
      if (!val || val === 'ALL') {
        graphSelectedEntityValues.clear();
      } else {
        if (graphSelectedEntityValues.has(val)) {
          graphSelectedEntityValues.delete(val);
        } else {
          graphSelectedEntityValues.add(val);
        }
      }
      graphFocusedNodeId = null;
      renderSelectedDocument();
    }

    function onGraphSearchInputChanged(val) {
      graphEntitySearchQuery = val || '';
      renderDocumentGraphCanvasOnly();
    }

    function setGraphScope(scope) {
      graphScope = scope;
      graphFocusedNodeId = null;
      graphCustomNodePositions = {};
      renderSelectedDocument();
    }

    function resetGraphState() {
      graphEntityDropdownOpen = false;
      graphSelectedEntityNames.clear();
      graphSelectedEntityValues.clear();
      graphSelectedEntityValuePair = 'ALL';
      graphEntityDropdownSearch = '';
      graphEntitySearchQuery = '';
      graphFocusedNodeId = null;
      graphDetailsOpen = false;
      graphHoveredNodeId = null;
      graphDepthMode = 'GLOBAL';
      graphZoom = 1;
      graphPanX = 0;
      graphPanY = 0;
      graphCustomNodePositions = {};
      graphLayoutSize = null;
    }

    function resetGraphFilters(evt) {
      if (evt) evt.stopPropagation();
      resetGraphState();
      renderSelectedDocument();
    }

    function zoomGraphBy(delta) {
      if (delta === 0) {
        graphZoom = 1;
        graphPanX = 0;
        graphPanY = 0;
        graphCustomNodePositions = {};
      } else {
        graphZoom = Math.min(2.4, Math.max(0.55, +(graphZoom + delta).toFixed(2)));
      }
      renderDocumentGraphCanvasOnly();
    }

    function focusGraphNode(nodeId) {
      graphFocusedNodeId = (graphFocusedNodeId === nodeId) ? null : nodeId;
      graphDetailsOpen = graphFocusedNodeId !== null;
      renderDocumentGraphCanvasOnly(true);
    }

    function toggleGraphDetails() {
      graphDetailsOpen = !graphDetailsOpen;
      graphFocusedNodeId = null;
      renderDocumentGraphCanvasOnly(true);
    }

    function stopGraphCanvas() {
      if (graphSimAnimFrame !== null) cancelAnimationFrame(graphSimAnimFrame);
      graphSimAnimFrame = null;
      graphCanvasDisposer?.();
      graphCanvasDisposer = null;
      graphCanvasRefresh = null;
      graphCanvasNodeIds.clear();
      graphHoveredNodeId = null;
    }

    function jumpFromGraphToSlice(jobId, sliceIdx) {
      selectedJobId = jobId;
      selectedSliceIndex = sliceIdx;
      docResultTab = sliceIdx === -1 ? 'insights' : 'document';
      setWorkspaceMode('documents');
    }

    async function reevaluateSelectedJobPrompts() {
      const job = tenantJobs.find(j => j.jobId === selectedJobId);
      if (!job || !currentTenant) return;
      showToast('Evaluating Prompt Columns on PDF/Base64...');
      const resp = await fetch(`/api/harness/tenants/${encodeURIComponent(currentTenant.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/evaluate-prompts`, {
        method: 'POST'
      });
      if (resp.ok) {
        const updatedJob = await resp.json();
        const idx = tenantJobs.findIndex(j => j.jobId === updatedJob.jobId);
        if (idx >= 0) tenantJobs[idx] = updatedJob;
        renderSelectedDocument();
        showToast('Prompt Columns updated');
      } else {
        showToast('Failed to evaluate prompts');
      }
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
      showToast('Exported JSON');
    }

    function renderSelectedDocument() {
      stopGraphCanvas();
      const body = document.getElementById('docCenterBody');
      body.dataset.layout = '';
      const sliceRow = document.getElementById('sliceSelectorRow');
      const navRow = document.getElementById('meridianNavRow');
      const exportBtn = document.getElementById('btnHeaderExportJson');
      const headerEl = document.querySelector('.meridian-header');
      if (headerEl) headerEl.style.display = '';

      if (workspaceMode === 'ask') {
        updateDocumentStatusNotice(null);
        body.dataset.tab = 'ask';
        if (navRow) navRow.style.display = 'none';
        if (exportBtn) exportBtn.style.display = 'none';
        sliceRow.innerHTML = '';
        document.getElementById('selectedDocTitle').textContent = 'Ask AI';
        document.getElementById('selectedDocMetaText').textContent = currentTenant?.displayName || 'Workspace';
        renderCrossDocumentAskTab(body);
        return;
      }

      if (workspaceMode === 'graph') {
        updateDocumentStatusNotice(null);
        body.dataset.tab = 'graph';
        if (navRow) navRow.style.display = 'none';
        if (exportBtn) exportBtn.style.display = 'none';
        sliceRow.innerHTML = '';
        document.getElementById('selectedDocTitle').textContent = 'Graph';
        document.getElementById('selectedDocMetaText').textContent = currentTenant?.displayName || 'Workspace';
        renderDocumentGraphTab(body);
        return;
      }

      if (navRow) navRow.style.display = '';
      if (exportBtn) exportBtn.style.display = '';
      body.dataset.tab = docResultTab;

      const job = tenantJobs.find(j => j.jobId === selectedJobId);
      if (exportBtn) exportBtn.hidden = !job;

      if (!job) {
        updateDocumentStatusNotice(null);
        document.getElementById('selectedDocTitle').textContent = currentTenant ? currentTenant.displayName : 'Document Intelligence';
        document.getElementById('selectedDocMetaText').textContent = currentTenant ? currentTenant.tenantId : 'Idle';
        sliceRow.innerHTML = '';
        if (navRow) navRow.style.display = 'none';
        const upload = document.getElementById('drawerUploadLabel');
        const canUpload = upload && upload.style.display !== 'none';
        body.innerHTML = `
          <div class="workspace-start">
            <h2>Your documents, ready to explore</h2>
            <p>${canUpload ? 'Upload a PDF to identify its documents and view the results here.'
              : 'Select a document from the list. Ask your workspace administrator to upload documents.'}</p>
            ${canUpload ? '<button type="button" class="workspace-start-upload" data-click="chooseWorkspaceUpload">Upload a PDF</button><small>One PDF at a time · up to 25 MiB</small>' : ''}
          </div>
        `;
        return;
      }

      const segs = job.segments || [];
      updateDocumentStatusNotice(job);
      const cleanTitle = (job.fileName || 'Document').replace(/\.pdf$/i, '');
      document.getElementById('selectedDocTitle').textContent = cleanTitle;
      document.getElementById('selectedDocTitle').title = job.fileName;
      const provenance = job.status !== 'Completed' ? '' : job.hasDevelopmentResults === true
        ? ' · Development/simulated results' : job.hasDevelopmentResults == null ? ' · Legacy results: verify source' : '';
      document.getElementById('selectedDocMetaText').textContent = `${job.totalPages || 0} pp${provenance}`;

      const segmentedEntities = segs.flatMap(s => s.extractedEntities || []);
      const totalEntitiesCount = (segmentedEntities.length ? segmentedEntities : job.extractedEntities || []).length;
      const totalStructuredCount = segs.reduce((acc, s) => {
        const de = s.dataExtraction;
        if (!de) return acc;
        return acc + ((de.fields || []).length) + ((de.tables || []).length);
      }, 0);

      const activeSegs = selectedSliceIndex === -1
        ? segs
        : segs.filter(s => s.segmentIndex === selectedSliceIndex);

      const caps = currentTenant?.capabilities || {};
      const insightEnabled = caps.DocumentInsight?.enabled !== false ||
        Boolean(cleanInsightText(job.packetSummary)) || segs.some(s => cleanInsightText(s.insightSummary));
      const entityEnabled = caps.EntityExtraction?.enabled !== false || totalEntitiesCount > 0;
      const dataExtEnabled = caps.DataExtraction?.enabled !== false || totalStructuredCount > 0;
      const promptColsEnabled = caps.PromptColumns?.enabled !== false ||
        (job.promptColumnResults || []).length > 0 || segs.some(s => (s.promptColumnResults || []).length > 0);

      // Collect distinct Prompt Columns that matched the entity filter on the active document / slice selection
      const activePromptResults = promptColsEnabled
        ? (activeSegs.length ? activeSegs.flatMap(s =>
            (s.promptColumnResults && s.promptColumnResults.length > 0)
              ? s.promptColumnResults
              : (job.promptColumnResults || []).filter(r => r.segmentIndex === s.segmentIndex)
          ) : selectedSliceIndex === -1 ? job.promptColumnResults || [] : [])
        : [];
      const matchedPromptColumnsMap = new Map();
      activePromptResults.forEach(r => {
        if (r && r.filterMatched && r.columnId && !matchedPromptColumnsMap.has(r.columnId)) {
          matchedPromptColumnsMap.set(r.columnId, {
            columnId: r.columnId,
            columnName: r.columnName || r.columnId
          });
        }
      });
      const matchedPromptColumns = [...matchedPromptColumnsMap.values()];

      // Current feature settings must not hide results already stored by an earlier run.
      const coreTabsList = [
        ...(insightEnabled ? [{ id: 'insights', label: 'Insights' }] : []),
        { id: 'document', label: 'View Document' },
        ...(entityEnabled ? [{ id: 'entities', label: `Entities${totalEntitiesCount > 0 ? ` <span class="tab-count">${totalEntitiesCount}</span>` : ''}` }] : []),
        ...(dataExtEnabled ? [{ id: 'structured', label: `Structured Data${totalStructuredCount > 0 ? ` <span class="tab-count">${totalStructuredCount}</span>` : ''}` }] : []),
        { id: 'metadata', label: 'Metadata' }
      ];

      // Ensure docResultTab is valid for the enabled tabs on this tenant/document
      const validCoreIds = new Set(coreTabsList.map(t => t.id));
      if (docResultTab.startsWith('prompt:')) {
        const activeColId = docResultTab.substring('prompt:'.length);
        if (!promptColsEnabled || !matchedPromptColumnsMap.has(activeColId)) {
          docResultTab = coreTabsList[0].id;
          body.dataset.tab = docResultTab;
        }
      } else if (!validCoreIds.has(docResultTab)) {
        docResultTab = coreTabsList[0].id;
        body.dataset.tab = docResultTab;
      }

      // Render core tabs + one individual tab per matched Prompt Column (e.g. ✨ Summary, ✨ Highlights)
      const tabBar = document.getElementById('insightsTabBar');
      const coreTabsHtml = coreTabsList.map(t => `
        <button type="button" class="meridian-tab ${docResultTab === t.id ? 'active' : ''}" data-tab="${t.id}" data-click="setDocResultTab_arg" data-args="[${escapeJsArg(t.id)}]">${t.label}</button>
      `).join('');

      const promptTabsHtml = matchedPromptColumns.map(col => {
        const tabId = `prompt:${col.columnId}`;
        const isActive = docResultTab === tabId;
        return `
          <button type="button" class="meridian-tab ${isActive ? 'active' : ''}" data-tab="${escapeHtml(tabId)}" data-click="setDocResultTab_arg" data-args="[${escapeJsArg(tabId)}]" title="Prompt Column: ${escapeHtml(col.columnName)}">
            <span style="color:var(--accent);margin-right:4px;">✦</span>${escapeHtml(col.columnName)}
          </button>
        `;
      }).join('');

      tabBar.innerHTML = coreTabsHtml + promptTabsHtml;

      // Single clean slice dropdown on the right of the tab bar
      sliceRow.innerHTML = '';
      if (segs.length > 1) {
        const select = document.createElement('select');
        select.className = 'meridian-slice-select';
        if (docResultTab !== 'document') {
          const allOpt = document.createElement('option');
          allOpt.value = '-1';
          allOpt.textContent = `All slices (${segs.length})`;
          if (selectedSliceIndex === -1) allOpt.selected = true;
          select.appendChild(allOpt);
        }

        const activePdfSliceIdx = (docResultTab === 'document' && selectedSliceIndex === -1) ? 0 : selectedSliceIndex;

        segs.forEach(s => {
          const opt = document.createElement('option');
          opt.value = String(s.segmentIndex);
          const labelName = s.identifiedDocumentName || s.documentTypeName || `Slice ${s.segmentIndex + 1}`;
          opt.textContent = `Slice ${s.segmentIndex + 1}: ${labelName} (p.${s.startPage}–${s.endPage})`;
          if (activePdfSliceIdx === s.segmentIndex) opt.selected = true;
          select.appendChild(opt);
        });

        select.onchange = (e) => {
          selectedSliceIndex = parseInt(e.target.value, 10);
          renderSelectedDocument();
        };
        sliceRow.appendChild(select);
      }

      if (docResultTab === 'document' && segs.length > 0) {
        const focusedSeg = (selectedSliceIndex >= 0 ? segs.find(s => s.segmentIndex === selectedSliceIndex) : segs[0]) || segs[0];
        const origPdfUrl = `/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/slices/${focusedSeg.segmentIndex}/pdf`;
        if (!focusedSeg.wasTranslated) {
          const openLink = document.createElement('a');
          openLink.href = origPdfUrl;
          openLink.target = '_blank';
          openLink.rel = 'noopener noreferrer';
          openLink.className = 'ghost-action-btn';
          openLink.textContent = 'Open PDF ↗';
          sliceRow.appendChild(openLink);
        }
      }

      // 1. View Document Tab — Side-by-Side (Original PDF + Translated PDF) when translated, or single full-bleed PDF
      if (docResultTab === 'document') {
        body.dataset.layout = 'pdf';
        const focusedSeg = (selectedSliceIndex >= 0 ? segs.find(s => s.segmentIndex === selectedSliceIndex) : segs[0]) || segs[0];
        const jobUrl = `/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}`;
        const origPdfUrl = focusedSeg ? `${jobUrl}/slices/${focusedSeg.segmentIndex}/pdf` : `${jobUrl}/original`;
        const transPdfUrl = `${origPdfUrl}?translated=true`;

        if (focusedSeg?.wasTranslated === true) {
          const splitKey = `${job.jobId}:${focusedSeg.segmentIndex}:side-by-side`;
          const existingSplit = body.querySelector('.pdf-side-by-side');
          if (existingSplit && existingSplit.dataset.splitKey === splitKey) {
            return;
          }
          const srcLang = (focusedSeg.detectedLanguage || 'Unknown language').toUpperCase();
          const tgtLang = (focusedSeg.targetLanguage || 'Unknown language').toUpperCase();
          body.innerHTML = `
            <div class="pdf-side-by-side" data-split-key="${escapeHtml(splitKey)}">
              <div class="pdf-pane">
                <div class="pdf-pane-bar">
                  <span><strong>Original Document (${escapeHtml(srcLang)})</strong> · Pages ${focusedSeg.startPage}–${focusedSeg.endPage}</span>
                  <a href="${origPdfUrl}" target="_blank" rel="noopener noreferrer" class="text-link-btn">Open ↗</a>
                </div>
                ${pdfPreviewMarkup(origPdfUrl, 'Original PDF Preview')}
              </div>
              <div class="pdf-pane">
                <div class="pdf-pane-bar">
                  <span><strong>Translated Document (${escapeHtml(tgtLang)})</strong> · Azure Document Translation</span>
                  <a href="${transPdfUrl}" target="_blank" rel="noopener noreferrer" class="text-link-btn">Open ↗</a>
                </div>
                ${pdfPreviewMarkup(transPdfUrl, 'Translated PDF Preview')}
              </div>
            </div>
          `;
          startPdfPreviews(body);
          return;
        }

        const note = translationPreviewNote(job, focusedSeg);
        const previewKey = JSON.stringify([origPdfUrl, note]);
        const existingPreview = body.querySelector('.pdf-preview');
        if (existingPreview?.dataset.previewKey === previewKey && !body.querySelector('.pdf-side-by-side')) {
          return;
        }
        body.innerHTML = (note ? `<p class="pdf-translation-note" data-kind="${note.kind}">${escapeHtml(note.text)}</p>` : '') +
          pdfPreviewMarkup(origPdfUrl, 'Original PDF Preview');
        const renderedPreview = body.querySelector('.pdf-preview');
        if (renderedPreview) renderedPreview.dataset.previewKey = previewKey;
        startPdfPreviews(body);
        return;
      }

      // 2. Insights Tab — Calm Editorial Prose, Zero Nested Boxes
      if (docResultTab === 'insights') {
        const missingMessage = unavailableResultMessage(job, 'GenerateDocumentInsightsExecutor', 'Insight generation',
          'No insight summary is stored for this selection.');
        const documentInsights = segs.filter(s => cleanInsightText(s.insightSummary));
        if (documentInsights.length === 0) {
          sliceRow.innerHTML = '';
          const prose = cleanInsightText(job.packetSummary);
          const lastPage = job.totalPages || Math.max(0, ...segs.map(s => s.endPage || 0));
          const originalUrl = `/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/original`;
          body.innerHTML = `
            <div class="content-column">
              <article class="slice-section expanded">
                <div class="slice-section-head"><div>
                  <h3 class="slice-section-title">File Insights</h3>
                  <div class="slice-section-sub">${escapeHtml(job.fileName)} · Complete File${lastPage ? ` · Pages 1–${lastPage}` : ''}</div>
                </div>
                ${prose ? `<a class="text-link-btn" href="${originalUrl}" target="_blank" rel="noopener noreferrer">Open File ↗</a>` : ''}
                </div>
                <div class="slice-card-body"><p class="insight-prose" style="margin:0;">${escapeHtml(prose ||
                  missingMessage)}</p></div>
              </article>
            </div>`;
          return;
        }
        const distinctLangs = [...new Set(segs.map(s => s.detectedLanguage).filter(Boolean).map(l => l.toUpperCase()))];
        const translatedTargets = [...new Set(segs.filter(s => s.wasTranslated === true)
          .map(s => s.targetLanguage).filter(Boolean).map(l => l.toUpperCase()))];

        const summaryLineHtml = `
          <div class="packet-summary-line">
            <span>${job.totalPages || 0} pages · ${documentInsights.length} document${documentInsights.length === 1 ? '' : 's'}${distinctLangs.length ? ` · ${escapeHtml(distinctLangs.join(', '))}` : ''}${translatedTargets.length ? ` → ${escapeHtml(translatedTargets.join(', '))} translated` : ''} · ${totalEntitiesCount} entities extracted</span>
            <span>Harness v${job.appliedHarnessVersion || 1}</span>
          </div>
        `;

        const sectionsHtml = activeSegs.filter(s => cleanInsightText(s.insightSummary)).map((s, idx) => {
          const docName = s.identifiedDocumentName || s.documentTypeName || `Document ${s.segmentIndex + 1}`;
          const prose = cleanInsightText(s.insightSummary);
          const langNote = s.wasTranslated
            ? ` · Translated ${(s.detectedLanguage || 'Unknown').toUpperCase()} → ${(s.targetLanguage || 'Unknown').toUpperCase()}`
            : '';
          const isExpanded = idx === 0;

          return `
            <article class="slice-section ${isExpanded ? 'expanded' : ''}">
              <div class="slice-section-head" data-click="toggleSliceCard_self">
                <div>
                  <h3 class="slice-section-title">${escapeHtml(docName)}</h3>
                  <div class="slice-section-sub">Document ${s.segmentIndex + 1} · Pages ${s.startPage}–${s.endPage}${escapeHtml(langNote)}</div>
                </div>
                <div style="display:flex;align-items:center;gap:8px;">
                  <button type="button" class="text-link-btn" data-click="jumpToSlicePdf_arg_event" data-args="[${s.segmentIndex}]">Pages ${s.startPage}–${s.endPage} ↗</button>
                  <button type="button" class="slice-chevron-btn" aria-label="Expand or collapse document insight">
                    <svg width="15" height="15" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8">
                      <path d="M5.5 7.5L10 12l4.5-4.5" stroke-linecap="round" stroke-linejoin="round"/>
                    </svg>
                  </button>
                </div>
              </div>
              <div class="slice-card-body">
                <p class="insight-prose" style="margin:0;">${escapeHtml(prose)}</p>
              </div>
            </article>
          `;
        }).join('');

        const missingSelection = activeSegs.some(s => !cleanInsightText(s.insightSummary));
        body.innerHTML = `<div class="content-column">${summaryLineHtml}<div class="slices-wide-grid">${sectionsHtml}</div>` +
          (missingSelection ? `<p>${escapeHtml(missingMessage)}</p>` : '') + '</div>';
        return;
      }

      // 3. Entities Tab — Clean Scannable Table + Project Meridian Filter Pills
      if (docResultTab === 'entities') {
        const selectedEntities = activeSegs.flatMap(s => s.extractedEntities || []);
        const allEntities = selectedEntities.length ? selectedEntities : (job.extractedEntities || [])
          .filter(e => selectedSliceIndex === -1 || e.segmentIndex === selectedSliceIndex);

        if (allEntities.length === 0) {
          const message = unavailableResultMessage(job, 'ExtractEntitiesExecutor', 'Entity extraction',
            'No extracted entities are stored for this selection.');
          body.innerHTML = `<div class="content-column" style="color:var(--text-secondary);font-size:14px;">${escapeHtml(message)}</div>`;
          return;
        }

        const categories = ['ALL', ...new Set(allEntities.map(e => (e.category || 'Entity').toUpperCase()))];
        if (!categories.includes(selectedEntityCategory)) selectedEntityCategory = 'ALL';
        const filteredEntities = selectedEntityCategory === 'ALL'
          ? allEntities
          : allEntities.filter(e => (e.category || 'Entity').toUpperCase() === selectedEntityCategory);

        body.innerHTML = `
          <div class="content-column">
            <div class="filter-pill-bar">
              ${categories.map(cat => `
                <button type="button" class="filter-pill-btn ${selectedEntityCategory === cat ? 'active' : ''}" data-click="setEntityCategoryFilter_arg" data-args="[${escapeJsArg(cat)}]">
                  ${cat === 'ALL' ? `All (${allEntities.length})` : escapeHtml(cat.charAt(0) + cat.slice(1).toLowerCase())}
                </button>
              `).join('')}
            </div>

            <table class="clean-table">
              <thead>
                <tr>
                  <th>Entity</th>
                  <th>Value</th>
                  <th>Category</th>
                  <th>Model score</th>
                  <th></th>
                </tr>
              </thead>
              <tbody>
                ${filteredEntities.map(e => {
                  const info = DocIntelEvidence.describe(e, 1);
                  return `
                    <tr>
                      <td style="color:var(--text-secondary);">${escapeHtml(e.name)}</td>
                      <td style="font-weight:600;color:var(--text-primary);">${escapeHtml(e.value)}</td>
                      <td style="color:var(--text-secondary);font-size:12.5px;">${escapeHtml(e.category || 'Entity')}</td>
                      <td title="${escapeHtml(info.tooltip)}" style="color:var(--text-secondary);font-size:12.5px;">${info.modelPercent === null ? 'Unknown' : info.modelPercent + '%'}</td>
                      <td style="text-align:right;"><button type="button" class="text-link-btn" data-click="copyTextValue_arg" data-args="[${escapeJsArg(e.value)}]">Copy</button></td>
                    </tr>
                  `;
                }).join('')}
              </tbody>
            </table>
          </div>
        `;
        return;
      }

      // 3b. Document Graph by Entities Tab — Interactive Entity-Filtered Knowledge Graph
      if (docResultTab === 'graph') {
        renderDocumentGraphTab(body, job);
        return;
      }

      // 4. Structured Data Tab — Clean Definition List + Tables
      if (docResultTab === 'structured') {
        const slicesWithData = activeSegs.filter(s => s.dataExtraction && ((s.dataExtraction.fields && s.dataExtraction.fields.length > 0) || (s.dataExtraction.tables && s.dataExtraction.tables.length > 0)));
        if (slicesWithData.length === 0) {
          const message = unavailableResultMessage(job, 'ExtractDocumentDataExecutor', 'Structured data extraction',
            'No structured fields or tables are stored for this selection.');
          body.innerHTML = `
            <div class="content-column" style="color:var(--text-secondary);font-size:14px;">
              ${escapeHtml(message)}
            </div>
          `;
          return;
        }

        body.innerHTML = `<div class="content-column"><div class="slices-wide-grid">` + slicesWithData.map((s, idx) => {
          const de = s.dataExtraction;
          const isExpanded = idx === 0;
          const fieldsHtml = (de.fields && de.fields.length > 0) ? `
            <dl class="kv-grid">
              ${de.fields.map(f => `
                <div class="kv-item">
                  <dt>${escapeHtml(f.fieldName)} <span style="color:var(--text-muted);" title="${escapeHtml(DocIntelEvidence.describe(f, 100).tooltip)}">(${formatModelScore(f, 100)})</span></dt>
                  <dd>${escapeHtml(f.fieldValue || '—')}${renderValueProvenance(f, job, 100)}</dd>
                </div>
              `).join('')}
            </dl>
          ` : '';

          const tablesHtml = (de.tables || []).map(t => {
            const rows = t.rows || [];
            const colNames = rows.length > 0 ? (rows[0].fields || []).map(c => c.fieldName) : [];
            return `
              <div style="margin-top:16px;overflow-x:auto;">
                <div style="font-size:13px;font-weight:600;color:var(--text-primary);margin-bottom:8px;">${escapeHtml(t.tableName)}</div>
                ${rows.length > 0 ? `
                  <table class="clean-table">
                    <thead>
                      <tr>${colNames.map(cn => `<th>${escapeHtml(cn)}</th>`).join('')}</tr>
                    </thead>
                    <tbody>
                      ${rows.map(r => `
                        <tr>${(r.fields || []).map(cell => `<td title="${escapeHtml(DocIntelEvidence.describe(cell, 100).tooltip)}">${escapeHtml(cell.fieldValue)}${renderValueProvenance(cell, job, 100)}</td>`).join('')}</tr>
                      `).join('')}
                    </tbody>
                  </table>
                ` : '<div style="font-size:12.5px;color:var(--text-muted);">No rows matched in this slice.</div>'}
              </div>
            `;
          }).join('');

          return `
            <section class="slice-section ${isExpanded ? 'expanded' : ''}">
              <div class="slice-section-head" data-click="toggleSliceCard_self">
                <div>
                  <h3 class="slice-section-title">${escapeHtml(de.documentName)}</h3>
                  <div class="slice-section-sub">Slice ${s.segmentIndex + 1} · Pages ${escapeHtml(de.pageRange)}</div>
                </div>
                <div style="display:flex;align-items:center;gap:8px;">
                  <button type="button" class="text-link-btn" data-click="jumpToSlicePdf_arg_event" data-args="[${s.segmentIndex}]">View PDF ↗</button>
                  <button type="button" class="slice-chevron-btn" aria-label="Expand or collapse slice">
                    <svg width="15" height="15" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8">
                      <path d="M5.5 7.5L10 12l4.5-4.5" stroke-linecap="round" stroke-linejoin="round"/>
                    </svg>
                  </button>
                </div>
              </div>
              <div class="slice-card-body">
                ${fieldsHtml}
                ${tablesHtml}
              </div>
            </section>
          `;
        }).join('') + `</div></div>`;
        return;
      }

      // 5. Individual Prompt Column Tab (`prompt:<columnId>`) — Clean Outcome Only
      if (docResultTab.startsWith('prompt:')) {
        const targetColumnId = docResultTab.substring('prompt:'.length);
        const matchingSliceEntries = activePromptResults
          .filter(r => r.columnId === targetColumnId && r.filterMatched)
          .map(result => ({
            slice: activeSegs.find(s => s.segmentIndex === result.segmentIndex ||
              (s.promptColumnResults || []).includes(result)),
            result
          }));
        const missingMessage = unavailableResultMessage(job, 'EvaluatePromptColumnsExecutor', 'Custom analysis',
          'No custom result is stored for this selection.');

        const sliceCardsHtml = matchingSliceEntries.map(({ slice, result }, idx) => {
          const docName = slice
            ? slice.identifiedDocumentName || slice.documentTypeName || `Slice ${slice.segmentIndex + 1}`
            : job.fileName || 'File Result';
          const cleanedOutcome = String(result.resultText || '')
            .replace(/^Summary\s*\([^)]*\):\s*/i, '')
            .replace(/^(?:Summarize the contents of|Extract the most important[^'\n]*from|Verify contract identifiers[^'\n]*in)\s+/i, '')
            .replace(/\s*in a clear and concise manner\.?\s*Focus on key points, main arguments, and important conclusions\.?/gi, '')
            .replace(/\s*as concise bullet points\.?/gi, '')
            .trim();
          const isExpanded = idx === 0;

          return `
            <article class="slice-section ${isExpanded ? 'expanded' : ''}">
              <div class="slice-section-head" data-click="toggleSliceCard_self">
                <div>
                  <h3 class="slice-section-title">${escapeHtml(docName)}</h3>
                  <div class="slice-section-sub">${slice ? `Pages ${slice.startPage}–${slice.endPage}` : 'Complete File'}</div>
                </div>
                <div style="display:flex;align-items:center;gap:8px;">
                  ${slice ? `<button type="button" class="text-link-btn" data-click="jumpToSlicePdf_arg_event" data-args="[${slice.segmentIndex}]">Pages ${slice.startPage}–${slice.endPage} ↗</button>` : ''}
                  <button type="button" class="slice-chevron-btn" aria-label="Expand or collapse slice">
                    <svg width="15" height="15" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8">
                      <path d="M5.5 7.5L10 12l4.5-4.5" stroke-linecap="round" stroke-linejoin="round"/>
                    </svg>
                  </button>
                </div>
              </div>
              <div class="slice-card-body">
                <p class="insight-prose" style="margin:0;">${escapeHtml(cleanedOutcome || missingMessage)}</p>
              </div>
            </article>
          `;
        }).join('');

        body.innerHTML = `<div class="content-column"><div class="slices-wide-grid">${sliceCardsHtml ||
          `<p>${escapeHtml(missingMessage)}</p>`}</div></div>`;
        return;
      }

      // 6. Metadata Tab — Clean Provenance Table + Collapsible Engineering Trace
      if (docResultTab === 'metadata') {
        const trace = job.executionTrace || [];
        body.innerHTML = `
          <div class="content-column">
            <dl class="kv-grid" style="padding-bottom:20px;border-bottom:1px solid var(--line);">
              <div class="kv-item"><dt>Job ID</dt><dd style="font-family:monospace;font-size:12.5px;">${escapeHtml(job.jobId)}</dd></div>
              <div class="kv-item"><dt>Workspace</dt><dd>${escapeHtml(job.tenantId)}</dd></div>
              <div class="kv-item"><dt>Status</dt><dd>${escapeHtml(documentJobStatus(job).label)}</dd></div>
              <div class="kv-item"><dt>Harness Version</dt><dd>v${job.appliedHarnessVersion || 1}</dd></div>
              <div class="kv-item"><dt>Language Model</dt><dd>${escapeHtml(job.modelIdUsed || 'gpt-5-mini')}</dd></div>
            </dl>

            <h3 class="slice-section-title" style="margin:24px 0 12px 0;">Document Slices</h3>
            <table class="clean-table" style="margin-bottom:28px;">
              <thead>
                <tr><th>Slice</th><th>Pages</th><th>Identified Document</th><th>Matched Rule</th><th>Footer Signature</th><th>PDF</th></tr>
              </thead>
              <tbody>
                ${activeSegs.map(s => `
                  <tr>
                    <td>#${s.segmentIndex + 1}</td>
                    <td>${s.startPage}–${s.endPage}</td>
                    <td style="font-weight:600;">${escapeHtml(s.identifiedDocumentName || s.documentTypeName || 'Undefined')}</td>
                    <td style="color:var(--text-secondary);font-size:12.5px;">${escapeHtml(s.matchedIdentificationRule || '—')}</td>
                    <td style="font-family:monospace;font-size:12px;color:var(--text-secondary);">${escapeHtml((s.footers || []).join(', ') || '—')}</td>
                    <td><a href="/api/harness/tenants/${encodeURIComponent(job.tenantId)}/jobs/${encodeURIComponent(job.jobId)}/slices/${s.segmentIndex}/pdf" target="_blank" class="text-link-btn">Open ↗</a></td>
                  </tr>
                `).join('')}
              </tbody>
            </table>

            <details>
              <summary style="cursor:pointer;font-size:13px;font-weight:600;color:var(--text-secondary);user-select:none;">Pipeline Execution Trace (${trace.length} steps)</summary>
              <table class="clean-table" style="margin-top:12px;">
                <thead>
                  <tr><th>Executor Step</th><th>Status</th><th>Model / Service</th><th>Duration</th><th>Notes</th></tr>
                </thead>
                <tbody>
                  ${trace.map(t => `
                    <tr>
                      <td style="font-weight:500;">${escapeHtml(t.executorName)}</td>
                      <td>${executionTraceStatus(t)}</td>
                      <td style="color:var(--text-secondary);">${escapeHtml(t.modelIdUsed || 'Deterministic')}</td>
                      <td style="font-family:monospace;font-size:12px;">${t.durationMs ? t.durationMs.toFixed(1) + ' ms' : '0 ms'}</td>
                      <td style="color:var(--text-secondary);font-size:12.5px;">${escapeHtml(t.notes || '')}</td>
                    </tr>
                  `).join('')}
                </tbody>
              </table>
            </details>
          </div>
        `;
      }
    }

    function graphEntityPairKey(name, value) {
      const escape = text => text.replaceAll('%', '%25').replaceAll('|', '%7C');
      return `${escape(name)}||${escape(value)}`;
    }

    function computeFilteredGraphData() {
      const completedJobs = tenantJobs.filter(j => j.status === 'Completed');

      const allEntityNamesMap = new Map();
      const allEntityValuePairsMap = new Map();

      completedJobs.forEach(j => {
        const seenInJob = new Set();
        (j.segments || []).forEach(s => {
          (s.extractedEntities || []).forEach(e => {
            if (!e || !e.name || !e.value) return;
            const eName = e.name.trim();
            const eVal = e.value.trim();
            const pairKey = graphEntityPairKey(eName, eVal);
            if (seenInJob.has(pairKey)) return;
            seenInJob.add(pairKey);

            allEntityNamesMap.set(eName, (allEntityNamesMap.get(eName) || 0) + 1);
            if (!allEntityValuePairsMap.has(pairKey)) {
              allEntityValuePairsMap.set(pairKey, { name: eName, value: eVal, category: e.category || 'Entity', count: 0 });
            }
            allEntityValuePairsMap.get(pairKey).count++;
          });
        });
      });

      const docNodes = [];
      const entityNodesMap = new Map();
      const edges = [];
      const q = (graphEntitySearchQuery || '').trim().toLowerCase();

      completedJobs.forEach(j => {
        const docNodeId = `doc:${j.jobId}`;
        const cleanFileName = (j.fileName || 'Document').replace(/\.pdf$/i, '');
        const segs = j.segments || [];
        const docTypesSummary = [...new Set(segs.map(s => s.identifiedDocumentName || s.documentTypeName).filter(Boolean))].join(', ');

        // Collect all entities present on this document
        const docEntities = [];
        const docEntityNamesSet = new Set();
        const docEntityPairsSet = new Set();

        segs.forEach(s => {
          (s.extractedEntities || []).forEach(e => {
            if (!e || !e.name || !e.value) return;
            const eName = e.name.trim();
            const eVal = e.value.trim();
            const pairKey = graphEntityPairKey(eName, eVal);
            docEntityNamesSet.add(eName);
            docEntityPairsSet.add(pairKey);
            docEntities.push({ eName, eVal, pairKey, category: (e.category || 'Entity').toUpperCase() });
          });
        });

        // Evaluate strict AND condition across all selected Entity Types and Entity Values
        for (const reqName of graphSelectedEntityNames) {
          if (!docEntityNamesSet.has(reqName)) return;
        }
        for (const reqPair of graphSelectedEntityValues) {
          if (!docEntityPairsSet.has(reqPair)) return;
        }
        if (q && !cleanFileName.toLowerCase().includes(q) && !docTypesSummary.toLowerCase().includes(q) &&
          !docEntities.some(entity => entity.eName.toLowerCase().includes(q) || entity.eVal.toLowerCase().includes(q))) return;

        const hasAnyFilter = graphSelectedEntityNames.size > 0 || graphSelectedEntityValues.size > 0;
        const matchingEntitiesForDoc = [];

        docEntities.forEach(({ eName, eVal, pairKey, category }) => {
          if (hasAnyFilter) {
            const matchedByType = graphSelectedEntityNames.has(eName);
            const matchedByValue = graphSelectedEntityValues.has(pairKey);
            if (!matchedByType && !matchedByValue) {
              return;
            }
          }

          const entNodeId = `ent:${pairKey}`;
          if (!entityNodesMap.has(entNodeId)) {
            entityNodesMap.set(entNodeId, {
              id: entNodeId,
              type: 'entity',
              pairKey,
              entityName: eName,
              entityValue: eVal,
              category,
              connectedDocs: []
            });
          }

          matchingEntitiesForDoc.push(entNodeId);
        });

        const uniqEntIds = [...new Set(matchingEntitiesForDoc)];
        if (uniqEntIds.length > 0) {
          const docNode = {
            id: docNodeId,
            type: 'doc',
            jobId: j.jobId,
            fileName: cleanFileName,
            sliceIndex: 0,
            docName: cleanFileName,
            pageRange: `${j.totalPages || segs.length} pp · ${segs.length} slice${segs.length === 1 ? '' : 's'}`,
            docTypesSummary,
            entityIds: uniqEntIds,
            isCurrentJob: j.jobId === selectedJobId
          };
          docNodes.push(docNode);

          uniqEntIds.forEach(entNodeId => {
            edges.push({ source: docNodeId, target: entNodeId });
            const entNode = entityNodesMap.get(entNodeId);
            if (entNode) {
              entNode.connectedDocs.push(docNode);
            }
          });
        }
      });

      // Sort entities so shared cross-document entities appear first
      const sortedEntities = [...entityNodesMap.values()].sort((a, b) => b.connectedDocs.length - a.connectedDocs.length);
      const sortedPairs = [...allEntityValuePairsMap.entries()]
        .map(([pairKey, info]) => ({ pairKey, ...info }))
        .sort((a, b) => b.count - a.count);

      return {
        allEntityNames: [...allEntityNamesMap.entries()].map(([name, count]) => ({ name, count })),
        allEntityValuePairs: sortedPairs,
        docNodes,
        entityNodes: sortedEntities,
        edges
      };
    }

    function renderGraphEntityOptions(pairs) {
      const query = graphEntityDropdownSearch.trim().toLowerCase();
      const matches = pairs.filter(pair => !query || pair.name.toLowerCase().includes(query) ||
        pair.value.toLowerCase().includes(query));
      const groupedByType = new Map();
      const countsByType = new Map();
      matches.forEach(pair => countsByType.set(pair.name, (countsByType.get(pair.name) || 0) + 1));
      matches.slice(0, maximumGraphEntityOptions).forEach(p => {
        if (!groupedByType.has(p.name)) groupedByType.set(p.name, []);
        groupedByType.get(p.name).push(p);
      });
      const groups = [...groupedByType.entries()].map(([groupName, items]) => {
        const isTypeSelected = graphSelectedEntityNames.has(groupName);
        return `
          <div class="entity-group-col" data-group="${escapeHtml(groupName)}">
            <button type="button" class="entity-group-header" aria-pressed="${isTypeSelected}" data-click="toggleGraphEntityNameFilter_arg_event" data-args="[${escapeJsArg(groupName)}]">
              <span>${isTypeSelected ? '☑' : '☐'} Has ${escapeHtml(groupName)}</span>
              <span class="entity-option-count">${countsByType.get(groupName)}</span>
            </button>
            ${items.map(item => {
              const checked = graphSelectedEntityValues.has(item.pairKey);
              return `<button type="button" class="entity-value-option ${checked ? 'checked' : ''}" aria-pressed="${checked}" data-search="${escapeHtml(item.value)}" data-click="toggleGraphEntityValueSelection_arg_event" data-args="[${escapeJsArg(item.pairKey)}]">
                <span style="white-space:nowrap;overflow:hidden;text-overflow:ellipsis;">${checked ? '✓ ' : ''}${escapeHtml(item.value)}</span>
                <span class="entity-option-count">${item.count} doc${item.count === 1 ? '' : 's'}</span>
              </button>`;
            }).join('')}
          </div>`;
      }).join('');
      const notice = matches.length > maximumGraphEntityOptions
        ? `<p class="entity-filter-note" role="status">Showing ${maximumGraphEntityOptions} of ${matches.length} values. Search to find any other entity or value.</p>` : '';
      return notice + (groups || '<p class="entity-filter-note">No matching extracted entities are available.</p>');
    }

    function renderDocumentGraphTab(body) {
      const data = computeFilteredGraphData();
      const totalSelected = graphSelectedEntityNames.size + graphSelectedEntityValues.size;

      const selectedTagsList = [
        ...[...graphSelectedEntityNames].map(name => `
          <span class="entity-selected-tag" data-click="stopPropagation">
            <span>Has ${escapeHtml(name)}</span>
            <button type="button" title="Remove" data-click="toggleGraphEntityNameFilter_arg_event" data-args="[${escapeJsArg(name)}]">✕</button>
          </span>
        `),
        ...[...graphSelectedEntityValues].map(pk => {
          const [eName, eVal] = pk.split('||').map(value => value.replaceAll('%7C', '|').replaceAll('%25', '%'));
          return `
            <span class="entity-selected-tag" data-click="stopPropagation">
              <span><strong>${escapeHtml(eName)}:</strong> ${escapeHtml(eVal)}</span>
              <button type="button" title="Remove" data-click="toggleGraphEntityValueSelection_arg_event" data-args="[${escapeJsArg(pk)}]">✕</button>
            </span>
          `;
        })
      ];
      const selectedTagsHtml = selectedTagsList.join('<span class="graph-filter-and">AND</span>');

      const groupsHtml = renderGraphEntityOptions(data.allEntityValuePairs);

      body.innerHTML = `
        <div class="graph-shell" data-click="closeGraphDropdown">
          <div class="graph-toolbar">
          <div class="graph-filter-actions">
          <div class="entity-multiselect-wrap" data-click="stopPropagation">
            <button type="button" class="ghost-action-btn entity-multiselect-box ${graphEntityDropdownOpen ? 'open' : ''}"
              id="entityMultiSelectBox" aria-expanded="${graphEntityDropdownOpen}" aria-controls="entityMultiSelectPopover"
              ${data.allEntityValuePairs.length ? '' : 'disabled'}
              data-click="toggleEntityMultiSelectDropdown_event">Filter Entities${totalSelected ? ` (${totalSelected})` : ''} <span aria-hidden="true">▾</span></button>

            <div class="entity-multiselect-popover ${graphEntityDropdownOpen ? 'open' : ''}" id="entityMultiSelectPopover">
              <div class="entity-popover-search">
                <input type="search" id="entityPopoverSearchInput" aria-label="Search Entities" placeholder="Search entities or values" value="${escapeHtml(graphEntityDropdownSearch)}" data-input="filterEntityPopoverOptions_value" />
              </div>
              <p class="entity-filter-note">Documents must match all selected filters.</p>
              <div class="entity-popover-list" id="entityPopoverList">
                ${groupsHtml || '<p class="entity-filter-note">No extracted entities are available yet.</p>'}
              </div>
            </div>
          </div>
          <button type="button" class="text-link-btn" data-click="resetGraphFilters_event"${totalSelected ? '' : ' disabled'}>Clear Filters</button>
          </div>
          <span class="graph-status" id="graphInfoStripContainer" role="status"></span>
          <button type="button" class="ghost-action-btn" id="graphDetailsToggle" aria-expanded="${graphDetailsOpen}"
            aria-controls="graphInspectorAside" data-click="toggleGraphDetails">Documents (${data.docNodes.length})</button>
          </div>
          <div class="graph-active-filters">${selectedTagsHtml || '<span class="graph-detail-meta">All Entities</span>'}</div>

          <div class="graph-workbench" id="graphWorkbenchContainer"></div>
        </div>
      `;

      if (graphEntityDropdownSearch) {
        filterEntityPopoverOptions(graphEntityDropdownSearch);
      }
      renderDocumentGraphCanvasOnly();
    }

    function setGraphDepthMode(mode) {
      graphDepthMode = mode;
      if (mode !== 'GLOBAL' && !graphFocusedNodeId && selectedJobId) {
        graphFocusedNodeId = `doc:${selectedJobId}`;
      }
      renderSelectedDocument();
    }

    function renderGraphDocumentLinks(documents) {
      const notice = documents.length > maximumGraphInspectorLinks
        ? `<p class="graph-detail-meta">Showing ${maximumGraphInspectorLinks} of ${documents.length} documents. Narrow entity filters or use Documents search to reach every document.</p>` : '';
      return notice + documents.slice(0, maximumGraphInspectorLinks).map(d => `
        <button type="button" class="graph-inspector-item" data-click="jumpFromGraphToSlice_arg_arg"
          data-args="[${escapeJsArg(d.jobId)},-1]">
          <span class="graph-document-name">${escapeHtml(d.docName)} <span aria-hidden="true">↗</span></span>
          <span class="graph-detail-meta">${escapeHtml(d.pageRange)}${d.docTypesSummary ? ' · ' + escapeHtml(d.docTypesSummary) : ''}</span>
        </button>
      `).join('');
    }

    function buildGraphCanvasWindow(docNodes, entityNodes, edges) {
      const allIds = new Set([...docNodes, ...entityNodes].map(node => node.id));
      const preferred = new Set([graphFocusedNodeId, selectedJobId ? `doc:${selectedJobId}` : null,
        ...[...graphSelectedEntityValues].map(pair => `ent:${pair}`)].filter(id => allIds.has(id)));
      const visible = new Set([...preferred].slice(0, maximumGraphCanvasNodes));
      const visibleEdges = [];
      for (const preferredPass of [true, false]) {
        for (const edge of edges) {
          if ((preferred.has(edge.source) || preferred.has(edge.target)) !== preferredPass) continue;
          if (visibleEdges.length >= maximumGraphCanvasEdges) break;
          const missing = [edge.source, edge.target].filter(id => !visible.has(id));
          if (visible.size + missing.length > maximumGraphCanvasNodes) continue;
          missing.forEach(id => visible.add(id));
          visibleEdges.push(edge);
        }
      }
      return {
        docNodes: docNodes.filter(node => visible.has(node.id)),
        entityNodes: entityNodes.filter(node => visible.has(node.id)),
        edges: visibleEdges,
        limited: visible.size < allIds.size || visibleEdges.length < edges.length,
        totalNodes: allIds.size,
        totalEdges: edges.length
      };
    }

    function renderDocumentGraphCanvasOnly(keepCanvas = false) {
      const container = document.getElementById('graphWorkbenchContainer');
      if (!container) return;

      const reuseCanvas = keepCanvas && graphDepthMode === 'GLOBAL' &&
        (!graphFocusedNodeId || graphCanvasNodeIds.has(graphFocusedNodeId)) &&
        document.getElementById('obsidianGraphCanvas')?.isConnected && graphCanvasRefresh;
      if (!reuseCanvas) stopGraphCanvas();

      const rawData = computeFilteredGraphData();
      let { docNodes, entityNodes, edges } = rawData;

      // Apply Obsidian-style Local Graph Depth (1-Hop or 2-Hop neighborhood around focused node)
      if (graphDepthMode !== 'GLOBAL' && graphFocusedNodeId) {
        const allowed = new Set([graphFocusedNodeId]);
        edges.forEach(ed => {
          if (ed.source === graphFocusedNodeId) allowed.add(ed.target);
          if (ed.target === graphFocusedNodeId) allowed.add(ed.source);
        });
        if (graphDepthMode === 'HOP2') {
          const hop1 = new Set(allowed);
          edges.forEach(ed => {
            if (hop1.has(ed.source)) allowed.add(ed.target);
            if (hop1.has(ed.target)) allowed.add(ed.source);
          });
        }
        docNodes = docNodes.filter(d => allowed.has(d.id));
        entityNodes = entityNodes.filter(e => allowed.has(e.id));
        edges = edges.filter(ed => allowed.has(ed.source) && allowed.has(ed.target));
      }

      const nodeById = new Map();
      docNodes.forEach(d => nodeById.set(d.id, d));
      entityNodes.forEach(e => nodeById.set(e.id, e));

      const focusedNode = graphFocusedNodeId ? nodeById.get(graphFocusedNodeId) : null;
      if (!focusedNode) graphFocusedNodeId = null;
      const canvasWindow = buildGraphCanvasWindow(docNodes, entityNodes, edges);
      const limitNotice = canvasWindow.limited
        ? `Canvas shows ${canvasWindow.docNodes.length + canvasWindow.entityNodes.length} of ${canvasWindow.totalNodes} nodes and ${canvasWindow.edges.length} of ${canvasWindow.totalEdges} connections. Narrow entity filters to see a smaller view.` : '';
      const infoStrip = document.getElementById('graphInfoStripContainer');
      if (infoStrip) infoStrip.textContent = `${edges.length} connection${edges.length === 1 ? '' : 's'}${limitNotice ? ' · Limited canvas' : ''}`;
      const detailsToggle = document.getElementById('graphDetailsToggle');
      if (detailsToggle) {
        detailsToggle.textContent = graphDetailsOpen ? 'Hide Details' : `Documents (${docNodes.length})`;
        detailsToggle.setAttribute('aria-expanded', String(graphDetailsOpen));
        detailsToggle.disabled = docNodes.length === 0 && !graphDetailsOpen;
      }
      container.classList.toggle('details-open', graphDetailsOpen);

      let inspectorHtml = '';

      if (focusedNode && focusedNode.type === 'entity') {
        inspectorHtml = `
          <span class="graph-detail-meta">${escapeHtml(focusedNode.entityName)}</span>
          <h2 class="graph-detail-title">${escapeHtml(focusedNode.entityValue)}</h2>
          <p class="graph-detail-meta">Connected Documents (${focusedNode.connectedDocs.length})</p>
          ${renderGraphDocumentLinks(focusedNode.connectedDocs)}
        `;
      } else if (focusedNode && focusedNode.type === 'doc') {
        const connectedEntities = focusedNode.entityIds.map(id => nodeById.get(id)).filter(Boolean);
        inspectorHtml = `
          <h2 class="graph-detail-title">${escapeHtml(focusedNode.docName)}</h2>
          <p class="graph-detail-meta">${escapeHtml(focusedNode.pageRange)}</p>
          <button type="button" class="ghost-action-btn" data-click="jumpFromGraphToSlice_arg_arg"
            data-args="[${escapeJsArg(focusedNode.jobId)},-1]">Open Document ↗</button>
          <h3 class="graph-detail-subtitle">Entities (${connectedEntities.length})</h3>
          ${connectedEntities.length > maximumGraphInspectorLinks ? `<p class="graph-detail-meta">Showing ${maximumGraphInspectorLinks} of ${connectedEntities.length} entities. Use entity filters to inspect other connections.</p>` : ''}
          ${connectedEntities.slice(0, maximumGraphInspectorLinks).map(e => `
            <button type="button" class="graph-inspector-item" data-click="focusGraphNode_arg" data-args="[${escapeJsArg(e.id)}]">
              <span class="graph-detail-meta">${escapeHtml(e.entityName)}</span>
              <span>${escapeHtml(e.entityValue)}</span>
            </button>
          `).join('')}
        `;
      } else {
        inspectorHtml = `
          <h2 class="graph-detail-title">Documents (${docNodes.length})</h2>
          <p class="graph-detail-meta">Open a document to view its results.</p>
          ${renderGraphDocumentLinks(docNodes)}
        `;
      }

      const inspectorContents = `<button type="button" class="graph-detail-close" aria-label="Close Graph Details"
        data-click="toggleGraphDetails">\u00d7</button>${inspectorHtml}`;
      if (reuseCanvas) {
        const inspector = document.getElementById('graphInspectorAside');
        inspector.innerHTML = inspectorContents;
        inspector.hidden = !graphDetailsOpen;
        graphCanvasRefresh();
        return;
      }

      const filtered = graphSelectedEntityNames.size > 0 || graphSelectedEntityValues.size > 0;
      container.innerHTML = `
        <div class="graph-canvas-card">
          ${limitNotice ? `<p class="graph-limit-notice" role="status">${escapeHtml(limitNotice)}</p>` : ''}
          ${docNodes.length === 0 ? `
            <div class="graph-empty">
              <h2>${filtered ? 'No Matching Connections' : 'No Connections Yet'}</h2>
              <p>${filtered ? 'Try removing one or more entity filters.'
                : 'Connections appear after entities are extracted from completed documents.'}</p>
              ${filtered ? '<button type="button" class="ghost-action-btn" data-click="resetGraphFilters">Clear Filters</button>' : ''}
            </div>
          ` : `
            <canvas id="obsidianGraphCanvas" tabindex="0" aria-label="${escapeHtml(graphCanvasDescription)}"></canvas>
          `}

          ${docNodes.length ? `
          <div class="graph-floating-hud">
            <div class="graph-legend">
              <span><i class="legend-dot graph-document-dot"></i>Documents</span>
              <span><i class="legend-dot graph-entity-dot"></i>Entities</span>
            </div>
            <div class="graph-zoom-bar">
              <button type="button" class="text-link-btn" aria-label="Zoom Out" data-click="zoomGraphBy_arg" data-args="[-0.2]">−</button>
              <span id="graphZoomLabel">${Math.round(graphZoom * 100)}%</span>
              <button type="button" class="text-link-btn" aria-label="Zoom In" data-click="zoomGraphBy_arg" data-args="[0.2]">+</button>
              <button type="button" class="text-link-btn" data-click="zoomGraphBy_arg" data-args="[0]">Reset View</button>
            </div>
          </div>` : ''}
        </div>

        <aside class="graph-inspector-card" id="graphInspectorAside" aria-label="Graph Details"${graphDetailsOpen ? '' : ' hidden'}>
          ${inspectorContents}
        </aside>
      `;

      if (docNodes.length > 0) {
        initObsidianForceCanvas(canvasWindow.docNodes, canvasWindow.entityNodes, canvasWindow.edges);
      }
    }

    function initObsidianForceCanvas(docNodes, entityNodes, edges) {
      const canvas = document.getElementById('obsidianGraphCanvas');
      if (!canvas) return;
      const rect = canvas.getBoundingClientRect();
      const dpr = window.devicePixelRatio || 1;
      const width = Math.max(1, rect.width);
      const height = Math.max(1, rect.height);
      canvas.width = Math.round(width * dpr);
      canvas.height = Math.round(height * dpr);
      const ctx = canvas.getContext('2d');
      if (!ctx) throw new Error('The graph canvas could not be initialized.');
      graphCanvasNodeIds = new Set([...docNodes, ...entityNodes].map(node => node.id));
      const fontFamily = getComputedStyle(canvas).fontFamily;
      const readPalette = () => {
        const style = getComputedStyle(canvas);
        const color = name => style.getPropertyValue(name).trim();
        return {
          document: color('--graph-document'), entity: color('--graph-entity'),
          edge: color('--graph-edge'), edgeDim: color('--graph-edge-dim'),
          halo: color('--graph-halo'), labelBackground: color('--graph-label-bg'),
          text: color('--text-heading'), surface: color('--surface-soft')
        };
      };
      let palette = readPalette();
      const labelWidth = Math.max(70, Math.min(180, width * 0.35));
      const previousSize = graphLayoutSize;
      const savedPosition = id => {
        const saved = graphCustomNodePositions[id];
        if (!saved) return null;
        if (previousSize?.width === width && previousSize?.height === height) return saved;
        return previousSize
          ? { x: saved.x * width / previousSize.width, y: saved.y * height / previousSize.height }
          : saved;
      };
      graphLayoutSize = { width, height };

      const cx = width / 2;
      const cy = height / 2;
      const baseNodeRadius = Math.min(10, Math.max(3,
        Math.sqrt(width * height / Math.max(1, docNodes.length + entityNodes.length)) * 0.2));

      const simNodes = [];
      const simNodeMap = new Map();

      // Seed entity hubs in an inner ring and documents around their primary entity hub
      entityNodes.forEach((e, i) => {
        const angle = (2 * Math.PI * i) / Math.max(1, entityNodes.length);
        const ringR = Math.min(width, height) * 0.24;
        const saved = savedPosition(e.id);
        const degree = e.connectedDocs.length;
        const node = {
          id: e.id,
          type: 'entity',
          label: e.entityValue,
          subLabel: e.entityName,
          degree,
          radius: baseNodeRadius * Math.min(1.6, 1 + Math.sqrt(degree) * 0.1),
          x: saved ? saved.x : cx + Math.cos(angle) * ringR + ((i % 3) - 1) * 18,
          y: saved ? saved.y : cy + Math.sin(angle) * ringR + ((i % 2) - 0.5) * 18,
          vx: 0,
          vy: 0,
          raw: e
        };
        simNodes.push(node);
        simNodeMap.set(node.id, node);
      });

      docNodes.forEach((d, i) => {
        const angle = Math.PI + (2 * Math.PI * i) / Math.max(1, docNodes.length);
        const outerR = Math.min(width, height) * 0.37;
        const saved = savedPosition(d.id);
        const degree = d.entityIds.length;
        const node = {
          id: d.id,
          type: 'doc',
          label: d.docName,
          subLabel: d.pageRange,
          degree,
          radius: baseNodeRadius,
          x: saved ? saved.x : cx + Math.cos(angle) * outerR,
          y: saved ? saved.y : cy + Math.sin(angle) * outerR,
          vx: 0,
          vy: 0,
          raw: d
        };
        simNodes.push(node);
        simNodeMap.set(node.id, node);
      });

      const simEdges = edges
        .map(ed => ({
          source: simNodeMap.get(ed.source),
          target: simNodeMap.get(ed.target)
        }))
        .filter(ed => ed.source && ed.target);

      const preservedLayout = previousSize?.width === width && previousSize?.height === height &&
        simNodes.every(node => graphCustomNodePositions[node.id]);
      let alpha = preservedLayout ? 0 : 1;
      let draggedNode = null;
      let isPanning = false;
      let startMouseX = 0;
      let startMouseY = 0;
      let pointerMoved = false;
      let keyboardFocusNodeId = null;

      function screenToWorld(sx, sy) {
        return {
          x: (sx - cx - graphPanX) / graphZoom + cx,
          y: (sy - cy - graphPanY) / graphZoom + cy
        };
      }

      function findNodeAt(sx, sy) {
        const pt = screenToWorld(sx, sy);
        for (let i = simNodes.length - 1; i >= 0; i--) {
          const n = simNodes[i];
          const hitR = (n.radius + 5) / Math.sqrt(graphZoom);
          const dx = pt.x - n.x;
          const dy = pt.y - n.y;
          if (dx * dx + dy * dy <= hitR * hitR) {
            return n;
          }
        }
        return null;
      }

      function stepPhysics() {
        if (alpha < 0.008 && !(draggedNode && pointerMoved)) return;

        // 1. Repulsion between nodes
        const repulsion = 1450;
        for (let i = 0; i < simNodes.length; i++) {
          const a = simNodes[i];
          for (let j = i + 1; j < simNodes.length; j++) {
            const b = simNodes[j];
            let dx = b.x - a.x;
            let dy = b.y - a.y;
            let distSq = dx * dx + dy * dy;
            if (distSq < 9) {
              dx = (Math.random() - 0.5) * 2;
              dy = (Math.random() - 0.5) * 2;
              distSq = 9;
            }
            if (distSq > 95000) continue; // spatial cutoff
            const dist = Math.sqrt(distSq);
            const force = (repulsion * alpha) / distSq;
            const fx = (dx / dist) * force;
            const fy = (dy / dist) * force;
            a.vx -= fx;
            a.vy -= fy;
            b.vx += fx;
            b.vy += fy;
          }
        }

        // 2. Link spring attraction
        const targetDist = Math.min(240, Math.max(180, width * 0.4));
        const springK = 0.048 * alpha;
        for (let i = 0; i < simEdges.length; i++) {
          const e = simEdges[i];
          const dx = e.target.x - e.source.x;
          const dy = e.target.y - e.source.y;
          const dist = Math.sqrt(dx * dx + dy * dy) || 1;
          const delta = (dist - targetDist) * springK;
          const fx = (dx / dist) * delta;
          const fy = (dy / dist) * delta;
          e.source.vx += fx;
          e.source.vy += fy;
          e.target.vx -= fx;
          e.target.vy -= fy;
        }

        // 3. Center gravity + integration
        const centerK = 0.014 * alpha;
        for (let i = 0; i < simNodes.length; i++) {
          const n = simNodes[i];
          if (n === draggedNode) {
            n.vx = 0;
            n.vy = 0;
            continue;
          }
          n.vx += (cx - n.x) * centerK;
          n.vy += (cy - n.y) * centerK;
          n.vx *= 0.78;
          n.vy *= 0.78;
          n.x += n.vx;
          n.y += n.vy;
          const margin = Math.max(12, n.radius + 5);
          n.x = Math.min(width - margin, Math.max(margin, n.x));
          n.y = Math.min(height - 56, Math.max(24, n.y));
          graphCustomNodePositions[n.id] = { x: n.x, y: n.y };
        }

        alpha *= 0.965;
      }

      function drawFrame() {
        graphSimAnimFrame = null;
        if (!canvas.isConnected || workspaceMode !== 'graph') return;
        stepPhysics();

        ctx.save();
        ctx.scale(dpr, dpr);
        ctx.clearRect(0, 0, width, height);

        ctx.translate(cx + graphPanX, cy + graphPanY);
        ctx.scale(graphZoom, graphZoom);
        ctx.translate(-cx, -cy);

        const activeAnchorId = graphHoveredNodeId || keyboardFocusNodeId || graphFocusedNodeId;
        const neighborSet = new Set();
        if (activeAnchorId) {
          neighborSet.add(activeAnchorId);
          for (let i = 0; i < simEdges.length; i++) {
            const ed = simEdges[i];
            if (ed.source.id === activeAnchorId) neighborSet.add(ed.target.id);
            if (ed.target.id === activeAnchorId) neighborSet.add(ed.source.id);
          }
        }

        // Draw edges
        for (let i = 0; i < simEdges.length; i++) {
          const ed = simEdges[i];
          const isHigh = activeAnchorId && (ed.source.id === activeAnchorId || ed.target.id === activeAnchorId);
          ctx.beginPath();
          ctx.moveTo(ed.source.x, ed.source.y);
          ctx.lineTo(ed.target.x, ed.target.y);
          if (isHigh) {
            ctx.strokeStyle = palette.entity;
            ctx.lineWidth = 1.6 / Math.sqrt(graphZoom);
          } else if (activeAnchorId) {
            ctx.strokeStyle = palette.edgeDim;
            ctx.lineWidth = 0.75 / Math.sqrt(graphZoom);
          } else {
            ctx.strokeStyle = palette.edge;
            ctx.lineWidth = (simNodes.length > 200 ? 0.55 : 1.2) / Math.sqrt(graphZoom);
          }
          ctx.globalAlpha = activeAnchorId || simNodes.length <= 200 ? 1 : 0.35;
          ctx.stroke();
        }
        ctx.globalAlpha = 1;

        // Draw nodes and interaction-only labels.
        for (let i = 0; i < simNodes.length; i++) {
          const n = simNodes[i];
          const isSelectedEntity = n.type === 'entity' && graphSelectedEntityValues.has(n.id.substring(4));
          const showLabel = n.id === graphHoveredNodeId || n.id === keyboardFocusNodeId ||
            n.id === graphFocusedNodeId || isSelectedEntity;
          const isDimmed = activeAnchorId && !neighborSet.has(n.id) && !showLabel;
          const isFocusedOrHovered = n.id === activeAnchorId;

          ctx.save();
          ctx.globalAlpha = isDimmed ? 0.2 : 1;

          // Subtle halo on entity hubs or active nodes
          if (isFocusedOrHovered || (n.type === 'entity' && n.degree >= 3)) {
            ctx.beginPath();
            ctx.arc(n.x, n.y, n.radius + (isFocusedOrHovered ? 4 : 2.5), 0, Math.PI * 2);
            ctx.fillStyle = palette.halo;
            ctx.fill();
          }

          ctx.beginPath();
          if (n.type === 'doc') ctx.rect(n.x - n.radius, n.y - n.radius, n.radius * 2, n.radius * 2);
          else ctx.arc(n.x, n.y, n.radius, 0, Math.PI * 2);
          ctx.fillStyle = n.type === 'doc' ? palette.document : palette.entity;
          ctx.fill();
          ctx.lineWidth = isFocusedOrHovered ? 1.8 : 1.0;
          ctx.strokeStyle = palette.surface;
          ctx.stroke();

          if (showLabel) {
            const fontSize = 13;
            ctx.font = `${isFocusedOrHovered ? '600' : '400'} ${fontSize}px ${fontFamily}`;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'top';
            if (n.labelFont !== ctx.font) {
              n.displayLabel = fitGraphLabel(ctx, n.label, labelWidth);
              n.labelFont = ctx.font;
            }
            const text = n.displayLabel;
            const labelY = n.y + n.radius + 7;

            if (isFocusedOrHovered) {
              const tw = ctx.measureText(text).width + 8;
              ctx.fillStyle = palette.labelBackground;
              ctx.fillRect(n.x - tw / 2, labelY - 1.5, tw, fontSize + 4);
            }

            ctx.fillStyle = palette.text;
            ctx.fillText(text, n.x, labelY);
          }

          ctx.restore();
        }

        ctx.restore();
        if (alpha >= 0.008 || (draggedNode && pointerMoved)) requestDraw();
      }

      function requestDraw() {
        if (graphSimAnimFrame === null && canvas.isConnected)
          graphSimAnimFrame = requestAnimationFrame(drawFrame);
      }

      canvas.onkeydown = event => {
        if (!simNodes.length) return;
        if (['ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp'].includes(event.key)) {
          event.preventDefault();
          const current = simNodes.findIndex(node => node.id === keyboardFocusNodeId);
          const direction = ['ArrowLeft', 'ArrowUp'].includes(event.key) ? -1 : 1;
          const index = current < 0 ? (direction > 0 ? 0 : simNodes.length - 1)
            : (current + direction + simNodes.length) % simNodes.length;
          const node = simNodes[index];
          keyboardFocusNodeId = node.id;
          canvas.setAttribute('aria-label', `${node.label} · ${node.subLabel}. Arrow keys change node; Enter selects; Escape clears.`);
          requestDraw();
        } else if (event.key === 'Enter' && keyboardFocusNodeId) {
          event.preventDefault();
          focusGraphNode(keyboardFocusNodeId);
        } else if (event.key === 'Escape') {
          event.preventDefault();
          keyboardFocusNodeId = null;
          graphFocusedNodeId = null;
          graphHoveredNodeId = null;
          graphDetailsOpen = false;
          canvas.setAttribute('aria-label', graphCanvasDescription);
          renderDocumentGraphCanvasOnly(true);
        }
      };
      canvas.onblur = () => {
        keyboardFocusNodeId = null;
        canvas.setAttribute('aria-label', graphCanvasDescription);
        requestDraw();
      };

      canvas.onmousedown = (e) => {
        const b = canvas.getBoundingClientRect();
        const sx = e.clientX - b.left;
        const sy = e.clientY - b.top;
        startMouseX = e.clientX;
        startMouseY = e.clientY;
        pointerMoved = false;

        const hit = findNodeAt(sx, sy);
        if (hit) {
          draggedNode = hit;
          canvas.style.cursor = 'grabbing';
        } else {
          isPanning = true;
          canvas.style.cursor = 'grabbing';
        }
        requestDraw();
      };

      canvas.onmousemove = (e) => {
        const b = canvas.getBoundingClientRect();
        const sx = e.clientX - b.left;
        const sy = e.clientY - b.top;
        const dx = e.clientX - startMouseX;
        const dy = e.clientY - startMouseY;
        if (Math.abs(dx) > 2 || Math.abs(dy) > 2) pointerMoved = true;

        if (draggedNode && pointerMoved) {
          const pt = screenToWorld(sx, sy);
          draggedNode.x = pt.x;
          draggedNode.y = pt.y;
          graphCustomNodePositions[draggedNode.id] = { x: pt.x, y: pt.y };
          alpha = Math.max(alpha, 0.25);
        } else if (isPanning) {
          graphPanX += dx;
          graphPanY += dy;
          startMouseX = e.clientX;
          startMouseY = e.clientY;
        } else {
          const hit = findNodeAt(sx, sy);
          graphHoveredNodeId = hit ? hit.id : null;
          canvas.style.cursor = hit ? 'pointer' : 'grab';
          canvas.title = hit ? `${hit.label} · ${hit.subLabel}` : '';
        }
        requestDraw();
      };
      canvas.onmouseleave = () => {
        if (draggedNode || isPanning) return;
        graphHoveredNodeId = null;
        canvas.title = '';
        canvas.style.cursor = 'grab';
        requestDraw();
      };

      const mouseUp = () => {
        if (!pointerMoved && draggedNode) {
          focusGraphNode(draggedNode.id);
        }
        draggedNode = null;
        isPanning = false;
      };
      window.addEventListener('mouseup', mouseUp);

      canvas.onwheel = (e) => {
        e.preventDefault();
        const delta = e.deltaY < 0 ? 0.12 : -0.12;
        graphZoom = Math.min(2.6, Math.max(0.45, +(graphZoom + delta).toFixed(2)));
        const lbl = document.getElementById('graphZoomLabel');
        if (lbl) lbl.textContent = `${Math.round(graphZoom * 100)}%`;
        requestDraw();
      };

      const resize = () => {
        if (!canvas.isConnected || workspaceMode !== 'graph') return;
        const current = canvas.getBoundingClientRect();
        if (Math.abs(current.width - width) > 1 || Math.abs(current.height - height) > 1)
          renderDocumentGraphCanvasOnly();
      };
      const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(resize);
      if (observer) observer.observe(canvas);
      else window.addEventListener('resize', resize);
      graphCanvasDisposer = () => {
        observer?.disconnect();
        window.removeEventListener('mouseup', mouseUp);
        if (!observer) window.removeEventListener('resize', resize);
      };
      graphCanvasRefresh = () => { palette = readPalette(); requestDraw(); };
      drawFrame();
    }

    function fitGraphLabel(context, label, maxWidth) {
      if (context.measureText(label).width <= maxWidth) return label;
      const characters = Array.from(label);
      let low = 0;
      let high = characters.length;
      while (low < high) {
        const middle = Math.ceil((low + high) / 2);
        const candidate = characters.slice(0, middle).join('') + '…';
        if (context.measureText(candidate).width <= maxWidth) low = middle;
        else high = middle - 1;
      }
      return characters.slice(0, low).join('') + '…';
    }

    function saveAskAiChatForTenant(tenantId, chat, partition) {
      if (currentTenant?.tenantId === tenantId && globalThis.DocIntelIdentity?.chatPartition === partition) {
        const index = tenantChats.findIndex(c => c.id === chat.id);
        if (index < 0) return;
        tenantChats[index] = chat;
        saveTenantChats();
        return;
      }
      try {
        const key = getChatsStorageKey(tenantId, partition);
        const chats = JSON.parse(localStorage.getItem(key) || '[]');
        const index = chats.findIndex(c => c.id === chat.id);
        if (index < 0) return;
        chats[index] = chat;
        localStorage.setItem(key, JSON.stringify(chats));
      } catch {
        console.error('Unable to save Ask AI chat state for its original tenant.');
      }
    }

    function previousAskAiRunId(chat) {
      const messages = Array.isArray(chat?.messages) ? chat.messages : [];
      const latest = messages.findLast(message => message?.role === 'assistant' && !message.isError);
      return typeof latest?.runId === 'string' && latest.runId.length > 0 && latest.runId.length <= 512
        ? latest.runId : null;
    }

    async function runCrossDocumentAskAi(customQuestion, resume = false) {
      if (!currentTenant || askAiLoading) return;
      loadTenantChats();
      const tenantId = currentTenant.tenantId;
      const chatPartition = globalThis.DocIntelIdentity?.chatPartition;
      const chat = getActiveChat();
      const inputEl = document.getElementById('askAiMainInputEl');
      const q = resume ? chat.pendingAskAi?.question
        : (customQuestion != null ? customQuestion : (inputEl ? inputEl.value : askAiQuestionInput)).trim();
      if (!q || (resume && !chat.pendingAskAi?.runId)) return;
      if (!resume && chat.pendingAskAi?.runId) {
        showToast('Check the pending response or start a new chat before asking another question.');
        return;
      }

      if (!chat.messages) chat.messages = [];
      if (!resume && (chat.messages.length === 0 || chat.title === 'New chat')) {
        chat.title = q.length > 42 ? q.slice(0, 41) + '…' : q;
      }
      if (!resume) {
        chat.messages.push({ role: 'user', text: q, timestamp: new Date().toISOString() });
        chat.pendingAskAi = { question: q, runId: null };
      }
      chat.updatedAt = new Date().toISOString();
      saveAskAiChatForTenant(tenantId, chat, chatPartition);

      askAiQuestionInput = '';
      askAiLoading = true;
      askAiLoadingChatId = chat.id;
      askAiLoadingTenantId = tenantId;
      askAiProgressText = resume ? 'Reconnecting to your question...' : 'Submitting your question...';
      renderDocumentList();
      renderSelectedDocument();

      try {
        if (!resume) {
          chat.pendingAskAi.runId = await DocIntelAskAi.submit(tenantId, q, previousAskAiRunId(chat));
          saveAskAiChatForTenant(tenantId, chat, chatPartition);
        }
        const finalData = await DocIntelAskAi.waitForRun(tenantId, chat.pendingAskAi.runId, run => {
          askAiProgressText = run.status === 'Queued' ? 'Your question is queued...'
            : run.steps?.at(-1)?.stepName === 'provider_throttled'
              ? 'The AI provider is rate limited. Waiting to continue your analysis...'
            : (run.steps?.length ? 'Reviewing document evidence...' : 'Working on your question...');
          if (currentTenant?.tenantId === tenantId && activeChatId === chat.id) {
            const progress = document.getElementById('askAiProgress');
            if (progress) progress.textContent = askAiProgressText;
          }
        });
        const completedRunId = chat.pendingAskAi.runId;
        delete chat.pendingAskAi;
        chat.messages.push({
          role: 'assistant',
          text: finalData.status === 'Failed' ? finalData.errorMessage || 'Ask AI could not complete this request.'
            : finalData.answer,
          isError: finalData.status === 'Failed',
          runId: completedRunId,
          tenantId,
          errorCode: finalData.status === 'Failed' ? finalData.errorCode || null : null,
          providerFailure: finalData.status === 'Failed' ? finalData.providerFailure || null : null,
          analyses: finalData.status === 'Completed' && Array.isArray(finalData.analyses) ? finalData.analyses : [],
          citations: finalData.status === 'Completed' ? finalData.citations || [] : [],
          executedCosmosQueries: finalData.executedCosmosQueries || [],
          timestamp: new Date().toISOString()
        });
        chat.updatedAt = new Date().toISOString();
        saveAskAiChatForTenant(tenantId, chat, chatPartition);
      } catch (err) {
        const interruptedRunId = chat.pendingAskAi?.runId || null;
        if (!err.resumable || !chat.pendingAskAi?.runId) delete chat.pendingAskAi;
        chat.messages.push({
          role: 'assistant',
          text: err.message || 'Unable to connect to Ask AI. The question was not automatically resubmitted.',
          isError: true,
          runId: interruptedRunId,
          tenantId,
          citations: [],
          timestamp: new Date().toISOString()
        });
        saveAskAiChatForTenant(tenantId, chat, chatPartition);
      } finally {
        askAiLoading = false;
        askAiLoadingChatId = null;
        askAiLoadingTenantId = null;
        askAiProgressText = '';
        renderDocumentList();
        renderSelectedDocument();
      }
    }

    function jumpToHumanReview(jobId) {
      showToast('Human review is no longer available. Ask a new question against the original document results.');
    }

    function citationAction(citation) {
      const reviewed = ['HumanReviewStatus', 'HumanReviewedField'].includes(citation.evidenceKind);
      const action = reviewed ? 'jumpToHumanReview_arg' : 'jumpFromGraphToSlice_arg_arg';
      const args = reviewed ? [String(citation.jobId)] :
        [String(citation.jobId), Number.isSafeInteger(citation.sliceIndex) ? citation.sliceIndex : 0];
      return `data-click="${action}" data-args="${escapeHtml(JSON.stringify(args))}"`;
    }

    function openAskAiAnalysisCitation(citation) {
      if (['HumanReviewStatus', 'HumanReviewedField'].includes(citation.evidenceKind)) {
        jumpToHumanReview(citation.jobId);
      } else {
        jumpFromGraphToSlice(citation.jobId, citation.sliceIndex);
      }
    }

    function formatMarkdownBold(text, citations) {
      let html = escapeHtml(text || '')
        .replace(/\*\*([^*]+)\*\*/g, '<strong>$1</strong>')
        .replace(/\*([^*]+)\*/g, '<em>$1</em>');

      if (Array.isArray(citations) && citations.length > 0) {
        html = html.replace(/\[(\d+)\]/g, (match, numStr) => {
          const num = parseInt(numStr, 10);
          const cite = citations.find(c => c.citationNumber === num);
          if (!cite) return match;
          return `<button type="button" class="text-link-btn" style="padding:1px 6px;font-size:10.5px;margin:0 2px;" title="${escapeHtml(cite.cleanName)} (${escapeHtml(cite.pageRange)})" ${citationAction(cite)}>[${num}]</button>`;
        });
      }
      return html;
    }

    function askFailureDetails(message) {
      if (!message.isError) return '';
      const time = new Date(message.timestamp);
      const timestamp = Number.isNaN(time.getTime()) ? 'Failure time unavailable'
        : `<time datetime="${escapeHtml(time.toISOString())}">${escapeHtml(time.toLocaleString())}</time>`;
      const tenantId = message.tenantId || currentTenant?.tenantId;
      const runId = typeof message.runId === 'string' && /^[A-Za-z0-9_-]{1,128}$/.test(message.runId)
        ? message.runId : null;
      const url = runId && typeof tenantId === 'string'
        ? `/ask-run.html?tenant=${encodeURIComponent(tenantId)}&run=${encodeURIComponent(runId)}` : null;
      const providerStatus = message.providerFailure?.httpStatus;
      return `<div class="ask-failure-meta">
        <span>Saved failure · ${timestamp}${message.errorCode ? ` · ${escapeHtml(message.errorCode)}` : ''}
          ${Number.isInteger(providerStatus) && providerStatus >= 100 && providerStatus <= 599 ? ` · HTTP ${providerStatus}` : ''}</span>
        ${url ? `<a class="text-link-btn" href="${escapeHtml(url)}" target="_blank" rel="noopener noreferrer">View Run Details ↗</a>` : ''}
      </div>`;
    }

    function renderCrossDocumentAskTab(body) {
      loadTenantChats();
      const chat = getActiveChat();
      const messages = chat?.messages || [];
      const chatIsLoading = askAiLoading && askAiLoadingChatId === chat?.id &&
        askAiLoadingTenantId === currentTenant?.tenantId;
      const isEmptyChat = messages.length === 0 && !chatIsLoading;

      if (isEmptyChat) {
        body.innerHTML = `
          <div class="ask-main-pane">
            <div class="ask-minimal-stage">
              <h2 class="ask-minimal-heading">${escapeHtml(currentTenant?.agent?.purpose || 'Explore and answer questions across your documents')}</h2>

              <div class="ask-pill-bar">
                <input type="text" class="ask-pill-input" id="askAiMainInputEl" value="${escapeHtml(askAiQuestionInput)}" placeholder="Ask ${escapeHtml(currentTenant?.agent?.displayName || 'DocIntel')} about your documents..." data-input="updateAskDraft_value" data-enter="runCrossDocumentAskAi_value" />
                <button type="button" class="ask-pill-send" title="Send message" data-click="submitAskInput">
                  <svg width="15" height="15" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8">
                    <path d="M4 10h12M11 5l5 5-5 5" stroke-linecap="round" stroke-linejoin="round" />
                  </svg>
                </button>
              </div>
            </div>
          </div>
        `;
        return;
      }

      const threadHtml = messages.map((m, messageIndex) => {
        if (m.role === 'user') {
          return `
            <div style="align-self:flex-end;background:var(--surface-soft);color:var(--text-primary);padding:10px 16px;border-radius:18px;font-size:14px;font-weight:500;max-width:82%;">
              ${escapeHtml(m.text)}
            </div>
          `;
        }
        const cites = Array.isArray(m.citations)
          ? m.citations.filter(citation => citation && typeof citation === 'object') : [];
        return `
          <div class="ask-response" style="align-self:stretch;display:flex;flex-direction:column;gap:10px;padding:4px 0 12px 0;border-bottom:1px solid var(--line);">
            <div class="insight-prose" ${m.isError ? 'role="alert"' : ''} style="margin:0;font-size:14px;line-height:1.65;">${formatMarkdownBold(m.text, cites)}</div>
            ${askFailureDetails(m)}
            ${!m.isError && Array.isArray(m.analyses) && m.analyses.length
              ? `<div class="ask-analysis-host" data-ask-analysis="${messageIndex}"></div>` : ''}
            ${cites.length > 0 ? `
              <div style="display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                ${cites.map(c => `
                  <button type="button" class="text-link-btn" ${citationAction(c)} title="${escapeHtml(c.documentName)} · ${escapeHtml(c.customerName || '')}">
                    [${escapeHtml(c.citationNumber || '')}] ${escapeHtml(c.cleanName)} (${escapeHtml(c.pageRange)}) ↗
                  </button>
                `).join('')}
              </div>
            ` : ''}
          </div>
        `;
      }).join('');

      body.innerHTML = `
        <div class="ask-main-pane" style="justify-content:flex-start;">
          <div class="ask-minimal-stage" style="justify-content:space-between;flex:1;margin:0 auto;gap:24px;">
            <div class="ask-thread" style="width:100%;display:flex;flex-direction:column;gap:18px;padding-bottom:28px;">
              ${threadHtml}
              ${chatIsLoading ? `<div id="askAiProgress" role="status" style="color:var(--text-secondary);font-size:13.5px;">${escapeHtml(askAiProgressText)}</div>` : ''}
              ${!chatIsLoading && chat?.pendingAskAi?.runId ? '<button type="button" class="text-link-btn" data-click="runCrossDocumentAskAi_arg_arg" data-args="[null,true]">Check pending response</button>' : ''}
              ${!chatIsLoading && !chat?.pendingAskAi?.runId && messages.at(-1)?.role === 'user' ? '<div role="status">No confirmed run is linked to this question. It was not automatically submitted again.</div>' : ''}
            </div>

            <div class="ask-pill-bar" style="position:sticky;bottom:8px;">
              <input type="text" class="ask-pill-input" id="askAiMainInputEl" value="${escapeHtml(askAiQuestionInput)}" placeholder="Ask ${escapeHtml(currentTenant?.agent?.displayName || 'DocIntel')} about your documents..." data-input="updateAskDraft_value" data-enter="runCrossDocumentAskAi_value" />
              <button type="button" class="ask-pill-send" title="Send message" data-click="submitAskInput">
                <svg width="15" height="15" viewBox="0 0 20 20" fill="none" stroke="currentColor" stroke-width="1.8">
                  <path d="M4 10h12M11 5l5 5-5 5" stroke-linecap="round" stroke-linejoin="round" />
                </svg>
              </button>
            </div>
          </div>
        </div>
      `;
      const latestRunId = previousAskAiRunId(chat);
      for (const [index, message] of messages.entries()) {
        if (message.role === 'user' || message.isError ||
          !Array.isArray(message.analyses) || !message.analyses.length) continue;
        const host = body.querySelector(`[data-ask-analysis="${index}"]`);
        if (!host) continue;
        DocIntelAnalyses.render(host, message.analyses, message.citations, {
          onCitation: openAskAiAnalysisCitation,
          onFollowUp: !askAiLoading && !chat?.pendingAskAi?.runId && message.runId && message.runId === latestRunId
            ? question => runCrossDocumentAskAi(question) : undefined
        });
      }
    }

    DocIntelActions.register({
      chooseWorkspaceUpload: { arity: 0, run: function() {
        const control = document.getElementById('drawerUploadLabel');
        if (control && !control.hidden && control.style.display !== 'none') document.getElementById('docUploadInput')?.click();
      } },
      closeGraphDropdown: { arity: 0, run: function() { closeGraphDropdown(); } },
      copyTextValue_arg: { arity: 1, run: function(args, event) { return copyTextValue(args[0]); } },
      deleteAskAiChat_arg_event: { arity: 1, run: function(args, event) { return deleteAskAiChat(args[0], event); } },
      exportSelectedJobJson: { arity: 0, run: function(args, event) { return exportSelectedJobJson(); } },
      filterEntityPopoverOptions_value: { arity: 0, run: function(args, event) { return filterEntityPopoverOptions(this.value); } },
      focusGraphNode_arg: { arity: 1, run: function(args, event) { return focusGraphNode(args[0]); } },
      jumpFromGraphToSlice_arg_arg: { arity: 2, run: function(args, event) { return jumpFromGraphToSlice(args[0], args[1]); } },
      jumpToHumanReview_arg: { arity: 1, run: function(args, event) { return jumpToHumanReview(args[0]); } },
      jumpToSlicePdf_arg: { arity: 1, run: function(args, event) { return jumpToSlicePdf(args[0]); } },
      jumpToSlicePdf_arg_event: { arity: 1, run: function(args, event) { return jumpToSlicePdf(args[0], event); } },
      onTenantSwitched_value: { arity: 0, run: function(args, event) { return onTenantSwitched(this.value); } },
      openAdminForTenant: { arity: 0, run: function(args, event) { this.href='/admin.html?tenant=' + encodeURIComponent(currentTenant ? currentTenant.tenantId : ''); } },
      renderDocumentList: { arity: 0, run: function(args, event) { return renderDocumentList(); } },
      resetGraphFilters: { arity: 0, run: function(args, event) { return resetGraphFilters(); } },
      resetGraphFilters_event: { arity: 0, run: function(args, event) { return resetGraphFilters(event); } },
      runCrossDocumentAskAi_arg_arg: { arity: 2, run: function(args, event) { return runCrossDocumentAskAi(args[0], args[1]); } },
      runCrossDocumentAskAi_value: { arity: 0, run: function(args, event) { return runCrossDocumentAskAi(this.value); } },
      selectAskAiChat_arg: { arity: 1, run: function(args, event) { return selectAskAiChat(args[0]); } },
      setDocResultTab_arg: { arity: 1, run: function(args, event) { return setDocResultTab(args[0]); } },
      setEntityCategoryFilter_arg: { arity: 1, run: function(args, event) { return setEntityCategoryFilter(args[0]); } },
      setWorkspaceMode_arg: { arity: 1, run: function(args, event) { return setWorkspaceMode(args[0]); } },
      signOut: { arity: 0, run: function(args, event) { return DocIntelIdentity.signOut(); } },
      startNewAskAiChat: { arity: 0, run: function(args, event) { return startNewAskAiChat(); } },
      stopPropagation: { arity: 0, run: function(args, event) { event.stopPropagation(); } },
      submitAskInput: { arity: 0, run: function(args, event) { return runCrossDocumentAskAi(document.getElementById('askAiMainInputEl').value); } },
      updateAskDraft_value: { arity: 0, run: function() { askAiQuestionInput = this.value; } },
      toggleDocListPane: { arity: 0, run: function(args, event) { return toggleDocListPane(); } },
      toggleGraphDetails: { arity: 0, run: function() { toggleGraphDetails(); } },
      toggleEntityMultiSelectDropdown_event: { arity: 0, run: function(args, event) { return toggleEntityMultiSelectDropdown(event); } },
      toggleGraphEntityNameFilter_arg_event: { arity: 1, run: function(args, event) { return toggleGraphEntityNameFilter(args[0], event); } },
      toggleGraphEntityValueSelection_arg_event: { arity: 1, run: function(args, event) { return toggleGraphEntityValueSelection(args[0], event); } },
      toggleSliceCard_self: { arity: 0, run: function(args, event) { return toggleSliceCard(this); } },
      uploadAndRunTenantDocument_self: { arity: 0, run: function(args, event) { return uploadAndRunTenantDocument(this); } },
      zoomGraphBy_arg: { arity: 1, run: function(args, event) { return zoomGraphBy(args[0]); } }
    });

    init();
