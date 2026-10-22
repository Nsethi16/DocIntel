# DocIntel — End-to-End Architecture & Document Processing Guide

This document provides a comprehensive technical walkthrough of **what** happens and **how** a document packet is processed end-to-end in **DocIntel**—from PDF upload and per-tenant Agent Harness loading to deterministic PDF slicing, AI document identification, translation, insight generation, entity extraction, and schema-driven structured data extraction.

## Current ingestion and channel changes

Cloud ingestion now uses **Azure Content Understanding `prebuilt-layout`**, GA API `2025-11-01`,
for every original PDF page. Native Markdown, source spans and word geometry are retained and
cached under tenant/source/analyzer/API/endpoint identity. PdfPig validates geometry and physically
copies PDF pages; native text extraction is only an explicit offline/development path when CU
is not configured. The older native-text/DI OCR sections below describe the historical pipeline,
not the configured CU production path.

Human review is retired: new jobs do not create review sessions, exports use labeled machine
values without approval, and Ask AI no longer advertises a human-review tool. Historical records
remain stored, and legacy review endpoints return 410. No audit or review data is purged.

Admin Studio separates **Document processing** from **Agent & channels**. The tenant's versioned
agent name, purpose, tone and response guidance apply to web/email/Teams, but do not grant access
or weaken source/citation policies. Channel toggles are distinct from operator connection status.
Graph email ingestion uses a durable leased delta cursor and a source-free sign-in/confirmation
challenge; the signed-in account must own the original sender address before its question is
exposed or queued. Teams shared mentions queue under the verified user and reply privately, or
provide an actor-authorized web link if a personal chat cannot be established. No document answer
is posted into an unverified shared audience. External sends persist intent before sending and
uncertain outcomes never automatically replay. See the README's current channel setup and
verification sections; real provider credentials, consent and live delivery are separate setup.

---

## Ask AI execution policy

Ask AI requires a configured, non-simulated model endpoint. It does not inherit the legacy
document-processing simulator: missing configuration, invalid model
output, provider errors, and cancellation must not be converted into successful document answers.
Tests inject scripted model clients; they do not require a cloud deployment. The tenant must be
active with the `AskAi` capability enabled.

The document-processing catalog also no longer falls back across providers or into simulation
after errors. Simulation must be explicitly enabled with `Models:AllowDevelopmentSimulation` on
a loopback Development host; it is rejected in Production. Development-derived jobs are tagged
and surfaced in the workspace; untagged legacy jobs have unknown provenance. Missing/malformed
model output, provider failures, and cancellation are not successful model results. In particular,
zero/missing confidence remains zero rather than receiving an artificial high confidence default.

Prompt-column execution selects PDF or text inputs from server-owned model capability metadata.
It never retries a failed file request as text, reconstructs a missing source PDF for live
execution, or substitutes a deterministic result for invalid JSON. Cached results include the
source text/PDF, entity context, model, and input-mode fingerprint. Slice result updates are staged
until evaluation succeeds. Live translation uses provider-side source-language detection without
claiming every non-Spanish input is English; no local phrase substitution follows a provider error.
Tenant translation URLs must match the server-configured provider before credentials can be used.

All `/api/harness` operations now require authenticated, server-authorized access. Entra browser
sessions and API bearer tokens are validated with the standard ASP.NET Core authentication
handlers. Tenant roles come from server membership configuration, not request bodies, email
senders, token role labels, or a directory tenant GUID treated as an application tenant slug.
Document lists, graph projections, point lookups, PDFs, run polling, and agent search/inspection
apply document permissions. Job lookups use only permitted tenant partitions. Run actors are
captured from the verified caller and rechecked during execution, including before the final
answer is returned; revocation produces no answer or citations.

Role/resource authorization runs before body binding. Cookie-authenticated mutations also require
CSRF protection. Browser sign-in uses authorization code + PKCE, short-lived HttpOnly application
sessions, and a minimal identity-only principal. API bearer tokens require the separate API
audience and access scope, or an app role on an explicitly mapped service principal. Request
limits reject excess work with 429/Retry-After rather than retaining unbounded HTTP requests.
Production cannot use the loopback development identity or omit the required Entra settings.
See README for the identity configuration contract and deployment prerequisites.

Account/tenant browser cache partitioning prevents one signed-in account from loading another's
chat history. An identity response header detects account changes in other tabs before data is
returned to the old UI state. Legacy unscoped browser history is neither imported nor deleted.
Untrusted labels/values passed to inline handlers are JSON-encoded and then HTML-encoded; HTML
entity decoding must not turn document-derived text into JavaScript.

The conversational loop uses `FunctionInvokingChatClient` with `search_documents` and
`inspect_document_slice`. Search accepts a typed plan, not model-generated SQL: the server binds
tenant, status, text, document type, and ANDed entity conditions as Cosmos parameters and queries
only that tenant partition. Results are lightweight pages of at most 20 references; no-match and
storage errors never expand to all documents. Slice inspection returns bounded original text,
insights, entities, structured data, and matching prompt-column results. Tenant configuration and
active prompt-column definitions are provided to the model as context.

Evidence IDs, file names, page ranges, and links come from inspected stored slices. Final document
answers must cite those IDs, and inline citation numbering is checked. Greetings, clarifications,
and no-evidence responses can have no citations. This validates source identity and numbering, not
the factual entailment of every generated sentence or OCR bounding-box accuracy. Per-run limits
are 12 model iterations and 24 tool calls, including registered extension tools; source chunks are
at most 3,000 characters. Providers must support native function calling and structured responses;
the existing text-only Foundry Inference adapter is explicitly unsupported for Ask AI.

