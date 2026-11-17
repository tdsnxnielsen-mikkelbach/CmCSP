# Multi-tenant subsystem

Multi-tenancy is implemented and feature-gated. It is not enabled merely by
upgrading the UI to Radzen or updating the .NET 10 dependencies.

The SQL registry maps customers to Entra tenants and subscriptions. OIDC issuer
validation accepts the home tenant and active registered tenants. Server-side
tenant scopes constrain queries, SQL facts, and cache keys.

Partners can administer customers through `/customers`; customer users receive
their own data scope. Shared subscription/date controls are view filters, not
security boundaries.

Cross-tenant collection uses service-principal tokens for the customer authority.
Home collection reads home export blobs; other customers use their mapped API
scopes. GDAP relationships and Azure access grants require external setup.

The [multi-tenancy guide](../phase9-multitenancy.md) documents the visibility model,
onboarding, feature configuration, and validation checklist. The
[collection runbook](../data-collection.md) describes partitioned execution.
