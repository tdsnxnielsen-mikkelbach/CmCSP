# Cost insights

The current Blazor/Radzen pages combine ApexCharts with cost services from the
.NET 10 Core library.

| Insight | Implementation and view |
|---|---|
| Native forecast | `CostManagementService.GetForecastAsync`; Trend & Forecast, alongside local extrapolation |
| Marketplace split | Publisher-type/service query; `/marketplace` KPIs, charts, and grid |
| Cost anomalies | `CostAnomalyDetector` over loaded daily subscription/service rows; Home anomaly grid |
| Reservation trend | Multi-month Cost Details utilization; Reservations chart |

Anomaly detection compares recent daily spend to its trailing baseline; it does
not issue a separate Azure API call. Native forecast/publisher results depend on
the configured subscriptions and supported Cost Management dimensions.
Reservation trend loading is independent of the primary month view.

Actual and amortized metrics remain distinct. All amounts use the configured
normalization rules where supported.

See [Cost Details](../cost-details-api.md), [permissions](../azure-roles.md),
and [dashboard routes](../../README.md#ui).
