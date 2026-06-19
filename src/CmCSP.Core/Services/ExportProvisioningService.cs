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

/// <summary>The detected export-provisioning route for a single subscription (read-only).</summary>
public enum ExportPathState
{
    /// <summary>ExportBlob mode is off or no destination storage is configured.</summary>
    NotApplicable,
    /// <summary>ExportBlob mode is on but a required setting (e.g. StorageAccountResourceId) is missing.</summary>
    Misconfigured,
    /// <summary>Exports could not be enumerated (e.g. insufficient permissions / not SP mode).</summary>
    Unknown,
    /// <summary>No export targets the configured storage account yet.</summary>
    NotProvisioned,
    /// <summary>The canonical <c>cmcsp-daily-export</c> exists with a managed identity.</summary>
    Provisioned,
    /// <summary>A differently-named export targeting the same storage is being reused.</summary>
    Reused,
    /// <summary>An export exists but has no managed identity (storage role grant skipped).</summary>
    NoIdentity
}

/// <summary>Short, display-friendly summary of a subscription's detected export path.</summary>
public sealed record ExportPathStatus(
    ExportPathState State,
    string ShortLabel,
    string Detail,
    string? ExportName = null);

/// <summary>
/// Finds and reuses the daily Cost Management export for a subscription and (re)grants the
/// export's managed identity write access to the shared storage account.
///
/// Flow:
///   1. GET  /subscriptions/{subId}/providers/Microsoft.CostManagement/exports
///      — authenticated as the Entra App SP (AzureTokenService); finds an export targeting the
///        configured storage account (requires Cost Management Reader on the subscription).
///   2. PUT  {storageAccountResourceId}/providers/Microsoft.Authorization/roleAssignments/{guid}
///      — authenticated as the Container App MI (DefaultAzureCredential); requires
///        User Access Administrator on the storage account (one-time Bicep role assignment).
///
/// Exports are NOT created here: Azure Cost Management denies all service principals
/// (Entra-app SPs and managed identities) write access to exports, so creation happens at
/// deploy time as the deployer (a user principal) via the postprovision hook. When no
/// reusable export exists this service returns actionable guidance to re-run 'azd provision'.
///
/// No-op when ExportBlob.Enabled = false or StorageAccountResourceId is not configured.
/// </summary>
public sealed class ExportProvisioningService
{
    private const string ExportName                   = "cmcsp-daily-export";
    private const string StorageBlobContributorRoleId = "ba92f5b4-2d11-453d-a403-e96b0029c9fe";
    private const string CostMgmtContributorRoleId    = "434105ed-43f6-45c7-a02f-909b2ba83430";
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
    /// Finds and reuses the daily export on <paramref name="subscriptionId"/> and (re)grants its
    /// managed identity write access to the shared storage account. Safe to call multiple times
    /// (the storage role grant is idempotent). Does not create exports — when none targets the
    /// configured storage account it returns actionable guidance to re-run 'azd provision', because
    /// Azure denies service principals write access to Cost Management exports.
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
            _logger.LogError(
                "ExportProvisioning[{CorrelationId}]: misconfigured for {SubId} — ExportBlob is enabled but " +
                "AzureCostManagement:ExportBlob:StorageAccountResourceId is empty. Exports cannot be created or " +
                "detected until the Container App is re-wired with the destination storage account resource id " +
                "(check the postprovision hook / STORAGE_ACCOUNT_RESOURCE_ID).",
                correlationId, subscriptionId);
            return new ExportProvisioningResult(false, false,
                "Export storage is not configured: ExportBlob is enabled but StorageAccountResourceId is empty. " +
                "Re-run provisioning (azd provision) so the Container App is wired to the destination storage account.");
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

        // Best-effort: ensure the Entra App SP holds Cost Management Contributor on this
        // subscription so newly-added subscriptions can be onboarded from the UI without a
        // manual role grant. No-op when the SP already has it; logs guidance (and continues)
        // when the Container App MI lacks rights to assign roles on the subscription.
        await EnsureCostManagementContributorAsync(subscriptionId, correlationId, ct);

