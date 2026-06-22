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
    [Inject] protected ITenantScopeProvider   ScopeProvider { get; set; } = default!;
    [Inject] protected TenantScopeAccessor    ScopeAccessor { get; set; } = default!;
    [Inject] protected CustomerStore          Customers     { get; set; } = default!;

    protected bool    _loading = true;
    protected string? _error;
    protected int     _withDataSubCount;

    /// <summary>
    /// The number of subscriptions in the current tenant scope, shown as "selected" on the
    /// <c>SubscriptionScopeBadge</c>. In the single-tenant path this is the home subscription
    /// registry; under multi-tenancy it also includes the mapped subscriptions of every customer
    /// in scope (which live in a separate table from the home registry).
    /// </summary>
    protected int     _selectedSubCount;
    protected Dictionary<string, string> _subNames = new(StringComparer.OrdinalIgnoreCase);

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    protected override void OnInitialized() =>
        State.OnStateChanged += RefreshData;

    protected override async Task OnInitializedAsync() =>
        await LoadWithScopeAsync();

    private async void RefreshData()
    {
        await LoadWithScopeAsync();
        await InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Phase 9: resolves the signed-in user's tenant scope and publishes it onto the ambient
    /// <see cref="TenantScopeAccessor"/> (in this page's async context, so it flows down to the
    /// singleton cost service's cache-key and SQL scoping) before invoking <see cref="LoadAsync"/>.
    /// A denied scope (unknown/suspended tenant) short-circuits with an access error and loads no
    /// data. In the single-tenant path the scope is <c>Unscoped</c> and this is a no-op pass-through.
    /// </summary>
    private async Task LoadWithScopeAsync()
    {
        var scope = await ScopeProvider.GetScopeAsync();

        // Partner drill-in: when a partner has selected a single customer in the picker, narrow
        // the resolved (all-customers) scope to just that customer so reads + cache key partition
        // to it. The selection is ignored unless it's within the partner's authorised set.
        if (scope.IsPartner && State.SelectedCustomerId is { } picked && scope.CustomerIds.Contains(picked))
        {
            scope = scope with { IsPartner = false, CustomerIds = [picked] };
        }

        ScopeAccessor.Current = scope;

        if (scope.IsDenied)
        {
            _selectedSubCount = 0;
            _loading = false;
            _error = "Your account's tenant is not authorized to view this dashboard.";
            return;
        }

        await ComputeSelectedSubCountAsync(scope);
        await LoadAsync();
    }

    /// <summary>
    /// Computes <see cref="_selectedSubCount"/> — the subscriptions in the current scope. In the
    /// single-tenant (unscoped) path this is just the home registry (<see cref="SubStore"/>),
    /// identical to before. Under multi-tenancy it unions the home registry (only when the home
    /// customer is in scope — so a customer never sees the partner's own subscription count) with
    /// every in-scope customer's mapped subscriptions from the <c>CustomerSubscription</c> table.
    /// </summary>
    private async Task ComputeSelectedSubCountAsync(TenantScope scope)
    {
        if (scope.IsUnscoped)
        {
            _selectedSubCount = SubStore.AllIds.Count;
            return;
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The home/partner's own subscriptions live in the SubStore registry rather than the
        // CustomerSubscription table, so only fold them in when the home customer is in scope
        // (partner aggregate, or a partner drill-in to the home customer itself).
        var home = await Customers.GetHomeCustomerAsync();
        if (home is not null && scope.CustomerIds.Contains(home.Id))
            foreach (var id in SubStore.AllIds)
                ids.Add(id);

        foreach (var customerId in scope.CustomerIds)
            foreach (var id in await Customers.GetSubscriptionIdsAsync(customerId))
                ids.Add(id);

        _selectedSubCount = ids.Count;
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