Email HTML and Teams cards are formatted payloads, not delivered integrations. Request-supplied
callback URLs are rejected until an approved destination policy exists. Microsoft 365 delivery and
live cloud/identity verification remain prerequisites before production rollout. The native shared
storage path and local persisted execution are described below.

The web client submits one asynchronous run and polls that run rather than retrying a failed
synchronous request as a new execution. It stores the confirmed run ID with the chat and resumes
polling after reopening; an unconfirmed submission is never automatically retried. Failed runs
display explicit error messages, progress is scoped to the originating chat, and a tenant switch
cannot move an answer into another tenant's chat. Polling lasts up to 30 minutes; after connection
failure or that limit, the same confirmed run can be checked again without creating a new one.

Run reads require both tenant ID and run ID and return isolated snapshots. The worker persists
`Running` before model invocation, limits execution to `AskAi:MaxRunSeconds` (default 600, allowed
1-3,600), and persists safe `Failed` states for provider failure or timeout. Inline web/email/Teams
adapters use the same execution policy and wait if another worker owns their run.

### Single-host restart recovery

When a harness settings path is supplied, run snapshots default to its sibling `ask-ai-runs`
directory. Override this using `AskAi:RunStorageDirectory` (`AskAi__RunStorageDirectory` in environment
variables). Snapshots have a schema version, hashed tenant/run storage keys, a 2 MiB size bound, and
atomic replacement protected by a short snapshot lock. A separate OS-owned execution lock prevents
concurrent processes on the same host from executing the same run. Polling never waits for the
entire model execution. Disposed owners cannot save through an old lease.

The persisted run is the dispatch source of truth. The channel holds at most 100 wake-up hints;
when full, accepting a persisted run does not discard its work. A five-second discovery loop also
schedules unfinished snapshots without needing a surviving channel message. After an abrupt process
exit, the OS releases ownership and a new worker can retry the same read-only question, up to three
execution attempts. Graceful shutdown requeues that same run. Final states are not replayed.
If extension tools are registered, interrupted runs instead require review because those tools
may have external side effects; no exactly-once side-effect guarantee is claimed.

This mode requires a service-owned directory on a filesystem supporting exclusive locks and atomic
replacement. It covers application-process crashes, not arbitrary power-loss recovery or multiple
independent Container Apps replica disks. Container Apps' default ephemeral filesystem is **not**
durable storage: provision a supported persistent store/broker before cloud rollout. Without a
configured directory or harness path, isolated library consumers/tests may still use the in-memory
store. Local document metadata uses SQLite when a harness path or database path is configured.
Missing/corrupt runs are surfaced, not replaced by fabricated answers.

Regression commands from the repository root:

```powershell
dotnet test tests\DocIntel.Tests\DocIntel.Tests.csproj -c Release
node --test tests\DocIntel.Ui.Tests\ask-ai.test.cjs
dotnet build src\DocIntel.Api\DocIntel.Api.csproj -c Release
node --test tests\DocIntel.Ui.Tests\ask-ai-restart.test.cjs
node --test tests\DocIntel.Ui.Tests\document-restart.test.cjs
node --test tests\DocIntel.Ui.Tests\identity-http.test.cjs tests\DocIntel.Ui.Tests\identity-client.test.cjs
```

The restart test uses a loopback-only scripted model endpoint and isolated temporary storage. It
kills the real API process while a run is active, starts a new process, and verifies completion
under the original run ID. It tests recovery and provider-adapter wiring, not live model quality.

The identity test hosts a loopback-only OIDC/JWKS provider with generated signing keys. It exercises
the actual bearer handler, browser code/PKCE callback, session cookies, CSRF, membership reload,
role/document restrictions, native model tools, and mid-run revocation. It does not validate a
live Entra registration or grant Azure permissions.

Canonical legacy tenant artifact names remain unchanged. Noncanonical or reserved `v2-` tenant
IDs use a versioned SHA-256 namespace instead of lossy normalization, preventing distinct tenants
from sharing artifact paths. Legacy artifacts for those noncanonical IDs need an explicit,
verified migration; there is no ambiguous fallback across tenant namespaces. Document artifact
keys reject path separators/traversal, and private run/key directories cannot be placed under
the web root.

## 1. High-Level System Architecture

### Shared Ask AI execution

When an Azure Blob endpoint is configured, DI selects `AzureBlobAskAiRunStore` and
`AzureQueueAskAiQueue`, not a local fallback. Invalid cloud endpoints fail configuration. The queue
endpoint can be explicit, or derived from the standard account Blob endpoint. Native storage
uses managed identity; `AzureAI:DevelopmentStorageConnectionString` is restricted to a loopback
Development emulator and is rejected for real remote endpoints.

Run bodies are bounded snapshots under hashed tenant/run keys. Status and encoded identifiers
are atomically written as Blob metadata so a bounded recovery sweep can rediscover queued or
abandoned work. Snapshot ownership is a finite 60-second Blob lease, renewed every 20 seconds.
Every owned write includes the storage lease ID. Renewal/fence loss cancels the old worker, which
must not publish a terminal result over its replacement. Completed states are not replayed.

Azure Queue messages contain bounded run/tenant tickets, not questions or document bodies.
Visibility covers the configured execution budget; acknowledgement follows consumer processing.
Malformed/repeated tickets go to a poison queue. Dispatch/ack failures are logged without raw
payloads, and persisted snapshots remain the recovery source of truth. Discovery scans Blob
metadata and is intended for the current pilot scale, not a claimed 100K-run query index.

