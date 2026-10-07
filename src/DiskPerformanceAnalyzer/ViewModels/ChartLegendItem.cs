using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore;
using SkiaSharp;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>
/// One clickable legend chip above the chart: toggles its series on and off and, while a range
/// is selected, shows that series' average over the range.
/// </summary>
public sealed partial class ChartLegendItem : ObservableObject
{
    private readonly ISeries _series;

    public ChartLegendItem(ISeries series, string label, SKColor color, bool isArea)
    {
        _series = series;
        Label = label;
        IsArea = isArea;
        Stroke = new ImmutableSolidColorBrush(Color.FromRgb(color.Red, color.Green, color.Blue));
        Fill = new ImmutableSolidColorBrush(Color.FromArgb(0x66, color.Red, color.Green, color.Blue));
    }

    public string Label { get; }

    /// <summary>Data series are drawn as filled areas, request series as lines; the swatch matches.</summary>
    public bool IsArea { get; }
    public bool IsLine => !IsArea;

    public IBrush Stroke { get; }
    public IBrush Fill { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    private bool _isVisible = true;

    /// <summary>"Ø 12.3 MB/s" while a range is selected, otherwise null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAverage))]
    private string? _averageText;

    public bool HasAverage => AverageText is not null;

    public string Tooltip => IsVisible ? $"Hide {Label} in the chart" : $"Show {Label} in the chart";

    partial void OnIsVisibleChanged(bool value) => _series.IsVisible = value;
}
