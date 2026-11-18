# Azure permissions

The Blazor/Radzen/ApexCharts UI does not change Azure authorization requirements.
Grant permissions to the identity used for each operation, not indiscriminately to
every principal. Infrastructure definitions live in [infra](../infra).

## Identities and permissions

| Principal | Access | Scope / implementation |
|---|---|---|
| Deployment operator | Resource deployment and required role-assignment rights | Deployment subscription/resource group |
| API-token identity | Cost Management Reader for read-only cost operations | Each target subscription or supported billing scope |
| Export provisioning operator | Export write permissions, plus authority to grant delivery access | Target subscription and export storage |
| Export managed identity | Storage Blob Data Contributor | Export destination storage |
| Web/collector managed identities | Storage Blob Data Reader | Export storage account |
| Web/collector managed identities | Storage Blob Data Contributor | Cache blob container |
| Web/collector managed identities | Storage Table Data Contributor | Storage account, for legacy cache/audit |
| Image pull identity | AcrPull | Container registry |
| Runtime managed identities | Key Vault Secrets User | Required vault |
| Web identity | Collector-specific custom operator role | Collector job only |
| SQL runtime identities | Contained users in `db_datareader` and `db_datawriter` | Application database |
| Redis runtime identities | Entra data-access policies | Managed Redis database |

`storage.bicep`, `app.bicep`, `data.bicep`, and `main.bicep` define the deployed
assignments. The web application's runtime setting persistence can also require
the vault write rights configured in the app module for the non-SQL fallback.

The custom collector role allows `Microsoft.App/jobs/read`,
`Microsoft.App/jobs/start/action`, and `Microsoft.App/jobs/executions/read`.
It does not grant general Container Apps administration.

## Subscription cost and exports

Read-only cost access and export management are different privileges. Export
creation needs Cost Management Contributor or equivalent write rights, but some
CSP scopes reject application-identity export writes even with that role.
Provision through the authorized deployer using the postprovision hook when
runtime creation returns `RBACAccessDenied`.

The Entra service principal is used in client-secret mode; managed identity is
used by the token service when no application secret is configured. Assign
read permissions to the identity that actually acquires the token.

Cost visibility must also be enabled in Partner Center for applicable CSP
subscriptions. `IndirectCostDisabled` is an agreement/visibility issue rather
than simply a missing role assignment.

## Inventory, security, sustainability, reservations

- Subscription **Reader** supports Resource Graph and the relevant read-only
  enrichment feeds. `grantReaderOnSubscription` covers the deployment subscription.
- Additional subscriptions require their own grants; use
  [reader-sub.bicep](../infra/modules/reader-sub.bicep) or an approved parent scope.
- **Security Reader** and **Carbon Optimization Reader** are optional narrower
  alternatives configured by `grantSecurityCarbonRolesOnSubscription`.
- Reservation expiry (`GET /providers/Microsoft.Capacity/reservationOrders`) needs
  **Reservations Reader**. Reservations are tenant-level resources and do not inherit
  subscription RBAC; see [Customer tenant onboarding](#customer-tenant-onboarding).
- Reservation purchase recommendations (`Microsoft.Consumption/reservationRecommendations`)
  are covered by **Cost Management Reader** on each subscription.
- Billing-account/customer access must follow the billing agreement's authorization
  model. Do not assume subscription RBAC grants billing-account access. Microsoft
  Partner Agreement billing roles do not grant reservation access.

## Managed data platform

SQL uses `Authentication=Active Directory Default`; Redis uses Entra tokens over
TLS on port 10000. Neither requires committed passwords/access keys.
The provisioning hook applies SQL schema and contained users as the configured
Entra SQL administrator. Azure resource RBAC alone does not grant SQL table access.

## Multi-tenant deployments

Customer consent, GDAP relationship setup, and Azure subscription RBAC are distinct.
The application registration must be present/consented in the customer tenant and
its API-token identity must have the needed Azure access there. A home-tenant
managed identity cannot be used as a general cross-tenant identity.

### Entra app registration permissions

Microsoft Graph permissions do not grant any Azure cost or reservation access; that
comes only from the Azure role assignments below.

| Permission | Type | Purpose |
|---|---|---|
| `openid`, `profile`, `User.Read` | Delegated | User sign-in and consent |
| `CrossTenantInformation.ReadBasic.All` | Application | Resolve customer tenant display names |

No other API permissions are required; remove unused ones.

### Customer tenant onboarding

Each tenant (home and every customer) has its **own** service principal for the
multi-tenant app, with a different object ID. Assign roles to that tenant's object ID
(**Entra ID → Enterprise applications** → search the app's client ID). The app is
read-only and never assigns roles to itself, so these assignments are made by a
customer administrator.

| Role | Scope | Used for |
|---|---|---|
| Cost Management Reader | Root management group (covers future subscriptions) or each subscription | Cost queries, reservation recommendations |
| Reader | Same as above | Subscription discovery, inventory |
| Reservations Reader | Tenant level `/providers/Microsoft.Capacity`, or each reservation order | Reservation expiry |
| Cost Management Contributor | Subscription (optional) | Only when the app creates exports |

1. Register the customer on `/customers` and have a Global Administrator native to the
   customer tenant open the consent link. This creates the service principal there.
2. Find the service principal object ID in the customer tenant.
3. Assign Cost Management Reader and Reader:

   ```powershell
   az role assignment create --assignee-object-id <sp-object-id> --assignee-principal-type ServicePrincipal `
     --role "Cost Management Reader" --scope "/providers/Microsoft.Management/managementGroups/<tenant-id>"
   az role assignment create --assignee-object-id <sp-object-id> --assignee-principal-type ServicePrincipal `
     --role "Reader" --scope "/providers/Microsoft.Management/managementGroups/<tenant-id>"
   ```

4. Assign Reservations Reader at tenant level. This requires User Access Administrator
   at root, which a Global Administrator obtains through
   [elevate access](https://learn.microsoft.com/azure/role-based-access-control/elevate-access-global-admin):

   ```powershell
   Connect-AzAccount -Tenant <tenant-id>
   New-AzRoleAssignment -Scope "/providers/Microsoft.Capacity" -PrincipalId <sp-object-id> `
     -RoleDefinitionName "Reservations Reader"
   ```

   Alternatively use the portal (**Reservations → select a reservation → Role assignment**)
   or assign per order at `/providers/Microsoft.Capacity/reservationOrders/<orderId>`.
   See [Grant RBAC access to reservations](https://learn.microsoft.com/azure/cost-management-billing/reservations/manage-reservations-rbac-powershell).
5. Verify with `az role assignment list --assignee <sp-object-id> --all -o table`.
   Tenant-level reservation assignments made through PowerShell are not shown in the portal.
6. On `/customers`, run **Discover subscriptions** and then collection. Role changes can
   take several minutes to propagate, and reservation results are memoized for
   `CacheExpirationMinutes`.

See [multi-tenancy](phase9-multitenancy.md), [CSP setup](csp-deployment-guide.md),
and [collection operations](data-collection.md).
