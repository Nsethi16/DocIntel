# DocIntel — Multi-Tenant Agent Harness Studio

A modern **.NET 10 + .NET Aspire + Microsoft Agent Framework (`Microsoft.Agents.AI.Workflows`)** multi-tenant Document Intelligence solution with a clean **Agent Harness Studio** Admin UI.

## Features

Admins can manage isolated **Tenants** and configure each tenant's **Agent Harness** at runtime:

- **Harness-Level Model Routing**:
  - **Decision Model** (`Microsoft Decision 1` / `decision-1`) for fast, closed-set document classification and routing.
  - **Language Model** (`Microsoft Phi-4`, `Microsoft MAI-Reasoning`, `Meta Llama 3.3 70B`, `DeepSeek R1`, `Mistral Large`, `GPT-5 Mini`, `GPT-4o`) via `Azure.AI.Inference` and `Azure.AI.OpenAI`.
- **6 Toggleable & Configurable Capabilities**:
  1. **File Splitting & Grouping**: Deterministic `PdfPig` layout, coordinate, pagination (`Page X of Y`), orientation-shift, and Levenshtein footer clustering that slices multi-document PDFs into standalone PDFs.
  2. **Identify Documents**: Classifies each PDF slice into admin-defined `(DocumentName, IdentificationPrompt)` rules or `"Undefined"` using **Microsoft Decision 1**.
  3. **Document Translation**: Translates non-target-language slices using **Azure Document Translation** (`Azure.AI.Translation.Document` / `Text` on the same Microsoft Foundry resource) while honoring `DoNotTranslateTerms`.
  4. **Document Insights**: Generates executive insights guided by custom **Insight instructions**.
  5. **Entity Extraction**: Extracts structured entities guided by a custom **Entity extraction prompt**.
  6. **Data Extraction**: Extracts per-document structured **Key-Value Pairs** and **Tables (with Columns + Instructions)** mapped to each defined document type.

## Quick Start (Local)

```powershell
# Run API + Agent Harness Studio UI directly
$env:DOTNET_ENVIRONMENT = "Development"
dotnet run --project src\DocIntel.Api --no-launch-profile --urls "http://127.0.0.1:5095"

# Or run with the .NET Aspire Dashboard (OpenTelemetry traces & metrics)
dotnet run --project src\DocIntel.AppHost

# Run unit & workflow tests
dotnet test DocIntel.slnx -c Release

# Browser, identity, and real-process recovery regressions (requires the Release API build)
dotnet build src\DocIntel.Api\DocIntel.Api.csproj -c Release
node --test tests\DocIntel.Ui.Tests\ask-ai.test.cjs tests\DocIntel.Ui.Tests\identity-client.test.cjs `
  tests\DocIntel.Ui.Tests\identity-http.test.cjs tests\DocIntel.Ui.Tests\ask-ai-restart.test.cjs `
  tests\DocIntel.Ui.Tests\document-restart.test.cjs tests\DocIntel.Ui.Tests\admin-ocr.test.cjs `
  tests\DocIntel.Ui.Tests\evidence.test.cjs tests\DocIntel.Ui.Tests\review-client.test.cjs
