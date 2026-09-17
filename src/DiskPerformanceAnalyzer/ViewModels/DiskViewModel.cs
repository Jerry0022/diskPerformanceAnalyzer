using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskPerformanceAnalyzer.Monitoring;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>One physical disk in the left rail: name, current numbers and a 60 s sparkline.</summary>
public partial class DiskViewModel : ObservableObject
{
    public const int SparklineLength = 60;

    [ObservableProperty] private double _activePercent;
    [ObservableProperty] private double _readBytesPerSec;
    [ObservableProperty] private double _writeBytesPerSec;
    [ObservableProperty] private double _queueLength;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _driveLetters;

    public DiskViewModel(int diskNumber, IReadOnlyList<string> driveLetters)
    {
        DiskNumber = diskNumber;
        _driveLetters = string.Join(" ", driveLetters);
        _name = DisplayName(diskNumber, driveLetters);

        SparkSeries =
        [
            new LineSeries<double>
            {
                Values = Sparkline,
                Fill = new SolidColorPaint(new SKColor(0x3B, 0x8E, 0xEA, 0x50)),
                Stroke = new SolidColorPaint(new SKColor(0x3B, 0x8E, 0xEA), 1.5f),
                GeometryFill = null,
                GeometryStroke = null,
                GeometrySize = 0,
                LineSmoothness = 0,
                AnimationsSpeed = TimeSpan.Zero,
            },
        ];
        SparkXAxes = [new Axis { IsVisible = false, MinLimit = 0, MaxLimit = SparklineLength - 1 }];
        SparkYAxes = [new Axis { IsVisible = false, MinLimit = 0, MaxLimit = 100 }];
    }

    public int DiskNumber { get; }

    public ObservableCollection<double> Sparkline { get; } = new();

    public ISeries[] SparkSeries { get; }
    public Axis[] SparkXAxes { get; }
    public Axis[] SparkYAxes { get; }

    public string ActivePercentText => Formatting.Percent(ActivePercent);
    public string ReadRateText => Formatting.Rate(ReadBytesPerSec);
    public string WriteRateText => Formatting.Rate(WriteBytesPerSec);
    public string ThroughputText => $"R {ReadRateText}   W {WriteRateText}";

    public static string DisplayName(int diskNumber, IReadOnlyList<string> driveLetters) =>
        driveLetters.Count == 0
            ? $"Disk {diskNumber}"
            : $"Disk {diskNumber} ({string.Join(" ", driveLetters)})";

    public void Update(DiskSample sample)
    {
        ActivePercent = Math.Clamp(sample.ActivePercent, 0, 100);
        ReadBytesPerSec = sample.ReadBytesPerSec;
        WriteBytesPerSec = sample.WriteBytesPerSec;
        QueueLength = sample.QueueLength;
        if (sample.DriveLetters.Count > 0)
        {
            DriveLetters = string.Join(" ", sample.DriveLetters);
            Name = DisplayName(sample.DiskNumber, sample.DriveLetters);
        }

        Sparkline.Add(ActivePercent);
        while (Sparkline.Count > SparklineLength)
        {
            Sparkline.RemoveAt(0);
        }

        OnPropertyChanged(nameof(ActivePercentText));
        OnPropertyChanged(nameof(ReadRateText));
        OnPropertyChanged(nameof(WriteRateText));
        OnPropertyChanged(nameof(ThroughputText));
    }
}