        var (existingExport, listFailed) = await FindReusableExportAsync(subscriptionId, correlationId, ct);
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
        else if (listFailed)
        {
            // Could not enumerate exports (likely 401/403) — skip rather than blindly attempting to create.
            _logger.LogWarning(
                "ExportProvisioning[{CorrelationId}]: skipping export creation for {SubId} — could not list existing exports (insufficient permissions). " +
                "Ensure the Entra App SP has at least 'Cost Management Reader' on this subscription.",
                correlationId, subscriptionId);
            return new ExportProvisioningResult(false, true, "Could not list existing exports — insufficient permissions to enumerate or create exports.");
        }
        else
        {
            // No reusable export exists. Azure Cost Management denies ALL service principals
            // (Entra-app SPs and managed identities) write access to exports — even with
            // Cost Management Contributor — so this app cannot create one from the running
            // container. Exports are created at deploy time as the deployer (a user principal)
            // by the postprovision hook. Return actionable guidance instead of attempting a
            // PUT that always fails with 401 RBACAccessDenied.
            _logger.LogWarning(
                "ExportProvisioning[{CorrelationId}]: no export targets the configured storage account on {SubId}. " +
                "Service principals cannot create Cost Management exports (Azure returns 401 RBACAccessDenied); " +
                "creation happens at deploy time. Re-run 'azd provision' to create '{ExportName}'.",
                correlationId, subscriptionId, ExportName);
            return new ExportProvisioningResult(false, true,
                $"No export targets the configured storage account yet. Service principals cannot create Cost " +
                $"Management exports, so '{ExportName}' must be created at deploy time — re-run 'azd provision' " +
                "(it provisions exports as the deployer, which Azure permits).", ExportName);
        }

        if (string.IsNullOrWhiteSpace(exportMiPrincipalId))
        {
            // Export exists and targets the correct storage account but has no managed identity.
            // The export is clearly functional (data loads), so treat this as success and skip role grant.
            _logger.LogInformation(
                "ExportProvisioning[{CorrelationId}]: export '{ExportName}' on {SubId} has no managed identity — skipping storage role grant (export already operational).",
                correlationId, resolvedExportName, subscriptionId);
            return new ExportProvisioningResult(true, false, $"{resolutionMessage} No managed identity present — storage role grant skipped.", resolvedExportName);
        }

        var grantSucceeded = await GrantStorageRoleAsync(exportMiPrincipalId, correlationId, ct);
        if (!grantSucceeded)
            return new ExportProvisioningResult(false, false, $"{resolutionMessage} Storage role assignment for the export managed identity failed.", resolvedExportName, exportMiPrincipalId);

