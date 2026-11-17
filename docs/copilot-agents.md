# Copilot customizations

The repository includes two read-only specialist agents and a scoped service
instruction file. They describe the current .NET 10 application: Blazor
Interactive Server + Radzen.Blazor + ApexCharts, with an optional SQL/Redis platform.

## Azure Cache Optimizer

[Agent definition](../.github/agents/azure-cache-optimizer.agent.md).
Use for cache TTL, cold starts, shared-cache behavior, collector publication, or
SQL read-path performance. It distinguishes export source blobs, durable SQL
facts, and `ICacheService` serving caches.

Examples:

- Investigate slow first reads after a revision restart.
- Check customer cache prefixes and partner aggregate freshness.
- Review Redis versus legacy Table/Blob configuration.

## Bicep Reviewer

[Agent definition](../.github/agents/bicep-reviewer.agent.md).
Use for infrastructure changes, managed-identity role assignments, export
configuration, and data-platform enablement. It checks templates under `infra`
and their parameter/output contracts with azd hooks.

## Service instructions

[services-cache.instructions.md](../.github/instructions/services-cache.instructions.md)
applies to `src/**/Services/**/*.cs`, covering web and Core service implementations.
It defines:

- Source feed versus durable storage versus disposable cache.
- `ICacheService` encapsulation and cache dataset ownership.
- Tenant-scoped keys and SQL authorization boundaries.
- Collector-owned refresh, rehydration-only warmup, and concurrency guards.
- Managed-identity authentication for SQL/Redis/Storage.

UI changes should preserve those service contracts. Radzen is a presentation
dependency; do not move a UI-framework date-range type into the shared data model.
