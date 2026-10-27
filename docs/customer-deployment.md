# Guided customer deployment

## Delivery plan

1. **Fresh-install foundation (current implementation):** customer-isolated infrastructure,
   preview/approval, owner validation, separate Entra registrations, runtime federation,
   redirect wiring, digest-pinned public images, and exact-revision readiness.
2. **Distribution:** promote a validated image and matching installer/templates as one
   versioned bundle in a public distribution repository. Include checksums, provenance,
   and a release manifest. Private-source GitHub Releases are not public distribution.
3. **Guided onboarding:** build the Portal entry point on the same setup contract, create
   the first customer workspace without sample tenants, add resumable installation
   receipts and operator recovery, and verify a PDF upload plus a cited Ask AI answer.
4. **Optional integrations:** onboard email/Teams separately, including Microsoft 365
   licensing, mailbox scope, permissions, and consent. Private networking is separate
   work, not an advertised but unimplemented Enterprise tier.

The foundation is a fresh-install tool, not an upgrade or existing-resource adoption tool.
Existing installations continue to use `scripts/Setup-DocIntelGitHub.ps1`. No existing
deployment defaults are renamed. Until distribution and clean-subscription acceptance
are complete, this is not a one-click customer release.

## Prerequisites

Use PowerShell 7.3+ and Azure CLI with Bicep available, including Azure Cloud Shell's
PowerShell environment. Run from a reviewed DocIntel checkout. No local .NET SDK, Docker,
package-feed PAT, GitHub deployment identity, or GitHub Azure secrets are needed.

Sign in to the **customer's directory** using `az login --tenant <directory-id>`.
The operator needs Azure provisioning and runtime-role-assignment authority at the
selected scopes, and Entra authority to create/update the installation's applications.
The installer does not grant itself permissions, grant tenant-wide Graph consent,
or register Azure resource providers. Have the administrator register Microsoft.App,
Microsoft.OperationalInsights, Microsoft.ManagedIdentity, Microsoft.Storage,
Microsoft.DocumentDB, Microsoft.KeyVault, and Microsoft.CognitiveServices first.

Use a new or empty resource group. Each customer receives separate data resources,
runtime identity, web/API registrations, and an explicitly selected platform owner.
The owner has access to every workspace **within this installation**, not to other
customer installations.

Supply a reviewed, anonymously pullable public image from
`ghcr.io/nsethi16/docintel-api@sha256:<64-lowercase-hex-digits>`.
The current change does not publish an image or change repository/package visibility.
Do not substitute `latest` or another mutable tag.

## Preview and apply

```powershell
$setup = @{
    CustomerSlug = 'acme'
    Location = 'westus'
    SubscriptionId = '<customer-subscription-id>'
    Image = 'ghcr.io/nsethi16/docintel-api@sha256:<published-release-digest>'
    OutputDirectory = (Join-Path $HOME 'docintel-acme-setup')
}

.\scripts\Install-DocIntelCustomer.ps1 @setup
# Review customer-plan.json and its PlanHash. Keep this directory private.
.\scripts\Install-DocIntelCustomer.ps1 @setup -Mode Apply `
    -Confirmation APPLY -ExpectedPlanHash '<reviewed-PlanHash>'
```

By default, the signed-in directory user is the initial owner. Supply
`-InitialOwnerObjectId <user-object-id>` to select a different enabled user in the same
directory. Directory/subscription IDs are derived and checked, not copied between forms.
Use different slugs or `-EnvironmentName dev` for separate installations.

Preview performs reads and ARM what-if only. Apply rechecks the input/template/directory/
resource-group snapshot against the reviewed hash before any mutation. It creates the
registrations and service principals, preauthorizes the web client for `DocIntel.Access`,
provisions dependencies without starting the app, wires `/signin-oidc` and managed-identity
federation, then creates the app without redeploying its data roots, runtime roles, or
encryption key. Incomplete/short-circuited ARM analysis is rejected, not treated as approval.
It checks production settings, the exact released image/revision, and provider readiness.
No token-based admin endpoint or client secret is created.

Region preflight uses the published Content Understanding region list and checks the
subscription's advertised GPT-5 Mini model/version/SKU and available token quota. Those
reads cannot guarantee capacity at apply time, prove all write permissions, or establish
data-residency/compliance suitability. The current model uses GlobalStandard processing;
choose and approve the region/deployment type accordingly. Costs are usage-dependent;
review Azure pricing and your workload before typing APPLY.

## Boundaries and recovery

This version deliberately refuses a nonempty resource group. It never silently adopts,
renames, deletes, or repoints an existing installation. Failed applies retain their resources
and report failure; there is no automatic deletion/rollback. Do not retry with a different
customer identity or erase the group to hide a failure. Preserve the setup output and have
an authorized operator inspect any partial infrastructure. Resumable recovery is a later
milestone, not claimed by this first implementation.

Readiness is not a complete customer acceptance test. Sign in as the chosen owner, create
the customer's workspace through New tenant, add authorized users, process a representative
PDF, and ask a question that produces a real source citation. The current app still seeds
its existing sample workspace definitions; sample-free first-workspace onboarding remains
in milestone 3. Email, Teams, subscriptions, licenses, and private networking are not enabled
by this installer.
