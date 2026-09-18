using System;
using DiskPerformanceAnalyzer.Platform;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Platform;

public class ProcessImagePathTests
{
    [Fact]
    public void TryGet_CurrentProcess_ReturnsExecutableOrLibraryPath()
    {
        var found = ProcessImagePath.TryGet(Environment.ProcessId, out var path);

        Assert.True(found);
        Assert.NotNull(path);
        if (OperatingSystem.IsWindows())
        {
            Assert.True(
                path!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            Assert.True(System.IO.Path.IsPathRooted(path));
            Assert.True(System.IO.File.Exists(path));
        }
    }

    [Fact]
    public void TryGet_PidZero_ReturnsFalse()
    {
        var found = ProcessImagePath.TryGet(0, out var path);

        Assert.False(found);
        Assert.Null(path);
    }
}
