using Microsoft.Identity.Client;
using CmCSP.Models;

namespace CmCSP.Services;

/// <summary>
/// Acquires OAuth2 bearer tokens for the Azure Management API using the
/// client-credentials flow.  MSAL handles token caching internally so calls
/// after the first are served from the in-memory MSAL cache until the token
/// nears expiry.
/// </summary>
public sealed class AzureTokenService
{
    private static readonly string[] Scopes = ["https://management.azure.com/.default"];

    private readonly IConfidentialClientApplication _app;

    public AzureTokenService(CostManagementOptions options)
    {
        _app = ConfidentialClientApplicationBuilder
            .Create(options.ClientId)
            .WithClientSecret(options.ClientSecret)
            .WithAuthority(AzureCloudInstance.AzurePublic, options.TenantId)
            .Build();
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        var result = await _app
            .AcquireTokenForClient(Scopes)
            .ExecuteAsync(ct);

        return result.AccessToken;
    }
}
