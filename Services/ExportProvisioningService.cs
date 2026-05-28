using System.Net.Http.Headers;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using CmCSP.Models;

namespace CmCSP.Services;

public sealed record ExportProvisioningResult(
    bool Succeeded,
    bool Skipped,
    string Message,
    string? ExportName = null,
    string? PrincipalId = null);

/// <summary>
/// Automatically provisions a daily Cost Management export and its storage role assignment
/// when a new subscription is added through the UI.
///
/// Flow:
///   1. PUT  /subscriptions/{subId}/providers/Microsoft.CostManagement/exports/cmcsp-daily-export
///      — authenticated as the Entra App SP (AzureTokenService); requires Cost Management Contributor
///        on the target subscription.
///   2. PUT  {storageAccountResourceId}/providers/Microsoft.Authorization/roleAssignments/{guid}
///      — authenticated as the Container App MI (DefaultAzureCredential); requires
///        User Access Administrator on the storage account (one-time Bicep role assignment).
///
/// No-op when ExportBlob.Enabled = false or StorageAccountResourceId is not configured.
/// </summary>
public sealed class ExportProvisioningService
{
    private const string ExportName                   = "cmcsp-daily-export";
    private const string StorageBlobContributorRoleId = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string RoleAssignmentsApiVersion    = "2022-04-01";

    private readonly AzureTokenService                  _spTokenService;
    private readonly DefaultAzureCredential             _miCredential;
    private readonly IHttpClientFactory                 _httpFactory;
    private readonly CostManagementOptions              _options;
    private readonly ILogger<ExportProvisioningService> _logger;

    private sealed record ExistingExportInfo(string Name, string ResourceId, string? PrincipalId, string? StorageResourceId);

    public ExportProvisioningService(
        AzureTokenService spTokenService,
        IHttpClientFactory httpFactory,
        CostManagementOptions options,
        ILogger<ExportProvisioningService> logger)
    {
        _spTokenService = spTokenService;
        _miCredential   = new DefaultAzureCredential();
        _httpFactory    = httpFactory;
        _options        = options;
        _logger         = logger;
    }

