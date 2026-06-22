using CmCSP.Data;
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
    [Inject] protected TenantNameService      TenantNames   { get; set; } = default!;

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

    /// <summary>
    /// The subscriptions in the current tenant scope (home registry + every in-scope customer's
    /// mapped subscriptions). Built by <see cref="ComputeSelectedSubCountAsync"/> and used by
    /// <see cref="GetOrderedSubscriptionIds"/> so chart series include customer subscriptions, not
    /// just the home registry.
    /// </summary>
    protected readonly HashSet<string> _scopeSubIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Authoritative customer subscription id → name fallback, populated from the
    /// <c>CustomerSubscription</c> registry each load. Kept separate from <see cref="_subNames"/>
    /// (which pages reassign from the home-only display-name API) so customer subscription names
    /// are never lost and never render as a bare GUID.
    /// </summary>
    protected readonly Dictionary<string, string> _customerSubNames = new(StringComparer.OrdinalIgnoreCase);

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
        _scopeSubIds.Clear();
        _customerSubNames.Clear();

        if (scope.IsUnscoped)
        {
            foreach (var id in SubStore.AllIds) _scopeSubIds.Add(id);

            // Single-tenant: attribute the home subscriptions to the home tenant so any tenant
            // label (when multi-tenancy display is toggled) resolves to a name, not a GUID.
            if (Options.MultiTenancy.Enabled)
                await IndexHomeTenantAsync();
        }
        else
        {
            // The home/partner's own subscriptions live in the SubStore registry rather than the
            // CustomerSubscription table, so only fold them in when the home customer is in scope
            // (partner aggregate, or a partner drill-in to the home customer itself).
            var home = await Customers.GetHomeCustomerAsync();
            if (home is not null && scope.CustomerIds.Contains(home.Id))
            {
                foreach (var id in SubStore.AllIds)
                    _scopeSubIds.Add(id);
                await IndexHomeTenantAsync(home);
            }

            // For every in-scope customer pull the authoritative subscription names + tenant from
            // the CustomerSubscription registry. This guarantees customer subscriptions render a
            // friendly name (and tenant) in every visual even when the cost rows carry no name.
            // Names go into _customerSubNames (a fallback that pages never overwrite) so a page
            // reassigning _subNames from the home-only display-name API can't drop customer names.
            foreach (var customerId in scope.CustomerIds)
            {
                var customer = home is not null && customerId == home.Id
                    ? home
                    : await Customers.GetByIdAsync(customerId);
                var tenantId = customer?.TenantId;
                if (!string.IsNullOrWhiteSpace(tenantId))
                    await TenantNames.GetDisplayNameAsync(tenantId);

                foreach (var sub in await Customers.GetSubscriptionsAsync(customerId))
                {
                    _scopeSubIds.Add(sub.SubscriptionId);
                    if (!string.IsNullOrWhiteSpace(sub.SubscriptionName))
                        _customerSubNames[sub.SubscriptionId] = sub.SubscriptionName;
                    if (!string.IsNullOrWhiteSpace(tenantId))
                        _subTenantMap[sub.SubscriptionId] = tenantId!;
                }
            }
        }

        // The badge reflects what is actually in view: the scope narrowed by the picker's
        // subscription selection (an empty selection means "all in scope").
        var sel = State.SelectedSubscriptionIds;
        _selectedSubCount = sel.Count == 0
            ? _scopeSubIds.Count
            : _scopeSubIds.Count(sel.Contains);
    }

    /// <summary>
    /// Attributes the home registry's subscriptions to the home tenant in <see cref="_subTenantMap"/>
    /// and warms that tenant's display name, so home subscriptions carry a tenant label (and never a
    /// bare GUID) in multi-tenant visuals. The home tenant is the configured home tenant id (falling
    /// back to the deployment tenant id).
    /// </summary>
    private async Task IndexHomeTenantAsync(CustomerEntity? home = null)
    {
        home ??= await Customers.GetHomeCustomerAsync();
        var homeTenant = !string.IsNullOrWhiteSpace(home?.TenantId)
            ? home!.TenantId
            : !string.IsNullOrWhiteSpace(Options.MultiTenancy.HomeTenantId)
                ? Options.MultiTenancy.HomeTenantId
                : Options.TenantId;
        if (string.IsNullOrWhiteSpace(homeTenant)) return;

        await TenantNames.GetDisplayNameAsync(homeTenant);
        foreach (var id in SubStore.AllIds)
            _subTenantMap[id] = homeTenant;
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
        _subNames.TryGetValue(subscriptionId, out var name) && !string.IsNullOrWhiteSpace(name) &&
        !name.Equals(subscriptionId, StringComparison.OrdinalIgnoreCase)
            ? name
            : _customerSubNames.TryGetValue(subscriptionId, out var cn) && !string.IsNullOrWhiteSpace(cn)
                ? cn
                : subscriptionId;

    // ── Subscription view-filter + tenant display (Phase 9) ───────────────────

    /// <summary>True when the partner has narrowed the view to a subset of subscriptions.</summary>
    protected bool HasSubFilter => State.SelectedSubscriptionIds.Count > 0;

    /// <summary>
    /// Applies the user's subscription view-filter (<see cref="DashboardStateService.SelectedSubscriptionIds"/>)
    /// to a set of cost rows. An empty selection means "all" (no filtering). This is a presentation
    /// filter layered on top of the security <see cref="TenantScope"/> — it only ever narrows the
    /// rows already authorised for the user, never widens them.
    /// </summary>
    protected IReadOnlyList<CostRow> ApplySubFilter(IEnumerable<CostRow> rows)
    {
        var sel = State.SelectedSubscriptionIds;
        if (sel.Count == 0)
            return rows as IReadOnlyList<CostRow> ?? rows.ToList();
        return rows.Where(r => sel.Contains(r.SubscriptionId)).ToList();
    }

    /// <summary>True when tenant attribution is meaningful (multi-tenancy on with rows tagged).</summary>
    protected bool ShowTenantColumn => Options.MultiTenancy.Enabled;

    /// <summary>Subscription id → owning tenant id, built from the loaded rows by <see cref="IndexSubscriptionData"/>.</summary>
    protected readonly Dictionary<string, string> _subTenantMap = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Indexes the loaded cost rows so every visual can render names instead of GUIDs and attribute
    /// each subscription to its tenant. It (1) merges the row-level <c>SubscriptionName</c> into
    /// <see cref="_subNames"/> so customer subscriptions — which aren't in the home display-name
    /// lookup — still show a friendly name in charts/tables, (2) builds <see cref="_subTenantMap"/>,
    /// and (3) warms the tenant-name cache. Call this right after fetching data in each page.
    /// </summary>
    protected async Task IndexSubscriptionData(IEnumerable<CostRow> rows)
    {
        var list = rows as ICollection<CostRow> ?? rows.ToList();
        foreach (var r in list)
        {
            if (string.IsNullOrWhiteSpace(r.SubscriptionId)) continue;
            if (!string.IsNullOrWhiteSpace(r.SubscriptionName) &&
                (!_subNames.TryGetValue(r.SubscriptionId, out var existing) ||
                 string.IsNullOrWhiteSpace(existing) ||
                 existing.Equals(r.SubscriptionId, StringComparison.OrdinalIgnoreCase)))
            {
                _subNames[r.SubscriptionId] = r.SubscriptionName;
            }
            if (!string.IsNullOrWhiteSpace(r.TenantId))
                _subTenantMap[r.SubscriptionId] = r.TenantId;
        }
        await WarmTenantNamesAsync(list);
    }

    /// <summary>
    /// Warms the tenant-id → display-name cache for every tenant appearing in <paramref name="rows"/>
    /// so subsequent synchronous <see cref="TenantLabel"/> calls render names rather than GUIDs.
    /// </summary>
    protected async Task WarmTenantNamesAsync(IEnumerable<CostRow> rows)
    {
        if (!Options.MultiTenancy.Enabled) return;
        foreach (var tid in rows.Select(r => r.TenantId)
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await TenantNames.GetDisplayNameAsync(tid);
        }
    }

    /// <summary>A friendly tenant label for a row (resolved name, else GUID, else empty).</summary>
    protected string TenantLabel(CostRow r) => TenantNames.GetCachedOrId(r.TenantId);

    /// <summary>The friendly tenant name that owns <paramref name="subscriptionId"/> (empty if unknown).</summary>
    protected string SubTenantName(string subscriptionId) =>
        _subTenantMap.TryGetValue(subscriptionId, out var tid) ? TenantNames.GetCachedOrId(tid) : string.Empty;

    /// <summary>
    /// The label to use for a subscription in a chart series/legend: the subscription name, suffixed
    /// with its tenant name when multi-tenancy is on so a partner can see which tenant each series
    /// belongs to, in the form <c>subscription name [tenant name]</c>. Never a bare GUID when a name
    /// is known.
    /// </summary>
    protected string SubChartLabel(string subscriptionId)
    {
        var name = GetSubName(subscriptionId);
        if (Options.MultiTenancy.Enabled)
        {
            var tenant = SubTenantName(subscriptionId);
            if (!string.IsNullOrWhiteSpace(tenant) &&
                !tenant.Equals(name, StringComparison.OrdinalIgnoreCase))
                return $"{name} [{tenant}]";
        }
        return name;
    }

    /// <summary>
    /// Returns subscription IDs in alphabetical order by display name, deduped. Spans every
    /// subscription in the current tenant scope (home + customers), narrowed by the active
    /// subscription view-filter, so chart series include customer subscriptions for a partner.
    /// </summary>
    protected List<string> GetOrderedSubscriptionIds()
    {
        var source = _scopeSubIds.Count > 0 ? (IEnumerable<string>)_scopeSubIds : SubStore.AllIds;
        var sel = State.SelectedSubscriptionIds;
        return source
            .Where(id => sel.Count == 0 || sel.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetSubName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

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
