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
- Reservation expiry data may need **Reservations Reader** at the applicable
  reservation/tenant scope.
- Billing-account/customer access must follow the billing agreement's authorization
  model. Do not assume subscription RBAC grants billing-account access.

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

See [multi-tenancy](phase9-multitenancy.md), [CSP setup](csp-deployment-guide.md),
and [collection operations](data-collection.md).
