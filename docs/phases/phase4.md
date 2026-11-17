# Data platform subsystem

The optional platform is implemented in [data.bicep](../../infra/modules/data.bicep)
and the .NET 10 [Core library](../../src/CmCSP.Core/CmCSP.Core.csproj).
Blazor/Radzen/ApexCharts is the presentation layer, not a replacement for durable storage.

- Blob exports are the source feed.
- SQL `CostFact` is durable; collector/backfill perform idempotent upserts.
- SQL also stores audit, runtime settings/subscriptions, and the customer registry.
- `ICacheService` provides memory L1 and Managed Redis L2 with native TTL.
- Legacy Table/Blob L2 remains available when Redis is disabled.
- SQL and Redis use Entra/managed identity rather than stored passwords/access keys.

Enablement is gated by `DEPLOY_DATA_PLATFORM`. Provisioning creates resources,
wires settings, applies schema, and creates runtime SQL users. Application deployment
is a separate step.

See [SQL and Redis](../phase4-data-platform.md) for configuration, current capacity,
authentication, and failure behavior.
