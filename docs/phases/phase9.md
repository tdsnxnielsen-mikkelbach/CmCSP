# Phase 9 – CSP multi-tenancy (GDAP + multi-tenant Entra app)

> Part of the [CmCSP roadmap](../todo.md). **Status: 📋 Planned.**

**Goal:** Turn the single-tenant, multi-subscription app into a **CSP product** a reseller can
offer their customers — each customer's Azure data isolated and accessed cross-tenant.
**Explicitly out of scope:** `api.partnercenter.microsoft.com` (no Partner Center API) and any
M365/Graph license data — this stays **Azure spend + Azure posture only**. Cross-customer
billing aggregation, if needed, uses the **ARM Cost Management billing-account/customer scopes**
that [`CostDetailsService`](../../src/Services/CostDetailsService.cs) already supports — not Partner Center.

> **Note:** This is a platform-level effort with its own design doc:
> [`docs/phase9-multitenancy.md`](../phase9-multitenancy.md). The foundation already exists —
> `SubscriptionId` is in the `CostFact` natural key, `ICacheService` keys are prefixable, and
> billing-scope code is in `CostDetailsService` — so this is an **extension, not a teardown**.
>
> **Visibility rule:** a user signing in from the **CSP home tenant** sees **all**
> subscriptions; a user signing in from a **customer tenant** sees **only** their own
> tenant's subscriptions (decided by the token `tid` claim).

| Sub-task | Priority | Status | Notes |
|---|---|---|---|
| **Data model: customer → tenant → subscriptions** | P1 | 📋 Planned | Add `Customer`/`Tenant` entities above `UserSubscription`; add `CustomerId`/`TenantId` columns to `CostFact` (+ index). Additive only — natural key already includes `SubscriptionId`. Update [`CmcspDbContext`](../../src/CmCSP.Core/Data/CmcspDbContext.cs) + `schema.sql` |
| **Per-tenant token acquisition (multi-tenant Entra app)** | P1 | 📋 Planned | Make [`AzureTokenService`](../../src/CmCSP.Core/Services/AzureTokenService.cs) acquire tokens **per customer tenant** (`login.microsoftonline.com/{customerTenantId}`) instead of one fixed tenant. App registration becomes multi-tenant; consented per customer |
| **GDAP delegated access onboarding** | P1 | 📋 Planned | Use **GDAP** (Granted Delegated Admin Privileges) for time-bound, least-privilege roles (Reader / Cost Management Reader / Security Reader) in each customer tenant. Onboarding flow = consent + GDAP relationship, replacing manual subscription-ID entry |
| **Tenant-isolated cache + data access** | P0 | 📋 Planned | **Critical isolation boundary.** Prefix every cache key (`cm_main`, etc. + warmup keys) and scope every SQL read by `CustomerId`/`TenantId` so tenants can never read each other's data. Update [`RedisCacheService`](../../src/CmCSP.Core/Services/RedisCacheService.cs) consumers + `LoadFromSqlAsync` |
| **Customer picker + row-level authorization** | P1 | 📋 Planned | UI customer selector for the partner; row-level authz so a logged-in **customer** sees only their own tenant while the **partner** sees all. Enforce in `DashboardStateService` + page queries |
| **Per-tenant collector fan-out** | P2 | 📋 Planned | Extend the existing partitioning (`COLLECT_PARTITION_*`) so the collector iterates customers/tenants, acquiring a per-tenant token per slice. Builds directly on the Phase 5 fan-out runbook |