    /// <summary>
    /// Creates the daily export on <paramref name="subscriptionId"/> and grants its managed
    /// identity write access to the shared storage account. Safe to call multiple times
    /// (both ARM operations are idempotent).
    /// </summary>
    public async Task<ExportProvisioningResult> ProvisionAsync(
        string subscriptionId,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");

        if (!_options.ExportBlob.Enabled)
        {
            _logger.LogDebug("ExportProvisioning[{CorrelationId}]: skipped — ExportBlob mode is not enabled", correlationId);
            return new ExportProvisioningResult(false, true, "ExportBlob mode is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(_options.ExportBlob.StorageAccountResourceId))
        {
            _logger.LogWarning(
                "ExportProvisioning[{CorrelationId}]: skipped — AzureCostManagement:ExportBlob:StorageAccountResourceId is not configured",
                correlationId);
            return new ExportProvisioningResult(false, true, "ExportBlob storage account resource ID is not configured.");
        }

        _logger.LogInformation(
            "ExportProvisioning[{CorrelationId}]: provisioning cost export for subscription {SubId}",
            correlationId, subscriptionId);

        if (!_spTokenService.UsingServicePrincipal)
        {
            _logger.LogError(
                "ExportProvisioning[{CorrelationId}]: aborted for {SubId} — AzureTokenService is running in " +
                "DefaultAzureCredential (MI) mode, not Service Principal mode. " +
                "Ensure AzureCostManagement:ClientSecret (or its Key Vault reference) is " +
                "correctly configured so the Entra App SP is used for export creation.",
                correlationId, subscriptionId);
            return new ExportProvisioningResult(false, false, "AzureTokenService is not using service principal mode.");
        }

        var existingExport = await FindReusableExportAsync(subscriptionId, correlationId, ct);
        string? exportMiPrincipalId;
        string resolvedExportName;
        string resolutionMessage;

        if (existingExport is not null)
        {
            exportMiPrincipalId = existingExport.PrincipalId;
            resolvedExportName = existingExport.Name;
            resolutionMessage = string.Equals(existingExport.Name, ExportName, StringComparison.OrdinalIgnoreCase)
                ? $"Export '{existingExport.Name}' is already provisioned."
                : $"Reused existing export '{existingExport.Name}' targeting the configured storage account.";
        }
        else
        {
            exportMiPrincipalId = await CreateExportAsync(subscriptionId, correlationId, ct);
            if (string.IsNullOrWhiteSpace(exportMiPrincipalId))
                return new ExportProvisioningResult(false, false, $"Failed to create or update export '{ExportName}'.", ExportName);

            resolvedExportName = ExportName;
            resolutionMessage = $"Created or updated export '{ExportName}'.";
        }

        if (string.IsNullOrWhiteSpace(exportMiPrincipalId))
            return new ExportProvisioningResult(false, false, $"Export '{resolvedExportName}' does not expose a managed identity principal.", resolvedExportName);

        var grantSucceeded = await GrantStorageRoleAsync(exportMiPrincipalId, correlationId, ct);
        if (!grantSucceeded)
            return new ExportProvisioningResult(false, false, $"{resolutionMessage} Storage role assignment for the export managed identity failed.", resolvedExportName, exportMiPrincipalId);

        return new ExportProvisioningResult(true, false, $"{resolutionMessage} Storage access verified.", resolvedExportName, exportMiPrincipalId);
    }

    private async Task<ExistingExportInfo?> FindReusableExportAsync(
        string subscriptionId,
        string correlationId,
        CancellationToken ct)
    {
        var spToken = await _spTokenService.GetAccessTokenAsync(ct);
        var client  = _httpFactory.CreateClient("AzureMgmt");
        var url     = $"https://management.azure.com/subscriptions/{subscriptionId}/providers/Microsoft.CostManagement/exports?api-version={_options.ApiVersion}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", spToken);

        using var resp = await client.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "ExportProvisioning[{CorrelationId}]: could not enumerate existing exports on {SubId}: HTTP {Status} — {Body}",
                correlationId, subscriptionId, (int)resp.StatusCode, json);
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var exportsElement) || exportsElement.ValueKind != JsonValueKind.Array)
            return null;

        ExistingExportInfo? sameNameExport = null;
        ExistingExportInfo? sameStorageExport = null;

        foreach (var export in exportsElement.EnumerateArray())
        {
            var name = GetString(export, "name");
            var resourceId = GetString(export, "id");
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(resourceId))
                continue;

            var principalId = GetString(export, "identity", "principalId");
            var storageResourceId = GetString(export, "properties", "deliveryInfo", "destination", "resourceId");

            var exportInfo = new ExistingExportInfo(name, resourceId, principalId, storageResourceId);
            if (string.Equals(name, ExportName, StringComparison.OrdinalIgnoreCase))
                sameNameExport = exportInfo;

            if (string.Equals(storageResourceId, _options.ExportBlob.StorageAccountResourceId, StringComparison.OrdinalIgnoreCase))
                sameStorageExport ??= exportInfo;
        }

        if (sameNameExport is not null)
        {
            var matchesStorage = string.Equals(
                sameNameExport.StorageResourceId,
                _options.ExportBlob.StorageAccountResourceId,
                StringComparison.OrdinalIgnoreCase);

            if (matchesStorage && !string.IsNullOrWhiteSpace(sameNameExport.PrincipalId))
            {
                _logger.LogInformation(
                    "ExportProvisioning[{CorrelationId}]: found existing canonical export '{ExportName}' on {SubId}; reusing export MI {PrincipalId}",
                    correlationId, sameNameExport.Name, subscriptionId, sameNameExport.PrincipalId);
                return sameNameExport;
            }

            _logger.LogInformation(
                "ExportProvisioning[{CorrelationId}]: canonical export '{ExportName}' exists on {SubId} but will be updated (storage match: {MatchesStorage}, principal present: {HasPrincipal})",
                correlationId, sameNameExport.Name, subscriptionId, matchesStorage, !string.IsNullOrWhiteSpace(sameNameExport.PrincipalId));
            return null;
        }

        if (sameStorageExport is not null && !string.IsNullOrWhiteSpace(sameStorageExport.PrincipalId))
        {
            _logger.LogInformation(
                "ExportProvisioning[{CorrelationId}]: found compatible export '{ExportName}' on {SubId}; reusing it instead of creating '{CanonicalName}'",
                correlationId, sameStorageExport.Name, subscriptionId, ExportName);
            return sameStorageExport;
        }

        if (sameStorageExport is not null)
        {
            _logger.LogInformation(
                "ExportProvisioning[{CorrelationId}]: found storage-compatible export '{ExportName}' on {SubId} without a managed identity principal; creating canonical export '{CanonicalName}' instead",
                correlationId, sameStorageExport.Name, subscriptionId, ExportName);
        }

        return null;
    }

    // ── Step 1: create / update the export resource ──────────────────────────

    private async Task<string?> CreateExportAsync(
        string subscriptionId,
        string correlationId,
        CancellationToken ct)
    {
        var spToken         = await _spTokenService.GetAccessTokenAsync(ct);
        var client          = _httpFactory.CreateClient("AzureMgmt");
        var recurrenceFrom  = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-ddT00:00:00Z");

        var body = new
        {
            location = "swedencentral",
            identity = new { type = "SystemAssigned" },
            properties = new
            {
                format     = "Csv",
                definition = new
                {
                    type      = "ActualCost",
                    timeframe = "MonthToDate",
                    dataSet   = new { granularity = "Daily", configuration = new { } }
                },
                deliveryInfo = new
                {
                    destination = new
                    {
                        type           = "AzureBlob",
                        resourceId     = _options.ExportBlob.StorageAccountResourceId,
                        container      = _options.ExportBlob.ContainerName,
                        rootFolderPath = _options.ExportBlob.BlobPrefix
                    }
                },
                schedule = new
                {
                    recurrence       = "Daily",
                    recurrencePeriod = new { from = recurrenceFrom, to = "2099-12-31T00:00:00Z" },
                    status           = "Active"
                },
                dataOverwriteBehavior = "CreateNewReport",
                compressionMode       = "none"
            }
        };

        var url = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                  $"/providers/Microsoft.CostManagement/exports/{ExportName}" +
                  $"?api-version={_options.ApiVersion}";

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", spToken);
        req.Content = JsonContent.Create(body);

        using var resp = await client.SendAsync(req, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            var hint = resp.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                       or System.Net.HttpStatusCode.Forbidden
                ? " — ensure the Entra App SP has 'Cost Management Contributor' on this subscription"
                : string.Empty;

            _logger.LogError(
                "ExportProvisioning[{CorrelationId}]: failed to create export on subscription {SubId}: HTTP {Status} — {Body}{Hint}",
                correlationId, subscriptionId, (int)resp.StatusCode, json, hint);
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var principalId = doc.RootElement
            .GetProperty("identity")
            .GetProperty("principalId")
            .GetString();

        _logger.LogInformation(
            "ExportProvisioning[{CorrelationId}]: created/updated export '{ExportName}' on {SubId}; export MI principal: {PrincipalId}",
            correlationId, ExportName, subscriptionId, principalId);

        return principalId;
    }

    // ── Step 2: grant Storage Blob Data Contributor to the export MI ──────────

    private async Task<bool> GrantStorageRoleAsync(
        string exportMiPrincipalId,
        string correlationId,
        CancellationToken ct)
    {
        var tokenCtx = new TokenRequestContext(["https://management.azure.com/.default"]);
        var miToken  = (await _miCredential.GetTokenAsync(tokenCtx, ct)).Token;

        var client            = _httpFactory.CreateClient("AzureMgmt");
        var storageResourceId = _options.ExportBlob.StorageAccountResourceId;

        // Built-in role definitions live in any subscription context; use the storage account's.
        var storageSubId  = storageResourceId.Split('/')[2];
        var roleDefId     = $"/subscriptions/{storageSubId}/providers/Microsoft.Authorization/roleDefinitions/{StorageBlobContributorRoleId}";
        var assignmentGuid = Guid.NewGuid().ToString();

        var url = $"https://management.azure.com{storageResourceId}" +
                  $"/providers/Microsoft.Authorization/roleAssignments/{assignmentGuid}" +
                  $"?api-version={RoleAssignmentsApiVersion}";

        var body = new
        {
            properties = new
            {
                roleDefinitionId = roleDefId,
                principalId      = exportMiPrincipalId,
                principalType    = "ServicePrincipal"
            }
        };

        using var req = new HttpRequestMessage(HttpMethod.Put, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", miToken);
        req.Content = JsonContent.Create(body);

        using var resp = await client.SendAsync(req, ct);

        // 409 Conflict = assignment already exists — idempotent, treat as success.
        if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            _logger.LogDebug(
                "ExportProvisioning[{CorrelationId}]: Storage Blob Data Contributor already assigned to export MI {PrincipalId}",
                correlationId, exportMiPrincipalId);
            return true;
        }

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogError(
                "ExportProvisioning[{CorrelationId}]: failed to grant Storage Blob Data Contributor to export MI {PrincipalId}: HTTP {Status} — {Body}",
                correlationId, exportMiPrincipalId, (int)resp.StatusCode, error);
            return false;
        }

        _logger.LogInformation(
            "ExportProvisioning[{CorrelationId}]: granted Storage Blob Data Contributor to export MI {PrincipalId} on {StorageAccount}",
            correlationId, exportMiPrincipalId, storageResourceId);
        return true;
    }

    private static string? GetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Null => null,
            _ => current.ToString()
        };
    }
}
