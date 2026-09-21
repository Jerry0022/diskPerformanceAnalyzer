using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// "Start with Windows" for an app that requires administrator: a Run-key entry would be
/// blocked by UAC at logon, so this registers a Task Scheduler logon task with
/// RunLevel=HighestAvailable instead. The XML route (rather than schtasks switches) is what lets
/// the task have no execution time limit — the default of 72 h would kill a monitor that stays open.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "Disk Performance Analyzer";

    [SupportedOSPlatform("windows")]
    public static bool Exists() => RunSchtasks(["/Query", "/TN", TaskName]).ExitCode == 0;

    [SupportedOSPlatform("windows")]
    public static void Enable(string exePath)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var xml = BuildTaskXml(exePath, identity.Name);
        var file = Path.Combine(Path.GetTempPath(), $"dpa-logon-task-{Guid.NewGuid():N}.xml");
        File.WriteAllText(file, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        try
        {
            var result = RunSchtasks(["/Create", "/TN", TaskName, "/XML", file, "/F"]);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Task Scheduler refused the logon task: {result.Message}");
            }
        }
        finally
        {
            File.Delete(file);
        }
    }

    [SupportedOSPlatform("windows")]
    public static void Disable()
    {
        var result = RunSchtasks(["/Delete", "/TN", TaskName, "/F"]);
        if (result.ExitCode != 0 && Exists())
        {
            throw new InvalidOperationException($"The logon task could not be removed: {result.Message}");
        }
    }

    /// <summary>Task Scheduler definition: run <paramref name="exePath"/> elevated when <paramref name="userId"/> logs on.</summary>
    internal static string BuildTaskXml(string exePath, string userId)
    {
        var exe = SecurityElement.Escape(exePath);
        var dir = SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? string.Empty);
        var user = SecurityElement.Escape(userId);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts {AppInstall.ProductName} when you sign in.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{exe}"</Command>
                  <WorkingDirectory>{dir}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    [SupportedOSPlatform("windows")]
    private static (int ExitCode, string Message) RunSchtasks(string[] arguments)
    {
        var start = new ProcessStartInfo("schtasks.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in arguments)
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return (-1, "schtasks.exe could not be started.");
            }

            // Drain both pipes concurrently so neither can fill up and block the other.
            var stderrTask = process.StandardError.ReadToEndAsync();
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = stderrTask.GetAwaiter().GetResult();
            process.WaitForExit();
            var message = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return (process.ExitCode, message.Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, ex.Message);
        }
    }
}
