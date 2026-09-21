using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DiskPerformanceAnalyzer.Platform;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>
/// The settings flyout: install the program to a fixed per-user folder, and once installed
/// toggle the Start menu shortcut and the elevated logon task. Every change runs off the UI
/// thread (file copies, schtasks) and is then re-read from the system, so a failed change snaps
/// the checkbox back and shows why. Windows only; on other platforms the flyout says so.
/// </summary>
public partial class IntegrationViewModel : ObservableObject
{
    private bool _syncing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallStateText), nameof(InstallButtonText), nameof(CanInstall), nameof(CanUninstall), nameof(CanToggleShortcut), nameof(CanToggleTask))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallStateText), nameof(CanInstall), nameof(ShowInstallButton))]
    private bool _isRunningInstalledCopy;

    /// <summary>A copy, task or shortcut change is in flight; the controls wait for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall), nameof(CanUninstall), nameof(CanToggleShortcut), nameof(CanToggleTask))]
    private bool _isBusy;

    /// <summary>Set when the process runs as a different account than the signed-in user (over-the-shoulder UAC); blocks every change.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProfileWarning), nameof(CanInstall), nameof(CanUninstall), nameof(CanToggleShortcut), nameof(CanToggleTask))]
    private string? _profileWarning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleTask))]
    private bool _startWithWindows;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanToggleShortcut))]
    private bool _startMenuShortcut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string? _statusText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorText;

    public bool IsSupported => OperatingSystem.IsWindows();
    public bool HasStatus => StatusText is not null;
    public bool HasError => ErrorText is not null;
    public bool HasProfileWarning => ProfileWarning is not null;
    public string InstallDirectory => AppInstall.InstallDirectory;

    private bool CanChange => IsSupported && !IsBusy && ProfileWarning is null;

    public bool ShowInstallButton => !IsRunningInstalledCopy;
    public bool CanInstall => CanChange && !IsRunningInstalledCopy;
    public bool CanUninstall => CanChange && IsInstalled;

    // An orphaned shortcut or task (install folder removed by hand) must stay removable.
    public bool CanToggleShortcut => CanChange && (IsInstalled || StartMenuShortcut);
    public bool CanToggleTask => CanChange && (IsInstalled || StartWithWindows);

    public string InstallButtonText => IsInstalled ? "Reinstall" : "Install";

    public string InstallStateText => IsRunningInstalledCopy
        ? "Running the installed copy."
        : IsInstalled
            ? "Installed, but this window runs another copy. Reinstall replaces the installed one with this build."
            : "Not installed. Install copies the program to your user profile so the Start menu and the logon task have a fixed path.";

    /// <summary>Re-reads install state, shortcut and task from the system; called when the flyout opens.</summary>
    public async Task RefreshAsync()
    {
        if (!OperatingSystem.IsWindows() || IsBusy)
        {
            return; // a change in flight re-reads the state itself when it finishes
        }

        StatusText = null;
        ErrorText = null;
        try
        {
            Apply(await Task.Run(ReadState));
        }
        catch (Exception ex)
        {
            ErrorText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private Task InstallAsync()
    {
        var wantShortcut = !IsInstalled || StartMenuShortcut; // a reinstall keeps the user's choice
        return TryAsync(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            AppInstall.Install();
            if (wantShortcut)
            {
                Platform.StartMenuShortcut.Create(AppInstall.InstalledExePath);
            }

            if (StartupTask.Exists())
            {
                StartupTask.Enable(AppInstall.InstalledExePath); // point an existing task at the fresh copy
            }
        }, wantShortcut
            ? $"Installed to {AppInstall.InstallDirectory} and added to the Start menu."
            : $"Installed to {AppInstall.InstallDirectory}.");
    }

    [RelayCommand(CanExecute = nameof(CanUninstall))]
    private async Task UninstallAsync()
    {
        var deferred = false;
        await TryAsync(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            // Best effort: every step runs, the failures are reported together.
            var failures = new List<string>();
            Step(StartupTask.Disable, "logon task", failures);
            Step(Platform.StartMenuShortcut.Remove, "Start menu entry", failures);
            Step(() =>
            {
                if (OperatingSystem.IsWindows())
                {
                    deferred = AppInstall.Uninstall();
                }
            }, "program folder", failures);
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(string.Join(" ", failures));
            }
        }, null);
        if (ErrorText is null)
        {
            StatusText = deferred
                ? "Start menu entry and logon task removed. The program folder is deleted when you close this window."
                : "Uninstalled: program folder, Start menu entry and logon task removed.";
        }
    }

    private static void Step(Action action, string what, List<string> failures)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            failures.Add($"The {what} could not be removed: {ex.Message}");
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        _ = TryAsync(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            if (value)
            {
                StartupTask.Enable(AppInstall.InstalledExePath);
            }
            else
            {
                StartupTask.Disable();
            }
        }, value ? "Starts elevated when you sign in (Task Scheduler, no UAC prompt)." : "Logon task removed.");
    }

    partial void OnStartMenuShortcutChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        _ = TryAsync(() =>
        {
            if (!OperatingSystem.IsWindows())
            {
                return;
            }

            if (value)
            {
                Platform.StartMenuShortcut.Create(AppInstall.InstalledExePath);
            }
            else
            {
                Platform.StartMenuShortcut.Remove();
            }
        }, value ? "Added to the Start menu." : "Removed from the Start menu.");
    }

    partial void OnIsBusyChanged(bool value) => NotifyCommands();

    partial void OnIsInstalledChanged(bool value) => NotifyCommands();

    partial void OnIsRunningInstalledCopyChanged(bool value) => NotifyCommands();

    partial void OnProfileWarningChanged(string? value) => NotifyCommands();

    private void NotifyCommands()
    {
        InstallCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a worker thread so file copies and schtasks never stall
    /// the UI, then re-reads the system state and applies it on the UI thread.
    /// </summary>
    private async Task TryAsync(Action action, string? success)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        ErrorText = null;
        try
        {
            var state = await Task.Run(() =>
            {
                action();
                return ReadState();
            });
            StatusText = success;
            Apply(state);
        }
        catch (Exception ex)
        {
            StatusText = null;
            ErrorText = ex.Message;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Apply(await Task.Run(ReadState));
                }
                catch (Exception readEx)
                {
                    ErrorText = $"{ex.Message} (state could not be re-read: {readEx.Message})";
                }
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private readonly record struct State(bool Installed, bool RunningInstalled, bool Shortcut, bool Task, string? ProfileWarning);

    private static State ReadState()
    {
        if (!OperatingSystem.IsWindows())
        {
            return default;
        }

        return new State(AppInstall.IsInstalled, AppInstall.IsRunningInstalledCopy, Platform.StartMenuShortcut.Exists(), StartupTask.Exists(), InteractiveUser.ProfileMismatch());
    }

    private void Apply(State state)
    {
        _syncing = true;
        try
        {
            IsInstalled = state.Installed;
            IsRunningInstalledCopy = state.RunningInstalled;
            StartMenuShortcut = state.Shortcut;
            StartWithWindows = state.Task;
            ProfileWarning = state.ProfileWarning;
        }
        finally
        {
            _syncing = false;
        }
    }
}
