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

Chat history is partitioned by a server-derived account identifier and application tenant.
Legacy unscoped history is not automatically assigned to a signed-in account or deleted.
The UI reloads if another browser tab changes the signed-in account.

## Deploy to Azure

**Deployment wiring is not complete for the hardened configuration.** Do not run the legacy
deployment examples unchanged: configure Entra registrations/memberships and protected shared
key storage first, and use a durable shared run store/broker for multiple Container Apps replicas.
The default container filesystem and the local run store are not cloud durability.
Live Entra/Azure verification requires an authorized deployment subscription; local identity tests
use an isolated issuer and generated test keys, not a real directory.

```powershell
# Option 1: Azure Developer CLI (azd)
azd up

# Option 2: PowerShell + Bicep (UAMI + Storage + Container App)
.\Deploy-DocIntelAzure.ps1 `
  -SubscriptionId "<subscription-id>" `
  -ResourceGroup "rg-docintel-dev-westus2" `
  -Location "westus2" `
  -FoundryEndpoint "https://<foundry-name>.services.ai.azure.com/models"
```
