using DiskPerformanceAnalyzer.Platform;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.Platform;

public class StartupTaskTests
{
    [Fact]
    public void BuildTaskXml_RunsElevatedAtLogonWithoutTimeLimit()
    {
        var xml = StartupTask.BuildTaskXml(@"C:\Users\Me\AppData\Local\Programs\Disk Performance Analyzer\DiskPerformanceAnalyzer.exe", @"PC\Me");

        Assert.Contains("<LogonTrigger>", xml);
        Assert.Contains(@"<UserId>PC\Me</UserId>", xml);
        Assert.Contains("<RunLevel>HighestAvailable</RunLevel>", xml);
        Assert.Contains("<LogonType>InteractiveToken</LogonType>", xml);
        Assert.Contains("<ExecutionTimeLimit>PT0S</ExecutionTimeLimit>", xml);
        Assert.Contains("<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>", xml);
        Assert.Contains(@"<Command>""C:\Users\Me\AppData\Local\Programs\Disk Performance Analyzer\DiskPerformanceAnalyzer.exe""</Command>", xml);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"UTF-16\"?>", xml);
    }

    [Fact]
    public void BuildTaskXml_EscapesXmlCharacters()
    {
        var xml = StartupTask.BuildTaskXml(@"C:\Tools & Co\app.exe", "DOM\\a<b");

        Assert.Contains("C:\\Tools &amp; Co\\app.exe", xml);
        Assert.Contains("DOM\\a&lt;b", xml);
        Assert.DoesNotContain("& Co", xml);
    }
}