The Bicep path provisions run/key containers, dispatch/poison queues, managed-identity data roles,
and a versionless rotating Key Vault key for the standard ASP.NET Core Blob key-ring provider.
Production key blobs require Key Vault encryption. Role/registration setup must be validated in
the actual target directory; local issuer tests do not validate a live Entra deployment.
Readiness fails when run storage, document metadata, or a dispatch/poison queue is missing/inaccessible. The deployment script
is preview-first, requires explicit identity inputs, and never replaces the running app before
its web registration/federation prerequisites are met.

SDK-level tests cover fencing, renewal failure, tenant namespaces, acknowledgement ordering and
quarantine. Live/emulator storage integration is not yet verified in this environment.

### Durable document ingestion

Original ingestion files are now persisted before accepting/enqueueing a new job. Local writes
use a temporary file followed by non-overwriting publication; Blob writes use `If-None-Match: *`.
The stored original cannot be silently replaced through the artifact API. A SHA-256 binds it to
the accepted job, and both processing and original-file retrieval check that binding. Document
queue tickets contain only job/tenant identifiers, not filenames or PDF bytes.

Local metadata is transactional SQLite WAL, defaulting to `document-jobs.sqlite` beside the
harness settings; `DocumentJobs:DatabasePath` can select another private file. Shared deployment
uses Cosmos job records plus `document-jobs` / `document-jobs-poison` queues. Shared queues cannot
use local metadata, and all document/Ask AI dispatch/poison queue names must differ. The common
queue transport bounds ticket size, acknowledges after processing, and quarantines malformed or
repeated deliveries. A recovery loop redispatches unowned queued/running records. This is
at-least-once scheduling, not an exactly-once external-action guarantee.

Each claim has a unique owner, a 60-second expiry, and a persisted execution count. Renewal every
20 seconds and every save require the current owner plus SQLite row version or Cosmos ETag.
Missing/wildcard ETags are rejected. Renewals preserve the last persisted snapshot instead of
publishing mutable in-flight workflow lists. Expired/replaced owners are cancelled and cannot
overwrite a replacement, including during finalization or release.

New uploads persist the accepted harness snapshot and version. Execution/recovery uses that
snapshot while rechecking the submitter's current processing permission and the tenant's active
state; newer tenant definitions do not silently change an interrupted job. Legacy jobs without
a snapshot require a still-matching harness version. Artifacts are written under a unique
`{jobId}-a{executionAttempt}` namespace, while the immutable original remains under `{jobId}`.
The public slice and prompt-evaluation routes follow the saved attempt pointer. A stale worker
may leave orphaned attempt files, but cannot overwrite the replacement's files or metadata.

Execution defaults to 1,800 seconds and permits three attempts after interruptions. Graceful
shutdown requeues the same job; a process crash requires the finite ownership lease to expire
before a replacement claims it. Terminal jobs are not automatically replayed. Local recovery
polls every five seconds; Bicep sets 30 seconds for shared deployment and scales on both queues.
SQLite search applies the same typed predicates and document grants but currently scans one
tenant's metadata, so it is a local/pilot backend, not a large-dataset query index.

The document restart regression kills an actual API process during a native model request,
changes the current tenant configuration, restarts from the same private storage, and verifies
the original job completes using its accepted configuration and source bytes. It also writes
to the stale attempt's file and verifies the public PDF route still reads the replacement.
These local process tests and mocked Cosmos/Queue contracts do not verify live Azure behavior.

### Source layout evidence

The layout extraction stage publishes an immutable `layout.json` before the corresponding job
metadata references its SHA-256. The versioned envelope identifies the tenant, attempt, and original
PDF SHA-256. Pages preserve native text, original dimensions, image-area coverage, source/provider
labels, and stable word IDs with normalized, top-left-origin four-corner polygons. Native words
have null OCR confidence: presence in a PDF text layer is not a recognition-accuracy estimate.
The extractor preserves the existing portrait word strategy and uses PdfPig's rotation-aware
nearest-neighbour strategy for rotated pages; tests verify all rotations and nonzero origins.

`GET /api/harness/tenants/{tenantId}/jobs/{jobId}/layout` follows the authorized job's active attempt
and checks both the saved layout digest and original-source identity. No client-supplied storage
path or URL is accepted. Content, schema, tenant/document/source mismatches fail explicitly.
The store bounds evidence to 16 MiB, 1,000 sequential pages and 50,000 words per page. Word arrays
stay in the artifact, not Cosmos job bodies; metadata exposes only availability and a server-owned
route. Old documents without evidence return 404 instead of fabricated geometry.

Both local and Blob writes are non-overwriting; local publication uses the same temporary-file /
flush / atomic-move helper as immutable originals. Blob uses `If-None-Match: *`. The process-crash
regression verifies word IDs survive restart and stale-attempt layout changes cannot change the
public route. Storage integrity does not by itself prove OCR accuracy or field-value entailment.
Connector delivery and global audit archival remain subsequent work; human review/exports are described below.

The extraction stage now applies the accepted `Ocr.Mode` before publishing layout evidence and
before boundary detection. `Auto` selects pages with fewer than 50 body characters and image
coverage strictly above 0.5; `Always` selects all pages and `Never` selects none. A configured
native `DocumentIntelligenceClient` sends the actual original PDF and selected page numbers to
`prebuilt-read`, under the worker's cancellation/deadline policy. The credential-bearing endpoint
is server configuration, never a tenant-supplied URL.

Recognition output must contain exactly the requested original pages. Dimensions, page identity,
provider identity, word confidence and normalized geometry are validated. A source/provider aspect
ratio mismatch fails rather than guessing coordinate rotation. All recognized pages are staged
before replacing native state, and the common text-layout composer applies existing footer,
pagination and markdown behavior. The immutable layout records `TextSource=Ocr`, the actual
provider name and actual word scores, including zero. The OCR trace is present only when OCR ran;
native-text processing performs no OCR call.

