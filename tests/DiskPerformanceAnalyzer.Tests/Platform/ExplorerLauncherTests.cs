using DiskPerformanceAnalyzer.Platform;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Platform;

public class ExplorerLauncherTests
{
    [Fact]
    public void BuildSelectArguments_QuotesPathWithSpaces()
    {
        var result = ExplorerLauncher.BuildSelectArguments(@"C:\Program Files\App\file.txt");

        Assert.Equal("/select,\"C:\\Program Files\\App\\file.txt\"", result);
    }

    [Fact]
    public void BuildSelectArguments_TrimsTrailingBackslash()
    {
        var result = ExplorerLauncher.BuildSelectArguments(@"C:\Data\Folder\");

        Assert.Equal("/select,\"C:\\Data\\Folder\"", result);
    }
}
