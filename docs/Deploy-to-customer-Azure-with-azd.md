# Deploy DocIntel to your own Azure with `azd up`

This is the fastest way to stand up a secured DocIntel in **your own Azure
subscription and Entra directory**. It uses the
[Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/)
to provision the hardened infrastructure in [`infra/main.bicep`](../infra/main.bicep)
and to prepare the Entra sign-in configuration automatically.

For an existing managed deployment driven from GitHub, use the preview-first
**Set up DocIntel Azure** / **Validate and deploy DocIntel** workflows described
in the top-level [README](../README.md) instead. The two paths provision the
same resources; pick one per environment and do not run both against the same
target at once.

## What `azd up` does

1. **Pre-provision** ([`infra/hooks/preprovision.ps1`](../infra/hooks/preprovision.ps1)) —
   ensures the `docintel-web` (confidential web) and `docintel-api` app
   registrations and their service principals exist, and records the directory
   tenant ID plus the deploying admin's object ID (to seed the first tenant
   admin). An unambiguous existing registration is reused; nothing is deleted.
2. **Provision** — deploys `infra/main.bicep`: Container Apps + environment,
   Storage (Blob/Queue), Cosmos DB, Key Vault (with the identity-encryption
   key), a user-assigned managed identity, the Foundry account and the
   `gpt-5-mini` model deployment, and all runtime RBAC. Sign-in runs in
   `Identity__Mode=Entra`; **no connection strings or client secrets are
   deployed** — storage, Cosmos, Key Vault and model access all use the managed
   identity.
3. **Post-provision** ([`infra/hooks/postprovision.ps1`](../infra/hooks/postprovision.ps1)) —
   exposes the enabled `DocIntel.Access` delegated scope on `docintel-api`,
   adds the `<origin>/signin-oidc` redirect URI to `docintel-web`, and adds the
   **managed-identity federated credential** `docintel-web` uses for
   passwordless token exchange. It only adds what is missing and is safe to
   re-run.
4. **Deploy** — builds the root `Dockerfile`, pushes the image to the
   provisioned registry, and updates the Container App.

## Prerequisites

| Tool / access | Why |
|---|---|
| [`azd`](https://aka.ms/azd-install) | Drives provisioning and deploy |
| [Azure CLI (`az`)](https://learn.microsoft.com/cli/azure/install-azure-cli) + `az login --tenant <your-tenant>` | The hooks use `az` for the Entra app registrations (azd and az keep separate sign-in state) |
| [PowerShell 7+ (`pwsh`)](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) | The hooks are cross-platform PowerShell |
| Docker | `azd` builds the container image locally |
| Azure permissions | Create resources + role assignments in the target resource group, and create/own the two app registrations (for example `Application.ReadWrite.OwnedBy`) |

The deploy does **not** grant directory-wide consent. After `azd up`, a
directory admin still admin-consents whatever Microsoft Graph / API permissions
you choose to enable.

## Run it

```bash
az login --tenant <your-directory-tenant-id>
azd auth login
azd init            # only if you have not initialized this environment yet
azd up
```

`azd up` prompts for an environment name, subscription, and location, then runs
the four steps above end to end.

### Useful knobs (optional)

Set these before `azd up` with `azd env set <NAME> <value>`:

| azd env variable | Effect |
|---|---|
| `DOCINTEL_APP_TENANT` | Application tenant slug for the seeded admin membership (lowercase, 1–128 chars). Default `default`. |
| `DOCINTEL_ADMIN_OBJECT_ID` | Explicit first-admin object ID when you deploy non-interactively (e.g. a pipeline). Leave unset to use the signed-in user. |
| `WEB_CLIENT_ID` / `API_CLIENT_ID` | Reuse specific existing registrations instead of resolving by name. |

Keep the azd environment name short and lowercase — it suffixes generated
resource names (e.g. `ca-docintel-<env>`).

## After deploy — verify

- `https://<origin>/health/live` and `/health/ready` return healthy.
- Sign in at the origin, upload a real PDF, and confirm a cited Ask AI answer.
- State persists across a Container App restart.

Live Azure/Entra behavior, model quota, and inference quality must be verified
against your actual subscription; a successful `azd up` provisions and
configures resources but does not itself prove live AI quality or cost.

## Re-running and tearing down

`azd up` and the hooks are idempotent — re-run after changes. `azd provision`
re-applies infrastructure only; `azd deploy` re-ships the image only.
`azd down` removes the provisioned resources (it does **not** delete the Entra
app registrations the hooks created; remove those manually if desired).
