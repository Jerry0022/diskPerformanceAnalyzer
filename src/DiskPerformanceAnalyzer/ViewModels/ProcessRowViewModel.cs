using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.Platform;

namespace DiskPerformanceAnalyzer.ViewModels;

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
    [NotifyPropertyChangedFor(nameof(TopFileText), nameof(HasTopFile))]
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand))]
    private string? _topFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopFileTooltip))]
    private IReadOnlyList<string> _topFiles = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareText))]
    private double _share;

    public ProcessRowViewModel(ProcessIo io, long windowTotalBytes, IProcessRowHost host)
    {
        _host = host;
        Pid = io.Pid;
        ImagePath = ProcessImagePath.TryGet(io.Pid, out var path) ? path : null;
        Update(io, windowTotalBytes);
    }

    public int Pid { get; }
    public string? ImagePath { get; }

    /// <summary>Refreshes the mutable columns from a new aggregation of the same PID.</summary>
    public void Update(ProcessIo io, long windowTotalBytes)
    {
        ProcessName = string.IsNullOrWhiteSpace(io.ProcessName) ? $"PID {io.Pid}" : io.ProcessName;
        ReadBytes = io.ReadBytes;
        WriteBytes = io.WriteBytes;
        TotalBytes = io.TotalBytes;
        TopFile = io.TopFiles.Count > 0 ? io.TopFiles[0] : null;
        TopFiles = io.TopFiles;
        Share = windowTotalBytes > 0 ? (double)TotalBytes / windowTotalBytes : 0;
    }

    public string ReadText => Formatting.Bytes(ReadBytes);
    public string WriteText => Formatting.Bytes(WriteBytes);
    public string TotalText => Formatting.Bytes(TotalBytes);
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
