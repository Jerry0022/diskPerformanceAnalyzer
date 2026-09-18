using System.Collections.ObjectModel;
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
/// One line in the process table: who issued how many requests and moved how many bytes on the
/// selected disk, and where. Rows are long-lived and updated in place once per second so the
/// visual tree is not rebuilt on every tick. An expanded row shows the folder breakdown.
/// </summary>
public sealed partial class ProcessRowViewModel : ObservableObject
{
    private readonly IProcessRowHost _host;
    private ProcessIo _io;

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

    /// <summary>This process's part of all requests in the window (0–1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareOpsText), nameof(DetailTooltip))]
    private double _shareOps;

    /// <summary>This process's part of all bytes in the window (0–1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareBytesText), nameof(DetailTooltip))]
    private double _shareBytes;

    /// <summary>Which share bar is the sort key; the other one is drawn dimmed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpsPrimary), nameof(IsBytesPrimary))]
    private ProcessSort _primaryMetric = ProcessSort.Ops;

    /// <summary>Folder breakdown visible below the row. The breakdown is only computed while expanded.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    public ProcessRowViewModel(ProcessIo io, long windowTotalOps, long windowTotalBytes, ProcessSort primaryMetric, IProcessRowHost host)
    {
        _host = host;
        _io = io;
        Pid = io.Pid;
        ImagePath = ProcessImagePath.TryGet(io.Pid, out var path) ? path : null;
        Update(io, windowTotalOps, windowTotalBytes, primaryMetric);
    }

    public int Pid { get; }
    public string? ImagePath { get; }

    /// <summary>Top folders of this process (see <see cref="FolderBreakdown"/>); filled while <see cref="IsExpanded"/>.</summary>
    public ObservableCollection<FolderRowViewModel> Breakdown { get; } = new();

    /// <param name="windowTotalOps">All requests in the window; the request share bar is relative to it.</param>
    /// <param name="windowTotalBytes">All bytes in the window; the data share bar is relative to it.</param>
    public void Update(ProcessIo io, long windowTotalOps, long windowTotalBytes, ProcessSort primaryMetric)
    {
        _io = io;
        ProcessName = string.IsNullOrWhiteSpace(io.ProcessName) ? $"PID {io.Pid}" : io.ProcessName;
        ReadBytes = io.ReadBytes;
        WriteBytes = io.WriteBytes;
        TotalBytes = io.TotalBytes;
        ReadOps = io.ReadOps;
        WriteOps = io.WriteOps;
        TotalOps = io.TotalOps;
        TopFile = io.TopFiles.Count > 0 ? io.TopFiles[0] : null;
        TopFiles = io.TopFiles;
        ShareOps = windowTotalOps > 0 ? (double)TotalOps / windowTotalOps : 0;
        ShareBytes = windowTotalBytes > 0 ? (double)TotalBytes / windowTotalBytes : 0;
        PrimaryMetric = primaryMetric == ProcessSort.Bytes ? ProcessSort.Bytes : ProcessSort.Ops;
        if (IsExpanded)
        {
            RefreshBreakdown();
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            RefreshBreakdown();
        }
        else
        {
            Breakdown.Clear();
        }
    }

    /// <summary>Rebuilds the folder rows in place (keyed by path) so an open breakdown does not flicker.</summary>
    private void RefreshBreakdown()
    {
        var shares = FolderBreakdown.Build(_io);
        var byPath = Breakdown.ToDictionary(r => r.Path, StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < shares.Count; index++)
        {
            var share = shares[index];
            if (!byPath.TryGetValue(share.Path, out var row))
            {
                row = new FolderRowViewModel(share.Path, _host);
            }

            row.Update(share, TotalOps, TotalBytes);
            var current = Breakdown.IndexOf(row);
            if (current != index)
            {
                if (current >= 0)
                {
                    Breakdown.RemoveAt(current);
                }

                Breakdown.Insert(index, row);
            }
        }

        while (Breakdown.Count > shares.Count)
        {
            Breakdown.RemoveAt(Breakdown.Count - 1);
        }
    }

    /// <summary>Value of a sortable column, used by the owner to order rows.</summary>
    public IComparable SortKey(ProcessSort column) => column switch
    {
        ProcessSort.Name => ProcessName,
        ProcessSort.Pid => Pid,
        ProcessSort.Read => ReadBytes,
        ProcessSort.Write => WriteBytes,
        ProcessSort.Bytes => TotalBytes,
        ProcessSort.TopFile => TopFile ?? string.Empty,
        _ => TotalOps,
    };