The Admin policy participates in dirty-state/save handling and is frozen with the accepted job
harness. Bicep configures a separate passwordless Document Intelligence account/data role, with an
optional existing endpoint. SDK mocks and a real image-only PDF test verify parser/pipeline/storage
wiring, not live OCR accuracy.

Persistent recognition reuse lives in each tenant's reserved `_ocr-cache` artifact namespace.
Keys contain only hashes of the original PDF and provider/model/API/endpoint configuration plus
the original page number. The native client explicitly selects API version `2024-11-30`.
An entry's schema, content digest, tenant/input/provider identity, page geometry and actual word
scores are validated before reuse. Corrupt or inaccessible cache data fails explicitly rather
than silently billing another provider attempt. Cache writes use non-overwriting local publication
or Blob `If-None-Match: *`; a racing loser validates the existing winner.

Only uncached pages enter the native request. The recognition processor stages both cached and
fresh pages, validates the whole result, saves validated fresh cache entries, then publishes the
new state. Cached raw words are composed using the new job's accepted footer policy, not a prior
job's derived body text. `OcrCacheHit` and trace counts distinguish reuse from actual recognition.
Custom providers need a stable explicit cache identity; otherwise persistent reuse is disabled.
Tests recreate local stores/processors, exercise partial hits and concurrent immutable writers,
reject changed source/provider/tenant keys and corrupt entries, and check native Blob contracts.

Air-gapped Tesseract assets and live Azure verification remain outstanding. Concurrent misses and
interrupted provider/cache writes can repeat billable analysis; cache persistence is not an
exactly-once OCR billing or offline recognition guarantee.

### Live OCR cost controls

OCR operational limits are separate from accepted/frozen processing harnesses. Defaults are
100,000 request pages per tenant/UTC month and an 80% warning threshold; zero disallows fresh
provider work while preserving cached reuse. Every fresh provider invocation first reserves its
uncached pages under a server-generated request identifier. SQLite serializes budget/counter/
request writes in one transaction. Cosmos uses the separate tenant-partitioned `ocr-usage`
container and a transactional batch with specific budget/month ETags plus a new request record.
A concurrent live budget edit or allocation rejects the stale batch and triggers a bounded reread;
storage/authentication errors never become an in-memory authorization fallback.

Reservation replay must match tenant, period, page count, provider and document attempt. It does
not allocate again. Counters and policy revisions persist across process restarts. Reservations
are retained after failures and timeouts, conservatively accounting for possibly completed
provider work rather than refunding an uncertain outcome. These counters are reserved request
pages, not confirmed Azure charges, token usage or currency-valued cost estimates.

Tenant-wide Admin authorization precedes binding for usage/budget endpoints. Reads support a
validated `yyyy-MM` period; edits use a separate policy revision and return 409 on a stale edit.
Budget edits do not change document harness versions. The Admin panel ignores stale tenant loads,
does not automatically retry unconfirmed mutations and requires reload after an edit conflict.
Cookie writes require the normal CSRF token. Threshold crossings and rejections generate
structured warnings; live monitoring alert provisioning and invoice reconciliation are separate.

### Value-level source occurrence grounding

After typed extraction/prompt evaluation, `GroundExtractedValuesExecutor` builds a page-local
word index from the actual original layout. It attaches additive provenance to entities, structured
fields and every table cell while preserving their existing values and numeric score contracts.
Only a unique whole-word occurrence within that slice's original page range is marked
`ExactTextMatch`. Whitespace is normalized and case comparison is ordinal-ignore-case. Punctuation,
currency symbols, translations and cross-page concatenations are not guessed. A second occurrence
produces `Ambiguous` with no arbitrarily chosen geometry; missing/incomplete source words remain
`Unavailable`. References exceeding 64 words per value or 1,024 attached words per job are explicit
rather than partially cited; heavy job annotations must not grow without bounds.

An exact match contains the actual original word IDs/polygons and a bounded original-text excerpt.
Model scores are normalized separately (entities 0-1, structured values 0-100), retain zero and
unknown signals, and stay `Calibrated=false`. Grounding score describes text presence only.
There is deliberately no fused average or probability claim before validated calibration/review.
The UI labels model scores accordingly, displays occurrence status and links exact matches to the
authorized immutable original PDF page. It does not claim a bounding-box overlay or semantic
entailment merely because the text appears.

The job retains field annotations, but Ask AI tool projections omit heavy geometry while retaining
values, scores and occurrence status so large annotation arrays cannot crowd out source text.
Prompt fingerprints omit provenance-only annotations because prompt entity context is unchanged.
Regression tests cover actual PDF words, table/entity consistency, slice isolation, ambiguity,
incomplete scans, substring/currency/translation rejection, zero scores, annotation size limits,
cache stability and bounded native Ask AI tool payloads.

`inspect_human_review` is a separate bounded read tool. It requires a preceding authorized
document search and current resource grants, validates the source-bound session/audit prefix,
and returns a status evidence ID plus at most five approved effective fields per page. Its
citations explicitly identify `HumanReviewStatus` or `HumanReviewedField` and the review version.
Excerpts are actual stored status/value data, not rewritten original source quotations.
Unapproved effective fields are not returned. Cited review versions/statuses are revalidated
before answer publication, and the browser routes these citation buttons to the human review
workspace rather than falsely implying a matching PDF text span.

### Actor-audited human review

Accepted `ReviewPolicy` controls automatic queue creation on completion, optional dual review
and elapsed UTC due hours (not business-calendar hours). A review snapshot binds the immutable
original/layout hashes, applied harness version and original field IDs/labels/values. Legacy
completed documents with a persisted source can start explicitly. Corrections change only the
review's effective values, never the original model output, model score or source annotation.
Any changed extraction/source fingerprint prevents use of an incompatible review snapshot.

