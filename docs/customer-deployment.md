# Guided customer deployment

## Customer experience

Use the versioned installer ZIP, not a local application build. In Azure Cloud Shell
**PowerShell**, signed in to the customer directory:

1. Download the `customer-setup` artifact from the successful validation run, or an approved
   distribution release. Verify the ZIP against its `checksums.txt` and extract into a new directory.
2. Run `.\Start-DocIntelSetup.ps1`. Enter a customer short name and select the subscription.
   The production environment and West US region are defaults; parameters override them.
3. Review the saved resource/directory plan and Azure pricing, then type `APPLY`.
4. Open the reported customer workspace URL and sign in as the selected owner. Process a
   representative PDF and verify an Ask AI answer with real source citations.

The default kit uses **CustomerRegistryBuild**: Azure builds the exact reviewed public
source commit in a new private registry owned by the customer. It does not depend on a public
GHCR image, another public repository, publisher credentials, or GitHub-to-Azure OIDC.
The existing source repository must remain publicly readable for this explicit mode.
It never sends a GitHub credential or privately accessible source to the build service.

The ZIP includes the full source commit, image-source contract, per-file hashes, and
compiled ARM template. The build's final SHA-256 image digest is captured before app deployment.
Customers do not need .NET, Docker, Bicep, a package-feed PAT,
GitHub Azure secrets, a GitHub deployment identity, or access to the application source.
Internal manifest hashes detect corruption; the ZIP checksum from the trusted release
page is the external trust anchor. Never execute an unverified download.

A guided Cloud Shell entry point is intentional: an ARM-only button cannot complete
the directory work after Azure allocates the runtime principal and final app origin.
The same session orchestrates those phases without copying four GUIDs between forms,
storing client secrets, or exposing an HTTP admin-promotion endpoint.

## Before applying

Use PowerShell 7.3+ and Azure CLI in commercial Azure. Sign in to the **customer's directory**
using `az login --tenant <directory-id>`. The subscription directory and Graph credential
must match. The installer does not change your default Azure CLI subscription.

The operator needs Azure provisioning and runtime-role-assignment authority at the selected
scopes, plus Entra authority to create/update the installation's applications. Those are
separate permissions. The installer does not grant itself permissions, grant tenant-wide
Graph application consent, or automatically register resource providers.
The subscription-scoped entry point also requires permission to create subscription deployments
and the selected resource group; resource-group Contributor alone is not sufficient for that entry point.

Have the Azure administrator register Microsoft.App, Microsoft.OperationalInsights,
Microsoft.ManagedIdentity, Microsoft.Storage, Microsoft.DocumentDB, Microsoft.KeyVault,
and Microsoft.CognitiveServices. Review the region, model quota, data-processing geography
and usage-dependent costs. The pinned model uses GlobalStandard processing; choosing a
storage region alone does not establish a data-residency or compliance guarantee.
CustomerRegistryBuild additionally requires Microsoft.ContainerRegistry registration and
ACR Tasks availability in the subscription. Basic registry storage and remote build compute
are billable and appear in the approval plan; a build has a 1,200-second service timeout.
The normal source Dockerfile restores content-hashed locked packages without feed credentials.

The first install requires a new or empty resource group. Each customer receives separate
data resources, runtime identity and web/API registrations. The default initial owner is
the signed-in directory user. Use `-InitialOwnerObjectId <user-object-id>` when deploying
on behalf of a different enabled user. The owner administers every workspace within this
installation, not other customer installations.

## Optional guided parameters

```powershell
.\Start-DocIntelSetup.ps1 -CustomerSlug acme -CustomerName 'Acme Legal' `
    -SubscriptionId '<customer-subscription-id>' -Location westus `
    -EnvironmentName prod

# Read-only preview; never prompts for APPLY:
.\Start-DocIntelSetup.ps1 -CustomerSlug acme -PreviewOnly
```

By default, state is retained under `$HOME/.docintel/installations/`, separated by customer,
environment and subscription. `-StateDirectory` selects another private persistent folder.
Keep this directory: it contains deployment receipts and directory identifiers, not credentials.
Do not upload it to the public distribution repository.

The configured customer workspace is created automatically, using only the provisioned
GPT-5 Mini and decision-1 routes. It does not create Contoso/Fabrikam sample definitions.
Existing saved workspace settings are never replaced by customer bootstrap. Installations
without bootstrap settings retain their existing legacy seeding/migration behavior.

## Resume and conservative recovery

Re-run the **same extracted bundle and setup command**. The launcher detects its journal,
keeps the original owner/image/settings and produces a new reviewable plan:

- If application registration setup was interrupted before the foundation started,
  installation-tagged applications are rediscovered without guessing or duplicating them.
- After a completed foundation, resume verifies the live Azure deployment parameters and
  resource-group request binding, repairs sign-in wiring, then creates only the app.
  It does not redeploy data roots, runtime roles, model deployments, or encryption keys.
  Already-allocated model capacity does not require another quota allocation.
