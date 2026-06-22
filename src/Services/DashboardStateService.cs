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
}
