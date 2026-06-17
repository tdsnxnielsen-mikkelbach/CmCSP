namespace CmCSP.Models;

public class CostManagementOptions
{
    public const string SectionName = "AzureCostManagement";

    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>All subscription IDs the service principal has Cost Management Reader access to.</summary>
    public List<string> SubscriptionIds { get; set; } = [];

    /// <summary>3-letter ISO 4217 currency code to normalise all costs into (e.g. "DKK").</summary>
    public string TargetCurrency { get; set; } = "DKK";

    /// <summary>
    /// Exchange rates relative to the target currency.
    /// Key = ISO currency code (e.g. "USD"), Value = how many TargetCurrency units equal 1 of that currency.
    /// Example: USD -> 6.89 means 1 USD = 6.89 DKK.
    /// </summary>
    public Dictionary<string, decimal> ExchangeRates { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["USD"] = 6.89m,
        ["EUR"] = 7.46m,
        ["GBP"] = 8.72m,
        ["SEK"] = 0.67m,
        ["NOK"] = 0.65m
    };

    /// <summary>How long to keep API results in memory before re-fetching. Default 60 minutes.</summary>
    public int CacheExpirationMinutes { get; set; } = 60;

    /// <summary>Monthly budget amount in TargetCurrency for the Budgets page.</summary>
    public decimal MonthlyBudget { get; set; } = 125_000m;

    /// <summary>Azure Cost Management REST API version to use.</summary>
    public string ApiVersion { get; set; } = "2025-03-01";

    /// <summary>
    /// UTC hour (0–23) at which the daily background API refresh runs.
    /// Only applies when ExportBlob.Enabled = true.  The refresh calls the Cost Management
    /// Query API directly so the cache always contains data from the last few hours,
    /// regardless of when the daily blob export landed.  Default 0 = 00:00 UTC.
    /// </summary>
    public int ApiDailyRefreshHourUtc { get; set; } = 0;

    // ── Export / Blob mode ────────────────────────────────────────────────────

    /// <summary>
    /// When Enabled = true, the dashboard reads pre-built cost exports from Azure Blob Storage
    /// instead of calling the Cost Management Query API directly. This eliminates the
    /// 5-req/min rate limit and is the recommended approach for production.
    /// Deploy bicep/export-sub.bicep (and optionally bicep/export-billing.bicep) to create
    /// the scheduled exports, then configure this section.
    /// </summary>
    public ExportBlobOptions ExportBlob { get; set; } = new();

    public sealed class ExportBlobOptions
    {
        /// <summary>Set to true to use blob exports instead of the Query API.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// Full URI of the storage account, e.g. https://&lt;account&gt;.blob.core.windows.net
        /// Used with DefaultAzureCredential (recommended for production / managed identity).
        /// Leave empty and set ConnectionString instead for local development without az login.
        /// </summary>
        public string StorageAccountUri { get; set; } = string.Empty;

        /// <summary>
        /// Storage connection string. Only used when StorageAccountUri is empty.
        /// Use dotnet user-secrets or an environment variable — never commit this.
        /// </summary>
        public string ConnectionString { get; set; } = string.Empty;

        /// <summary>Name of the blob container that receives the export files.</summary>
        public string ContainerName { get; set; } = "cost-exports";

        /// <summary>
        /// Prefix path inside the container where export files are written.
        /// Matches the rootFolderPath parameter in the Bicep files (default: "exports").
        /// </summary>
        public string BlobPrefix { get; set; } = "exports";

        /// <summary>
        /// ARM resource ID of the storage account, e.g.
        /// /subscriptions/{subId}/resourceGroups/{rg}/providers/Microsoft.Storage/storageAccounts/{name}
        /// Required for automated export provisioning (ExportProvisioningService).
        /// </summary>
        public string StorageAccountResourceId { get; set; } = string.Empty;
    }

    // ── Azure distributed cache (Table + Blob Storage) ────────────────────────

    /// <summary>
    /// When Enabled = true, cost data is cached in Azure Storage instead of in-process memory.
    /// Small payloads (≤ 64 KB serialised) go to Azure Table Storage.
    /// Large payloads (> 64 KB) are stored in Blob Storage under the CacheContainerName container.
    /// This allows multiple Container App replicas to share the same cache and survive restarts.
    /// Uses DefaultAzureCredential – the Container App managed identity needs
    /// 'Storage Table Data Contributor' and 'Storage Blob Data Contributor' on the storage account.
    /// </summary>
    public AzureCacheOptions AzureCache { get; set; } = new();

    public sealed class AzureCacheOptions
    {
        public bool   Enabled            { get; set; } = false;
        public string StorageAccountUri  { get; set; } = string.Empty;  // https://<account>.table/blob.core.windows.net prefix — use base URI
        public string TableName          { get; set; } = "cmcspcache";
        public string CacheContainerName { get; set; } = "cmcspcache";
    }

    // ── Cost Details API (generateCostDetailsReport) ──────────────────────────

    /// <summary>
    /// Configuration for the Cost Details API (generateCostDetailsReport).
    /// This is an async, report-based API that returns line-item cost data
    /// including reservation Used/Unused breakdown via AmortizedCost metric.
    /// Supports both subscription scope and billing-account/customer scope (MCA/CSP).
    /// </summary>
    public CostDetailsApiOptions CostDetails { get; set; } = new();

    public sealed class CostDetailsApiOptions
    {
        /// <summary>Set to true to enable the Cost Details API features (Reservations page, AmortizedCost toggle).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// API version for generateCostDetailsReport.
        /// Supported values: 2023-11-01 (GA for EA/MCA/CSP).
        /// </summary>
        public string ApiVersion { get; set; } = "2023-11-01";

        /// <summary>How long to poll for a report before timing out (seconds). Default 600 (10 min).</summary>
        public int PollingTimeoutSeconds { get; set; } = 600;

        /// <summary>Interval between polling requests (seconds). Default 15.</summary>
        public int PollingIntervalSeconds { get; set; } = 15;

        /// <summary>
        /// Cache TTL for Cost Details results (hours). The API data updates every ~4 hours.
        /// Default 4 hours. Setting lower increases API usage.
        /// </summary>
        public int CacheTtlHours { get; set; } = 4;
    }

    // ── CSP Billing Account (MCA partner model) ───────────────────────────────

    /// <summary>
    /// Billing account configuration for CSP partners (Microsoft Customer Agreement).
    /// When configured, the Reservations page can fetch per-customer reservation data
    /// at billing-account scope, giving full Used/Unused/Total RI utilisation.
    /// Without this, reservation data is fetched at subscription scope only.
    /// </summary>
    public BillingAccountOptions BillingAccount { get; set; } = new();

    public sealed class BillingAccountOptions
    {
        /// <summary>
        /// The billing account ID from Azure portal → Cost Management → Properties.
        /// Format: numeric string, e.g. "12345678".
        /// </summary>
        public string BillingAccountId { get; set; } = string.Empty;

        /// <summary>
        /// List of CSP customers under this billing account.
        /// Each entry maps a customer's Azure AD tenant/customer ID to a display name.
        /// The CustomerId is the value shown under Billing Account → Customers in the portal.
        /// </summary>
        public List<BillingCustomerOptions> Customers { get; set; } = [];
    }

    public sealed class BillingCustomerOptions
    {
        /// <summary>Azure billing customer ID (e.g. "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx").</summary>
        public string CustomerId { get; set; } = string.Empty;

        /// <summary>Human-readable display name shown in the dashboard.</summary>
        public string DisplayName { get; set; } = string.Empty;
    }
}
