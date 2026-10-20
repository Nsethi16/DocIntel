(function (root) {
  'use strict';

  function describe(value, scale) {
    const signals = value?.confidenceSignals;
    const raw = signals ? signals.modelScore :
      (scale === 1 ? value?.confidence : value?.confidenceValue) / scale;
    const modelPercent = typeof raw === 'number' && Number.isFinite(raw) && raw >= 0 && raw <= 1
      ? Math.round(raw * 100) : null;
    const provenance = value?.provenance;
    const validBoxes = Array.isArray(provenance?.boxes) && provenance.boxes.length > 0 &&
      provenance.boxes.every(box => Number.isSafeInteger(box.page) && box.page > 0 &&
        Array.isArray(box.polygon) && box.polygon.length === 8 &&
        box.polygon.every(coordinate => Number.isFinite(coordinate) && coordinate >= 0 && coordinate <= 1));
    const exact = provenance?.status === 'ExactTextMatch' && provenance.grounded === true && validBoxes &&
      Array.isArray(provenance.wordIds) && provenance.wordIds.length === provenance.boxes.length &&
      provenance.wordIds.every(id => typeof id === 'string' && id.length > 0);
    const status = exact ? 'Exact source text' : provenance?.status === 'Ambiguous' ? 'Ambiguous source match' :
      provenance?.status === 'NotFound' ? 'No source text match' :
      provenance?.status === 'ReferenceLimitExceeded' ? 'Source match exceeds reference limit' : 'Source evidence unavailable';
    const pages = exact ? [...new Set(provenance.boxes.map(box => box.page))] : [];
    const score = modelPercent === null ? 'Unknown model score' : `Model score ${modelPercent}%`;
    return {
      modelPercent, status, pages,
      tooltip: `${score} (uncalibrated). ${status}. Text presence is not semantic verification or review approval.`
    };
  }

  root.DocIntelEvidence = Object.freeze({ describe });
})(globalThis);
