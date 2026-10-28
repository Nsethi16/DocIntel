# DocIntel — Document Intelligence Workspaces

A modern **.NET 10 + .NET Aspire + Microsoft Agent Framework (`Microsoft.Agents.AI.Workflows`)** Document Intelligence solution with isolated workspaces and an Admin UI.

The application calls each isolated document and settings space a **workspace**. Existing API routes,
JSON/storage identifiers such as `tenantId`, and the `TenantAdmin` role value remain compatible;
the role is displayed as **Workspace Admin**. A Microsoft Entra directory tenant and a source-document
field named **Tenant ID** are separate concepts and keep their original names.

## Conservative lifecycle controls

`DELETE /api/harness/tenants/{tenantId}/jobs/{jobId}` soft-deletes an available document.
`DELETE /api/harness/tenants/{tenantId}` deactivates a tenant. Both require the server-assigned
Admin role, an `X-DocIntel-Confirm-Delete` header exactly matching the target document/tenant ID,
and browser CSRF verification when using a session cookie. Whole-tenant deactivation requires
tenant-wide administration. Active worker ownership/version fences prevent deleting over an
in-flight result; conflicts are explicit rather than replayed.

Responses explicitly say `dataRetained: true` and `permanentErasure: false`. These operations
are **not physical erasure**: originals, archived JSON, OCR cache, review/audit history and
stored conversations are retained. Deleted documents cannot be read, searched, reviewed,
exported or downloaded normally. Ask AI tracks all retrieved document IDs, not just citations,
and rechecks availability before final answers, polling and channel delivery. Legacy
document-based runs without that complete reference inventory require a fresh question.
Previously downloaded files, external messages and offline browser copies cannot be recalled.

Tenant `retention` metadata supports optional `documentDays`, `conversationDays`, `auditDays`
(1-36,500) and `legalHold`; no retention periods are guessed. Automated permanent purge is
not implemented or enabled, and attempts to set `permanentPurgeEnabled: true` are rejected.
Configuring retention metadata does not start a deletion scheduler. Production erasure,
user-forgetting, legal holds and archival guarantees require an explicitly approved lifecycle
design and end-to-end purge verification; no GDPR/HIPAA/SOC 2 compliance is claimed.

## Features

The interface remains document-first, with the original three Admin sections and individual
custom-result tabs. Processing settings use compact rows; opening a Manage drawer hides the
previous drawer without clearing its fields. Saved prompt columns stay visible while the
create/edit form is disclosed on demand. Ask AI temporarily hides the redundant document rail
and restores its prior state when returning to documents. These presentation changes do not
alter model routing, permissions, processing, integrations or explicit save behavior.

### Human review is retired

The application no longer creates review sessions, advertises review tools to Ask AI, or requires
approval before exports. Review navigation and Admin policy controls are removed; legacy review
API operations return HTTP 410. Exports default to `MachineExtracted`, never a human-verification
claim. Historical review/audit records and their compatibility readers are retained without
purge or automatic conversion into current answers. Old browser citations explain retirement
instead of reopening editable review controls.

### Content Understanding ingestion

Production document ingestion now uses Azure Content Understanding `prebuilt-layout`, pinned to
GA API `2025-11-01` through the native `Azure.AI.ContentUnderstanding` SDK. Configure the server-owned
`AzureAI:ContentUnderstandingEndpoint` to the Foundry resource root endpoint (not `/models`).
Assign its runtime managed identity the supported Content Understanding data-plane access and verify
regional availability/default model prerequisites in Content Understanding Studio. No resource,
model mapping or directory permission is silently created by application startup.

All original PDF pages use the same layout pipeline, irrespective of legacy OCR Auto/Always/Never
settings. PdfPig validates page counts/dimensions and copies original pages for splitting; it does
not supply a competing text representation when CU is configured. Native page Markdown, spans,
word quadrilaterals, paragraphs and table metadata are retained with original page identity.
Downstream extraction/prompts prefer Markdown (or an explicitly translated text result).

Page-level reuse is tenant/original-hash/analyzer/API/endpoint/mapping-version bound. Cache hits do
not consume fresh page reservations; incomplete pages, unexpected warnings, invalid geometry or
provider failures fail explicitly with no silent native-text fallback. CU page reservations share
the existing live operational page allowance; they are not an exact invoice or token-cost estimate.
The old DI endpoint is not used by the new provider. Offline loopback development can still use
native PDF text with explicit development settings; no live CU quality/cost claim is made from mocks.
The deployment template now supplies CU instead of provisioning another Form Recognizer account.

Local immutable artifacts are staged and flushed before publication. Per-target, OS-owned file
locks coordinate publishers across store instances/processes before the final move, so concurrent
writers cannot replace a winning cache entry, original, layout, result snapshot or export audit.
The empty publication lock files remain in place to avoid an unlink/reopen race; locks release when
the owning process closes/exits. Azure Blob publications retain storage-enforced create-only conditions.

Admins can manage isolated **Workspaces** and configure each workspace's **Agent Harness** at runtime:

- **Harness-Level Model Routing**:
  - **Decision Model** (`Microsoft Decision 1` / `decision-1`) for fast, closed-set document classification and routing.
  - **Language Model** (`Microsoft Phi-4`, `Microsoft MAI-Reasoning`, `Meta Llama 3.3 70B`, `DeepSeek R1`, `Mistral Large`, `GPT-5 Mini`, `GPT-4o`) via `Azure.AI.Inference` and `Azure.AI.OpenAI`.
