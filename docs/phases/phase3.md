# Collection subsystem

The .NET 10 `CostCollectorJob` runs independently of the Blazor/Radzen/ApexCharts
web application. Its current template schedule is `0 8 * * *` (08:00 UTC).
The Home page can start the same job manually and display execution/audit status.

Collection refreshes actual-cost exports into SQL when enabled and publishes
serving caches. Amortized cost uses the API. Audit persistence uses SQL or the
legacy Table Storage fallback. Startup warmup is rehydration, not fresh collection.

Manual scale-out uses separate executions with explicit partition indexes.
The application identity has a job-scoped operator role rather than broad
Container Apps administration.

`azd deploy` publishes the web image and the collector via its postdeploy hook.
It does not run collection or automatically deploy the backfill utility.

See the [operations runbook](../data-collection.md) for partitioning, identities,
audit fields, troubleshooting, and historical import.
