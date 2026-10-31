# Functional hardening and acceptance plan

## Objective

Keep the document-first workspace usable across different PDF types and large collections.
Graph labels are interaction-only: hover, keyboard focus, or explicit node/entity selection.
Do not infer completeness, calibrated confidence, or guaranteed classification from a successful model call.
This plan verifies supported behavior and explicit limits; it does not promise that every possible document is bug-free.

## Implementation priorities

| Area | Required behavior |
| --- | --- |
| Graph | No default or zoom-triggered labels. Bound the canvas to 500 nodes and 2,000 edges while showing complete filtered counts and an explicit limit notice. Bound inspector links to 100 and entity options to 200; searching must reach values outside the initial window. Preserve keyboard navigation, exact AND filters, and distinct entity keys. |
| Footer parsing | Share one parser for native layout and grouping. Recognize the exact emitted `PageFooter="..."` syntax, including its opening quote, while preserving legacy colon comments. |
| Source preservation | A same-language skip must not populate a substitute translation. Downstream features continue to use complete native Markdown, including tables and bottom-of-page content. |
| Per-document language | Detect each logical document separately. Supported chunks scoring at least 0.5 are usable; uncertain chunks do not veto them. Mixed/lower/partial evidence selects automatic source detection for a complete PDF batch. Only unanimous target-language chunks scoring at least 0.8 permit a skip. Retain safe chunk language/score/coverage in Metadata, including failures. An identified unsupported language keeps its actual code and explicit unsupported outcome, never `und`. |
| Harness acceptance | Reject case/outer-whitespace-equivalent duplicate document rules and extraction profiles before saving or freezing a processing configuration. |
| Access during inference | Recheck current document-processing permission and active workspace state after inference, before publication, and before releasing document-bearing responses. |
| Large documents | Preserve complete source content in source-bound artifacts. Keep Cosmos metadata below its item limit; deterministic payload rejection must not become lease loss and repeated execution. |
| Ask AI numbers | Require complete quoted numeric tokens, including credit signs and accounting parentheses. Keep physical units case-sensitive for aggregates and outlier populations. |
| Ask AI inspection | Original-source windows must remain available even when derived tables are large. Preserve supplementary Unicode characters across window boundaries and reject offsets inside a surrogate pair. Return bounded derived previews with counts, shortening/omission flags, and original-source continuation. |
| Saved answers | Recheck recorded source versions before polling or external delivery. Suppress stale output without silently replaying or sending it. |
| Inline webhooks | Persist once, then process or wait for the existing owner. Recovery must not cause a second-save overwrite race or duplicate model execution. |
| Exports | Machine export flattening must not inherit the retired 2,000-field human-review limit. Preserve values or return an explicit export-limit response. |
| Glossaries and browser policy | Reject control characters before TSV staging. Use system/local fonts and remove unused third-party font origins from CSP. |

## Document and failure matrix

| Test family | Examples and acceptance criteria |
| --- | --- |
| Native continuous text | Academic articles and reports with changing bottom text remain complete logical documents; exact source is retained. |
| Financial packets | Quotes, invoices, agreements, amendments, repeated copies, same-type instances, and page resets preserve actual document boundaries and identifiers. |
| OCR and language | Scans, empty/numeric-only text, uncertain recognition, same-language PDFs, non-English PDFs, and mixed sources report source provenance and uncertainty honestly. Test 0.5/0.79 acceptance, a good chunk alongside uncertain/unsupported chunks, and separate English/French/undetermined logical documents with independent results. |
| Large source and tables | A 200-page source exceeding 2 MB of job JSON retains full content without retry loops. A 200-row derived table cannot block a 3,000-character source inspection. Exports above 2,000 cells preserve every value. |
| Analytical questions | Credits, decimal/grouping formats, missing values, duplicates, mixed currencies, case-sensitive physical units, observed dates, and outliers use server calculations and original quotations. |
| Lifecycle and authorization | Revoked membership, deactivated workspace, changed completed source, competing recovery owner, cancellation, and storage failure cannot publish stale or unauthorized output. |
| Large graph collections | 1,500/3,000-document fixtures, narrow/wide viewports, light/dark themes, hover/leave, keyboard selection, and nodes outside the initial canvas window remain responsive and disclose visual limits. |
| Live provider acceptance | Use authored synthetic PDFs for provider tests, never customer content in logs. Verify genuine translated PDF bytes, preserved pages/protected terms, and independent results after an injected translation-only failure. |

## Review recommendation dispositions

- The footer-comment mismatch is a confirmed bug. Adding `=` alone is insufficient unless the opening quote is consumed too.
- OCR accounting reserves attempts atomically before provider usage. Unknown provider failures can still incur charges;
  refunding them or reserving only after a call would undermine concurrent spend protection. Preserve attempt-based accounting.
- A late-access failure can leave a private, per-attempt archive under the explicit no-permanent-purge retention policy.
  Do not delete it blindly or describe it as authorized output. Revalidate access before any publication or response.
- `CanProcess` uses explicit parentheses without changing its role/document-scope semantics.
- The translation service already rejected TSV delimiters; validation is also enforced at the public batch-client boundary.
- Unused external font allowlists are removed without weakening required script, frame, or authorization policies.

## Release gate

Run the smallest targeted regressions first, then the repository's complete clean Release build/test/workflow gate.
Use real-browser checks for graph behavior and responsive/theme layouts.
Publish only the exact validated commit; keep model endpoints, GPT capacity 100, original storage,
retention settings, and access policies unchanged unless the user specifically requests otherwise.
Record live acceptance separately from mocked/unit results and report any remaining unsupported boundary explicitly.