- **6 Toggleable & Configurable Capabilities**:
  1. **File Splitting & Grouping**: Deterministic `PdfPig` layout, coordinate, pagination (`Page X of Y`), orientation-shift, and Levenshtein footer clustering that slices multi-document PDFs into standalone PDFs.
  2. **Identify Documents**: Scores extracted slice text against admin-defined `(DocumentName, IdentificationPrompt)`
     criteria plus `"Undefined"` using the configured **Microsoft-Decision-1** deployment and its native decision API.
  3. **Document Translation**: Translates non-target-language slices using **Azure Document Translation** (`Azure.AI.Translation.Document` / `Text` on the same Microsoft Foundry resource) while honoring `DoNotTranslateTerms`.
  4. **Document Insights**: Generates one integrated insight for the complete file, or one per complete
     identified logical document when splitting and identification are enabled and all documents are
     identified. Disabled, absent or incomplete identification falls back to one file insight covering
     every page; it never falls back to page-by-page summaries. Separate documents of the same type
     remain separate. Existing results require reprocessing or explicit reevaluation to use this policy.
  5. **Entity Extraction**: Extracts structured entities guided by a custom **Entity extraction prompt**.
  6. **Data Extraction**: Extracts per-document structured **Key-Value Pairs** and **Tables (with Columns + Instructions)** mapped to each defined document type.

### Native Microsoft Decision form identification

The `decision-1` catalog entry is a **native Microsoft-Decision-1 scorer**, not an alias for
GPT-5 Mini and not a generative chat client. Identification uses the tenant's configured
`DecisionModelId`, sends extracted title/top-line/footer text as `state`, and supplies the
active form rules plus an explicit Undefined option as closed `choice` criteria.
The native route is `<resource-root>/providers/microsoft/v1/systemone`; it is not chat
completions or the unrelated OpenAI Decisions API. No PDF/image bytes or generated rationale
are requested. The chosen class probability is retained, and results publish only after every
slice has a valid classification. Provider failures or unknown choices never trigger a GPT fallback.

| Runtime setting | Purpose |
|---|---|
| `AzureAI__DecisionEndpoint` | Optional separate HTTPS Foundry resource root hosting the native model |
| `AzureAI__DecisionDeploymentName` | Exact deployment name, default `Decision-1` |
| Tenant `decisionModelId` | Logical configured decision profile, normally `decision-1` |

When no separate endpoint is supplied, the catalog uses the configured Foundry resource root
(removing its standard `/models` suffix), never a hidden Azure OpenAI language-model alias.
For a model on another resource, configure that resource root explicitly and approve runtime
managed-identity access there. This does not repoint language, translation or Content Understanding
providers. The request uses the deployment name; the response may identify its underlying model
as `microsoft-decision-1`.
Existing-app GitHub releases reconcile this connection before publishing an image. They preserve
an established selection; otherwise they prefer a native deployment in the app's bound Foundry
account, or the unique native deployment in its resource group. If none exists, they provision
version 1 / GlobalStandard / capacity 1 in the bound account after availability/quota checks.
Only the selected account's Cognitive Services User role is added for the already-assigned runtime
identity, through the authorized deployment principal. No deployment-principal self-elevation,
account replacement, language/OCR repointing or GPT fallback is performed.

The exact model/role ARM preview is hash-fenced and included in the deployment summary. The
`docintel-dev` environment controls release approval; configure its reviewers to require human
approval. Normal configuration preflight still requires both native settings after reconciliation,
and the released revision must pass live startup model probes.
For ambiguous same-group resources, `DOCINTEL_DECISION_RESOURCE_ID` and optionally
`DOCINTEL_DECISION_DEPLOYMENT` select the intended target. `DOCINTEL_DECISION_ENDPOINT` is
also supported and must match its verified resource metadata. Conflicting existing settings,
cross-group automatic discovery, conditional grants, incomplete previews and missing authority
stop the release explicitly rather than overwriting a connection or bypassing permission checks.

Fresh customer infrastructure provisions `Microsoft-Decision-1` version `1`, GlobalStandard
capacity `1`, alongside the language model. Native model availability and quota are checked before
apply. An explicit existing Decision endpoint skips that new model deployment; access to that
existing resource is an operator prerequisite, not silently granted by application startup.
Microsoft Decision is preview and regional inference availability must be verified, not inferred
solely from deployment creation. Startup uses a real closed-choice probe when model probing is enabled.
Explicit offline development simulation remains marked as development, never production model proof.