        return new ExportProvisioningResult(true, false, $"{resolutionMessage} Storage access verified.", resolvedExportName, exportMiPrincipalId);
    }

    /// <summary>
    /// Read-only probe that reports which export-provisioning path a subscription is currently on,
    /// without creating or modifying any Azure resources. Mirrors the resolution logic used by
    /// <see cref="ProvisionAsync"/> so the UI can show the detected state next to each subscription.
    /// </summary>
    public async Task<ExportPathStatus> DetectAsync(
        string subscriptionId,
        string? correlationId = null,
        CancellationToken ct = default)
    {
        correlationId ??= Guid.NewGuid().ToString("N");

        if (!_options.ExportBlob.Enabled)
            return new ExportPathStatus(ExportPathState.NotApplicable, "n/a", "ExportBlob mode is not enabled.");

        if (string.IsNullOrWhiteSpace(_options.ExportBlob.StorageAccountResourceId))
            return new ExportPathStatus(ExportPathState.Misconfigured, "misconfigured",
                "ExportBlob is enabled but no destination storage account is configured " +
                "(StorageAccountResourceId is empty). Re-run provisioning to wire the Container App to storage.");

        if (!_spTokenService.UsingServicePrincipal)
            return new ExportPathStatus(ExportPathState.Unknown, "unknown",
                "Service principal mode is not active, so exports cannot be enumerated.");

        var (export, listFailed) = await FindReusableExportAsync(subscriptionId, correlationId, ct);

        if (listFailed)
            return new ExportPathStatus(ExportPathState.Unknown, "unknown",
                "Could not enumerate exports — the Entra App SP likely lacks 'Cost Management Reader' on this subscription.");

        if (export is null)
            return new ExportPathStatus(ExportPathState.NotProvisioned, "not provisioned",
                $"No export targets the configured storage account. Exports are created at deploy time " +
                $"(service principals cannot create them) — re-run 'azd provision' to create '{ExportName}'.");

        var hasIdentity = !string.IsNullOrWhiteSpace(export.PrincipalId);
        var isCanonical = string.Equals(export.Name, ExportName, StringComparison.OrdinalIgnoreCase);

        if (!hasIdentity)
            return new ExportPathStatus(ExportPathState.NoIdentity,
                isCanonical ? "no identity" : $"reused · no identity",
                $"Export '{export.Name}' exists but has no managed identity, so the storage role grant is skipped.",
                export.Name);

        return isCanonical
            ? new ExportPathStatus(ExportPathState.Provisioned, "provisioned",
                $"Export '{export.Name}' is provisioned with a managed identity.", export.Name)
            : new ExportPathStatus(ExportPathState.Reused, $"reused: {export.Name}",
                $"Reusing existing export '{export.Name}' (managed identity present).", export.Name);
    }

    /// <returns>The reusable export (if found) and a flag indicating whether the list call itself failed (true = skip creation).</returns>
    private async Task<(ExistingExportInfo? Export, bool ListFailed)> FindReusableExportAsync(
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
            return (null, true);
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("value", out var exportsElement) || exportsElement.ValueKind != JsonValueKind.Array)
            return (null, false);

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

            _logger.LogDebug(
                "ExportProvisioning[{CorrelationId}]: enumerated export '{Name}' on {SubId} — storageResourceId: '{StorageResourceId}', principalId: '{PrincipalId}'",
                correlationId, name, subscriptionId, storageResourceId, principalId);

            var exportInfo = new ExistingExportInfo(name, resourceId, principalId, storageResourceId);
            if (string.Equals(name, ExportName, StringComparison.OrdinalIgnoreCase))
                sameNameExport = exportInfo;

            if (NormalizeResourceId(storageResourceId) == NormalizeResourceId(_options.ExportBlob.StorageAccountResourceId))
                sameStorageExport ??= exportInfo;
        }

        if (sameNameExport is not null)
        {
            var matchesStorage = NormalizeResourceId(sameNameExport.StorageResourceId)
                              == NormalizeResourceId(_options.ExportBlob.StorageAccountResourceId);

            // Reuse the canonical export regardless of storage match — if it exists by name
            // it was provisioned by this app and is already operational.
            _logger.LogInformation(
                "ExportProvisioning[{CorrelationId}]: found canonical export '{ExportName}' on {SubId} (storage match: {MatchesStorage}, principal present: {HasPrincipal}); reusing.",
                correlationId, sameNameExport.Name, subscriptionId, matchesStorage, !string.IsNullOrWhiteSpace(sameNameExport.PrincipalId));
            return (sameNameExport, false);
        }

        if (sameStorageExport is not null)
        {
            var hasPrincipal = !string.IsNullOrWhiteSpace(sameStorageExport.PrincipalId);
            _logger.LogInformation(
                "ExportProvisioning[{CorrelationId}]: found compatible export '{ExportName}' on {SubId}; reusing it (has MI principal: {HasPrincipal})",
                correlationId, sameStorageExport.Name, subscriptionId, hasPrincipal);
            return (sameStorageExport, false);
        }

        _logger.LogWarning(
            "ExportProvisioning[{CorrelationId}]: no reusable export found on {SubId} matching storage resource ID '{StorageResourceId}'. " +
            "Exports must be created at deploy time (service principals cannot create them) — re-run 'azd provision'.",
            correlationId, subscriptionId, _options.ExportBlob.StorageAccountResourceId);
        return (null, false);
    }

    // ── Step 0 (best-effort): ensure the Entra App SP can manage exports here ──
    // Assigns Cost Management Contributor to the SP on the subscription using the
    // Container App MI. Enables UI onboarding of new subscriptions without a manual
    // grant — but only works when the MI itself holds role-assignment write (e.g.
    // 'Role Based Access Control Administrator' on the subscription / management group).
    // Always best-effort: never throws, never blocks export provisioning.
    private async Task EnsureCostManagementContributorAsync(
        string subscriptionId,
        string correlationId,
        CancellationToken ct)
    {
        var spObjectId = await _spTokenService.GetServicePrincipalObjectIdAsync(ct);
        if (string.IsNullOrWhiteSpace(spObjectId))
        {
            _logger.LogDebug(
                "ExportProvisioning[{CorrelationId}]: could not resolve SP object id — skipping Cost Management role pre-check on {SubId}.",
                correlationId, subscriptionId);
            return;
        }

        try
        {
            var tokenCtx = new TokenRequestContext(["https://management.azure.com/.default"]);
            var miToken  = (await _miCredential.GetTokenAsync(tokenCtx, ct)).Token;

            var roleDefId      = $"/subscriptions/{subscriptionId}/providers/Microsoft.Authorization/roleDefinitions/{CostMgmtContributorRoleId}";
            var assignmentGuid = Guid.NewGuid().ToString();
            var url            = $"https://management.azure.com/subscriptions/{subscriptionId}" +
                                 $"/providers/Microsoft.Authorization/roleAssignments/{assignmentGuid}" +
                                 $"?api-version={RoleAssignmentsApiVersion}";

            var body = new
            {
                properties = new
                {
                    roleDefinitionId = roleDefId,
                    principalId      = spObjectId,
                    principalType    = "ServicePrincipal"
                }
            };

            var client = _httpFactory.CreateClient("AzureMgmt");
            using var req = new HttpRequestMessage(HttpMethod.Put, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", miToken);
            req.Content = JsonContent.Create(body);

            using var resp = await client.SendAsync(req, ct);

            // 409 Conflict = already assigned — idempotent, the SP can already manage exports.
            if (resp.StatusCode == System.Net.HttpStatusCode.Conflict)
            {
                _logger.LogDebug(
                    "ExportProvisioning[{CorrelationId}]: SP already holds Cost Management Contributor on {SubId}.",
                    correlationId, subscriptionId);
                return;
            }

            if (resp.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "ExportProvisioning[{CorrelationId}]: granted Cost Management Contributor to the Entra App SP on {SubId} " +
                    "(role propagation may take a moment).",
                    correlationId, subscriptionId);
                return;
            }

            var error = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "ExportProvisioning[{CorrelationId}]: could not auto-grant Cost Management Contributor to the SP on {SubId}: HTTP {Status} — {Body}. " +
                "Grant it manually (scripts/onboard-subscription.ps1), or give the Container App managed identity " +
                "'Role Based Access Control Administrator' on the subscription / management group to enable automatic onboarding.",
                correlationId, subscriptionId, (int)resp.StatusCode, error);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "ExportProvisioning[{CorrelationId}]: Cost Management role pre-check failed on {SubId} — continuing with export provisioning.",
                correlationId, subscriptionId);
        }
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

    /// <summary>Normalises an ARM resource ID for comparison: lowercase, leading slash, no trailing slash.</summary>
    private static string NormalizeResourceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return string.Empty;
        return "/" + id.Trim().TrimStart('/').ToLowerInvariant().TrimEnd('/');
    }

    private static string? GetString(JsonElement element, params string[] path)    {
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