Claims are versioned and finite (15 minutes), renew on field decisions, and cannot be taken
from another active reviewer. Every change and its audit event commit together in one SQLite
transaction or same-tenant Cosmos batch with a specific ETag. Source/claim/version mismatches
are 409 conflicts, not last-writer-wins edits. Decisions require a reason for correction/rejection;
pending or rejected fields prevent approval. Dual-review submission records two distinct
directory identities; the second pass cannot silently alter first-pass decisions.

`Reviewer` is a least-privileged application role that supports document-scoped review without
upload/administration rights. Processors/Admins can also review authorized documents. Production
mutations reject app-only callers before body binding and derive the actor from verified claims.
Development identity activity is marked, not presented as a verified production human.
API writes require browser CSRF plus strong `If-Match: "review-N"` versions. Audit reads verify
the complete version/hash chain and share document authorization. There is no public audit
update/delete operation; privileged-storage tamper resistance and seven-year immutable archival
are separate, unimplemented guarantees.

The dedicated review SPA uses external scripts/styles and a focused CSP, pinned PDF.js, actual
normalized source polygons, field-to-source selection and source-to-field hit testing. Renderer
failures are explicit and retain the authorized original-PDF link. Mutations are never silently
retried; stale reads cannot populate another tenant/document's workspace. Original-value regions
remain labeled original when a human effective value differs. Keyboard operations require an
active claim; no score-only bulk approval is provided while signals are uncalibrated.
Visual checks used isolated generated development documents, confirmed source/overlay canvas
alignment at zero CSS-pixel offset and exercised an actual claimed correction without changing
the original value. They do not prove generalized OCR accuracy or a human throughput SLA.
The owned temporary preview process and fixture were stopped/removed after checks.

Queue reads are currently pilot-scale job-metadata traversal with bounded pages, not a large
review search index. Review sessions allow at most 2,000 typed fields and 10,000 audit versions.
Terminal-action capacity is reserved so repeated field edits cannot consume every opportunity
to finalize/reject; the admitted field count has sufficient ordinary and dual-review capacity.
Audit reads verify the prefix ending at their captured state version, not a later concurrent edit.
Shared chrome deduplication and global immutable audit infrastructure are not part of this
increment; neither are advertised as completed production controls.

### Human-reviewed export release

The export endpoint returns direct authenticated CSV/XLSX/JSON downloads, defaulting to approved
effective values. Source fingerprint, review/job identity, final approval and review audit prefix
must match before serialization. Current document grants, tenant activation and source snapshot
are rechecked before release. There is no silent original/unreviewed downgrade. A separate
explicit `view=original` is restricted to Processor/Admin roles and prominently labels its rows
as unreviewed source data.

Rows use stable server field identities for entities, structured fields and table cells. Both
original and human-effective values are retained. Source occurrence and uncalibrated score
metadata refer to the original model value, not a claim that a corrected value is grounded at
the old coordinates. Source/harness/review versions and development markers remain visible.
The normalized row contract avoids inventing common wide columns across incompatible schemas.

CSV strings are quoted and formula/control prefixes are apostrophe-neutralized. The approved
Open XML SDK produces inline-string Excel cells with no formula elements; SDK validation and
round-trip tests cover the actual workbook shape. Excel text/XML limits and 16-MiB output limits
produce explicit failures rather than silent truncation. JSON preserves exact field strings.

An immutable tenant/job export event is stored before sending bytes, with verified actor
reference, mode/format, source/review metadata, field count and generated-content hash. Events
contain no raw field values. Their correlation ID is returned in `X-DocIntel-Export-Id`.
Audit persistence failure prevents release, but a persisted event is not proof that an
interrupted client completed its download. Local/Blob application-level non-overwriting writes
are not regulatory retention/legal hold. This path deliberately avoids bearer SAS URLs and
untrusted outbound destinations. Bulk/Parquet exports and external delivery are separate.

**DocIntel** is built on **.NET 10**, **.NET Aspire** (`DocIntel.AppHost` + `DocIntel.ServiceDefaults`), and the **Microsoft Agent Framework** (`Microsoft.Agents.AI` + `Microsoft.Agents.AI.Workflows`).

