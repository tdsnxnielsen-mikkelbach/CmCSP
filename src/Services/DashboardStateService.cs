using MudBlazor;

namespace CmCSP.Services;

/// <summary>
/// Scoped (per SignalR circuit) service that holds the global date-range filter
/// shared across all dashboard pages — replicating Power BI's "Sync slicers"
/// behaviour.  Pages subscribe to OnStateChanged and call StateHasChanged when
/// the range changes.
/// </summary>
public sealed class DashboardStateService
{
    // Default: beginning of previous year → today, giving ~17 months of data.
    public DateRange SelectedRange { get; private set; } = new(
        new DateTime(DateTime.Now.Year - 1, 1, 1),
        DateTime.Now);

    /// <summary>
    /// Phase 9: the customer a partner has drilled into, or <c>null</c> for the "all customers"
    /// aggregate. Only meaningful for the partner (home tenant) when multi-tenancy is enabled;
    /// ignored otherwise. <see cref="CostPageBase"/> narrows the resolved scope to this customer.
    /// </summary>
    public long? SelectedCustomerId { get; private set; }

    /// <summary>
    /// Phase 9: the set of subscription ids the user has chosen to view (case-insensitive). An
    /// <b>empty</b> set means "show every subscription in the security scope" (the default). This is
    /// a <i>view</i> filter layered on top of the security <see cref="TenantScope"/> — it never
    /// widens what a user is allowed to see, only narrows the rows rendered on the pages.
    /// </summary>
    public IReadOnlySet<string> SelectedSubscriptionIds => _selectedSubscriptionIds;
    private readonly HashSet<string> _selectedSubscriptionIds = new(StringComparer.OrdinalIgnoreCase);

    public event Action? OnStateChanged;

    public void SetDateRange(DateRange range)
    {
        SelectedRange = range;
        OnStateChanged?.Invoke();
    }

    /// <summary>Partner-only: drill into a single customer (<c>null</c> = all customers).</summary>
    public void SetSelectedCustomer(long? customerId)
    {
        if (SelectedCustomerId == customerId) return;
        SelectedCustomerId = customerId;
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// Replaces the set of selected subscription ids. Pass an empty sequence to select "all"
    /// (the default, no filtering). Raises <see cref="OnStateChanged"/> when the set changes.
    /// </summary>
    public void SetSelectedSubscriptions(IEnumerable<string> subscriptionIds)
    {
        var next = new HashSet<string>(
            subscriptionIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        if (next.SetEquals(_selectedSubscriptionIds)) return;
        _selectedSubscriptionIds.Clear();
        foreach (var id in next) _selectedSubscriptionIds.Add(id);
        OnStateChanged?.Invoke();
    }

    /// <summary>
    /// True if <paramref name="subscriptionId"/> should be shown given the current selection. An
    /// empty selection set means everything is visible.
    /// </summary>
    public bool IsSubscriptionVisible(string? subscriptionId) =>
        _selectedSubscriptionIds.Count == 0 ||
        (subscriptionId is not null && _selectedSubscriptionIds.Contains(subscriptionId));
}
