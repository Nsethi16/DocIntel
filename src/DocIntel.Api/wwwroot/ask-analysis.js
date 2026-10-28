(() => {
  'use strict';
  const svgNamespace = 'http://www.w3.org/2000/svg';
  const maxRows = 100;
  const maxChartRows = 40;
  const numberFormat = new Intl.NumberFormat(undefined, { maximumSignificantDigits: 21 });
  let chartSequence = 0;

  function node(tag, className, text) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (text !== undefined) element.textContent = text;
    return element;
  }

  function svgNode(tag, attributes = {}, text) {
    const element = document.createElementNS(svgNamespace, tag);
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, String(value));
    if (text !== undefined) element.textContent = text;
    return element;
  }

  function text(value, fallback = '') {
    return typeof value === 'string' ? value : fallback;
  }

  function cell(row, key) {
    return row?.cells && Object.hasOwn(row.cells, key) ? row.cells[key] : null;
  }

  function finiteNumber(value) {
    return typeof value === 'number' && Number.isFinite(value);
  }

  function displayValue(value, kind) {
    if (value === null || value === undefined) return 'Missing';
    if (kind === 'number') return finiteNumber(value) ? numberFormat.format(value) : 'Invalid number';
    return typeof value === 'string' ? value : 'Invalid value';
  }

  function shortLabel(value, length = 25) {
    const characters = Array.from(value);
    return characters.length > length ? characters.slice(0, length - 1).join('') + '…' : value;
  }

  function rowCitations(row, citations) {
    if (!Array.isArray(row?.citationNumbers)) return [];
    return [...new Set(row.citationNumbers)].filter(number => Number.isSafeInteger(number) && number > 0)
      .map(number => citations.find(citation => citation?.citationNumber === number &&
        typeof citation.jobId === 'string' && citation.jobId.length > 0 && citation.jobId.length <= 512 &&
        Number.isSafeInteger(citation.sliceIndex) && citation.sliceIndex >= 0))
      .filter(Boolean);
  }

  function sourceLabel(citation) {
    const name = text(citation.cleanName, text(citation.fileName, 'Document'));
    return `Open source [${citation.citationNumber}]: ${name} ${text(citation.pageRange)}`.trim();
  }

  function addSources(target, row, citations, onCitation) {
    const sources = rowCitations(row, citations);
    if (!sources.length || typeof onCitation !== 'function') {
      target.appendChild(node('span', 'ask-analysis-muted', 'Source unavailable'));
      return;
    }
    for (const citation of sources) {
      const button = node('button', 'ask-analysis-source', `[${citation.citationNumber}]`);
      button.type = 'button';
      button.title = sourceLabel(citation);
      button.setAttribute('aria-label', sourceLabel(citation));
      button.addEventListener('click', () => onCitation(citation));
      target.appendChild(button);
    }
  }

  function renderTable(card, title, columns, rows, citations, onCitation) {
    const region = node('div', 'ask-analysis-table-region');
    region.setAttribute('role', 'region');
    region.setAttribute('aria-label', `${title} data table`);
    region.tabIndex = 0;
    const table = node('table', 'ask-analysis-table');
    table.appendChild(node('caption', '', `${title} — data and cited sources`));
    const head = node('thead');
    const headings = node('tr');
    for (const label of [...columns.map(column => column.label), 'Sources']) {
      const heading = node('th', '', label);
      heading.setAttribute('scope', 'col');
      headings.appendChild(heading);
    }
    head.appendChild(headings);
    table.appendChild(head);
    const body = node('tbody');
    for (const row of rows) {
      const line = node('tr');
      for (const column of columns) {
        line.appendChild(node('td', column.kind === 'number' ? 'ask-analysis-number' : '',
          displayValue(cell(row, column.key), column.kind)));
      }
      const sources = node('td', 'ask-analysis-sources');
      addSources(sources, row, citations, onCitation);
      line.appendChild(sources);
      body.appendChild(line);
    }
    table.appendChild(body);
    region.appendChild(table);
    card.appendChild(region);
  }

  function dateValue(value) {
    if (typeof value !== 'string' ||
      !/^\d{4}-\d{2}-\d{2}(?:T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2}))?$/.test(value)) {
      return null;
    }
    const day = Date.parse(value.slice(0, 10));
    if (!Number.isFinite(day) || new Date(day).toISOString().slice(0, 10) !== value.slice(0, 10)) return null;
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : null;
  }

  function chartData(analysis, columns, rows, totalRows) {
    if (analysis.visualization === 'table') return { reason: null };
    if (!['bar', 'line'].includes(analysis.visualization)) {
      return { reason: 'Table only: the chart type is not supported.' };
    }
    if (totalRows > maxChartRows) return { reason: 'Table only: charts are limited to 40 rows.' };
    const x = columns.find(column => column.key === analysis.xColumn);
    const y = columns.find(column => column.key === analysis.yColumn);
    if (!x || !y || x.key === y.key || y.kind !== 'number' || !rows.length) {
      return { reason: 'Table only: the chart columns are missing or invalid.' };
    }
    if (rows.some(row => cell(row, y.key) !== null && !finiteNumber(cell(row, y.key)))) {
      return { reason: 'Table only: chart values must be finite numbers or missing.' };
    }
    const units = new Set(rows.map(row => cell(row, 'unit')));
    const unit = [...units][0];
    if (units.size !== 1 || typeof unit !== 'string' || !unit.trim()) {
      return {
        reason: 'Table only: mixed or invalid units cannot share a chart axis; exactly one non-empty unit is required.'
      };
    }
    const points = rows.map(row => ({
      row, label: displayValue(cell(row, x.key), x.kind), value: cell(row, y.key),
      date: analysis.visualization === 'line' ? dateValue(cell(row, x.key)) : null
    }));
    if (!points.some(point => point.value !== null)) return { reason: 'Table only: all chart values are missing.' };
    if (analysis.visualization === 'line' && (x.kind !== 'date' ||
      points.some(point => point.date === null) || new Set(points.map(point => point.date)).size !== points.length)) {
      return { reason: 'Table only: a line chart requires distinct, valid ISO dates and one unit.' };
    }
    return { points, x, y, unit };
  }

  function chartPoint(shape, point, citations, onCitation) {
    const sources = rowCitations(point.row, citations);
    const description = `${point.label}: ${numberFormat.format(point.value)}`;
    shape.appendChild(svgNode('title', {}, description));
    if (sources.length && typeof onCitation === 'function') {
      const citation = sources[0];
      shape.setAttribute('role', 'link');
      shape.setAttribute('tabindex', '0');
      shape.setAttribute('aria-label',
        `${description}. ${sourceLabel(citation)}. All row sources are in the data table.`);
      shape.classList.add('ask-analysis-point');
      shape.addEventListener('click', () => onCitation(citation));
      shape.addEventListener('keydown', event => {
        if (event.key !== 'Enter' && event.key !== ' ') return;
        event.preventDefault();
        onCitation(citation);
      });
    } else {
      shape.setAttribute('aria-label', description);
    }
    return shape;
  }

  function renderChart(card, analysis, data, citations, onCitation) {
    const { points, x, y, unit } = data;
    const bar = analysis.visualization === 'bar';
    const height = bar ? 100 + points.length * 32 : 300;
    const svg = svgNode('svg', {
      viewBox: `0 0 760 ${height}`, class: 'ask-analysis-chart', role: 'group',
      'aria-labelledby': `ask-chart-${++chartSequence}`, focusable: 'false'
    });
    svg.appendChild(svgNode('title', { id: `ask-chart-${chartSequence}` },
      `${text(analysis.title, 'Analysis')} — ${bar ? 'bar' : 'line'} chart. ${y.label}${unit ? ` (${unit})` : ''}.`));
    svg.appendChild(svgNode('desc', {},
      'Values and all cited sources are also available in the data table below. Missing values are not zero.'));
    const magnitude = Math.max(...points.map(point => Math.abs(point.value ?? 0))) || 1;
    const minimum = Math.min(0, ...points.filter(point => point.value !== null).map(point => point.value / magnitude));
    const maximum = Math.max(0, ...points.filter(point => point.value !== null).map(point => point.value / magnitude)) ||
      (minimum === 0 ? 1 : 0);
    const fraction = value => (value / magnitude - minimum) / (maximum - minimum);
    const left = bar ? 180 : 90;
    const right = bar ? 625 : 730;
    const bottom = height - 60;
    const top = 24;
    const position = value => bar ? left + fraction(value) * (right - left)
      : bottom - fraction(value) * (bottom - top);
    for (let index = 0; index <= 4; index++) {
      const ratio = index / 4;
      const value = (minimum * (1 - ratio) + maximum * ratio) * magnitude;
      const coordinate = bar ? left + ratio * (right - left) : bottom - ratio * (bottom - top);
      svg.appendChild(svgNode('line', bar
        ? { x1: coordinate, x2: coordinate, y1: top - 8, y2: bottom, class: 'ask-analysis-grid' }
        : { x1: left, x2: right, y1: coordinate, y2: coordinate, class: 'ask-analysis-grid' }));
      const tickValue = Math.abs(value) >= 1e9 || (value !== 0 && Math.abs(value) < 0.001)
        ? value.toExponential(3) : numberFormat.format(Number(value.toPrecision(6)));
      const tick = svgNode('text', bar
        ? { x: coordinate, y: bottom + 20, 'text-anchor': 'middle', class: 'ask-analysis-axis' }
        : { x: left - 10, y: coordinate + 4, 'text-anchor': 'end', class: 'ask-analysis-axis' },
      tickValue);
      tick.appendChild(svgNode('title', {}, numberFormat.format(value)));
      svg.appendChild(tick);
    }
    svg.appendChild(svgNode('line', bar
      ? { x1: position(0), x2: position(0), y1: top - 8, y2: bottom, class: 'ask-analysis-zero' }
      : { x1: left, x2: right, y1: position(0), y2: position(0), class: 'ask-analysis-zero' }));
    if (bar) {
      points.forEach((point, index) => {
        const rowY = top + index * 32;
        const label = svgNode('text', { x: 2, y: rowY + 13, class: 'ask-analysis-axis' }, shortLabel(point.label));
        label.appendChild(svgNode('title', {}, point.label));
        svg.appendChild(label);
        const displayedValue = svgNode('text',
          { x: 754, y: rowY + 13, 'text-anchor': 'end', class: 'ask-analysis-axis' },
          point.value === null ? 'Missing' : shortLabel(numberFormat.format(point.value), 16));
        displayedValue.appendChild(svgNode('title', {},
          point.value === null ? 'Missing' : numberFormat.format(point.value)));
        svg.appendChild(displayedValue);
        if (point.value === null) return;
        const valueX = position(point.value);
        const shape = point.value === 0
          ? svgNode('circle', { cx: valueX, cy: rowY + 8, r: 3, class: 'ask-analysis-mark' })
          : svgNode('rect', {
            x: Math.min(position(0), valueX), y: rowY, width: Math.abs(valueX - position(0)), height: 16,
            rx: 2, class: 'ask-analysis-mark'
          });
        svg.appendChild(chartPoint(shape, point, citations, onCitation));
      });
    } else {
      const ordered = [...points].sort((a, b) => a.date - b.date);
      const firstDate = ordered[0].date;
      const lastDate = ordered.at(-1).date;
      const dateX = date => firstDate === lastDate ? (left + right) / 2
        : left + (date - firstDate) / (lastDate - firstDate) * (right - left);
      let segment = [];
      function finishSegment() {
        if (segment.length > 1) svg.appendChild(svgNode('polyline', {
          points: segment.join(' '), class: 'ask-analysis-line'
        }));
        segment = [];
      }
      for (const point of ordered) {
        if (point.value === null) finishSegment();
        else segment.push(`${dateX(point.date)},${position(point.value)}`);
      }
      finishSegment();
      for (const point of ordered.filter(item => item.value !== null)) {
        svg.appendChild(chartPoint(svgNode('circle', {
          cx: dateX(point.date), cy: position(point.value), r: 5, class: 'ask-analysis-mark'
        }), point, citations, onCitation));
      }
      for (const point of firstDate === lastDate ? [ordered[0]] : [ordered[0], ordered.at(-1)]) {
        const label = svgNode('text', {
          x: dateX(point.date), y: bottom + 20,
          'text-anchor': firstDate === lastDate ? 'middle' : point.date === firstDate ? 'start' : 'end',
          class: 'ask-analysis-axis'
        }, shortLabel(point.label, 22));
        label.appendChild(svgNode('title', {}, point.label));
        svg.appendChild(label);
      }
    }
    const axisLabel = bar ? `${y.label}${unit ? ` (${unit})` : ''}` : `${x.label} (actual time intervals)`;
    const axisTitle = svgNode('text', {
      x: (left + right) / 2, y: height - 10, 'text-anchor': 'middle', class: 'ask-analysis-axis'
    }, shortLabel(axisLabel, 64));
    axisTitle.appendChild(svgNode('title', {}, axisLabel));
    svg.appendChild(axisTitle);
    if (!bar) card.appendChild(node('p', 'ask-analysis-muted', `${y.label}${unit ? ` (${unit})` : ''}`));
    const chartRegion = node('div', 'ask-analysis-chart-region');
    chartRegion.setAttribute('role', 'region');
    chartRegion.setAttribute('aria-label', `${text(analysis.title, 'Analysis')} chart`);
    chartRegion.tabIndex = 0;
    chartRegion.appendChild(svg);
    card.appendChild(chartRegion);
    if (points.every(point => point.value === null || point.value === 0)) {
      card.appendChild(node('p', 'ask-analysis-muted', 'All available values are zero.'));
    }
    card.appendChild(node('p', 'ask-analysis-muted',
      'Missing values are not plotted as zero. Select a mark to open its first source; all sources are in the table.'));
  }

  function renderCoverage(card, coverage) {
    const value = name => Number.isSafeInteger(coverage?.[name]) && coverage[name] >= 0
      ? numberFormat.format(coverage[name]) : 'Not reported';
    card.appendChild(node('p', 'ask-analysis-scope', coverage?.scope === 'InspectedEvidence'
      ? 'Scope: inspected evidence only — not complete-workspace totals.'
      : 'Scope: not verified — do not treat these values as complete-workspace totals.'));
    const metrics = node('ul', 'ask-analysis-coverage');
    for (const [label, key] of [
      ['Records included', 'recordsIncluded'], ['Missing values', 'missingValues'],
      ['Duplicates excluded', 'duplicatesExcluded'], ['Documents matched', 'documentsMatched'],
      ['Documents inspected', 'documentsInspected']
    ]) metrics.appendChild(node('li', '', `${label}: ${value(key)}`));
    card.appendChild(metrics);
    card.appendChild(node('p', 'ask-analysis-muted', coverage?.hasMoreDocuments === true
      ? 'More matching documents exist and are not included in this analysis.'
      : coverage?.hasMoreDocuments === false
        ? 'No further matching documents reported. Coverage is still limited to inspected evidence.'
        : 'Whether more matching documents exist was not reported.'));
  }

  function render(host, analyses, citations = [], options = {}) {
    host.replaceChildren();
    if (!Array.isArray(analyses)) return;
    const sources = Array.isArray(citations) ? citations : [];
    for (const analysis of analyses.slice(0, 8)) {
      if (!analysis || typeof analysis !== 'object') continue;
      const card = node('section', 'ask-analysis');
      const title = text(analysis.title, 'Analysis');
      card.setAttribute('aria-label', title);
      card.appendChild(node('h3', 'ask-analysis-title', title));
      renderCoverage(card, analysis.coverage);
      const keys = new Set();
      const declared = Array.isArray(analysis.columns) ? analysis.columns : [];
      const columns = declared.filter(column => {
        if (!column || typeof column.key !== 'string' || !column.key || column.key.length > 128 ||
          !['text', 'number', 'date'].includes(column.kind) || keys.has(column.key)) return false;
        keys.add(column.key);
        return true;
      }).slice(0, 16).map(column => ({ ...column, label: text(column.label, column.key) }));
      const inputRows = Array.isArray(analysis.rows) ? analysis.rows : [];
      const rows = inputRows.slice(0, maxRows);
      if (declared.length !== columns.length || !columns.length) {
        card.appendChild(node('p', 'ask-analysis-notice',
          'Some declared columns are invalid or exceed the 16-column limit.'));
      }
      if (inputRows.length > maxRows) {
        card.appendChild(node('p', 'ask-analysis-notice', 'Only the first 100 rows are displayed.'));
      }
      if (columns.length) {
        const data = chartData(analysis, columns, rows, inputRows.length);
        if (data.points) renderChart(card, analysis, data, sources, options.onCitation);
        else if (data.reason) card.appendChild(node('p', 'ask-analysis-notice', data.reason));
        renderTable(card, title, columns, rows, sources, options.onCitation);
        if (!rows.length) card.appendChild(node('p', 'ask-analysis-muted', 'No rows were returned.'));
      }
      if (Array.isArray(analysis.notes)) {
        const notes = node('ul', 'ask-analysis-notes');
        for (const note of analysis.notes.slice(0, 10)) {
          if (typeof note === 'string') notes.appendChild(node('li', '', note));
        }
        card.appendChild(notes);
      }
      if (typeof options.onFollowUp === 'function') {
        const followUps = node('div', 'ask-analysis-followups');
        followUps.appendChild(node('span', 'ask-analysis-muted', 'Explore next'));
        const question = analysis.visualization === 'line'
          ? `Explain the trend and any gaps in "${title}" using the cited evidence.`
          : analysis.visualization === 'bar'
            ? `Explain the largest differences in "${title}" using the cited evidence.`
            : `Summarize the key findings and limitations in "${title}" using the cited evidence.`;
        const followUpLabel = analysis.visualization === 'line' ? 'Explain this trend'
          : analysis.visualization === 'bar' ? 'Explain these differences' : 'Explore these findings';
        for (const [label, prompt] of [
          [followUpLabel, question],
          ['What evidence is missing?', `What evidence or documents are missing from "${title}", and how might that limit the conclusions?`]
        ]) {
          const button = node('button', 'ask-analysis-followup', label);
          button.type = 'button';
          button.title = prompt;
          button.addEventListener('click', () => options.onFollowUp(prompt));
          followUps.appendChild(button);
        }
        card.appendChild(followUps);
      }
      host.appendChild(card);
    }
    if (analyses.length > 8) host.appendChild(node('p', 'ask-analysis-notice', 'Only the first eight analyses are displayed.'));
  }

  globalThis.DocIntelAnalyses = { render };
})();
