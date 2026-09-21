using DiskPerformanceAnalyzer.Platform;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Platform;

public class AppInstallTests
{
    [Fact]
    public void IsSingleFileLayout_TrueWhenNoManagedAssemblyNextToExe()
    {
        var dir = Directory.CreateTempSubdirectory("dpa-layout-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "DiskPerformanceAnalyzer.exe"), string.Empty);
            Assert.True(AppInstall.IsSingleFileLayout(dir.FullName));

            File.WriteAllText(Path.Combine(dir.FullName, "DiskPerformanceAnalyzer.dll"), string.Empty);
            Assert.False(AppInstall.IsSingleFileLayout(dir.FullName));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void PathsEqual_NormalizesAndRejectsMissing()
    {
        var root = Path.GetTempPath();
        var a = Path.Combine(root, "x", "app.exe");
        var b = Path.Combine(root, "x", ".", "app.exe");

        Assert.True(AppInstall.PathsEqual(a, b));
        Assert.False(AppInstall.PathsEqual(a, Path.Combine(root, "y", "app.exe")));
        Assert.False(AppInstall.PathsEqual(null, a));
        Assert.False(AppInstall.PathsEqual(a, string.Empty));
    }

    [Theory]
    [InlineData("DiskPerformanceAnalyzer-0.3.0-win-x64.exe", true)] // renamed release download
    [InlineData("DiskPerformanceAnalyzer.exe", true)]                // build output
    [InlineData("dotnet.exe", false)]                                // host when started as "dotnet X.dll"
    public void IsAppExecutable_AcceptsOnlyTheAppsOwnHost(string fileName, bool expected)
    {
        // Path.Combine keeps the test valid on Linux CI, where '\' is not a separator.
        var path = Path.Combine(Path.GetTempPath(), "some folder", fileName);

        Assert.Equal(expected, AppInstall.IsAppExecutable(path));
    }

    [Fact]
    public void IsInside_DetectsNestedButNotSiblingOrEqualPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "dpa-root");

        Assert.True(AppInstall.IsInside(Path.Combine(root, "sub", "x"), root));
        Assert.False(AppInstall.IsInside(root, root));
        Assert.False(AppInstall.IsInside(Path.Combine(Path.GetTempPath(), "dpa-root2"), root));
    }

    [Fact]
    public void InstallDirectory_IsPerUserProgramsFolder()
    {
        Assert.EndsWith(Path.Combine("Programs", AppInstall.ProductName), AppInstall.InstallDirectory);
        Assert.Equal(Path.Combine(AppInstall.InstallDirectory, AppInstall.ExeName), AppInstall.InstalledExePath);
    }
}
