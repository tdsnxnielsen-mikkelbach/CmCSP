# Current capability and maintenance index

This file describes the implementation, not a historical delivery backlog.
The application targets .NET 10 and uses Blazor Interactive Server,
Radzen.Blazor 13.1.0, and Blazor-ApexCharts 8.0.0.

## Implemented subsystems

| Area | Current behavior | Details |
|---|---|---|
| Collection | Separate scheduled/on-demand collector, audit, partitioned executions | [Collection](phases/phase3.md) |
| Data platform | Optional SQL durable facts and Managed Redis L2, managed identity | [Data platform](phases/phase4.md) |
| Performance | Memory L1, shared L2, warm replica, HTTP scaling, collector partitioning | [Scaling](phases/phase5.md) |
| Cost insights | Forecast, Marketplace split, anomalies, reservation trends | [Insights](phases/phase6.md) |
| Optimization | Resource Graph inventory, tag coverage, orphan finder, purchase recommendations | [Optimization](phases/phase7.md) |
| Posture | Defender for Cloud scores/findings and Carbon Optimization reports | [Posture](phases/phase8.md) |
| Multi-tenancy | Gated customer registry, issuer validation, scoped facts/cache, customer onboarding | [Multi-tenancy](phases/phase9.md) |
| UI | Radzen layout/forms/grids/alerts, ApexCharts, persisted themes, driver.js tours | [README](../README.md) |
| Public API | API-key-protected `/api/v1`, OpenAPI document, Scalar reference | [Endpoint definitions](../src/Api/PublicApiEndpoints.cs) |

## Maintenance workflow

- Keep all four project dependency sets compatible with .NET 10 and explicitly versioned.
- Build the solution in Release before publishing.
- Review direct and transitive advisories when updating packages.
- Check authenticated UI workflows in addition to compilation.
- Deploy the web and collector images; do not assume one proves the other succeeded.
- Keep provisioning, application deployment, collection, and historical backfill distinct.
- Update the relevant subsystem documentation when behavior changes.

There is currently no automated test project in the solution. Deployment status
must be established from actual Azure readiness/image checks, not inferred from
this capability list.
