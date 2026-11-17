# Performance and scaling

The web tier uses Blazor Interactive Server with Radzen and ApexCharts, so active
browser sessions depend on server-side circuits. Cache sharing does not make
circuits stateless or make arbitrary replica restarts invisible to users.

The [app module](../../infra/modules/app.bicep) defaults to one minimum replica,
four maximum replicas, and an HTTP-concurrency scale rule. Verify live settings
when diagnosing behavior; manual Azure overrides can differ from templates.

Cost reads use memory L1 and configured shared L2. Startup warmup rehydrates
datasets without issuing live collection requests. Managed Redis reduces repeated
SQL reads across replicas, but each replica still has its own L1.

SQL serverless can auto-pause after 60 idle minutes. A cold read may pay the
resume delay; use observed latency and cost before changing capacity/auto-pause.

Large collections can be partitioned into distinct executions. SQL natural keys
include subscriptions; preserve disjoint partition ownership.
Historical reloads use the separate backfill utility.

See [data platform](../phase4-data-platform.md) and
[collection operations](../data-collection.md).