See [Microsoft's native Decision model API](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).

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

### Reproducible standalone builds and CI

This repository is a standalone GitHub-to-Azure application. It deliberately restores from
**public NuGet.org only**, with package-source mapping and checked-in, content-hashed lockfiles
for every project. It does not require Azure DevOps, an internal package feed, a corporate PAT,
or a package-feed secret in GitHub. CI and container restore still use `--locked-mode`; package
changes must intentionally regenerate and review affected locks. Changing the package source does
not authorize ignoring lock/hash errors or substituting package versions.
Aspire's development AppHost references operating-system-specific dashboard/orchestration tools,
so it uses `packages.win-x64.lock.json` on Windows and `packages.linux-x64.lock.json` on the GitHub
Linux runner. Other projects use `packages.lock.json`. Additional AppHost platforms need their own
reviewed SDK-RID lockfile; locked restore is never disabled to accommodate platform differences.

The Dockerfile uses the checked-in `NuGet.config`, excludes local job/artifact data, editor
metadata and common credential files, and runs as the .NET non-root application user with a
writable `App_Data` directory. A normal container build needs no package credential:

```powershell
dotnet restore DocIntel.slnx --configfile NuGet.config --locked-mode
docker build --tag docintel:local .
```

The GitHub workflow validates all Release projects, .NET/browser/recovery tests, infrastructure,
and a Linux container build without publishing before deployment. Pull requests can restore public packages without deployment secrets. Azure
credentials are available only to the separate deployment job; keep that job disabled for pull
requests and do not use `pull_request_target` to run untrusted changes with privileges.

Documentation-only pushes do not deploy. Deployment requires `main`, an explicit repository
variable `DOCINTEL_DEPLOY_ENABLED=true`, and the `docintel-dev` environment. The workflow uses
Azure OIDC federation and `az acr login`, not registry passwords. Local restore/build checks do
not prove a Linux image build or live rollout; the latter require Docker or the GitHub runner,
authorized Azure access, and working network connectivity.

If NuGet.org fails with a TLS/certificate/network error, fix the machine's network/trust setup
or run validation on an approved network/runner. Do not disable certificate validation, package
auditing or warnings, add an unknown mirror, or silently use a private-feed fallback.

### Customer installations

For a new customer's subscription, see [Guided customer deployment](docs/customer-deployment.md).
The `customer-setup` artifact from validation provides one guided Cloud Shell setup: choose a
customer/subscription, review the plan/costs, and approve. The default kit builds the exact reviewed
public source commit inside the customer's own private ACR, then deploys its image digest and creates
the first customer workspace. It does not require public binary publishing, a new distribution
repository, local Docker/.NET, manual app-registration GUID copying, or GitHub OIDC. Saved-state
recovery resumes completed foundations and recorded builds without resetting customer data.
An optional prebuilt public-image channel remains available after explicit publisher setup.
The guided kit also offers optional organization-directory onboarding: reuse existing permission or
approve only Graph `User.Read.All`, verify lookup from the runtime identity and administrator eligibility,
then enable the people picker. A separate Entra-only administrator handoff needs no Azure resource access.
Use `-ConnectDirectory -DirectoryRequired` when verified directory-backed user maintenance is mandatory.
Existing installations use the flow below.

### Set up an existing Azure deployment from GitHub

Use **Actions > Set up DocIntel Azure** for infrastructure and runtime configuration, then
**Validate and deploy DocIntel** for the application image. You do not need to manually create
each queue, database container, key, runtime role or endpoint setting.

The setup workflow discovers the existing app, reuses its Container Apps environment and HTTPS
origin, and identifies bound storage, Cosmos, Key Vault, Foundry and runtime identity resources.
Unambiguous existing DocIntel resources are adopted without redeploying their root SKU, networking
or security properties. Missing dependencies and runtime resource roles are provisioned by Bicep.
An existing `gpt-5-mini` deployment/key is not reset. Runtime settings are merged; unrelated settings,
secret references, identities, application image, ingress and custom domains are preserved.

#### One-time prerequisites in GitHub/Azure

GitHub cannot establish its own initial trust or elevate its deployment identity:

| Setting / permission | Where and why |
|---|---|
| Three `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` secrets | GitHub repository or `docintel-dev` environment; existing `CADOCINTELDEV_AZURE_*` fallback names still work |
| GitHub federated credential | On the selected deployment identity in Azure; use the **actual subject from the Azure login log**, matching issuer and audience |
| Resource provisioning and role-assignment authority | Explicitly approved at the target resource group/resource scopes; image-push/app-update-only roles cannot provision the full stack |
| `docintel-dev` GitHub environment | Restrict to main. Configure required reviewers if an approval is wanted between Preview and Apply |
| `DOCINTEL_WEB_CLIENT_ID`, `DOCINTEL_API_CLIENT_ID` repository variables | The two existing app-registration client IDs; discovered from existing runtime settings when available |
| `DOCINTEL_ADMIN_OBJECT_ID` GitHub secret | Your directory user object ID, needed only when no nonempty bootstrap grants already exist |

For this repository's observed GitHub token, the deployment trust subject is:

```text
repo:Nsethi16@22230402/DocIntel@1422293341:environment:docintel-dev
```

The issuer is `https://token.actions.githubusercontent.com` and audience is
`api://AzureADTokenExchange`. Do not assume the older name-only subject matches.

For Azure resources, the deployment identity needs the permitted management operations in the target
group plus narrowly scoped role-assignment authority for runtime RBAC, and permission to attach the
runtime identity. It does not need subscription-wide Owner. Scope/delegate those permissions using
your Azure administrator; this workflow never grants privileges to its own deployment identity.
Runtime Cosmos access is a Cosmos data-plane role, separately provisioned by the template.

#### Run Preview first

1. Open **Actions > Set up DocIntel Azure > Run workflow** on `main`.
2. Select **Preview**. Leave directory application configuration off when reusing your manually
   configured web/API registrations without directory-write access.
3. Download **docintel-infrastructure-preview**. Review the selected resources, changes, role
   assignments, model provisioning and expected charges in `setup-plan.json` / `what-if.json`.
4. If an existing resource cannot be matched unambiguously, set its corresponding optional variable
   and preview again. Missing administrator configuration is reported without creating a default user.

Optional repository variables resolve explicit existing bindings:
`DOCINTEL_RUNTIME_IDENTITY_NAME`, `DOCINTEL_STORAGE_ACCOUNT_NAME`, `DOCINTEL_COSMOS_ACCOUNT_NAME`,
`DOCINTEL_KEY_VAULT_NAME`, `DOCINTEL_FOUNDRY_ACCOUNT_NAME`, and `DOCINTEL_CONTAINER_NAME`.
The normal `AZURE_RESOURCE_GROUP`, `AZURE_CONTAINER_APP_NAME`, and `AZURE_CONTAINER_REGISTRY`
variables select the target; their existing defaults are retained.

Preview performs reads and ARM what-if only, not Azure/Entra writes. Setup refuses data-account
repointing, ambiguous/cross-group data adoption, nonstandard partition keys, incompatible resource
types, local-only state overrides, and silently collapsing multiple AI accounts. Such migrations
need an explicit separately reviewed configuration, not guesses or duplicate resources.
Existing queues are discovered through the paginated Azure management API, without storage account
keys or deployment-identity queue data access. An incomplete inventory stops setup rather than
treating unread queues as missing and overwriting their configuration.

#### Apply and release

After reviewing Preview, run the workflow again with **Apply**, confirmation **APPLY**, and its
**reviewed_plan_hash** from the Preview job summary. It first compares a fresh preview with that
reviewed hash, then the apply job re-discovers targets and again requires the same plan before
changing anything. Use `docintel-dev` required reviewers to review/approve that
run's preview before allowing the apply job. A resource/app/template change blocks stale apply.
Resource provisioning can incur charges.

Apply provisions dependencies/RBAC and merges passwordless production settings, endpoint URLs,
protected browser keys and (when needed) your explicitly provided initial tenant-admin assignment.
It preserves existing channel toggles; absent directory/email/Teams toggles default to disabled.
It does not migrate local documents or overwrite existing tenant choices.

Then run **Validate and deploy DocIntel** on `main`. This builds/pushes the validated image, updates
the app, and checks the exact image/latest-ready revision and `/health/ready` before claiming success.
Setup and release serialize on the same Azure target so they cannot race.

If a release fails readiness, **Actions > Diagnose DocIntel Azure > Run workflow > main**
reads the latest revision, endpoint-presence flags, bounded startup error types and sanitized
request/document failure categories. It does not
deploy, change settings or print raw application logs, endpoint values, secrets or tenant data.
A successful diagnostics run means evidence was collected, not that application readiness passed.

Container Apps terminates TLS before forwarding HTTP to Kestrel. In non-development ACA hosting
(identified by `CONTAINER_APP_NAME` and `CONTAINER_APP_ENV_DNS_SUFFIX`), the app restores only the
HTTPS scheme from the single `X-Forwarded-Proto: https` header that ACA overwrites at ingress,
and only for `Identity:PublicOrigin`'s host. Forwarded client IP, host and prefix headers are not
trusted. Other hosting environments retain normal proxy trust defaults. Keep the application port
behind ACA HTTP ingress, without additional TCP ingress; release preflight enforces that boundary.
Secure session/CSRF cookies and CSRF checks remain required. Upload failures show an HTTP-specific
message and a safe correlation reference rather than silently failing.

Entity extraction requests a closed, non-null structured-output schema and explicitly instructs
non-empty entity names/values, confidence between 0 and 1, and an empty list for absent fields.
Length and confidence bounds remain server-validated because Azure's structured-output schema subset
does not support the `minLength`, `minimum` and `maximum` keywords. Invalid model
output still fails validation; it is not silently dropped, normalized from percentages or replaced
with simulated data. Slice entity results are published together only after every response validates.

Ask AI's `search_documents` tool advertises a closed query schema with page sizes restricted to
1-20, non-null entity arrays, and explicit filter/token limits. Unused filters are null and unused
entities are `[]`. Tenant identity and document access scopes remain server-owned; invalid tool
arguments fail before storage access and cannot be reported as successful empty searches.
Search results are references, not citation evidence. Ask AI must inspect each document it lists
or uses for factual claims, then cite the inspection's server-issued `evidenceId`, never its `jobId`.
The final answer schema is closed and non-null; unknown citations, missing evidence and mismatched
inline markers have distinct safe failure codes and never produce a fabricated answer.

For a saved failed Ask AI question, **Diagnose saved Ask AI run** can inspect only status and progress
counts. Its explicit **Replay** mode requires `REPLAY` confirmation and may incur model usage charges.
It runs the CLI inside the active app replica using the existing runtime identity and the original
actor's current document permissions. Replay is limited to failed web runs with built-in read-only
tools, has a 120-second deadline, never delivers messages, and does not modify the saved run or persist
its answer. Only fixed failure codes/counts are printed. No public diagnostic endpoint is exposed.

The older Fabrikam sample used `deepseek-r1`, whose legacy inference adapter cannot serve Ask AI
tool calls. New samples use `gpt-5-mini`, but saved profiles are not reset during deployment.
For the original version-1 Fabrikam sample only, the release workflow offers
**upgrade legacy sample defaults** (off by default). It verifies the seed identity, initial version,
allowed models and feature-routing defaults, validates the replacement client, and persists only
the default-model change through the normal optimistic-concurrency store. Edited/custom profiles
are never upgraded. A concurrent edit is re-read and is not overwritten. A qualifying upgrade is
logged and increments the harness version; readiness must still pass normally. Ordinary releases
turn this one-time startup option off.

#### Optional directory registration automation

The setup input **configure directory applications** enables permission-gated creation/configuration
of `docintel-web` / `docintel-api`, the delegated `DocIntel.Access` scope, web callback and runtime
federated credential. It preserves existing redirects/scopes and never creates a conflicting
same-named application role. Missing registrations can be created in Apply, never Preview.

This requires separately approved Microsoft Graph application-management permission, for example
`Application.ReadWrite.OwnedBy` with ownership of existing applications it must edit. The workflow
does not grant directory roles, Graph consent, or ownership to itself; do not grant directory-wide
write access merely for convenience. Without that authorization, reuse the existing client IDs and
manually completed registration/federation settings. Corporate people lookup, Exchange mailbox
provisioning/transport policy, Teams installation and user consent remain separate integrations,
disabled by default rather than represented as connected.

Fresh sample tenants now both use the provisioned `gpt-5-mini` tool-calling model. Existing tenant
model overrides remain untouched: unsupported/undeployed model selections still fail readiness and
must be corrected explicitly. Region/model quota, external network policies and live inference
quality must still be verified against the actual subscription.

### Personal Azure subscription release settings

The default existing target is Container App `ca-docintel-dev`, resource group `Docintel`,
and registry `acrdocinteldevokjs3fevazuto`. Override those defaults with repository Actions variables
`AZURE_CONTAINER_APP_NAME`, `AZURE_RESOURCE_GROUP`, and `AZURE_CONTAINER_REGISTRY` when using different
resources in your subscription. Use the registry's short name, not its `.azurecr.io` hostname.
A personal Azure subscription still has an Entra directory; no Microsoft corporate
account or enterprise employee directory integration is required. The current application uses
directory-based sign-in, not unrestricted sign-in by any personal Microsoft account.

1. In GitHub repository **Settings > Environments**, create/configure **docintel-dev** and
   restrict deployment to **main**. Add approval protection where your plan supports it.
2. Configure the deployment identity's Azure federated credential for issuer
   `https://token.actions.githubusercontent.com`, audience `api://AzureADTokenExchange`, and the
   actual subject shown above/from the workflow login log. This is the identity GitHub uses to deploy,
   not the runtime identity or browser web registration.
3. Make these workflow secrets available to the deployment job:
   `AZURE_CLIENT_ID` (deployment identity client ID),
   `AZURE_TENANT_ID` (your subscription's Entra directory ID), and
   `AZURE_SUBSCRIPTION_ID`. Set all three together when choosing a new identity/subscription.
   The older `CADOCINTELDEV_AZURE_CLIENT_ID`, `CADOCINTELDEV_AZURE_TENANT_ID`, and
   `CADOCINTELDEV_AZURE_SUBSCRIPTION_ID` names remain supported as fallbacks, so an existing connection need not
   be recreated. These are identifiers; no Azure client password or
   package-feed credential is required. Give that identity registry push and target Container App
   update access at the narrow resource scopes, using the roles appropriate to the registry mode.
4. Run **Set up DocIntel Azure** as described above to prepare the runtime: attached managed identity,
   persistent Cosmos/Blob/Queue storage, protected sign-in keys, web/API Entra registrations,
   HTTPS redirect/origin and initial tenant-admin grant. Production must use `Identity__Mode=Entra` and
   `Models__AllowDevelopmentSimulation=false`. See **Deploy to Azure** and **Identity and document access**
   below. Do not publish loopback-development identity or depend on ephemeral container files.
5. Configure a real `AzureAI__AzureOpenAIEndpoint` and deployed model such as `gpt-5-mini`,
   `AzureAI__ContentUnderstandingEndpoint` at the supported Foundry resource root, and any
   translation endpoint needed by enabled features. Grant the runtime identity the required
   data-plane roles. Select only deployed models in every active tenant.
6. Initially leave `Identity__Directory__Enabled`, `Channels__Email__Enabled`, and
   `Channels__Teams__Enabled` false. Operator-configured tenant grants still work. Directory
   photos/search, a mailbox, and a Teams bot are optional separate integrations, not prerequisites
   for deploying the web application.
7. After runtime setup, set the repository **Actions variable** `DOCINTEL_DEPLOY_ENABLED` to
   `true`, publish the latest feature changes and merge them to **main**. The push triggers
   **Validate and deploy DocIntel**. To redeploy the code already on main, use **Actions >
   Validate and deploy DocIntel > Run workflow > main**, or:

```powershell
gh workflow run "Validate and deploy DocIntel" --repo Nsethi16/DocIntel --ref main
```

The release workflow builds/pushes a commit-tagged image and updates the existing app.
Infrastructure is managed by the separate preview-first setup workflow, not implicitly by an image
push. Neither workflow copies local documents/tenant settings or grants its own Azure/Graph privileges. Verify
`/health/live`, `/health/ready`, authorized sign-in, a real PDF upload, a cited Ask AI answer and
persistence after restart. Directory/user-profile integration can be enabled later with approved
read-only Graph permission on the runtime identity.

To validate an unpublished feature branch without deploying, push it and dispatch the same
workflow with `--ref <feature-branch>`. Only validation (including the non-publishing image build)
runs; Azure deployment is independently restricted to `main` and the explicit deployment opt-in.

Local development uses explicit loopback-only access. It is not an authentication mode for a
publicly exposed service: `LocalDevelopment` is refused outside `Development` or when listening
on wildcard/non-loopback addresses. Ask AI separately requires a real configured tool-calling
model; missing endpoints do not produce simulated answers.

### Analytical Ask AI

Ask AI can browse bounded document-summary/field overviews to discover relevance by meaning,
inspect original logical-document text, and build question-specific evidence-backed datasets.
This first increment uses the existing stores and generative tool-calling model: it does not
provision a vector/embedding index or silently select a different deployment. Summary previews
are navigation hints, never source quotations. Continuation tokens and incomplete search scope
remain visible; browsing a page is not an exhaustive workspace search.

The `analyze_evidence` tool validates record quotations against inspected original text and
computes `rows`, `sum`, `average`, `count`, `min`, `max` and `outliers` in server-side decimal code.
Every nonempty cell must occur in that record's quote. Dates and decimal/grouping formats are
explicit; aggregation units must occur in the same quote. Ambiguous symbols do not imply a
currency, units remain separate, missing values never become zero, and repeated source records
are excluded. Include a source-record identifier column when otherwise identical records are
actually distinct. Outliers use the 1.5xIQR rule with at least four records per unit.

Runs expose additive `analyses` with typed columns, computed rows, server-resolved citation
numbers, coverage and method notes. The web conversation and standalone run view render local,
accessible SVG bar/date-line charts plus evidence-linked tables. Chart specifications contain
only allowlisted types and dataset column references, never executable code or arbitrary HTML.
Mixed-unit charts and oversized charts become explicitly disclosed tables. Email/Teams
payloads retain a bounded textual table preview instead of silently dropping analytical results.
No new frontend framework, package, cloud resource or change to the workspace navigation is required.

Web submissions may send `previousRunId`. The server loads at most four completed prior turns
owned by that same actor and workspace, checks current permissions and document fingerprints,
and supplies bounded prior answers as untrusted context. Follow-ups must reinspect source and
recompute results; old citation IDs are not reused as fresh evidence. Changed/deleted sources or
legacy cited runs without source versions require a new chat. Existing text-only answers and
browser-local chats remain readable.

Each question is bounded to 64 tool calls, 48 model rounds, 48 inspected evidence windows, 200
document references, four analyses, eight columns and 100 input records per analysis. Charts
contain at most 40 rows; analytical output is additionally byte-bounded. These limits and the
existing execution timeout are safeguards, not assertions of complete corpus coverage.
Quotation checks establish occurrence and calculations are reproducible; they do not prove that
the model interpreted a clause or assigned a field correctly. Read the sources, assumptions and
coverage before acting on an inferred recommendation.

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

`/health/live` (and legacy `/alive`) checks process liveness independently of dependencies.
`/health/ready` combines authenticated shared-storage probes with cached startup verification of
active harness/provider routing, required OCR/translation configuration and PDF-capable prompt
columns. Misconfiguration keeps readiness at 503 without crashing the UI or exposing provider
diagnostics. The existing `/health` aggregate remains available.

Set `Readiness:ProbeModelsOnStartup=true` to verify actual model access with one bounded, one-output-token
request per configured model at startup. This can incur provider usage and is **off by default**:
the readiness description explicitly distinguishes configuration-only checks from live inference.
Repeated health polling never generates model calls. `Readiness:TimeoutSeconds` bounds the whole
startup verification (default 60, range 1-300); restart after provider/harness changes to re-run it.
Live OCR/translation content requests are not issued by readiness checks. A disconnected local
preview may be live but not ready for its enabled AI/OCR features.

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

View Document checks the PDF response before opening its native browser preview, with explicit
missing-file, access, timeout and server-error notices, a retry, and an open-in-new-tab link.
If an original slice artifact is missing, its exact page range can be copied in memory from the
accepted, SHA-256-verified original; this does not rerun AI, change results or write artifacts.
Missing translations are never replaced with the original. Legacy results without recoverable
source PDFs require restoration from backup or uploading the actual original again; extracted text
is never rendered into a replacement and presented as source evidence.

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

## Unified PDF layout

Azure Content Understanding `prebuilt-layout` (GA `2025-11-01`) is the cloud ingestion provider
for text PDFs and scans, with native Markdown/page spans and original word quadrilaterals.
See Content Understanding ingestion above for configuration and cache behavior. Legacy OCR
Auto/Always/Never values are retained for old harness compatibility/offline development only;
they do not bypass CU page analysis when a CU endpoint is configured. Use a supported Foundry
region and validate the resource/analyzer before production use. PdfPig remains the PDF
validation/physical-slicing component, not a second cloud text extractor.


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

## Machine-extracted exports and retained history

Human review is no longer an application step. New jobs do not create review sessions, Ask AI
does not advertise a review tool and the workspace/Admin do not expose approval controls.
Legacy review API operations return HTTP 410, and the old page explains retirement.
Existing review/audit data is retained without purge or promotion into current answers.

`GET .../jobs/{jobId}/export?format=csv|xlsx|json` exports current machine-extracted values with
`view=MachineExtracted` metadata, original source/provenance and uncalibrated scores. It never
claims human verification and is available to authorized document readers without approval.
Explicit legacy `view=original` remains Processor/Admin-only and labeled unreviewed; removed
`view=reviewed` is not accepted. CSV formula defenses and safe text-only Open XML workbooks remain.
Reference-only immutable export receipts record the actual response SHA before download.

All responses retain MIME sniffing, anti-framing, referrer and permissions policies. Script CSP
permits self-hosted external application scripts only; review compatibility assets are not
loaded by the retired page. Workspace/Admin use shared safe action dispatch and no inline
executable handlers or eval; dynamic style attributes retain a style-only compatibility exception.
Original/slice PDFs remain same-origin embeddable. HTML is no-store and JS/CSS revalidates.


## Identity and document access

Production defaults to **Entra** and fails startup when identity configuration is incomplete.
Browser sign-in uses authorization code + PKCE and an HttpOnly application session; API clients
use validated bearer tokens. Application workspace slugs are **not** Entra tenant IDs. Server-side
memberships map `(directory tenant ID, object ID)` to an application workspace and a role.
Admin-managed people have workspace-wide reading/Ask AI access. Historical operator-configured
document restrictions remain enforced and are not broadened by the new two-role interface.

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
| `Memberships` | Server-side workspace role/document grants described below |
| `PlatformAdministrators` | Explicit directory/object pairs allowed to administer every application workspace |
| `Directory:Enabled` | Enable read-only enterprise people/profile/photo lookup through Microsoft Graph |

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

## Agent identity and channels

Admin Studio has **Document Processing**, **Agent & Channels**, and **Users & Access** tabs. Agent & Channels
stores workspace-versioned `agent.displayName`, `purpose`, `tone` (Professional/Friendly/Concise),
`instructions`, `emailEnabled` and `teamsEnabled`. The style preview is illustrative, not a model
completion. Channel toggles are distinct from read-only operator connection status; no token,
password, arbitrary callback URL or privileged role is configured through personality fields.
Name/purpose/guidance are bounded and JSON-encoded into presentation instructions; fixed
authorization, evidence, citation and tool policies always remain in force. Each executed run
captures its agent profile and harness version for consistent web/email/Teams responses.
The section tabs support arrow/Home/End navigation. Switching to Users & Access
hides harness-only controls without collapsing the header or shifting the section navigation.
All three settings sections share borderless rows, darker medium-weight titles, gray
**Manage** controls, dark switches and one centered 800px content column shared with the workspace
header and section tabs. Row text remains left-aligned and controls stay right-aligned.
Agent fields open one native disclosure
at a time without discarding drafts; the illustrative preview is optional. Channel Manage controls
only reveal setup guidance: they do not enable a channel, grant permissions or send a test message.
Changes still require **Save**. Narrow screens stack channel controls without changing the existing
three-section navigation or individual document result tabs.
The Documents sidebar uses a quiet vertical navigation, a top-level **Upload PDF** action and
flat selected rows; document selection is keyboard accessible. Navigation and control labels use
Title Case, while filenames, custom prompt names and document contents remain unchanged. All entry
pages share the same neutral palette, local Segoe UI/system font stack and visible keyboard focus;
there are no external font downloads. Technical values and code can still use monospace.
**Documents**, **Graph** and **Ask AI** use the same sidebar frame, navigation and manual collapse
toggle. Documents and Graph show **Upload PDF**, **Search Documents** and the document list. Ask AI
uses the same locations for **New Chat**, **Search Chats** and **Past Chats**, with selection and
deletion beside each saved conversation. The right pane contains only the welcome/composer or
selected conversation; there is no duplicate chat toolbar or second sidebar. Returning to Documents
restores its search and selection. Unsent questions survive mode changes and manual sidebar toggling;
switching workspaces clears the draft. History remains browser-local and partitioned by verified
account and workspace; this does not add cross-device chat synchronization.
Graph is canvas-first: one entity-filter control, removable AND-filter chips and a compact connection
count replace the summary-card dashboard. Document and entity details open on node selection or
through **Documents**; they are not an always-open second list. Documents use dark squares and
entities use teal circles, with measured, local-font labels and full names in tooltips/details.
The canvas uses its actual display size, adjusts when resized and stops animating once settled.
Graph filters and layout are cleared when switching workspaces.
The **Theme** icons in the sidebar footer offer **System** (monitor), **Light** (sun) and **Dark**
(moon), with tooltips and an explicit selected state. The choice is saved for this browser
and shared with settings and question-status pages. Dark mode themes controls, dialogs and the Graph
canvas; it does not invert or alter original PDFs. Appearance changes repaint Graph without moving it.
Graph reserves a fixed filter row and overlays details, so selections do not resize the canvas or
restart a settled layout. Drag the sidebar's right edge to resize it, or focus the divider and use
arrow keys (Shift for larger steps), Home or End. Double-click resets its default width. Explicit
widths are remembered across Documents and Admin, bounded to keep the content usable; narrow screens
retain an overlay drawer without a drag handle.

### Enterprise users and unified access

`Users & Access` searches existing corporate Entra people; it never creates corporate accounts,
passwords, or Entra administrator roles. Select one or several people, then assign **User**
(workspace-wide document reading and Ask AI) or **Workspace Admin** (`TenantAdmin`; also document processing, user
management, harness configuration and channels). All new people have the same document collection;
there is no document-scope picker. Directory photos, title, department and office help distinguish
people; initials are shown when no photo exists. Missing/failed directory connections are explicit,
not simulated employee records. User lists are paged, and search excludes already assigned users
across all pages. Only workspace-wide admins can search people, read photos or edit memberships.

Corporate directory identity `(directory tenant ID, user object ID)` is stable even when work
email changes. DocIntel persists assignment/role/status and reference-only access-change audit
records separately from harnesses, source artifacts and model prompts. The directory remains
the authority for profile, primary mail/UPN and enabled/member state. Its read-only Graph calls
normally require approved `User.Read.All` application permission; this also permits user-photo
retrieval, or use separately approved `ProfilePhoto.Read.All` when appropriate. Do not grant
directory-write permissions. The configured managed identity needs explicit corporate consent;
enabling the UI does not grant Graph access.

Membership changes use an expected `version` and atomically persist their audit with the change.
Stale edits return 409; do not retry mutations automatically. Removing/disabling a user leaves
a tombstone, overriding an old operator bootstrap grant even after restart. The last active
workspace-wide admin cannot be removed, disabled or demoted; assign a replacement first. Bootstrap
the first admin using existing `Identity:Memberships` or an explicitly configured platform admin.
Ordinary workspace admins cannot promote anyone to platform administrator. Existing service principals
and legacy restricted grants remain operator-owned; restricted assignments cannot be upgraded
through this UI.

The same current membership is resolved for HTML entry points, workspace lists, backend operations,
Ask AI tools/final answers, Teams reception and email execution/delivery. Existing session/API
tokens do not freeze their old role. Membership revocation is checked on subsequent operations;
directory account state has a bounded 30-second cache and lookup failures never grant access.
Display/photo caches are not permission sources. Shared deployments persist access in a private
`tenant-access/v1/` prefix in the existing run Blob container with ETag compare-and-swap; local
execution uses atomic, inter-process-locked files under `App_Data/tenant-users`. Existing
`Identity:Memberships` remain bootstrap/compatibility grants unless explicitly overridden.
Directory group provisioning/SCIM, last-activity monitoring and corporate account creation are not
implemented.

Both channel toggles are off by default. Operator configuration alone is insufficient:
enable the intended channel for the matching tenant in Admin. Current tenant activation,
channel toggle, actor grants, all referenced document IDs and source availability are rechecked
before delivery. Durable send intent is saved before external operations; uncertain sends are
`OutcomeUnknown`, not automatically retried. HTTP resilience retries are disabled for unsafe
methods so a POST is not silently replayed beneath the outbox. Sent means service acknowledgement,
not user receipt.

### Teams personal chat and mentions

The supported Microsoft Teams SDK hosts `POST /api/messages` only when `Channels:Teams:Enabled`
is true. Configure:

| `Channels:Teams` setting | Purpose |
|---|---|
| `Enabled` | Explicit operator opt-in |
| `TenantId` | Same Entra directory as `Identity:DirectoryTenantId` |
| `ClientId` | Bot registration/UAMI client GUID, separate from web/API registration |
| `ApplicationTenantId` | Fixed DocIntel tenant, never selected by message text |
| `ServiceUrl` | Exact HTTPS regional/Teams `smba.trafficmanager.net` service URL |
| `PublicOrigin` | Same HTTPS app origin as `Identity:PublicOrigin` |
| `ClientCredentials` | Optional native SDK federation settings; omit for UserAssignedMSI bots |

SDK signature/key/audience/lifetime checks and the explicit Bot Framework RS256 issuer policy
run before handling. Personal messages queue directly under the verified sender's AAD membership.
Group/channel messages require a structured mention entity for the configured bot: a display-name
string such as `@Meridian` alone is not authentication. The adapter requests a private conversation
with the verified user; the answer is never delivered to the shared conversation. Shared replies
contain a generic acknowledgement and actor-protected progress link only. If personal-chat
creation is unavailable (for example, the bot is not installed personally), the same run remains
available through that authenticated link rather than posting document details to the channel.

Provision/authorize the bot registration, identity, federation and Teams app installation
outside the app. Its manifest must declare the personal/team/group-chat scopes needed for the
intended use and the user must be eligible for the personal bot chat. This code does not create
registrations, grant directory permissions or install apps.

### Email mailbox and automatic assigned-user questions

Configure the operator-owned `Channels:Email` section:

| Setting | Purpose |
|---|---|
| `Enabled` | Explicit opt-in; disabled leaves the worker/routes/transport unregistered |
| `TenantId` | Same Entra directory as the web/API identity |
| `MailboxObjectId` | Directory object GUID of the single connected mailbox |
| `MailboxAddress` | Address shown in Admin connection status and confirmation messages |
| `ApplicationTenantId` | Fixed application tenant for incoming questions |
| `PublicOrigin` | Same HTTPS public origin used for sign-in |
| `PollSeconds` | Delta synchronization cadence, 10-300 seconds; default 30 |
| `AuthorizationMode` | `ConfirmSender` (safe default) or explicitly configured `TrustedInternal` |
| `CorporateSenderProof` | Secret transport attestation for `TrustedInternal`; store in a secret provider, never in source/Admin fields |

The runtime managed identity needs Microsoft Graph `Mail.Read` and `Mail.Send` application access
restricted to this mailbox through the organization's supported Exchange application access/RBAC
controls. Directory lookup requires approved application access to read the user's primary
`mail`/`userPrincipalName` (for example, `User.Read.All`); have an administrator approve and scope
the permissions. Do not grant tenant-wide mailbox access just to bypass setup. No credentials
are accepted in incoming emails or confirmation requests.

The mailbox worker persists an exclusive, shared Graph delta checkpoint in the existing run
store. It processes new inbox mail after first activation, not historical mailbox contents;
restarts retain the original checkpoint. Metadata pages and per-message bodies are bounded;
oversized/unsupported messages are logged as not accepted rather than blocking the cursor forever.
Questions must be plain text and at most 8,000 characters. Automatic replies/self-mail are ignored.
Immutable activity identity deduplicates messages. No incoming attachment or URL is fetched.

An email From address is not trusted as authorization. The initial message receives only a
source-free, 24-hour confirmation link. The sender signs in, sees the question and destination,
and explicitly confirms through a CSRF-protected POST. Graph verifies the signed-in directory
object owns the original sender address before revealing/executing the question. Forwarding the
link to a different authorized user cannot use that user's permissions to send data to the
original sender. A GET never runs the agent. Confirmation queues the same durable Ask AI run
once. Answers use the snapshotted personality, include citations/source links, and go to the
verified original address only; Reply-To/CC/BCC from the inbound message are not used.
Aliases not returned as the account's primary mail/UPN are rejected rather than guessed.

For **automatic email without interactive sign-in**, set `AuthorizationMode=TrustedInternal`
only after the operator has configured and verified a trusted corporate mail path. This also
requires `Identity:Directory:Enabled=true`. The dedicated mailbox's Exchange transport policy must
remove **all** preexisting `X-DocIntel-Internal-Sender-Proof` headers before trusted stamping,
then stamp the protected mailbox's delivery copy with the configured high-entropy secret only
for authenticated internal corporate mail. Do not use sender-domain matching, SPF/DKIM alone,
an externally supplied header, or an accepted-domain address as this authentication decision.
The proof must never be exposed through outbound mail, other recipients' delivery copies, forwarding,
model content, logs, or Admin APIs. Require a dedicated protected transport path, reject/strip
external or relay-originated claims, disable unauthorized mailbox forwarding, and validate
mixed-recipient/bypass/connector cases before enabling this mode. If your mail environment cannot
guarantee these invariants, leave confirmation enabled; the app cannot establish Exchange rule
correctness from an incoming header alone.

The application requires one exact proof header and matching Graph `sender`/`from` identities.
It resolves the work address to one active corporate directory member and checks their current
DocIntel tenant assignment. Accepted messages queue the existing Ask AI pipeline as that employee,
without a challenge or confirmation email. The answer is sent only to the employee's authoritative
directory work address, never arbitrary Reply-To/CC/BCC recipients. Untrusted, ambiguous, disabled
or unassigned senders create no question and receive no document answer. Membership and directory
address ownership are checked again before delivery. Rotate the transport proof as a credential;
changing authorization mode/proof invalidates old profile-bound pending mail rather than authorizing
it under a different trust policy.

Graph requests use a fixed HTTPS host, mailbox identity and Graph token audience, with redirects
disabled. Stored continuation links must belong to the configured mailbox delta resource.
Interrupted/uncertain challenges and answers are not automatically resent. Inspect run status
for failures/unknown outcomes; no end-user receipt or exactly-once provider billing claim is made.

### Activation and verification

Environment-variable names use `__` separators. Configure real identities/resources/Graph consent
securely, enable the matching tenant channel, then verify a personal Teams question, a real
shared mention with a private answer, and the chosen email authorization/answer round trip with two
different users to verify destination isolation. For automatic mail, also test spoofed/missing/duplicate
proofs, differing sender/from, outside-directory mail, unassigned/disabled users, membership revocation
and unrelated mailbox denial. Verify actual Exchange delivery separately from Graph acknowledgement.
The current send is a new plaintext email, not an Outlook-threaded reply; attachment ingestion and
dedicated failure-notification emails are not implemented. Endpoint refusals, native SDK/Graph contracts,
deduplication, delta restart behavior and confirmation UI have offline tests; no real mailbox,
bot installation, external message, directory consent or cloud deployment was performed here.



For legacy operator bootstrap configuration, each membership has `DirectoryTenantId`, `ObjectId`, `ApplicationTenantId`, `Role` (`Reader`,
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

Prefer the GitHub setup workflow above for an existing deployment. The advanced local PowerShell path
uses separate web/API Entra registrations and the enabled API `DocIntel.Access` delegated scope;
an application role is not required for browser sign-in. Provide a private JSON file containing the server-owned `Memberships` and
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