- If the app already exists with the saved image, owner and identity/workspace bindings,
  resume only verifies production configuration and exact-revision/provider readiness.
  It does not overwrite app settings, change images, remove owners, or update registrations.
- An interrupted customer-registry build resumes its saved ACR run. No new run is queued
  once its ID has been recorded. The result must be exactly the requested repository/tag,
  Linux AMD64 platform and a valid SHA-256 digest in the foundation's registry. The app
  pulls that digest using its managed identity, never an admin password or mutable tag.
- A terminal failed/canceled/timed-out build can be retried only with
  `.\Start-DocIntelSetup.ps1 -CustomerSlug acme -RetryFailedBuild`. Resolve the Azure/build
  error first, review the new paid-build plan, and approve again. Active or successful runs
  are not replaced. Ongoing runs can outlive the local polling window; rerun the same kit.

A running or **partially failed foundation** requires operator review of that Azure deployment;
the tool deliberately does not blindly replay a partial data/key deployment. A changed owner,
image, bundle, directory, missing group, or mismatched application/control-plane binding also
stops recovery. This is not a general upgrade, migration, or arbitrary-resource adoption tool.
No failed apply automatically deletes customer resources.

Incomplete or short-circuited ARM what-if analysis is not approval. An explicit `APPLY`
confirms the exact plan hash; the installer recalculates it immediately before mutation.
Receipts survive a rejected or stale approval.

## Publisher setup and release plan

The default source-build kit is available without this publisher setup. The repository's
validation workflow produces a `customer-setup` artifact bound to the exact validated source
commit. Download its ZIP, `checksums.txt` and instructions together. No application binary is
published to a public registry, and no new public repository is created.

The optional **PublicImage** channel avoids per-customer builds/registry costs by using an
approved, digest-pinned public GHCR image. This separate channel requires explicit publication
approval and the setup below; there is no silent fallback between image sources.

The implementation provides `Build customer release` (`release-customer.yml`):

1. Reuse the repository's complete build/test/container validation with `validation_only=true`.
   That invocation cannot deploy the existing Azure application.
2. Build an AMD64 OCI image with provenance/SBOM, then package the exact resulting digest
   with the launcher, installer, source commit, compiled ARM and integrity manifest.
3. Publication is explicit, main-only, and gated by the `docintel-distribution` environment.
   Existing image versions and release bundles are not overwritten. Prereleases never
   advance a mutable `latest` image tag.
4. Require the exact image to be anonymously pullable, then publish the bundle, manifest,
   checksums and instructions to the explicitly configured **separate public repository**.

Before first publication, the publisher must approve/create the public distribution repository
(proposed: `Nsethi16/DocIntel-docs`), configure `DOCINTEL_DISTRIBUTION_REPOSITORY`, and supply
`DOCINTEL_DISTRIBUTION_TOKEN` in the protected environment. Use a short-lived GitHub App
installation token or a narrowly scoped fine-grained token with Contents write only on that
distribution repository; it needs no Azure or private-source access. Configure required
reviewers and main-only environment deployment policy. The public GHCR package's visibility
must also be explicitly approved/configured. Private-source GitHub Releases are not public
distribution, and an ordinary source `GITHUB_TOKEN` cannot publish releases to another repository.

Until that publisher configuration is completed, the optional public-image channel is not
published. The customer-owned build kit remains independently usable. A live deployment still
requires the customer's permissions and approval; CI and mock execution are not proof of
an actual customer's sign-in, document processing, or invoice.
No repository visibility, package visibility, customer resource or existing deployment is
changed merely by committing this implementation.

## Advanced source-checkout workflow

The lower-level installer remains available for operators with a reviewed image:

```powershell
$setup = @{
    CustomerSlug = 'acme'
    CustomerName = 'Acme Legal'
    SubscriptionId = '<customer-subscription-id>'
    ImageSource = 'CustomerRegistryBuild'
    SourceCommit = '<reviewed-40-character-public-source-commit>'
    OutputDirectory = (Join-Path $HOME 'docintel-acme-setup')
}
.\scripts\Install-DocIntelCustomer.ps1 @setup
.\scripts\Install-DocIntelCustomer.ps1 @setup -Mode Apply `
    -Confirmation APPLY -ExpectedPlanHash '<reviewed-PlanHash>'
# With the original state/bundle, repeat Preview/Apply with -Resume after interruption.
```

For an approved published image instead, omit `ImageSource`/`SourceCommit` and supply
`-Image ghcr.io/nsethi16/docintel-api@sha256:<published-release-digest>`.

This advanced path compiles Bicep when `infra/customer.json` is absent, so it requires Bicep.
`scripts/Setup-DocIntelGitHub.ps1` remains the separate existing-deployment adoption path.
Email, Teams, Microsoft 365 subscriptions/mailboxes/consent, and private networking remain
explicit optional onboarding. Provider readiness is not proof of those integrations or of
real document/inference behavior.
