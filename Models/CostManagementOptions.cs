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
}
