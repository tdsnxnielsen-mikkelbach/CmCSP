namespace CmCSP.Models;

/// <summary>
/// Configuration for the TD SYNNEX <b>Ion Gateway</b> — the enrichment hub that fuses Ion
/// (StreamOne) cost/margin/MSRP with Partner Center list price. CmCSP calls it to decorate its
/// native Azure cost with the buy price/margin it does not hold natively.
///
/// The API key is a per-caller secret (format <c>{keyId}.{secret}</c>) and must be supplied via
/// <c>dotnet user-secrets</c> in development or the <c>IonGateway--ApiKey</c> Key Vault secret in
/// Azure — never committed. When <see cref="ApiKey"/> is empty the integration is disabled and the
/// dashboard degrades gracefully (native cost only, no margin).
/// </summary>
public sealed class IonGatewayOptions
{
    public const string SectionName = "IonGateway";

    /// <summary>Base URL of the Ion Gateway, e.g. <c>https://gateway.…azurecontainerapps.io</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Per-caller API key sent as the <c>X-Api-Key</c> header on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>How long to cache Ion Gateway responses before re-fetching. Default 30 minutes.</summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary><c>true</c> when both a base URL and an API key are configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}

/// <summary>
/// Configuration for the standalone <b>Partner Center Transfer (PCT)</b> API, which exposes
/// Microsoft Partner Center indirect-reseller data (resellers → customers → CSP subscriptions +
/// Microsoft list price, already decorated with Ion cost/margin). CmCSP uses it to enrich
/// customer/subscription detail beyond what the gateway's bootstrap directory returns.
///
/// The shared API key must be supplied via <c>dotnet user-secrets</c> in development or the
/// <c>PartnerCenter--ApiKey</c> Key Vault secret in Azure. When empty the integration is disabled.
/// </summary>
public sealed class PartnerCenterOptions
{
    public const string SectionName = "PartnerCenter";

    /// <summary>Base URL of the PCT API, e.g. <c>https://ca-pct.…azurecontainerapps.io</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Shared API key sent as the <c>X-Api-Key</c> header on every request.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Partner Center auth mode sent as the <c>X-Auth-Mode</c> header so the PCT service
    /// auto-acquires a token. Defaults to <c>secureapp</c> (App+User, delegated).
    /// </summary>
    public string AuthMode { get; set; } = "secureapp";

    /// <summary>How long to cache PCT responses before re-fetching. Default 30 minutes.</summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary><c>true</c> when both a base URL and an API key are configured.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(ApiKey);
}