```mermaid
flowchart TB
    subgraph Client["Unified Web UI (wwwroot/index.html)"]
        UI_Results["Document Intelligence Workspace<br/>(Insights · View Document · Entities · Structured Data · Metadata)"]
        UI_Harness["Agent Harness Studio<br/>(Models + 6 Capability Configurations per Tenant)"]
    end

    subgraph API["DocIntel.Api (ASP.NET Core Minimal APIs + Background Worker)"]
        Endpoints["HarnessEndpoints.cs<br/>(/api/harness/tenants, /jobs, /slices/{idx}/pdf)"]
        Queue["Azure Queue / bounded local dispatch hints"]
        Worker["DocumentJobWorker (BackgroundService)"]
    end

    subgraph Workflow["Microsoft.Agents.AI.Workflows Pipeline (DocumentSplittingWorkflow)"]
        E1["1. ExtractPageLayoutsExecutor<br/>(PdfPig Geometry & Footer Extraction)"]
        E2["2. DetectSliceBoundariesExecutor<br/>(100% Deterministic Boundary & Cover-Page Backfill)"]
        E3["3. WritePdfSlicesExecutor<br/>(PdfSharpCore Byte-Level PDF Slicing)"]
        E4["4. IdentifyDocumentsExecutor<br/>(Decision Model Rule Matching)"]
        E5["5. TranslateDocumentsExecutor<br/>(Azure AI Translator + Glossary Protection)"]
        E6["6. GenerateDocumentInsightsExecutor<br/>(Language Model Executive Summaries)"]
        E7["7. ExtractEntitiesExecutor<br/>(Prompt-Guided Named Entity Extraction)"]
        E8["8. ExtractDocumentDataExecutor<br/>(Per-Document-Type Fields & Tables)"]
        E1 --> E2 --> E3 --> E4 --> E5 --> E6 --> E7 --> E8
    end

    subgraph Storage["Azure Persistence & AI Services (Managed Identity RBAC)"]
        CosmosH[("Cosmos DB: harnesses<br/>PK: /tenantId")]
        CosmosJ[("Cosmos DB: jobs<br/>PK: /tenantId")]
        Blob[("Azure Blob Storage<br/>Container: tenant-{tenantId}")]
        Foundry["Microsoft Foundry (AIServices)<br/>gpt-5-mini + Decision Model + AI Translator"]
    end

    UI_Harness -->|PUT /api/harness/tenants/{id}| Endpoints
    UI_Results -->|POST /api/harness/tenants/{id}/jobs| Endpoints
    Endpoints -->|Read/Write Harness| CosmosH
    Endpoints -->|Enqueue JobId| Queue
    Queue --> Worker
    Worker -->|Execute Directed Graph| Workflow
    E3 -->|Upload Split PDFs| Blob
    E4 & E6 & E7 & E8 -->|IChatClient Inference| Foundry
    E5 -->|Translation API| Foundry
    Worker -->|Persist Job & Trace| CosmosJ
    UI_Results -->|Poll Job & Stream Slice PDFs| Endpoints
```

---

## 2. Multi-Tenant Governance Model (`TenantHarness`)

Every tenant (for example, `contoso-legal` or `fabrikam-procurement`) has a versioned **`TenantHarness`** stored in the `harnesses` Cosmos DB container (partitioned by `/tenantId`).

When a document is submitted for a tenant, the workflow reads the tenant's active `TenantHarness` snapshot, which governs **two model tiers** and **six toggleable capabilities**:

| Layer | Setting | Purpose |
|---|---|---|
| **Decision Model** | `DecisionModelId` (`decision-1`) | High-speed, deterministic classification model used in **Stage 4 (`IdentifyDocumentsExecutor`)** to match each PDF slice against admin-defined document rules. |
| **Language Model** | `DefaultModelId` (`gpt-5-mini`, `gpt-4o`, `deepseek-r1`, etc.) | Generative reasoning model used for **Document Insights**, **Entity Extraction**, and **Structured Data Extraction**. |
| **Capability 1** | `DocumentSplitting` (`Enabled`) | Controls whether multi-document PDF packets are split into logical sub-documents or processed as a single document. |
| **Capability 2** | `DocumentIdentification` (`Enabled`, `Rules[]`) | List of `(DocumentName, IdentificationPrompt)` rules evaluated per slice. |
| **Capability 3** | `DocumentTranslation` (`Enabled`, `TargetLanguage`, `AutoDetectSourceLanguage`, `DoNotTranslateTerms[]`) | Translates non-target-language slices while preserving protected legal/product terms. |
| **Capability 4** | `DocumentInsight` (`Enabled`, `InsightInstructions`) | Custom prompt instructions governing how executive summaries are written for each slice. |
| **Capability 5** | `EntityExtraction` (`Enabled`, `ExtractionPrompt`) | Custom instructions governing which entities (`Customer Name`, `Contract Number`, `Total Deal Value`, etc.) are extracted. |
| **Capability 6** | `DataExtraction` (`Enabled`, `Schemas[]`) | Per-document-type schema definitions containing **Key-Value Fields** (`FieldName`, `Instruction`) and **Tables** (`TableName`, `Instruction`, `Columns[]`). |

---

## 3. End-to-End Request & Async Job Lifecycle

```mermaid
sequenceDiagram
    autonumber
    actor User as User / Browser UI
    participant API as HarnessEndpoints
    participant Blob as BlobArtifactStore (tenant-{id})
    participant Cosmos as CosmosJobStore (jobs)
    participant Queue as Azure Queue / local dispatch hints
    participant Worker as DocumentJobWorker
    participant WF as DocumentSplittingWorkflow

    User->>API: POST /api/harness/tenants/{tenantId}/jobs (Multipart PDF)
    API->>Cosmos: Load TenantHarness for {tenantId}
    API->>Blob: SaveOriginalPdfAsync(tenantId, jobId, pdfBytes)
    API->>Cosmos: UpsertJobAsync(Queued, source hash, accepted harness snapshot)
    API->>Queue: TryEnqueueAsync(tenantId, jobId)
    API-->>User: 202 Accepted { jobId, status: "Queued", statusUrl }

    Queue->>Worker: Dequeue(jobId)
    Worker->>Cosmos: Acquire version-fenced ownership; save Running
    Worker->>Blob: Read and verify accepted original SHA-256
    Worker->>WF: ExecuteAsync(original, accepted harness, attempt artifact ID)
    Note over WF: Runs 8 Executors sequentially<br/>(Layout -> Boundaries -> PDF Slice -> Identify -> Translate -> Insights -> Entities -> Data)
    WF-->>Worker: DocumentPacketResult (Segments + DataExtractions + ExecutionTrace)
    WF->>Blob: SaveSlicePdfAsync() under attempt-specific namespace
    Worker->>Cosmos: Fenced save (Completed, segments, executionTrace)

    User->>API: GET /api/harness/jobs/{jobId}
    API->>Cosmos: GetJobAsync(jobId)
    API-->>User: 200 OK (Full Job JSON with Segments, Insights, Entities, Tables, Trace)
    User->>API: GET /api/harness/tenants/{tenantId}/jobs/{jobId}/slices/{idx}/pdf
    API->>Blob: GetSlicePdfAsync(tenantId, active artifact attempt, idx)
    API-->>User: 200 OK (application/pdf stream rendered in iframe)
```

