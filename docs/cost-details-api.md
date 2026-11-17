# Cost Details, amortization, and reservations

The .NET 10 dashboard uses Radzen controls/grids and ApexCharts for reservation
utilization and cost analytics. The data service is
[CostDetailsService](../src/Services/CostDetailsService.cs), with shared models in
[CostDetailsModels](../src/CmCSP.Core/Models/CostDetailsModels.cs).

## Request pipeline

`generateCostDetailsReport` is asynchronous:

1. POST a report request for the supported scope/date range.
2. Follow the polling location after `202 Accepted`, respecting `Retry-After`.
3. Poll until completion or the configured timeout.
4. Download the report CSV from the returned pre-authenticated blob URL.
5. Parse rows, normalize currency, aggregate reservation usage, and cache results.

Polling refreshes tokens as needed. Download SAS URLs are credentials: do not
log or copy them into documentation.

## Configuration

Settings are under `AzureCostManagement`:

```json
{
  "CostDetails": {
    "Enabled": false,
    "ApiVersion": "2023-11-01",
    "PollingTimeoutSeconds": 600,
    "PollingIntervalSeconds": 15,
    "CacheTtlHours": 4
  },
  "BillingAccount": {
    "BillingAccountId": "",
    "Customers": []
  }
}
```

Use environment variables, user secrets, or the deployed secret/settings workflow.
The Reservations page can enable Cost Details at runtime through the existing
settings store. Billing customer IDs are billing identifiers, not Entra tenant IDs.

## Scopes and access

- Subscription scope reports reservation usage applied to that subscription.
- Configured billing-account/customer scope supports the corresponding billing view.
- Without billing-account configuration, the page warns and uses subscription access.
- Subscription cost permissions do not automatically grant billing-account access.
  Follow the billing agreement's authorization model and [role guide](azure-roles.md).

Cost Details availability depends on the Azure billing agreement/API permissions;
enabling the local feature flag alone does not grant service access.

## Dashboard behavior

The Reservations page displays used/unused reservation cost, utilization, a
multi-month utilization trend, purchase recommendations, and expiry information
when the corresponding feeds are accessible. Trend and Subscription Breakdown
also expose amortized cost and reservation savings.

`GetAmortizedMainCostDataAsync` uses the Cost Management API and its
`cm_main_amort` cache. Actual-cost export CSVs are not treated as amortized data.
Reservation recommendations/expiry are supplied by `OptimizationService`,
not by parsing the Cost Details report.

## Deployment and diagnostics

`ENABLE_COST_DETAILS` configures provisioning; `azd deploy` updates application
images but does not apply new provisioning settings. Re-run provisioning when
changing the deployed configuration through azd.

For empty results, check feature enablement, requested month, scope, billing
agreement support, token identity, and permissions. For slow results, check
polling status/timeout and the report size before reducing polling intervals.
Retain explicit errors/access banners rather than treating failed API requests
as successful zero-cost reports.
