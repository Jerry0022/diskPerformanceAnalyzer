using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DiskPerformanceAnalyzer.Monitoring;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>One physical disk in the left rail: name, current requests/s and data rate (read/write split), and a 60 s requests sparkline.</summary>
public partial class DiskViewModel : ObservableObject
{
    public const int SparklineLength = 60;

    [ObservableProperty] private double _readBytesPerSec;
    [ObservableProperty] private double _writeBytesPerSec;
    [ObservableProperty] private double _queueLength;
    [ObservableProperty] private double _readsPerSec;
    [ObservableProperty] private double _writesPerSec;
    [ObservableProperty] private double _opsPerSec;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _driveLetters;

    public DiskViewModel(int diskNumber, IReadOnlyList<string> driveLetters, string? title = null)
    {
        DiskNumber = diskNumber;
        Title = title ?? $"Disk {diskNumber}";
        _driveLetters = string.Join(" ", driveLetters);
        _name = DisplayName(Title, driveLetters);

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
        SparkYAxes = [new Axis { IsVisible = false, MinLimit = 0 }];
    }

    public int DiskNumber { get; }

    public ObservableCollection<double> Sparkline { get; } = new();

    public ISeries[] SparkSeries { get; }
    public Axis[] SparkXAxes { get; }
    public Axis[] SparkYAxes { get; }

    /// <summary>Headline number without unit; the unit sits in its own, never-moving label.</summary>
    public string IopsText => Formatting.CountPerSec(OpsPerSec);
    public string ReadOpsText => Formatting.CountPerSec(ReadsPerSec);
    public string WriteOpsText => Formatting.CountPerSec(WritesPerSec);
    public string ReadRateText => Formatting.Rate(ReadBytesPerSec);
    public string WriteRateText => Formatting.Rate(WriteBytesPerSec);
    public string QueueText => QueueLength.ToString("0.0", CultureInfo.InvariantCulture);

    public string CardTooltip =>
        $"{Name}\n" +
        $"{Labels.Requests}: {Formatting.Iops(OpsPerSec)}  ·  {Labels.Read} {ReadOpsText}  {Labels.Write} {WriteOpsText}\n" +
        $"{Labels.Data}: {Formatting.Rate(ReadBytesPerSec + WriteBytesPerSec)}  ·  {Labels.Read} {ReadRateText}  {Labels.Write} {WriteRateText}\n" +
        $"Queue length {QueueText}";

    /// <summary>"Disk 0" / "nvme0n1" — see <see cref="DiskSample.Title"/>.</summary>
    public string Title { get; }

    /// <summary>"Disk 0 (C: D:)" on Windows, "nvme0n1 (/ /home)" on Linux.</summary>
    public static string DisplayName(string title, IReadOnlyList<string> driveLetters) =>
        driveLetters.Count == 0 ? title : $"{title} ({string.Join(" ", driveLetters)})";

    public void Update(DiskSample sample)
    {
        ReadBytesPerSec = sample.ReadBytesPerSec;
        WriteBytesPerSec = sample.WriteBytesPerSec;
        QueueLength = sample.QueueLength;
        ReadsPerSec = sample.ReadsPerSec;
        WritesPerSec = sample.WritesPerSec;
        OpsPerSec = sample.OpsPerSec;
        if (sample.DriveLetters.Count > 0)
        {
            DriveLetters = string.Join(" ", sample.DriveLetters);
            Name = DisplayName(Title, sample.DriveLetters);
        }

        Sparkline.Add(OpsPerSec);
        while (Sparkline.Count > SparklineLength)
        {
            Sparkline.RemoveAt(0);
        }

        OnPropertyChanged(nameof(IopsText));
        OnPropertyChanged(nameof(ReadOpsText));
        OnPropertyChanged(nameof(WriteOpsText));
        OnPropertyChanged(nameof(ReadRateText));
        OnPropertyChanged(nameof(WriteRateText));
        OnPropertyChanged(nameof(QueueText));
        OnPropertyChanged(nameof(CardTooltip));
    }
}
