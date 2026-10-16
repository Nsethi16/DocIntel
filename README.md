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
dotnet run --project src\DocIntel.Api --urls "http://localhost:5095"

# Or run with the .NET Aspire Dashboard (OpenTelemetry traces & metrics)
dotnet run --project src\DocIntel.AppHost

# Run unit & workflow tests
dotnet test DocIntel.slnx -c Release
```

## Deploy to Azure

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
