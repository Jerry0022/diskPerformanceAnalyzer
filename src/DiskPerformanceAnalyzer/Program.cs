using Avalonia;
using System;
using System.Diagnostics;
using System.Linq;

namespace DiskPerformanceAnalyzer;

sealed class Program
{
    /// <summary>Passed to the pkexec-relaunched copy so it never tries to elevate again.</summary>
    private const string NoElevateFlag = "--no-elevate";

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
        {
            return Probe.Run(args.Skip(1).ToArray());
        }

        // Windows elevates through the manifest (requireAdministrator). On Linux the trace
        // needs root, so ask PolicyKit once; if that is declined or unavailable the app still
        // runs and shows the disks, just without the process table.
        if (OperatingSystem.IsLinux() && !Environment.IsPrivilegedProcess && !args.Contains(NoElevateFlag))
        {
            var code = TryRelaunchWithPkexec(args);
            if (code is not null)
            {
                return code.Value;
            }
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args.Where(a => a != NoElevateFlag).ToArray());
    }

    /// <summary>
    /// Runs this executable again under pkexec with the display variables passed through, and
    /// returns its exit code. Returns null when the user cancelled the prompt, pkexec is missing
    /// or the elevated copy could not start — the caller then continues unprivileged.
    /// </summary>
    private static int? TryRelaunchWithPkexec(string[] args)
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            return null;
        }

        var start = new ProcessStartInfo("pkexec") { UseShellExecute = false };
        start.ArgumentList.Add("env");
        foreach (var name in new[] { "DISPLAY", "XAUTHORITY", "WAYLAND_DISPLAY", "XDG_RUNTIME_DIR", "XDG_SESSION_TYPE", "HOME", "LANG" })
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrEmpty(value))
            {
                start.ArgumentList.Add($"{name}={value}");
            }
        }

        start.ArgumentList.Add(exe);
        start.ArgumentList.Add(NoElevateFlag);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }

            process.WaitForExit();
            // 126 = user dismissed the authentication dialog, 127 = authorization failed / not
            // found: in both cases run without root instead of showing nothing.
            return process.ExitCode is 126 or 127 ? null : process.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Redirection-surface rendering instead of the WinUI compositor: the compositor
            // thread otherwise burns ~5 % of a core keeping frames flowing for a 1 Hz dashboard.
            .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] })
            .WithInterFont()
            .LogToTrace();
}
