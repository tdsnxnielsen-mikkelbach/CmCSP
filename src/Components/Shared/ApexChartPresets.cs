using ApexCharts;

namespace CmCSP.Components.Shared;

/// <summary>
/// Factory methods for commonly-used ApexChart configurations.
/// Eliminates repeated chart options boilerplate across pages.
/// </summary>
public static class ApexChartPresets
{
    private const string CurrencyFormatter =
        "function(val) { return val != null ? val.toFixed(2) : ''; }";

    private const string IntFormatter =
        "function(val) { return val != null ? val.toFixed(0) : ''; }";

    private const string ParseFloatFormatter =
        "function(val) { return val != null ? parseFloat(val).toFixed(2) : ''; }";

    /// <summary>Standard horizontal bar chart (no toolbar, rounded corners).</summary>
    public static ApexChartOptions<T> HorizontalBar<T>(int? height = null) where T : class => new()
    {
        Chart = new Chart
        {
            Toolbar = new Toolbar { Show = false },
            Height = height is not null ? $"{height}" : null
        },
        PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { Horizontal = true, BorderRadius = 3 } },
        DataLabels = new DataLabels { Enabled = false }
    };

    /// <summary>Stacked vertical bar chart with currency Y-axis.</summary>
    public static ApexChartOptions<T> StackedBar<T>(int borderRadius = 4) where T : class => new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false }, Stacked = true },
        PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { BorderRadius = borderRadius } },
        Yaxis = [new YAxis { Labels = new YAxisLabels { Formatter = CurrencyFormatter } }]
    };

    /// <summary>Vertical bar chart (non-stacked) with currency Y-axis.</summary>
    public static ApexChartOptions<T> VerticalBar<T>(int borderRadius = 3) where T : class => new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false } },
        PlotOptions = new PlotOptions { Bar = new PlotOptionsBar { BorderRadius = borderRadius } },
        Yaxis = [new YAxis { Labels = new YAxisLabels { Formatter = CurrencyFormatter } }]
    };

    /// <summary>Line chart with smooth curve and currency Y-axis.</summary>
    public static ApexChartOptions<T> SmoothLine<T>(double[]? dashArray = null) where T : class => new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false } },
        Stroke = new Stroke
        {
            Curve = Curve.Smooth,
            DashArray = dashArray is not null ? new Size(dashArray) : null
        },
        Yaxis = [new YAxis { Labels = new YAxisLabels { Formatter = CurrencyFormatter } }]
    };

    /// <summary>Donut chart with bottom legend.</summary>
    public static ApexChartOptions<T> Donut<T>() where T : class => new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false } },
        Legend = new Legend { Position = LegendPosition.Bottom }
    };

    /// <summary>
    /// Waterfall-style bar with green (negative) / red (positive) color ranges.
    /// Used for MoM change charts.
    /// </summary>
    public static ApexChartOptions<T> WaterfallBar<T>(bool horizontal = false) where T : class => new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false } },
        PlotOptions = new PlotOptions
        {
            Bar = new PlotOptionsBar
            {
                Horizontal = horizontal,
                BorderRadius = 3,
                Colors = new PlotOptionsBarColors
                {
                    Ranges =
                    [
                        new PlotOptionsBarColorRange { From = -10_000_000d, To = -0.01d, Color = "#00C853" },
                        new PlotOptionsBarColorRange { From = 0d, To = 10_000_000d, Color = "#D50000" }
                    ]
                }
            }
        },
        DataLabels = new DataLabels { Enabled = false },
        Yaxis = horizontal
            ? null
            : [new YAxis { Labels = new YAxisLabels { Formatter = CurrencyFormatter } }],
        Xaxis = horizontal
            ? new XAxis { Labels = new XAxisLabels { Formatter = ParseFloatFormatter } }
            : null
    };

    /// <summary>Apply currency-formatted Y-axis to existing options.</summary>
    public static ApexChartOptions<T> WithCurrencyYAxis<T>(this ApexChartOptions<T> opts) where T : class
    {
        opts.Yaxis = [new YAxis { Labels = new YAxisLabels { Formatter = CurrencyFormatter } }];
        return opts;
    }

    /// <summary>Apply integer-formatted X-axis labels.</summary>
    public static ApexChartOptions<T> WithIntXAxis<T>(this ApexChartOptions<T> opts) where T : class
    {
        opts.Xaxis = new XAxis { Labels = new XAxisLabels { Formatter = IntFormatter } };
        return opts;
    }
}