```

Local development uses explicit loopback-only access. It is not an authentication mode for a
publicly exposed service: `LocalDevelopment` is refused outside `Development` or when listening
on wildcard/non-loopback addresses. Ask AI separately requires a real configured tool-calling
model; missing endpoints do not produce simulated answers.

Document-processing simulation is an explicit development-only setting:
`Models:AllowDevelopmentSimulation` (environment variable `Models__AllowDevelopmentSimulation`).
The Development settings enable it for loopback demos/tests; set it to `false` to require real
configured providers locally. Production refuses this flag. A configured provider's errors never
trigger a switch to another provider or the simulator, even in development.

Jobs expose `hasDevelopmentResults`: `true` identifies development-derived results; `null` means
legacy provenance is unknown. The workspace labels these cases and Ask AI is told to verify
against original source text rather than trust unverified derived results. The explicit
`seed-examples` adapter is available only in loopback development with simulation enabled.
Uploads always require an actual PDF, with a 25 MiB limit; an empty request never creates a sample.
Kestrel and multipart parsing have an explicit 26 MiB total request limit (including multipart
overhead), so oversized bodies are rejected before full application-level buffering. Form values
are separately bounded. Unexpected errors return sanitized `application/problem+json` with a
generated `traceId`, also sent as `X-DocIntel-Correlation-Id`; safe server diagnostics record the
error type/code/site rather than raw provider messages, document values or credentials.

The existing OpenTelemetry listener now receives `DocIntel.Agent` activities for document/Ask AI
runs, every workflow executor (including explicit skips) and model calls. Stage logs carry tenant,
opaque job and harness-version scope, never filenames, source text, question text or raw provider
exceptions. Measured active/completed counts and duration metrics use bounded operation/outcome
dimensions, not per-document or per-tenant metric labels. Token counts are emitted only when
reported by the provider; no estimated costs, fabricated counts or document content are emitted.

Classification, insights (including explicit re-evaluation), entity/data extraction and prompt
columns separate document text/metadata from operator instructions using a shared escaped
`untrusted_document_content` envelope and system-level data-only policy. Prompt templates refer
to source text rather than substituting document content into instruction text; attached PDFs
are covered by the same policy. Prompt-column input fingerprints invalidate pre-boundary caches
on explicit evaluation, never on a read. These are prompt-injection defenses, not a guarantee
that a model cannot be influenced: closed-set/schema validation, authorization and human review
remain independent controls. Ask AI tool results stay structured data with the same data-only rule.

Accepted uploads persist an immutable `original.pdf` in the tenant artifact namespace before the
job is recorded/enqueued. The accepted SHA-256 is stored with the job; processing reads and verifies
that persisted source, not PDF bytes in a queue ticket. Authorized clients can retrieve it at
`/api/harness/tenants/{tenantId}/jobs/{jobId}/original`.

Before a durable document worker publishes `Completed`, it writes an immutable `result.json`
under the active attempt's artifact ID and stores its SHA-256 in the fenced job record. The
snapshot includes the accepted harness, source/layout identities and processing results, without
PDF byte arrays, internal leases or database ETags. Authorized callers can download it through
`.../jobs/{jobId}/processing-result`; reads verify the hash, tenant, source, harness and attempt.
An archive write failure fails the job instead of reporting a completed result with no snapshot.
Development seeding also archives its explicitly labeled results. Existing jobs are not silently
backfilled. `InitialProcessing` is a historical processing snapshot, not a human-approved export:
later prompt re-evaluation and human-review corrections remain separate records. This is not a
claim of immutable-storage/legal-hold compliance or a complete backup/restore system.

Document jobs persist locally in a sibling `document-jobs.sqlite` database (SQLite WAL); override
the private file using `DocumentJobs:DatabasePath`. Azure uses Cosmos metadata and distinct
`document-jobs` / `document-jobs-poison` queues. Shared execution refuses local metadata or queue
name collisions. Dispatch tickets contain only job/tenant identifiers, never filenames or PDFs.
Persisted queued/interrupted jobs are rediscovered, so a full local hint channel or a temporary
queue failure does not discard an accepted upload.

Claims expire after 60 seconds and renew every 20 seconds. Writes require the current owner plus
the local row version or Cosmos ETag; lost owners cannot publish results over a replacement.
Retries use separate artifact namespaces and the public slice route follows the saved active
attempt. Each new upload also freezes its accepted harness configuration, so a restart does not
silently apply newer prompts under an older version label. Current membership and tenant activation
are still enforced. Completed/failed jobs are not replayed; more than three interrupted executions
fail for review/resubmission. Execution is bounded by `DocumentJobs:MaxRunSeconds` (default 1,800;
range 1-3,600); recovery polling uses `DocumentJobs:RecoveryPollSeconds` (default 5; range 1-300).
SQLite covers a single persistent host, not independent ephemeral Container Apps replica disks.

Newly processed documents also persist an immutable, versioned `layout.json` beside their
attempt-specific slices. It contains original page text, stable `w_{page}_{index}` word IDs,
four-corner polygons in normalized top-left coordinates, and image-area coverage. Layout bytes
and the original PDF are bound by separate SHA-256 checks. Job metadata exposes `layoutAvailable`
and an authorized `layoutUrl`; it does not embed the word collection in Cosmos. Legacy documents
without saved evidence return 404 rather than reconstructed coordinates.

Native PDF words are labeled `EmbeddedText` / `PdfPig`, with null OCR confidence. Geometry
regressions include all four page rotations and nonzero page origins. Layout storage rejects
invalid geometry, duplicate IDs, content/identity mismatches, and oversized evidence (16 MiB,
up to 1,000 sequential pages and 50,000 words per page). Stored source evidence is not itself
field grounding, calibrated confidence, or review approval.

## Scanned PDF recognition

`Ocr.Mode` is part of each accepted tenant harness snapshot: `Auto` recognizes a page only when
its body text has fewer than 50 characters and embedded images cover more than half the page;
`Always` requests every page, and `Never` uses only the existing PDF text layer. Admin exposes
this policy under the file-splitting settings and includes it in unsaved-change tracking.

Configure the service-owned `AzureAI:DocumentIntelligenceEndpoint` to enable native Azure
`prebuilt-read` using managed identity. Requests send original PDF bytes plus explicit page
numbers; no client URL, local PDF rendering dependency, or generative text fallback is used.
Recognized words retain actual provider scores, pass geometry/source-page validation, and use
the same footer/pagination rules as embedded text before splitting and immutable layout
publication. Missing configuration, missing pages, malformed output, cancellation and provider
errors fail explicitly. OCR dimensions must match the source page orientation; no unverified
rotation correction is guessed.

Bicep provisions a passwordless Document Intelligence account and the application's data role,
or accepts `documentIntelligenceEndpoint` for an existing account whose access must be configured
separately. Tests verify the native SDK contract and an actual image-only PDF through a scripted
recognizer; live recognition quality and permissions remain unverified here.

Actual recognition results are cached immutably within each tenant, keyed by original-PDF SHA-256,
original page number, and a hash of the provider/model/API/endpoint identity. Recreated workers
reuse validated words and submit only missing pages; `Always` still permits valid cache reuse.
The page evidence reports `ocrCacheHit`, and traces distinguish recognition from reuse. Cache
entries include a checked content digest and input/provider/tenant identity; corruption is an
explicit failure, not an unnoticed provider retry. Raw recognized words are cached, so a new job
still applies its own accepted footer rules. Uncacheable custom providers run without persistent
reuse rather than inheriting another provider's identity.

Concurrent cache misses may both call the provider; immutable first-writer publication prevents
overwrites, not duplicate billing. An air-gapped Tesseract runtime remains outstanding, and cache
reuse is not an exactly-once usage guarantee.

### Live OCR allowances

Each tenant defaults to 100,000 reserved pages per UTC month with an 80% warning threshold.
Administrators can change the live allowance under **Live OCR allowance** in the splitting
settings, independently of the harness save/version. Zero blocks new recognition, not valid
cache reuse. A policy revision prevents concurrent administrators from silently overwriting edits.

Before a provider call, the service atomically reserves every uncached requested page against
the current live limit. Local deployment uses transactions in the private SQLite database;
shared deployment uses a separate `ocr-usage` Cosmos container with same-partition conditional
transactions covering the budget, month counter and unique request record. Retries do not
double-reserve the same request. Old frozen document settings cannot restore an older budget.

Reserved counts are conservative: failed/interrupted or ambiguous provider calls retain their
allowance because the provider may already have performed billable analysis. SDK/network retries
can also differ from invoiced usage; these are not price estimates or Azure billing totals.
Cached pages consume no new allowance. Crossing the warning percentage or exhausting a budget
emits a structured warning. Actual Azure Monitor alert rules are not provisioned by this change.

Tenant-wide administrators can use `GET/PUT .../tenants/{tenantId}/ocr-budget` and
`GET .../tenants/{tenantId}/ocr-usage?period=yyyy-MM` under `/api/harness`. Writes require the current
policy `version` and browser CSRF protection; stale edits return 409. Readers, processors and
document-restricted administrators cannot inspect or change tenant-wide usage controls.

## Field source evidence

Entities, structured fields and table cells retain their existing numeric model scores and add
`provenance` / `confidenceSignals`. A unique whole-word value occurrence within its own original
document slice receives actual word IDs and normalized polygons. Matching normalizes whitespace
and compares ordinally ignoring case; it does not remove punctuation, translate, infer, fuzzy-match,
join across pages or pick the first of several occurrences. Ambiguous, absent, incomplete and
over-64-word reference cases remain explicit and do not acquire invented coordinates. Jobs also
cap attached word references at 1,024 to keep duplicated metadata bounded; excess values receive
an explicit reference-limit status, never partial geometry.

The workspace labels **Model score**, never calibrated confidence, and distinguishes exact source
text from ambiguous/unavailable evidence. Exact matches link to the original PDF page, not a
translated page with unrelated coordinates. Text occurrence alone is not proof that the field
label or meaning is correct, and it is not human approval. Model and grounding signals are
separate; they are not averaged into a misleading probability.

Ask AI receives lightweight source-occurrence status without heavy polygon arrays; original
bounded text remains its citation evidence. Prompt-cache fingerprints track actual entity inputs,
not added provenance annotations, so grounding does not force an unnecessary reevaluation.

Ask AI can separately inspect human review status and approved effective fields with
`inspect_human_review`. It searches/authorizes the document first, verifies the review source
and audit snapshot, pages at most five fields, and rechecks cited review versions before
publishing. Human corrections receive `HumanReviewedField` citations linked to the review
workspace; they are never presented as verbatim original-PDF quotations. Pending review does
not expose unapproved effective-field evidence.

## Human review

Open **Human review** in the workspace or `/review.html?tenant=<tenant>&job=<job>`.
New completed jobs are routed to persisted review by the accepted `ReviewPolicy` (enabled by
default, elapsed-UTC due hours default 24, optional dual review). Existing completed jobs can
start a review explicitly. No uncalibrated score automatically approves a value.

The focused workspace keeps original PDF rendering/highlights separate from fields and human
decisions. It uses pinned PDF.js 4.6.82 from cdnjs with an explicit unavailable-renderer message
and an original-PDF link if loading fails. Clicking a field seeks its original page/region;
hovering a source region identifies the associated original field. Zoom and keyboard shortcuts
are available. Corrections never rewrite model outputs or pretend their new value is supported
by an old highlighted region.

`Reviewer`, `Processor` and `Admin` roles can review only documents they may read; `Reviewer`
does not gain upload, tenant-edit or budget permissions. Production decisions require a verified
human directory identity, not an app-only service principal. Claims expire after 15 minutes.
Every mutation requires the current strong `If-Match: "review-N"` version; stale edits and active
claim conflicts return 409. Reload after an unconfirmed mutation rather than retrying it blindly.
Dual review requires two different verified identities, and the second review cannot silently
rewrite the first review's field decisions. Pending/rejected fields block submission.

Sessions and their audit appends commit atomically in SQLite or the separate `review-sessions`
Cosmos tenant partition. Audit events carry verified actors, source-bound review identity,
sequence, UTC time and a checked SHA-256 continuity chain. Original and effective values are
separate. Development identity activity is explicitly tagged. Review fields are capped at 2,000
and audit versions at 10,000 per session, with reserved terminal-action capacity; source changes
invalidate old snapshots. This accommodates individual decisions for all 2,000 admitted fields.

This is application-level append-only review history, **not** immutable/legal-hold archival,
global endpoint audit coverage, SOC 2 certification or calibrated accuracy. Broad shared-chrome
refactoring, business-calendar SLA computation, leaderboard metrics and model-score bulk
approval are not claimed. All responses have MIME sniffing, referrer, permissions and frame
policies, including errors and static files. Production advertises a one-year HSTS policy;
HTTPS ingress is still required, and unrelated subdomains are not opted in. Review and question
pages use strict script policies; legacy workspace/Admin pages temporarily permit their existing
inline scripts/styles, without `unsafe-eval`, until the event handlers are migrated. Policies
allow only their existing font providers and same-origin/blob PDF frames. Original/slice PDFs
are embeddable by this application only (`SAMEORIGIN`), not blocked by the HTML page anti-framing
policy. This CSP compatibility exception is not a claim of complete XSS prevention.

## Reviewed exports

The review workspace exposes CSV, Excel and JSON downloads after explicit human approval.
`GET /api/harness/tenants/{tenantId}/jobs/{jobId}/export?format=csv|xlsx|json` checks current
document permissions, the approved source-matching review and its audit continuity. Pending,
rejected, missing or stale review never silently falls back to model values. Corrections are
exported as effective human values alongside original values, review outcomes/version, original
source evidence status/pages, source hash and development-provenance markers.

These are normalized field rows, including entity, structured-field and table-cell identities;
they are not a fabricated wide schema across unlike document types. Original model scores are
explicitly uncalibrated and are not scores for human corrections. Processor/Admin callers may
request `view=original` explicitly; those artifacts are labeled `OriginalUnreviewed`, and this mode
is forbidden to Readers/Reviewers even when they can read the approved document.

CSV quotes every cell and neutralizes formula-leading values, including whitespace/control
prefixes. Excel uses the approved Open XML SDK with inline-string cells and no formulas/macros.
Values beyond Excel's cell limit are rejected rather than truncated; JSON retains exact values.
Exports have bounded row/text/output sizes and direct authenticated downloads, not long-lived
bearer SAS links that survive permission revocation.

An immutable reference-only export event is persisted before bytes are released. The
`X-DocIntel-Export-Id` response correlates format, actor, review/source version and content SHA-256;
it does not log raw field values or prove that the client received every byte. This is application
audit evidence, not immutable regulatory archival. Parquet, bulk/background exports, arbitrary
webhooks, and SharePoint/Power Automate delivery remain subsequent connector work.

## Identity and document access

Production defaults to **Entra** and fails startup when identity configuration is incomplete.
Browser sign-in uses authorization code + PKCE and an HttpOnly application session; API clients
use validated bearer tokens. Application tenant slugs are **not** Entra tenant IDs. Server-side
memberships map `(directory tenant ID, object ID)` to an application tenant and a role, with
explicit document access. An empty document grant allows no documents.

Configure `Identity` through environment/configuration providers:

| Setting | Purpose |
|---|---|
| `DirectoryTenantId` | Entra directory issuing accepted identities |
| `ClientId` | Confidential web application registration; redirect URI is `<PublicOrigin>/signin-oidc` |
| `ApiClientId` | Separate API application registration/audience |
| `PublicOrigin` | Production HTTPS origin, without a path/query |
| `DataProtectionDirectory` | Persistent, service-owned, protected key-ring directory outside the web root |
| `DataProtectionBlobUri` | Alternative shared key-ring blob URI; production also requires Key Vault encryption |
| `DataProtectionKeyIdentifier` | Versionless HTTPS Key Vault key URI for envelope encryption |
| `ClientSecret` | Web credential from a secret provider, **never committed**; omit when using federation |
| `UseManagedIdentityAssertion` | Use managed-identity token exchange instead of a web client secret |
| `ManagedIdentityClientId` | Optional UAMI ID; otherwise `AZURE_CLIENT_ID` or system-assigned identity |
| `ApiAccessPermission` | Delegated scope/app role name; default `DocIntel.Access` |
| `Memberships` | Server-side tenant role/document grants described below |
| `PlatformAdministrators` | Explicit directory/object pairs allowed to administer every application tenant |

The API registration must expose the delegated permission and application role. App-only callers
also need an explicitly mapped membership with `ServicePrincipal: true`; neither a sender address
nor an asserted `Admin` token role grants application privileges. Legacy email/Teams webhook
payload endpoints require that verified service identity; they do not send messages or treat a
submitted sender address as the user's identity. Native personal-chat Teams delivery is separate,
as described below.
Trusted server adapters have a separate durable delivery state: the answer completes before
delivery is leased, current tenant activation and the original permission scope are rechecked,
and reviewed citations must still reference the same review version. The send marker is saved
before contacting a transport. Interrupted or uncertain sends become `OutcomeUnknown` and are
never automatically replayed; `Sent` means transport acknowledgement, not proof of user receipt.
Run polling exposes only delivery status/timestamps/error codes, not trusted routing configuration.
Conditional run creation prevents duplicate channel activities from replacing an existing answer.
Managed-identity sign-in requires a matching federated credential on the web app registration;
this code does not create registrations or consent/permission grants.

### Native Teams personal-chat adapter

The supported Microsoft Teams SDK (`Microsoft.Teams.Apps` 2.1.2, approved NuGet feed) hosts
`POST /api/messages` only when `Channels:Teams:Enabled` is true. The default is disabled.
This pilot supports **personal chats only**, one configured bot/application tenant and one exact
public-cloud service URL. Shared channels/group chats, arbitrary callback destinations and
anonymous bot authentication are rejected. A verified Teams AAD object ID must have current
server-side DocIntel membership; directory membership alone does not grant document access.

| `Channels:Teams` setting | Purpose |
|---|---|
| `Enabled` | Explicit opt-in; false leaves the native route and transport unregistered |
| `TenantId` | Entra directory GUID; must match `Identity:DirectoryTenantId` |
| `ClientId` | Separate bot application/UAMI client GUID, not the web/API registration |
| `ApplicationTenantId` | Fixed DocIntel tenant slug; never chosen from incoming message text |
| `ServiceUrl` | Exact HTTPS `smba.trafficmanager.net` regional/Teams URL for the registered bot |
| `PublicOrigin` | HTTPS application origin; must match `Identity:PublicOrigin` |
| `ClientCredentials` | Optional native SDK federation configuration; omit for a bot registered as UserAssignedMSI |

Environment variables use double underscores, for example `Channels__Teams__Enabled`.
For a UserAssignedMSI bot, assign that user-managed identity to the host and set `ClientId` to its
client ID. For an application registration using managed-identity federation, configure the SDK's
`ClientCredentials` with `SourceType: SignedAssertionFromManagedIdentity` and the managed identity
client ID. Provisioning identities, the Azure Bot registration, the Teams application manifest
(personal scope only), federation and installation/consent requires authorized operator setup;
the deployment script does not provision a bot or change directory permissions.

Native SDK signature/key, audience, lifetime and issuer validation runs before activity handling;
this adapter additionally restricts the issuer to Bot Framework and RS256. Browser/API authentication
remains separate, including PKCE, CSRF and live membership reload. SDK registration is deliberately
isolated from the application's default authorization policy and host-owned configuration lifetime.
The bounded in-memory cache is for reacquirable SDK tokens, **not** for questions or conversation state.

Accepted questions receive a private acknowledgement and a sign-in-required
`/ask-run.html?tenant=...&run=...` progress link. Activity deduplication, job execution and answer
delivery use the existing persistent run store/leases/recovery worker. A single bounded plaintext
answer preview includes citation labels and the full-answer link; large answers are not split into
replay-prone partial messages. The progress page shows the complete answer, collapsed cited evidence,
original PDF links or version-labeled human-review links, and explicit execution/delivery failures.
It only polls the original run, never resubmits it. An uncertain send must be investigated rather
than automatically replayed.

No real bot registration or private Teams message has been exercised by offline checks. Those
checks cover native-route authentication refusal, the existing bearer/browser/CSRF/revocation
flows with Teams enabled, trusted routing, duplicate activity recovery and bounded previews.
Mailbox Graph ingestion and Entra-confirmed email reply authorization remain separate connector work.

Each membership has `DirectoryTenantId`, `ObjectId`, `ApplicationTenantId`, `Role` (`Reader`,
`Reviewer`, `Processor`, or `Admin`), `AllDocuments`, and `DocumentIds`. Use the **user or enterprise service
principal object ID**, not an application/client ID. `AllDocuments` defaults to false;
`DocumentIds` is an explicit allowlist. Creating/processing new documents requires tenant-wide
processing permissions. Admins can edit tenant configuration, but document access is still explicit.
Membership changes are re-evaluated on requests and Ask AI tools/final answers; authentication
registration changes require restart.

Reads no longer evaluate missing prompt columns or rewrite document results. After changing
definitions, use the existing explicit prompt-evaluation action with processing/admin permissions.
Harness saves must carry the `harnessVersion` returned by the last read; new tenants use version
zero as create-only intent. A stale/missing tenant version or competing creation returns HTTP 409
with the current configuration, rather than retrying an overwrite. Cosmos replacements use the
read ETag; local files use an inter-instance OS lock and atomic whole-file replacement. Local reads
reload the shared file rather than retaining a stale process cache. Admin Studio preserves the
unsaved draft on conflicts or unknown network outcomes, offers a draft/server comparison and
requires explicit reload before another save. Edits typed during a successful save remain unsaved
against the newly acknowledged version.
Browser mutations require a request-verification token, acquired automatically by the bundled UI.
The default per-identity request limits are 300 reads and 20 mutations per minute, configurable
through `Identity:ReadRequestsPerMinute` and `Identity:WriteRequestsPerMinute`.

Prompt columns use the selected catalog model's explicit PDF capability. Text-only models receive
text with `ProcessedAsFileBase64=false`; PDF-capable models require the real slice PDF. There is no
provider-error text retry or invented PDF from extracted text. Invalid result JSON fails rather
than producing a templated answer. Cache reuse also requires a matching input/model/mode
fingerprint, and a failed reevaluation does not publish partial slice results.

Live translation requires a server-configured HTTPS endpoint and actual source PDF bytes. Tenant
settings cannot redirect credential-bearing translation calls to another endpoint. The provider
auto-detects source language; `und` records that a separate detected-language value is unavailable,
not that the source is English. Missing translated artifacts return 404 instead of the original
PDF or a newly generated text document. Confidence values are model signals, not calibrated
probabilities; missing/zero confidence is not inflated to 95-98 percent.

Chat history is partitioned by a server-derived account identifier and application tenant.
Legacy unscoped history is not automatically assigned to a signed-in account or deleted.
The UI reloads if another browser tab changes the signed-in account.

## Deploy to Azure

The PowerShell/Bicep path wires shared Ask AI Blob snapshots, Cosmos document metadata, fenced
execution, separate durable Azure Queue dispatch/poison handling, and Blob-backed session keys encrypted by a rotating
Key Vault key, queue backlog scaling, and readiness/liveness probes. Storage and Key Vault access
use managed identity. No connection strings or web client secrets are deployed.

Pre-create separate web/API Entra registrations and the API `DocIntel.Access` delegated/application
permissions. Provide a private JSON file containing the server-owned `Memberships` and
`PlatformAdministrators` described above. The script validates explicit subscription/directory/app
IDs and registration permissions before provisioning. Preview is the default; `-Apply` is explicit.
`-ConfigureWebRegistration` additionally opts into adding the HTTPS redirect URI and the UAMI
federated credential to the named web registration. It does not create registrations or grant
directory consent. ACR tokens are held only in process environment during image publishing.

Live Azure/Entra validation still requires an authorized target subscription. Native shared storage
has SDK contract tests; this environment could not authenticate to the approved npm feed for the
official Azurite emulator. Real-process tests verify local document/Ask AI crash recovery, accepted
source integrity, and attempt isolation. Shared Cosmos/Queue behavior has SDK contract coverage,
not live/emulator integration proof. Live storage, identity, and rollout verification remain prerequisites.

```powershell
# Preview against an existing resource group (no apply)
.\Deploy-DocIntelAzure.ps1 `
  -SubscriptionId "<subscription-id>" `
  -DirectoryTenantId "<directory-tenant-id>" `
  -WebClientId "<web-registration-client-id>" `
  -ApiClientId "<api-registration-client-id>" `
  -IdentityGrantsPath "<private-server-grants-file>" `
  -ResourceGroup "rg-docintel-dev-westus2" `
  -Location "westus2"

# Apply after review; add -ConfigureWebRegistration only when authorizing that registration change.
# The same parameters are required.
```

The script bootstraps shared infrastructure before replacing the app, publishes a uniquely tagged
image without Docker login credentials on disk, and claims readiness only after the health probe
passes. It restores the prior Azure CLI subscription and transient registry credential environment.
The Aspire/`azd` path is not yet equivalent to this hardened deployment path.
