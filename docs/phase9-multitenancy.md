# CSP multi-tenancy

Multi-tenancy is implemented behind `AzureCostManagement:MultiTenancy:Enabled`.
It requires the SQL customer registry. The UI remains Blazor Interactive Server
with Radzen.Blazor controls and ApexCharts; changing presentation must not weaken
server-side tenant isolation.

## Visibility and sign-in

| Identity | Data scope |
|---|---|
| Configured home-tenant user | Partner view over active customers |
| Active registered customer-tenant user | That customer's mapped subscriptions |
| Unknown/inactive tenant | Denied |

When enabled, OIDC uses the `organizations` authority and validates issuer tenants
against the home tenant and active registry. It does not accept every organization
merely because Entra issued its token. With the feature off, single-tenant behavior remains.

`ITenantScopeProvider` resolves the user's tenant claim into `TenantScope`.
`CostPageBase` publishes the ambient scope and applies view filters.
Date/subscription selections only narrow the allowed scope; they are not authorization.

## Registry and durable facts

[CustomerStore](../src/CmCSP.Core/Services/CustomerStore.cs) manages SQL customer
and subscription mappings. Customer status gates access. `CostFact` carries
`CustomerId` and `TenantId` for scoped reads while preserving its existing
subscription-containing natural key.

The schema seeds a home customer for existing data. The home subscription registry
remains distinct from per-customer subscription mappings.

## Cache and query boundaries

`TenantScope.CacheKeyPrefix` separates customer and partner cache entries.
SQL reads restrict customer IDs, writes carry ownership, and startup cache warmup
rehydrates the relevant partitions. Do not replace these with client-side filtering
or a single globally shared cost key.

The shared picker in the main Radzen layout groups subscriptions by tenant.
The `/customers` page is partner-only and feature-gated. Its controls support
customer registration/status, consent links, GDAP relationship recording, and
subscription discovery/mapping.

## Cross-tenant access

In service-principal mode, `AzureTokenService` acquires tokens using the customer
tenant authority. A home managed identity cannot generally cross tenants.
Customer consent and Azure RBAC grants remain required.

GDAP relationships are established out-of-band in Partner Center. The application
records the relationship and offers an admin-consent URL and ARM subscription
discovery; it does not itself create GDAP relationships or make consent equivalent
to an Azure role assignment.

The current collector reads home exports, queries other mapped customers through
their tenant token, writes scoped facts/caches, and rebuilds the partner aggregate.
Partition count/index can split customers across separate job executions.
See [collection operations](data-collection.md).

## Enablement checks

1. Provision SQL/Redis and apply the current schema.
2. Configure the Entra app for organizational multi-tenant sign-in and correct
   sign-in/admin-consent callback addresses.
3. Set the home tenant and enable the multi-tenancy feature through the
   deployment parameters/settings.
4. Register an active customer and complete consent, GDAP setup, and required Azure access.
5. Discover/map its subscriptions and run approved collection.
6. Test partner, customer, unknown-tenant, and suspended-customer behavior separately.

Do not infer tenant isolation from a hidden menu entry. Validate the server-side
scope and data/cache boundaries as well as the visible filters.
