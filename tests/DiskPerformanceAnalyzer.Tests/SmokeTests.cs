using Xunit;

namespace DiskPerformanceAnalyzer.Tests;

public class SmokeTests
{
    [Fact]
    public void ProjectReferenceResolves()
    {
        Assert.NotNull(typeof(App).Assembly);
    }
}
