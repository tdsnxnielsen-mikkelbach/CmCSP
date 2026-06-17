using CmCSP.Models;
using CmCSP.Services;
using Microsoft.AspNetCore.Components;

namespace CmCSP.Components.Shared;

/// <summary>
/// Abstract base class for all cost-data pages.
/// Provides:
///  • Common injected services (CostService, State, Options, SubStore)
///  • _loading / _error / _subNames / _withDataSubCount fields
///  • Subscribe/unsubscribe wiring for DashboardStateService.OnStateChanged
///  • Fmt() and GetSubName() helpers
///  • Date-range helpers (RangeStart, RangeEnd)
/// Subclasses implement LoadAsync() which is called on init and on every state change.
/// </summary>
public abstract class CostPageBase : ComponentBase, IDisposable
{
    [Inject] protected ICostManagementService CostService { get; set; } = default!;
    [Inject] protected DashboardStateService  State       { get; set; } = default!;
    [Inject] protected CostManagementOptions  Options     { get; set; } = default!;
    [Inject] protected SubscriptionStoreService SubStore  { get; set; } = default!;

    protected bool    _loading = true;
    protected string? _error;
    protected int     _withDataSubCount;
    protected Dictionary<string, string> _subNames = new(StringComparer.OrdinalIgnoreCase);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnInitialized() =>
        State.OnStateChanged += RefreshData;

    protected override async Task OnInitializedAsync() =>
        await LoadAsync();

    private async void RefreshData()
    {
        await LoadAsync();
        await InvokeAsync(StateHasChanged);
    }

    // ── Template method ───────────────────────────────────────────────────────

    /// <summary>
    /// Fetches and processes data for this page. Called once on init and again
    /// whenever the global date-range selection changes.
    /// Implementations should set page-specific fields and call base helpers as needed.
    /// The _loading / _error lifecycle is managed by <see cref="RunLoadAsync"/>.
    /// </summary>
    protected abstract Task LoadAsync();

    /// <summary>
    /// Wraps a load body in the standard _loading / _error lifecycle.
    /// Usage: override LoadAsync() and call await RunLoadAsync(async () => { ... your logic ... });
    /// </summary>
    protected async Task RunLoadAsync(Func<Task> body)
    {
        _loading = true;
        _error   = null;
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    // ── Date-range helpers ────────────────────────────────────────────────────

    protected DateTime RangeStart => State.SelectedRange.Start ?? DateTime.Now.AddYears(-1);
    protected DateTime RangeEnd   => State.SelectedRange.End   ?? DateTime.Now;

    // ── Common helpers ────────────────────────────────────────────────────────

    protected string Fmt(decimal v) => $"{v:N0} {Options.TargetCurrency}";

    protected string GetSubName(string subscriptionId) =>
        _subNames.TryGetValue(subscriptionId, out var name) ? name : subscriptionId;

    /// <summary>
    /// Returns subscription IDs in alphabetical order by display name, deduped.
    /// </summary>
    protected List<string> GetOrderedSubscriptionIds() =>
        SubStore.AllIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetSubName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Counts distinct subscription IDs that appear in <paramref name="rows"/>.
    /// Assigns the result to <see cref="_withDataSubCount"/>.
    /// </summary>
    protected void ComputeWithDataSubCount(IEnumerable<CostRow> rows)
    {
        _withDataSubCount = rows
            .Where(r => !string.IsNullOrWhiteSpace(r.SubscriptionId))
            .Select(r => r.SubscriptionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose() => State.OnStateChanged -= RefreshData;
}