---

## 4. Deep-Dive: The 8-Executor Workflow Pipeline

`DocumentSplittingWorkflow` orchestrates an 8-step directed graph built on `Microsoft.Agents.AI.Workflows` (`WorkflowBuilder` + `InProcessExecution.RunAsync`). Every executor records its latency, model used, and diagnostic notes into `WorkflowExecutionTrace`.

### Step 1: `ExtractPageLayoutsExecutor` (Deterministic Geometry & Footer Extraction)
- **What it does**: Parses the raw PDF byte array using `UglyToad.PdfPig` (`PdfPigLayoutExtractor`) without calling any LLM.
- **How it works**:
  1. Iterates through every page (`1..N`) and reads page geometry (`Width`, `Height`, and orientation: Portrait vs. Landscape).
  2. Groups words into horizontal text lines by clustering words whose baseline $Y$-coordinates are within `3.5 pt`.
  3. **Bottom-10% Footer Zone Extraction**: Any line positioned in the bottom 10% of page height ($Y \le 0.10 \times \text{PageHeight}$) is captured as a candidate footer line.
  4. **Sub-Document Pagination Parsing**: Applies a compiled regex (`(?:Page|Pg\.?|Página|Seite|Pág\.?)\s*(\d{1,3})\s*(?:of|de|von|/)\s*(\d{1,3})`) to extract `(SubPageNumber, SubTotalPages)` (e.g., `"Page 1 of 3"` $\rightarrow (1, 3)$).
  5. **Footer Template Normalization**: Strips page numbers, dates, and volatile digits from the raw footer text to compute a canonical **Footer Template Signature** (e.g., `x20-12661-01-cps-es` or `Form-MBSA-v1`) alongside a lightweight language detector (`en`, `es`, `de`, `fr`).

---

### Step 2: `DetectSliceBoundariesExecutor` (100% Deterministic Multi-Signal Slicing)
- **What it does**: Groups contiguous pages into logical sub-document slices (`DocumentSegment`) using physical layout, pagination resets, cover-page backfill, and fuzzy OCR footer clustering—with **zero LLM calls**.
- **How it works** (`SliceBoundaryAnalyzer.DetectBoundaries`):
  - **If `DocumentSplitting.Enabled == false`**: Keeps all pages (`1..N`) as a single slice (`SingleDocumentMode`).
  - **If `DocumentSplitting.Enabled == true`**: Evaluates four deterministic signals in priority order:
    1. **Cover-Page Lookahead Backfill (Signal A)**:
       Many enterprise contracts (such as a Customer Price Sheet following a 3-page Signature Form) have a **cover page with no footer** on Page $P$, followed by `"Page 2 of 6"` on Page $P+1$.
       Before scanning Page $P+1$, the analyzer checks if Page $P$ had no `SubPageNumber`, Page $P+1$ has `SubPageNumber == 2`, and either:
       - Page $P-1$ reached the end of its previous document (`SubPageNumber == SubTotalPages`, e.g., `3 of 3`), **or**
       - Page $P$ changed physical page dimensions/orientation compared to Page $P-1$ (e.g., Portrait $612\times792$ $\rightarrow$ Landscape $792\times612$).
       When true, a split boundary is placed **before Page $P$** so the cover page stays attached to Pages $P+1..P+5$.
    2. **Explicit Sub-Page Reset (Signal B)**:
       If Page $P$ has `SubPageNumber == 1` (and Page $P > 1$), a new slice starts at Page $P$.
    3. **Previous Document Completed (Signal C)**:
       If Page $P-1$ explicitly ended its pagination (`SubPageNumber == SubTotalPages`, e.g., `Page 3 of 3`), Page $P$ starts a new slice.
    4. **Pagination Total Mismatch or Fuzzy Footer Template Change (Signal D)**:
       - If both pages declare `SubTotalPages` and they differ (e.g., `of 3` vs. `of 6`), a split occurs.
       - Otherwise, the analyzer compares the normalized **Footer Template Signatures** using **Levenshtein similarity** ($\ge 0.72$ threshold). This prevents false splits when OCR slightly garbles a footer on one page (e.g., `x20-12661` vs `x2O-12661`), while cleanly splitting when the form code genuinely changes (e.g., `Form-MBSA-v1` $\rightarrow$ `Form-EA-v2`).

---

### Step 3: `WritePdfSlicesExecutor` (Byte-Level PDF Slicing)
- **What it does**: Physically splits the original PDF into standalone, valid PDF files—one per detected `DocumentSegment`.
- **How it works**:
  - Opens the source PDF stream with `PdfSharpCore.Pdf.IO.PdfReader` (`PdfDocumentOpenMode.Import`).
  - For each slice (`StartPage..EndPage`), creates a new `PdfDocument`, copies only the pages belonging to that slice, and serializes the resulting PDF bytes into `segment.SplitPdfBytes`.
  - When `DocumentJobWorker` finishes workflow execution, each slice's `SplitPdfBytes` is uploaded to the tenant's isolated Azure Blob container (`tenant-{tenantId}/{jobId}/slices/slice-{index}-pages-{start}-{end}.pdf`) and served via `GET /api/harness/tenants/{tenantId}/jobs/{jobId}/slices/{sliceIndex}/pdf`.

---

