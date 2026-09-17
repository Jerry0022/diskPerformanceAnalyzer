using CommunityToolkit.Mvvm.Input;
using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.Platform;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>Callbacks a process row needs from its owner (clipboard access lives in the view).</summary>
public interface IProcessRowHost
{
    Task CopyTextAsync(string text);
}

/// <summary>One line in the process table: who moved how many bytes on the selected disk, and where.</summary>
public sealed partial class ProcessRowViewModel
{
    private readonly IProcessRowHost _host;

    public ProcessRowViewModel(ProcessIo io, long windowTotalBytes, IProcessRowHost host)
    {
        _host = host;
        Pid = io.Pid;
        ProcessName = string.IsNullOrWhiteSpace(io.ProcessName) ? $"PID {io.Pid}" : io.ProcessName;
        ReadBytes = io.ReadBytes;
        WriteBytes = io.WriteBytes;
        TotalBytes = io.TotalBytes;
        TopFile = io.TopFiles.Count > 0 ? io.TopFiles[0] : null;
        TopFiles = io.TopFiles;
        Share = windowTotalBytes > 0 ? (double)TotalBytes / windowTotalBytes : 0;
        ImagePath = ProcessImagePath.TryGet(io.Pid, out var path) ? path : null;
    }

    public int Pid { get; }
    public string ProcessName { get; }
    public long ReadBytes { get; }
    public long WriteBytes { get; }
    public long TotalBytes { get; }
    public string? TopFile { get; }
    public IReadOnlyList<string> TopFiles { get; }
    public string? ImagePath { get; }

    /// <summary>Fraction (0..1) of all bytes in the window attributed to this process.</summary>
    public double Share { get; }

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
