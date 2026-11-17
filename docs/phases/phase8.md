# Security posture and sustainability

Two read-only Azure feeds enrich the .NET 10 Blazor/Radzen/ApexCharts dashboard.

## Defender for Cloud

[SecurityPostureService](../../src/CmCSP.Core/Services/SecurityPostureService.cs)
reads per-subscription secure scores and control findings. `/security` presents
score summaries, charts, and a findings grid. This is Azure Defender for Cloud
posture, not Microsoft 365 Secure Score.

## Carbon Optimization

[SustainabilityService](../../src/CmCSP.Core/Services/SustainabilityService.cs)
reads overall, monthly, and resource-type emissions. `/sustainability` presents
kg CO2e totals, monthly movement, and charts. Data availability is limited by the
Azure service's reporting window and lag; it is not a real-time emissions meter.

## Access and caching

Both use the existing ARM token pipeline. Subscription Reader covers the supported
read scenarios; Security Reader and Carbon Optimization Reader are optional
narrower roles in the templates. Assign access to the actual API-token identity.

Results use in-process memoization and explicit access/status banners. They are
not SQL cost facts or datasets produced by the cost collector.

See [Azure roles](../azure-roles.md) and [UI routes](../../README.md#ui).
