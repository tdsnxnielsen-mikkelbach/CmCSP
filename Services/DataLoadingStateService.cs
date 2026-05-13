namespace CmCSP.Services;

public enum LoadPhase { Idle, Loading, Ready, Failed }

/// <summary>State for a single cached dataset.</summary>
public sealed class DatasetStatus
{
    public required string   Key    { get; init; }
    public required string   Label  { get; init; }
    public          LoadPhase Phase  { get; internal set; } = LoadPhase.Idle;
    /// <summary>Optional detail shown in the UI chip, e.g. "1,234 rows".</summary>
    public          string?  Detail { get; internal set; }
}

/// <summary>
/// Singleton service that tracks the load phase of each of the three cached datasets.
/// Blazor components subscribe to <see cref="OnChanged"/> to get notified and re-render.
/// </summary>
public sealed class DataLoadingStateService
{
    public DatasetStatus Main { get; } = new() { Key = "cm_main", Label = "Cost by Service" };
    public DatasetStatus Rg   { get; } = new() { Key = "cm_rg",   Label = "Resource Groups" };
    public DatasetStatus Tag  { get; } = new() { Key = "cm_tag",  Label = "Tag Chargeback"  };

    public IReadOnlyList<DatasetStatus> All => [Main, Rg, Tag];

    public bool IsLoading => All.Any(d => d.Phase == LoadPhase.Loading);
    public bool IsAllDone => All.All(d => d.Phase is LoadPhase.Ready or LoadPhase.Failed);
    public int  DoneCount => All.Count(d => d.Phase is LoadPhase.Ready or LoadPhase.Failed);

    /// <summary>Raised on the calling thread whenever any dataset state changes.</summary>
    public event Action? OnChanged;

    /// <summary>Returns the <see cref="DatasetStatus"/> for the given cache key.</summary>
    internal DatasetStatus For(string cacheKey) => cacheKey switch
    {
        "cm_main" => Main,
        "cm_rg"   => Rg,
        "cm_tag"  => Tag,
        _         => throw new ArgumentOutOfRangeException(nameof(cacheKey))
    };

    /// <summary>Called by <see cref="CostManagementService"/> to report progress.</summary>
    internal void Update(string cacheKey, LoadPhase phase, string? detail = null)
    {
        var ds = cacheKey switch
        {
            "cm_main" => Main,
            "cm_rg"   => Rg,
            "cm_tag"  => Tag,
            _         => null
        };
        if (ds is null) return;

        ds.Phase  = phase;
        ds.Detail = detail;
        OnChanged?.Invoke();
    }
}
