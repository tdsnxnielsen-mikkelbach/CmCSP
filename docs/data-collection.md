# Cost collection operations

The .NET 10 [CostCollectorJob](../src/CostCollectorJob/Program.cs) owns fresh
collection. The Blazor/Radzen web app reads the results and exposes collection
controls; it does not host a nightly collection service.

## Data flow

1. Home-tenant cost exports arrive as CSV blobs in `cost-exports`.
2. `RefreshAsync` parses/aggregates cost and, with SQL enabled, upserts `CostFact`.
3. Ready-to-serve datasets are published through `ICacheService`: memory L1 plus
   Managed Redis L2, or the configured legacy Table/Blob L2. A daily
   subscription-cost summary is published alongside the detailed main-cost rows.
4. Amortized cost is fetched through the API path.
5. The collector writes an audit record and exits with success/failure status.

The web app normally reads SQL-backed aggregates and shared cache entries.
`CacheWarmupService` only rehydrates cache entries on startup; it does not trigger
live API collection. Without SQL, export parsing remains supported.

## Schedule and manual execution

The current [app module](../infra/modules/app.bicep) declares the collector cron
as **`0 8 * * *` (08:00 UTC daily)**. The Azure job configuration is the runtime
source of truth if an operator has overridden the template.

The Home page's **Data Collection** panel starts collection through
`JobControlService.StartScaledAsync`, displays bounded polling status, and links
to Azure execution history. `CollectorJob:SubscriptionId`, `ResourceGroup`, and
`JobName` must be configured; otherwise on-demand collection is disabled.

```powershell
az containerapp job execution list --name <collector-job> --resource-group <resource-group> --output table
az containerapp job start --name <collector-job> --resource-group <resource-group>
```

Starting a job consumes Azure resources and refreshes persisted data. Do not use
collection as a smoke test for an unrelated UI deployment.

## Partitioning

`COLLECT_PARTITION_COUNT` and `COLLECT_PARTITION_INDEX` select disjoint slices.
Single-tenant collection partitions subscriptions; multi-tenant collection
partitions customers. Home export collection runs on partition zero.

Container Apps Jobs do not assign a per-replica task index automatically.
Manual scale-out starts separate executions with fixed partition overrides.
The dashboard's partition count comes from `CollectorJob:PartitionCount`.
Do not equate increasing job replica parallelism with configuring partition indexes.

The SQL natural key contains `SubscriptionId`, allowing disjoint subscription
upserts without collision. Preserve this invariant when changing partitioning.

## Multi-tenant collection

Home-tenant subscriptions are collected from accessible home export blobs.
Other customer subscriptions are queried using per-tenant API tokens and their
mapped subscription IDs; customer exports are not assumed readable from home storage.
Tag chargeback can be unavailable where the CSP Query API rejects the tag dimension.
The collector publishes tenant-scoped caches and refreshes the partner aggregate.

## Audit and troubleshooting

`CollectionAuditService` uses SQL `CollectionAudit` when configured; otherwise it
uses the `cmcspcollectaudit` Azure table. Records include trigger, status,
timestamps, row counts, duration, error, replica, and correlation ID.
The latest audit record is shown in the Home page.

Investigate in this order:

1. Job execution state and container logs.
2. Export CSV availability and mapped subscriptions.
3. Correct token identity, tenant, cost visibility, and permissions.
4. SQL connectivity/contained users and Redis access policies.
5. Cache TTL and tenant-scoped keys.

## Historical backfill

[CostBackfillJob](../src/CostBackfillJob/Program.cs) reads historical export CSVs
without the collector's rolling window and upserts SQL facts. It requires
`ConnectionStrings:Sql` and export settings. It is an explicit operator utility,
not a job automatically created or run by `azd deploy`.

```powershell
dotnet run --project src\CostBackfillJob\CostBackfillJob.csproj --configuration Release
```

Run only after supplying the intended configuration and approving the data write.
Idempotent upserts do not make a large historical reload free or instantaneous.

## Image updates

`azd deploy` deploys the web app and runs
[postdeploy.ps1](../infra/hooks/postdeploy.ps1) to publish/update the collector image.
The hook does not start collection. Verify both workloads after deployment.
