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
/// One line in the process table: who moved how many bytes and issued how many requests on the
/// selected disk, and where. Rows are long-lived and updated in place once per second so the
/// visual tree is not rebuilt on every tick.
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
    [NotifyCanExecuteChangedFor(nameof(OpenFileLocationCommand), nameof(CopyFilePathCommand))]
    private string? _topFile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopFileTooltip))]
    private IReadOnlyList<string> _topFiles = Array.Empty<string>();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareText), nameof(DetailTooltip))]
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

    /// <param name="windowTotal">Sum of the share metric over all rows in the window; the share bar is relative to it.</param>
    /// <param name="sortBy">Which metric the share bar uses.</param>
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
        var metric = sortBy == ProcessSort.Bytes ? TotalBytes : TotalOps;
        Share = windowTotal > 0 ? (double)metric / windowTotal : 0;
    }

    /// <summary>Value of a sortable column, used by the owner to order rows.</summary>
    public IComparable SortKey(ProcessSort column) => column switch
    {
        ProcessSort.Name => ProcessName,
        ProcessSort.Pid => Pid,
        ProcessSort.Read => ReadBytes,
        ProcessSort.Write => WriteBytes,
        ProcessSort.Bytes => TotalBytes,
        ProcessSort.Share => Share,
        ProcessSort.TopFile => TopFile ?? string.Empty,
        _ => TotalOps,
    };

    public string ReadText => Formatting.Bytes(ReadBytes);
    public string WriteText => Formatting.Bytes(WriteBytes);
    public string TotalText => Formatting.Bytes(TotalBytes);
    public string ReadOpsText => Formatting.Count(ReadOps);
    public string WriteOpsText => Formatting.Count(WriteOps);
    public string TotalOpsText => Formatting.Count(TotalOps);
    public string OpsTooltip => $"{ReadOps:N0} read + {WriteOps:N0} write requests";
    public string ShareText => $"{Share * 100:0}%";

    /// <summary>The per-process numbers live behind the share bar, so the columns stay uncluttered.</summary>
    public string DetailTooltip =>
        $"{ProcessName}\n{Formatting.Count(TotalOps)} requests ({Formatting.Count(ReadOps)} read, {Formatting.Count(WriteOps)} write)\nRead {ReadText}  ·  Write {WriteText}  ·  Total {TotalText}";
    public string TopFileText => Formatting.ShortPath(TopFile);
    public string TopFileTooltip => TopFiles.Count == 0 ? "No file attributed" : string.Join(Environment.NewLine, TopFiles);
    public string ImagePathTooltip => ImagePath ?? $"{ProcessName} (PID {Pid}) - executable path unavailable";
    public bool HasTopFile => TopFile is not null;
    public bool HasImagePath => ImagePath is not null;

    /// <summary>Folder icon next to the process name: reveal the executable in Explorer.</summary>
    [RelayCommand(CanExecute = nameof(HasImagePath))]
    private void OpenInExplorer() => ExplorerLauncher.Reveal(ImagePath!);

    /// <summary>Share icon next to the process name: copy the executable path.</summary>
    [RelayCommand(CanExecute = nameof(HasImagePath))]
    private Task CopyImagePath() => _host.CopyTextAsync(ImagePath!);

    /// <summary>Folder icon next to the top file: reveal that file in Explorer.</summary>
    [RelayCommand(CanExecute = nameof(HasTopFile))]
    private void OpenFileLocation() => ExplorerLauncher.Reveal(TopFile!);

    /// <summary>Share icon next to the top file: copy its full path.</summary>
    [RelayCommand(CanExecute = nameof(HasTopFile))]
    private Task CopyFilePath() => _host.CopyTextAsync(TopFile!);
}

/// <summary>Sortable columns of the process table. <see cref="Ops"/> and <see cref="Bytes"/> also drive the share bar.</summary>
public enum ProcessSort
{
    Ops,
    Bytes,
    Name,
    Pid,
    Read,
    Write,
    Share,
    TopFile,
}
