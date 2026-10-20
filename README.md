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
  tests\DocIntel.Ui.Tests\identity-http.test.cjs tests\DocIntel.Ui.Tests\ask-ai-restart.test.cjs
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

Accepted uploads persist an immutable `original.pdf` in the tenant artifact namespace before the
job is recorded/enqueued. The accepted SHA-256 is stored with the job; processing reads and verifies
that persisted source, not PDF bytes in a queue ticket. Authorized clients can retrieve it at
`/api/harness/tenants/{tenantId}/jobs/{jobId}/original`. Originals alone do not make the remaining
document-dispatch queue restart-safe.

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
nor an asserted `Admin` token role grants application privileges. Email/Teams adapters require
that verified service identity and still return payloads rather than delivering messages.
Managed-identity sign-in requires a matching federated credential on the web app registration;
this code does not create registrations or consent/permission grants.

Each membership has `DirectoryTenantId`, `ObjectId`, `ApplicationTenantId`, `Role` (`Reader`,
`Processor`, or `Admin`), `AllDocuments`, and `DocumentIds`. Use the **user or enterprise service
principal object ID**, not an application/client ID. `AllDocuments` defaults to false;
`DocumentIds` is an explicit allowlist. Creating/processing new documents requires tenant-wide
processing permissions. Admins can edit tenant configuration, but document access is still explicit.
Membership changes are re-evaluated on requests and Ask AI tools/final answers; authentication
registration changes require restart.

Reads no longer evaluate missing prompt columns or rewrite document results. After changing
definitions, use the existing explicit prompt-evaluation action with processing/admin permissions.
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

The PowerShell/Bicep path wires shared Ask AI Blob snapshots, renewable storage-enforced leases,
durable Azure Queue dispatch/poison handling, Blob-backed session keys encrypted by a rotating
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
official Azurite emulator. Do not treat those tests as proof of a live cloud deployment. Document
processing jobs still use their existing process-local dispatch queue; Ask AI's shared queue does
not make document ingestion restart-safe. This remains a separate production rollout prerequisite.

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