### Step 4: `IdentifyDocumentsExecutor` (Decision Model Classification)
- **What it does**: Classifies each PDF slice into one of the tenant's admin-defined document types or **`"Undefined"`**.
- **How it works**:
  - Checks `harness.DocumentIdentification.Enabled`. If disabled or no rules exist, labels every slice `"Undefined"`.
  - Otherwise, resolves the tenant's **Decision Model** (`harness.DecisionModelId`, e.g., `decision-1`) via `IModelCatalog`.
  - Sends a structured classification prompt containing:
    - The tenant's configured `(DocumentName, IdentificationPrompt)` rules.
    - The slice's page range, footer signatures, and first `2,500` characters of extracted text.
  - Parses the JSON response (`documentName`, `matchedRule`, `confidence`). Only document names that match a defined rule (case-insensitive) are accepted; any non-matching or low-confidence slice is safely marked **`"Undefined"`**.

---

### Step 5: `TranslateDocumentsExecutor` (PDF Document Translation via `SingleDocumentTranslationClient`)
- **What it does**: Translates any PDF slice (`slice.SplitPdfBytes`, `application/pdf`) whose detected language differs from `harness.DocumentTranslation.TargetLanguage` (for example, Spanish `es` $\rightarrow$ English `en`) into a **translated PDF document (`slice.TranslatedPdfBytes`)** while preserving the original PDF layout, tables, and formatting.
- **How it works**:
  - Uses **Azure AI Document Translation** (`Azure.AI.Translation.Document.SingleDocumentTranslationClient` for synchronous PDF-in $\rightarrow$ PDF-out translation and `DocumentTranslationClient` for batch Blob-to-Blob translation)—**not** raw text translation.
  - Builds a `MultipartFormFileData` containing `slice.SplitPdfBytes` (`application/pdf`) and dynamically generates a TSV glossary (`donottranslate.tsv`, `text/tab-separated-values`) from `harness.DocumentTranslation.DoNotTranslateTerms` (e.g., `MBSA\tMBSA`, `Azure\tAzure`, `Contoso\tContoso`).
  - Invokes `SingleDocumentTranslationClient.DocumentTranslateAsync(targetLanguage, translateContent, sourceLanguage: detectedLang)` to receive the translated PDF binary (`TranslatedPdfBytes`), saves it to the tenant's Blob container (`{jobId}/slices/slice-{i}-translated.pdf`), and extracts `TranslatedText` from the translated PDF via `PdfPig` so downstream executors (Insights, Entities, Structured Data) operate on the translated document.
  - In the UI (`View Document`), users can toggle directly between the **Translated PDF** and the **Original PDF**.

---

### Step 6: `GenerateDocumentInsightsExecutor` (Executive Summary Generation)
- **What it does**: Generates a concise executive summary for each slice and an overall packet summary.
- **How it works**:
  - Checks `harness.DocumentInsight.Enabled`.
  - Resolves the tenant's **Language Model** (`harness.DefaultModelId`, e.g., `gpt-5-mini`).
  - Prompts the model with the admin's `InsightInstructions` (`"Summarize key commercial terms, effective dates, pricing commitments, and anomalies..."`) and the slice's text (`TranslatedText ?? BodyText`).
  - Populates `segment.InsightSummary` and `state.DocumentInsightSummary`.

---

### Step 7: `ExtractEntitiesExecutor` (Prompt-Guided Entity Extraction)
- **What it does**: Extracts structured named entities (`ExtractedEntityItem`: `Name`, `Value`, `Category`, `Confidence`, `PageNumber`) from each slice.
- **How it works**:
  - Checks `harness.EntityExtraction.Enabled`.
  - Uses the tenant's `ExtractionPrompt` and **Language Model** (`gpt-5-mini`) to extract entities in strict JSON format, with deterministic regex fallback (`ExtractHeuristicEntities`) for common commercial identifiers (`MBA/MBSA Number`, `Customer Name`, `Tenant ID`, `Quote Number`, `Currency`, `Total Deal Value`, `Effective Date`).

---

### Step 8: `ExtractDocumentDataExecutor` (Schema-Driven Key-Value & Table Extraction)
- **What it does**: Extracts structured **Key-Value Pairs** and multi-column **Tables** tailored to the slice's identified document type.
- **How it works**:
  - Checks `harness.DataExtraction.Enabled`.
  - Looks up the `DocumentTypeExtractionSchema` in `harness.DataExtraction.Schemas` whose `DocumentName` matches `segment.IdentifiedDocumentName` (determined in Step 4).
  - If a matching schema exists (for example, `Customer Price Sheet` with fields `Customer Name`, `Agreement Number`, `Currency` and table `Pricing Line Items` with columns `Item / SKU`, `Quantity`, `Net Price`), it prompts the Language Model with the exact field instructions and table column instructions.
  - Populates `segment.DataExtraction` (`Fields[]` + `Tables[]`), which renders in the **Structured Data** tab of the UI.

---

## 5. Multi-Tenant Data Storage & Security Architecture

| Resource | Container / Scope | Partition Key / Isolation | Purpose |
|---|---|---|---|
| **Azure Cosmos DB (`DocIntel`)** | `harnesses` | `/tenantId` | Stores the active and versioned `TenantHarness` configuration per tenant. |
| **Azure Cosmos DB (`DocIntel`)** | `jobs` | `/tenantId` | Stores `DocumentJobRecord` documents (status, segment metadata, insights, entities, structured data, and `executionTrace`). |
| **Azure Blob Storage** | `tenant-{tenantId}` | Dedicated container per tenant | Stores raw input PDFs (`{jobId}/input/...`), split slice PDFs (`{jobId}/slices/slice-{i}.pdf`), and `result.json`. |
| **Microsoft Foundry (`AIServices`)** | Model Deployments & Translator | User-Assigned Managed Identity | Hosts `gpt-5-mini` and Azure AI Translator accessed via keyless `DefaultAzureCredential`. |
