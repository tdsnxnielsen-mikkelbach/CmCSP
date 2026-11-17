# Deploying CmCSP with Azure Developer CLI

CmCSP deploys a .NET 10 Blazor + Radzen.Blazor + ApexCharts web app and a separate
cost collector to Azure Container Apps. The deployment entry point is
[azure.yaml](../azure.yaml); infrastructure is under [infra](../infra).

## Prerequisites

- .NET 10 SDK, Azure CLI, Azure Developer CLI, and PowerShell 7.
- Authenticated `az` and `azd` sessions for the intended subscription.
- Deployment rights, including permission to create the required role assignments.
- An Entra Web app registration and the cost permissions in [Azure roles](azure-roles.md).
- For SQL setup: a configured Entra SQL administrator and either `Invoke-Sqlcmd`
  or a compatible `sqlcmd` installation used by the provisioning hook.

## Existing environment: application update

```powershell
azd env list
dotnet build CmCSP.slnx --configuration Release
azd deploy --environment <environment-name> --no-prompt
```

The web service is built with SDK container publishing (no Dockerfile required).
The [postdeploy hook](../infra/hooks/postdeploy.ps1) separately publishes the
collector and updates its image reference. Both image builds include the updated
shared Core library. No infrastructure changes or collection runs are implied.
The historical backfill project is not an azd service or a provisioned job.
The collector hook isolates its temporary registry credentials from the operator's
Docker configuration and fails explicitly if publishing/digest resolution/job update fails.

## New environment

```powershell
azd auth login
az login
azd env new <environment-name>
azd env set CMCSP_TENANT_ID "<tenant-guid>"
azd env set CMCSP_CLIENT_ID "<app-client-id>"
azd env set CMCSP_SUBSCRIPTION_IDS "<subscription-guid>"
azd env set EXPORT_SCOPE subscription
```

Supply `CMCSP_CLIENT_SECRET` through your approved local secret-handling workflow;
never commit the environment's secret values or print them in diagnostics.
Set the location/subscription when prompted, then run `azd up`.

The stages are:

1. `infra/main.bicep` provisions app, registry, vault, storage, RBAC, and optional data platform.
2. `postprovision.ps1` seeds secrets, wires application/job settings, provisions
   exports with the deployer's Azure identity, and applies SQL setup when enabled.
3. azd publishes/deploys the web image.
4. `postdeploy.ps1` publishes/deploys the collector image.

## Export scope

| `EXPORT_SCOPE` | Behavior |
|---|---|
| `subscription` | Deploy subscription exports using managed identity for blob delivery |
| `billing` | Deploy billing-account export; requires `BILLING_ACCOUNT_ID` and billing access |
| `none` | Skip export provisioning |

Run `azd provision` after changing export configuration. Runtime export detection
and the Home page recovery action do not replace deployer-authorized provisioning.
If export creation returns `RBACAccessDenied`, use the provisioning hook as an
authorized deployer rather than assuming a service-principal role grant is sufficient.

## Optional configuration

| Environment setting | Purpose |
|---|---|
| `DEPLOY_DATA_PLATFORM=true` | Provision SQL and Managed Redis; incurs costs |
| `SQL_ADMIN_LOGIN`, `SQL_ADMIN_OBJECT_ID` | SQL Entra administrator |
| `ENABLE_COST_DETAILS=true` | Enable Cost Details integration |
| `EXPORT_NAME`, `HISTORICAL_MONTHS` | Export provisioning options |
| `AZURE_RESOURCE_GROUP`, `ACR_NAME`, `KEY_VAULT_NAME`, `STORAGE_ACCOUNT_NAME` | Adopt configured resource names |

See [SQL/Redis](phase4-data-platform.md) and [multi-tenancy](phase9-multitenancy.md)
before enabling their feature flags.

## Verification and rollback

Check the latest revision is ready and running the intended image; load the
dashboard and sign in. Verify Radzen CSS/JS and ApexCharts JS return successfully,
`/openapi/v1.json` returns a document, and `/api-docs` loads. Check the collector
job image separately: successful web deployment alone does not prove the hook succeeded.

Record the previous web and collector image digests before an update. Roll back
by updating those workloads to the recorded digests and verifying readiness again.
Do not reprovision or delete the environment merely to roll back an application image.
