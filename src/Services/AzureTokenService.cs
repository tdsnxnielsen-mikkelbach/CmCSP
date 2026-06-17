using Azure.Core;
using Azure.Identity;
using Microsoft.Identity.Client;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Acquires OAuth2 bearer tokens for the Azure Management API.
///
/// Two authentication modes:
///  • ClientSecret set  → MSAL client-credentials flow (service principal).
///  • ClientSecret null → DefaultAzureCredential (Managed Identity in Azure,
///                        az login / VS credential locally).  This is the
///                        preferred path when the app runs as a Container App
///                        with a SystemAssigned identity.
/// </summary>
public sealed class AzureTokenService
{
    private static readonly string[]    MsalScopes  = ["https://management.azure.com/.default"];
    private static readonly string[]    AzureScopes = ["https://management.azure.com/.default"];

    private readonly IConfidentialClientApplication? _app;
    private readonly TokenCredential?                _credential;

    /// <summary>
    /// <c>true</c> when using MSAL client-credentials (Entra App SP);
    /// <c>false</c> when falling back to DefaultAzureCredential (Managed Identity / az login).
    /// Export provisioning requires the SP path — call this before attempting provisioning.
    /// </summary>
    public bool UsingServicePrincipal => _app is not null;

    public AzureTokenService(CostManagementOptions options)
    {
        if (!string.IsNullOrEmpty(options.ClientSecret))
        {
            _app = ConfidentialClientApplicationBuilder
                .Create(options.ClientId)
                .WithClientSecret(options.ClientSecret)
                .WithAuthority(AzureCloudInstance.AzurePublic, options.TenantId)
                .Build();
        }
        else
        {
            // No client secret configured – fall back to DefaultAzureCredential.
            // In Azure Container Apps this uses the SystemAssigned managed identity.
            // Locally it tries az login / Visual Studio / environment credentials.
            _credential = new DefaultAzureCredential();
        }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            var result = await _app
                .AcquireTokenForClient(MsalScopes)
                .ExecuteAsync(ct);
            return result.AccessToken;
        }

        var token = await _credential!.GetTokenAsync(
            new TokenRequestContext(AzureScopes), ct);
        return token.Token;
    }
}
