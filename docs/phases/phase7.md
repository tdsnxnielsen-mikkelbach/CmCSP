# Inventory and optimization

[OptimizationService](../../src/CmCSP.Core/Services/OptimizationService.cs)
uses Azure Resource Graph, Consumption recommendations, and Capacity reservation
reads. The .NET 10 Blazor UI uses Radzen grids/cards and ApexCharts.

- `/optimization`: inventory totals, orphaned/idle resource findings, and untagged resources.
- `/tags`: live inventory tag-coverage counts in addition to cost chargeback.
- `/reservations`: purchase recommendations and reservation expiry when accessible.

Resource Graph queries are paged. Findings include unattached disks, unassociated
public IPs/NICs, empty App Service plans, and stopped-but-allocated VMs.
These are investigation candidates, not authorization to delete resources.

Enrichment results are memoized in-process for the configured cache duration.
They are on-demand ARM reads, not collected CSV facts, and do not use the
collector's durable dataset contract.

Subscription Reader grants are configured for the web/collector identities on
the deployment subscription. Other subscriptions and service-principal token
identities require their own appropriate access. Reservation expiry can need
additional Reservations Reader access.

Access problems are surfaced through logging and UI banners. See
[Azure roles](../azure-roles.md).
