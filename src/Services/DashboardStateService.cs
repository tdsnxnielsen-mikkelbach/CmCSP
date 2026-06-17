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

    public event Action? OnStateChanged;

    public void SetDateRange(DateRange range)
    {
        SelectedRange = range;
        OnStateChanged?.Invoke();
    }
}
