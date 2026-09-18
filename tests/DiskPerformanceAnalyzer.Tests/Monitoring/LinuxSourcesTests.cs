using DiskPerformanceAnalyzer.Monitoring.Linux;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Monitoring;

/// <summary>
/// The Linux data layer is plain file parsing, so it is exercised on every platform against a
/// fake sysfs/procfs tree; only the tracefs reader itself needs a real kernel.
/// </summary>
public class LinuxSourcesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dpa-linux-" + Guid.NewGuid().ToString("N"));

    public LinuxSourcesTests()
    {
        // sda (8:0) with sda1 (8:1) and sda2 (8:2); nvme0n1 (259:0) with p1 (259:1);
        // dm-0 (253:0) = LUKS on sda2; loop0 must be ignored.
        Device("sda", "8:0", ("sda1", "8:1"), ("sda2", "8:2"));
        Device("nvme0n1", "259:0", ("nvme0n1p1", "259:1"));
        Device("loop0", "7:0");
        Device("dm-0", "253:0");
        Directory.CreateDirectory(Path.Combine(_root, "sys", "block", "dm-0", "slaves", "sda2"));
        File.WriteAllText(Path.Combine(_root, "mountinfo"), string.Join('\n',
            "22 1 259:1 / / rw,relatime - ext4 /dev/nvme0n1p1 rw",
            "40 22 253:0 / /home rw,relatime - ext4 /dev/mapper/home rw",
            "41 22 8:1 / /mnt/backup\\040disk rw - ext4 /dev/sda1 rw",
            "50 22 0:30 / /proc rw - proc proc rw",
            "broken line"));
    }

    private void Device(string name, string dev, params (string Name, string Dev)[] partitions)
    {
        var dir = Path.Combine(_root, "sys", "block", name);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "dev"), dev + "\n");
        foreach (var (pName, pDev) in partitions)
        {
            var pDir = Path.Combine(dir, pName);
            Directory.CreateDirectory(pDir);
            File.WriteAllText(Path.Combine(pDir, "dev"), pDev + "\n");
            File.WriteAllText(Path.Combine(pDir, "partition"), "1\n");
        }
    }

    private LinuxBlockDevices Devices() => new(Path.Combine(_root, "sys", "block"), Path.Combine(_root, "mountinfo"));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void EnumeratesWholeDisks_SkipsLoopAndStackedDevices()
    {
        var devices = Devices();

        Assert.Equal(["nvme0n1", "sda"], devices.Disks.Select(d => d.Name).ToArray());
        Assert.Equal([0, 1], devices.Disks.Select(d => d.Index).ToArray());
    }

    [Fact]
    public void MapsPartitionsAndDeviceMapperToTheirDisk()
    {
        var devices = Devices();
        var sda = devices.Disks.Single(d => d.Name == "sda").Index;

        Assert.True(devices.TryGetDisk(8, 2, out var fromPartition));
        Assert.Equal(sda, fromPartition);
        Assert.True(devices.TryGetDisk(253, 0, out var fromDm));
        Assert.Equal(sda, fromDm);
        Assert.False(devices.TryGetDisk(7, 0, out _));
    }

    [Fact]
    public void MountPointsResolveByLongestPrefix_WithOctalEscapes()
    {
        var devices = Devices();
        var nvme = devices.Disks.Single(d => d.Name == "nvme0n1");
        var sda = devices.Disks.Single(d => d.Name == "sda");

        Assert.Equal(["/"], nvme.MountPoints);
        Assert.Equal(["/home", "/mnt/backup disk"], sda.MountPoints);
        Assert.True(devices.TryGetDiskForPath("/home/me/file", out var d1));
        Assert.Equal(sda.Index, d1);
        Assert.True(devices.TryGetDiskForPath("/homework/x", out var d2)); // "/homework" is not under "/home"
        Assert.Equal(nvme.Index, d2);
        Assert.True(devices.TryGetDiskForPath("/mnt/backup disk/a", out var d3));
        Assert.Equal(sda.Index, d3);
    }

    [Fact]
    public void ParsesDeviceNumbersInBothNotations()
    {
        Assert.True(LinuxBlockDevices.TryParseDev("8:16", out var major, out var minor));
        Assert.Equal((8, 16), (major, minor));
        Assert.True(LinuxBlockDevices.TryParseDev("259,1", out major, out minor));
        Assert.Equal((259, 1), (major, minor));
        Assert.False(LinuxBlockDevices.TryParseDev("sda", out _, out _));
    }

    [Fact]
    public void DiskStatsParse_ReadsTheIostatFields()
    {
        const string text = " 259       0 nvme0n1 100 1 800 20 200 2 1600 40 3 500 60 0 0 0 0 0 0\n" +
                            "   7       0 loop0 1 0 8 0\n"; // too short: skipped

        var lines = ProcDiskStatsSource.Parse(text).ToList();

        var nvme = Assert.Single(lines);
        Assert.Equal(new DiskStatsLine(259, 0, "nvme0n1", 100, 800, 200, 1600, 3, 500), nvme);
    }

    [Fact]
    public void DiskStatsSample_ReportsDeltasPerSecond()
    {
        var stats = Path.Combine(_root, "diskstats");
        File.WriteAllText(stats, "259 0 nvme0n1 100 0 800 0 200 0 1600 0 3 500 0 0 0 0 0 0 0\n8 0 sda 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
        using var source = new ProcDiskStatsSource(Devices(), stats);

        var first = source.Sample();
        Assert.Equal(2, first.Count);
        Assert.Equal("nvme0n1", first[0].Name);
        Assert.Equal("nvme0n1", first[0].Title);
        Assert.Equal(["/"], first[0].DriveLetters);
        Assert.Equal(0, first[0].ReadsPerSec); // no previous sample yet
        Assert.Equal(3, first[0].QueueLength);

        File.WriteAllText(stats, "259 0 nvme0n1 150 0 1824 0 210 0 1620 0 1 900 0 0 0 0 0 0 0\n8 0 sda 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 0\n");
        var second = source.Sample()[0];

        // Elapsed time between the two samples is a few ms, so compare ratios, not per-second values.
        Assert.True(second.ReadsPerSec > 0);
        Assert.Equal(1024 * 512.0 / 50, second.ReadBytesPerSec / second.ReadsPerSec, 3);
        Assert.Equal(20 * 512.0 / 10, second.WriteBytesPerSec / second.WritesPerSec, 3);
        Assert.Equal(100, second.ActivePercent); // 400 ms busy in a few ms window clamps to 100
        Assert.Equal(1, second.QueueLength);
        Assert.Equal(0, source.Sample()[1].ReadsPerSec);
    }

    [Theory]
    [InlineData("    kworker/u8:3-123     [002] d..1.   512.345678: block_rq_issue: 8,2 WS 4096 () 2048 + 8 [kworker/u8:3]", 8, 2, 123, true, 4096, "kworker/u8:3")]
    [InlineData("          chrome-4567    [000] ....    12.000001: block_rq_issue: 259,1 R 131072 () 100 + 256 [chrome]", 259, 1, 4567, false, 131072, "chrome")]
    [InlineData(" my-app-name-99 [001] ..... 1.0: block_rq_issue: 253,0 FWFS 0 () 0 + 0 [my-app-name]", 253, 0, 99, true, 0, "my-app-name")]
    [InlineData("jbd2/sda1-8-321 [003] d.... 2.0: block_rq_issue: 8,1 WM 0 () 64 + 16 [jbd2/sda1-8]", 8, 1, 321, true, 8192, "jbd2/sda1-8")]
    public void TracefsLine_Parses(string line, int major, int minor, int pid, bool isWrite, long bytes, string comm)
    {
        Assert.True(TracefsProcessIoSource.TryParseLine(line, out var maj, out var min, out var p, out var write, out var b, out var c));
        Assert.Equal((major, minor, pid, isWrite, bytes, comm), (maj, min, p, write, b, c));
    }

    [Fact]
    public void OpenFiles_ListsRegularFilesFromProcFd()
    {
        if (!OperatingSystem.IsLinux())
        {
            return; // /proc only
        }

        var probe = Path.Combine(_root, "held-open.bin");
        using var held = File.Create(probe);

        var files = TracefsProcessIoSource.OpenFiles(Environment.ProcessId, _ => true, max: int.MaxValue);

        Assert.Contains(files, f => f.Path == probe);
        Assert.All(files, f => Assert.StartsWith("/", f.Path));
        Assert.DoesNotContain(files, f => f.Path.StartsWith("/dev/", StringComparison.Ordinal));
        Assert.Empty(TracefsProcessIoSource.OpenFiles(Environment.ProcessId, _ => false));
        Assert.Empty(TracefsProcessIoSource.OpenFiles(int.MaxValue, _ => true));
    }

    [Theory]
    [InlineData("  fstrim-10 [000] ..... 3.0: block_rq_issue: 8,0 D 0 () 0 + 2048 [fstrim]")]      // discard
    [InlineData("  x-1 [000] ..... 3.0: block_rq_complete: 8,0 R () 0 + 8 [0]")]                    // other event
    [InlineData("  tracing_mark_write: DiskPerformanceAnalyzer stop")]
    public void TracefsLine_RejectsNonRequests(string line)
    {
        Assert.False(TracefsProcessIoSource.TryParseLine(line, out _, out _, out _, out _, out _, out _));
    }
}
