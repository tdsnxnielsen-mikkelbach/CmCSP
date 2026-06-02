using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CmCSP.Services;

/// <summary>
/// Reusable helper for paginated Azure Management API calls.
/// Eliminates duplicated while(!string.IsNullOrWhiteSpace(url)) loops
/// across CostManagementService and BlobCostManagementService.
/// </summary>
public static class PaginationHelper
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Fetches all pages from a paginated Azure REST API endpoint.
    /// Each page is deserialized as <typeparamref name="TPage"/> which must expose
    /// items via <paramref name="getItems"/> and a next link via <paramref name="getNextLink"/>.
    /// </summary>
    public static async Task<List<TItem>> FetchAllPagesAsync<TPage, TItem>(
        HttpClient client,
        string initialUrl,
        Func<TPage, IReadOnlyList<TItem>?> getItems,
        Func<TPage, string?> getNextLink,
        ILogger logger,
        string contextLabel,
        CancellationToken ct = default)
        where TPage : class
    {
        var results = new List<TItem>();
        var url = initialUrl;

        while (!string.IsNullOrWhiteSpace(url))
        {
            var response = await client.GetAsync(url, ct);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                logger.LogWarning(
                    "{Context}: API returned {Status} – stopping pagination. Body: {Body}",
                    contextLabel, (int)response.StatusCode, body);
                break;
            }

            var page = await response.Content.ReadFromJsonAsync<TPage>(JsonOpts, ct);
            if (page is null) break;

            var items = getItems(page);
            if (items is not null && items.Count > 0)
                results.AddRange(items);

            url = getNextLink(page) ?? string.Empty;
        }

        return results;
    }
}