    public bool IsOpsPrimary => PrimaryMetric == ProcessSort.Ops;
    public bool IsBytesPrimary => PrimaryMetric == ProcessSort.Bytes;

    public string ReadText => Formatting.Bytes(ReadBytes);
    public string WriteText => Formatting.Bytes(WriteBytes);
    public string TotalText => Formatting.Bytes(TotalBytes);
    public string ReadOpsText => Formatting.Count(ReadOps);
    public string WriteOpsText => Formatting.Count(WriteOps);
    public string TotalOpsText => Formatting.Count(TotalOps);
    public string OpsTooltip => $"{Labels.Read} {ReadOps:N0} + {Labels.Write} {WriteOps:N0} requests";
    public string ShareOpsText => Formatting.Share(ShareOps);
    public string ShareBytesText => Formatting.Share(ShareBytes);

    /// <summary>The per-process numbers live behind the share bars, so the columns stay uncluttered.</summary>
    public string DetailTooltip =>
        $"{ProcessName}\n" +
        $"{Labels.Requests}: {TotalOpsText} ({ShareOpsText} of all)  ·  {Labels.Read} {ReadOpsText}  {Labels.Write} {WriteOpsText}\n" +
        $"{Labels.Data}: {TotalText} ({ShareBytesText} of all)  ·  {Labels.Read} {ReadText}  {Labels.Write} {WriteText}";
    public string TopFileText => Formatting.ShortPath(TopFile);
    public string TopFileTooltip => TopFiles.Count == 0 ? "No file attributed" : string.Join(Environment.NewLine, TopFiles);
    public string ImagePathTooltip => ImagePath ?? $"{ProcessName} (PID {Pid}) - executable path unavailable";
    public bool HasTopFile => TopFile is not null;
    public bool HasImagePath => ImagePath is not null;

    /// <summary>Chevron in the process cell: show or hide the folder breakdown.</summary>
    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

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

/// <summary>One folder line in an expanded process row.</summary>
public sealed partial class FolderRowViewModel : ObservableObject
{
    private readonly IProcessRowHost _host;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpsText))]
    private long _ops;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BytesText))]
    private long _bytes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilesText))]
    private int _fileCount;

    /// <summary>Part of the owning process's requests (0–1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareOpsText))]
    private double _shareOps;

    /// <summary>Part of the owning process's bytes (0–1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShareBytesText))]
    private double _shareBytes;

    public FolderRowViewModel(string path, IProcessRowHost host)
    {
        Path = path;
        _host = host;
    }

    public string Path { get; }
    public bool IsUnnamed => Path.Length == 0;
    public bool HasPath => !IsUnnamed;
    public string PathText => IsUnnamed ? "Unnamed I/O — paging, NTFS metadata, cache flushes" : Formatting.ShortPath(Path);
    public string PathTooltip => IsUnnamed
        ? "The kernel reported no file for these requests: page file, $Mft/$LogFile, volume cache flushes or files that were already open before this app started."
        : Path;

    public string OpsText => Formatting.Count(Ops);
    public string BytesText => Formatting.Bytes(Bytes);
    public string FilesText => FileCount switch { 0 => string.Empty, 1 => "1 file", _ => $"{FileCount} files" };
    public string ShareOpsText => Formatting.Share(ShareOps);
    public string ShareBytesText => Formatting.Share(ShareBytes);

    public void Update(FolderShare share, long processOps, long processBytes)
    {
        Ops = share.Ops;
        Bytes = share.Bytes;
        FileCount = share.FileCount;
        ShareOps = processOps > 0 ? (double)share.Ops / processOps : 0;
        ShareBytes = processBytes > 0 ? (double)share.Bytes / processBytes : 0;
    }

    [RelayCommand(CanExecute = nameof(HasPath))]
    private void OpenFolder() => ExplorerLauncher.OpenFolder(Path);

    [RelayCommand(CanExecute = nameof(HasPath))]
    private Task CopyPath() => _host.CopyTextAsync(Path);
}

/// <summary>Sortable columns of the process table. <see cref="Ops"/> and <see cref="Bytes"/> are the two share bars.</summary>
public enum ProcessSort
{
    Ops,
    Bytes,
    Name,
    Pid,
    Read,
    Write,
    TopFile,
}
