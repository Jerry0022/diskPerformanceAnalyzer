using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.ViewModels;

namespace DiskPerformanceAnalyzer;

/// <summary>
/// <c>--probe [seconds]</c>: runs the data layer without a window and prints one line per disk
/// and the top processes each second. Lets the monitor be checked over SSH, in containers and
/// in CI where no display exists, and is the quickest way to see what a machine can attribute.
/// </summary>
internal static class Probe
{
    public static int Run(string[] args)
    {
        var seconds = args.Length > 0 && int.TryParse(args[0], NumberStyles.None, CultureInfo.InvariantCulture, out var s) ? s : 5;
        Console.WriteLine($"Disk Performance Analyzer probe: {Environment.OSVersion}, privileged={Environment.IsPrivilegedProcess}");

        using var monitor = DiskMonitor.Create();
        using var done = new ManualResetEventSlim();
        var count = 0;
        monitor.SnapshotReady += snapshot =>
        {
            Console.WriteLine($"--- {snapshot.Timestamp:HH:mm:ss}");
            foreach (var disk in snapshot.Disks)
            {
                var mounts = disk.DriveLetters.Count > 0 ? " " + string.Join(" ", disk.DriveLetters) : string.Empty;
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  [{disk.DiskNumber}] {disk.Name,-28} {disk.ActivePercent,5:0}% active  {Formatting.Iops(disk.OpsPerSec),12}  ↓{Formatting.Rate(disk.ReadBytesPerSec),10} ↑{Formatting.Rate(disk.WriteBytesPerSec),10}  queue {disk.QueueLength:0.#}{mounts}"));
                if (snapshot.ProcessIoByDisk.TryGetValue(disk.DiskNumber, out var processes))
                {
                    foreach (var p in processes.OrderByDescending(p => p.TotalOps).Take(5))
                    {
                        var file = p.TopFiles.Count > 0 ? "  " + Formatting.ShortPath(p.TopFiles[0]) : string.Empty;
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"        {p.ProcessName,-20} pid {p.Pid,-7} {Formatting.Count(p.TotalOps),8} req  {Formatting.Bytes(p.TotalBytes),10}{file}"));
                    }
                }
            }

            if (Interlocked.Increment(ref count) >= seconds)
            {
                done.Set();
            }
        };

        monitor.Start();
        if (monitor.Notice is not null)
        {
            Console.WriteLine($"notice: {monitor.Notice}");
        }

        done.Wait(TimeSpan.FromSeconds(seconds + 5));
        return count > 0 ? 0 : 1;
    }
}
