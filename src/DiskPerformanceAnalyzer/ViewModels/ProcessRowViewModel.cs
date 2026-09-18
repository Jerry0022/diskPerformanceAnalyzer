using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.Platform;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>Metric the process table is ordered by. Request count often explains a saturated disk better than bytes.</summary>
public enum ProcessSort
{
    Bytes,
    Ops,
}

/// <summary>Callbacks a process row needs from its owner (clipboard access lives in the view).</summary>
public interface IProcessRowHost
{
    Task CopyTextAsync(string text);
}

/// <summary>
/// One line in the process table: who moved how many bytes on the selected disk, and where.
/// Rows are long-lived and updated in place once per second so the visual tree (context menu,
/// buttons, tooltips) is not rebuilt on every tick.
/// </summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    private readonly IProcessRowHost _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ImagePathTooltip))]
    private string _processName = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadText))]
    private long _readBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteText))]
    private long _writeBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalText))]
    private long _totalBytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReadOpsText))]
    private long _readOps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WriteOpsText))]
    private long _writeOps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TotalOpsText), nameof(OpsTooltip))]
    private long _totalOps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopFileText), nameof(HasTopFile))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    private string? _topFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopFileTooltip))]
    private IReadOnlyList<string> _topFiles = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareText))]
    private double _share;

    public ProcessRowViewModel(ProcessIo io, long windowTotal, ProcessSort sortBy, IProcessRowHost host)
    {
        _host = host;
        Pid = io.Pid;
        ImagePath = ProcessImagePath.TryGet(io.Pid, out var path) ? path : null;
        Update(io, windowTotal, sortBy);
    }

    public int Pid { get; }
    public string? ImagePath { get; }

    /// <summary>Refreshes the mutable columns from a new aggregation of the same PID.</summary>
    /// <param name="windowTotal">Sum of the sort metric over all rows in the window; the share bar is relative to it.</param>
    /// <param name="sortBy">Which metric the share bar and ordering use.</param>
    public void Update(ProcessIo io, long windowTotal, ProcessSort sortBy)
    {
        ProcessName = string.IsNullOrWhiteSpace(io.ProcessName) ? $"PID {io.Pid}" : io.ProcessName;
        ReadBytes = io.ReadBytes;
        WriteBytes = io.WriteBytes;
        TotalBytes = io.TotalBytes;
        ReadOps = io.ReadOps;
        WriteOps = io.WriteOps;
        TotalOps = io.TotalOps;
        TopFile = io.TopFiles.Count > 0 ? io.TopFiles[0] : null;
        TopFiles = io.TopFiles;
        var metric = sortBy == ProcessSort.Ops ? TotalOps : TotalBytes;
        Share = windowTotal > 0 ? (double)metric / windowTotal : 0;
    }

    public string ReadText => Formatting.Bytes(ReadBytes);
    public string WriteText => Formatting.Bytes(WriteBytes);
    public string TotalText => Formatting.Bytes(TotalBytes);
    public string ReadOpsText => Formatting.Count(ReadOps);
    public string WriteOpsText => Formatting.Count(WriteOps);
    public string TotalOpsText => Formatting.Count(TotalOps);
    public string OpsTooltip => $"{ReadOps:N0} read + {WriteOps:N0} write requests";
    public string ShareText => $"{Share * 100:0}%";
    public string TopFileText => TopFile is null ? "-" : Path.GetFileName(TopFile) is { Length: > 0 } f ? f : TopFile;
    public string TopFileTooltip => TopFiles.Count == 0 ? "No file attributed" : string.Join(Environment.NewLine, TopFiles);
    public string ImagePathTooltip => ImagePath ?? $"{ProcessName} (PID {Pid}) - executable path unavailable";
    public bool HasTopFile => TopFile is not null;
    public bool HasImagePath => ImagePath is not null;

    [RelayCommand(CanExecute = nameof(HasImagePath))]
    private void OpenInExplorer() => ExplorerLauncher.Reveal(ImagePath!);

    [RelayCommand(CanExecute = nameof(HasTopFile))]
    private void OpenFileLocation() => ExplorerLauncher.Reveal(TopFile!);

    [RelayCommand]
    private Task CopyPath() => _host.CopyTextAsync(TopFile ?? ImagePath ?? ProcessName);

    [RelayCommand(CanExecute = nameof(HasImagePath))]
    private Task CopyImagePath() => _host.CopyTextAsync(ImagePath!);
}
