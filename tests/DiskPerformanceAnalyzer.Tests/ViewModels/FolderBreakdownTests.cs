using DiskPerformanceAnalyzer.Monitoring;
using DiskPerformanceAnalyzer.ViewModels;
using Xunit;

namespace DiskPerformanceAnalyzer.Tests.ViewModels;

public class FolderBreakdownTests
{
    private static ProcessIo System(params FileIo[] files) =>
        new(4, "System", files.Sum(f => f.Bytes), 0, [], files.Sum(f => f.Ops), 0) { Files = files };

    [Fact]
    public void GroupsFilesByFolder_SortedByRequests()
    {
        var io = System(
            new FileIo(@"C:\Users\Me\AppData\Local\Cache\a", 100, 10),
            new FileIo(@"C:\Users\Me\AppData\Local\Cache\b", 100, 30),
            new FileIo(@"C:\Windows\System32\x.dll", 5_000, 5));

        var rows = FolderBreakdown.Build(io);

        Assert.Equal([@"C:\Users\Me\AppData\Local\Cache", @"C:\Windows\System32"], rows.Select(r => r.Path).ToArray());
        Assert.Equal(40, rows[0].Ops);
        Assert.Equal(2, rows[0].FileCount);
        Assert.Equal(5_000, rows[1].Bytes);
    }

    [Fact]
    public void RollsSmallestFoldersUpIntoParents_UntilItFits()
    {
        var io = System(
            new FileIo(@"C:\a\big\1", 0, 100),
            new FileIo(@"C:\a\small1\1", 0, 1),
            new FileIo(@"C:\a\small2\1", 0, 2),
            new FileIo(@"C:\a\small3\1", 0, 3),
            new FileIo(@"C:\b\1", 0, 50));

        var rows = FolderBreakdown.Build(io, maxRows: 3);

        // The three small siblings merge into C:\a; the big folder keeps its full depth.
        Assert.Equal([@"C:\a\big", @"C:\b", @"C:\a"], rows.Select(r => r.Path).ToArray());
        Assert.Equal(6, rows[2].Ops);
        Assert.Equal(3, rows[2].FileCount);
    }

    [Fact]
    public void UnnamedAndUnattributedIoBecomeOneLine()
    {
        var io = new ProcessIo(4, "System", 10_000, 0, [], 500, 0)
        {
            Files = [new FileIo(@"C:\x\y", 1_000, 100), new FileIo(string.Empty, 4_000, 300)],
        };

        var rows = FolderBreakdown.Build(io);

        var unnamed = Assert.Single(rows, r => r.IsUnnamed);
        Assert.Equal(400, unnamed.Ops);      // 300 unnamed + 100 never attributed
        Assert.Equal(9_000, unnamed.Bytes);
        Assert.Equal(unnamed, rows[0]);       // sorted first: it has the most requests
    }

    [Fact]
    public void RootFilesMapToDriveRoot()
    {
        Assert.Equal(@"C:\", FolderBreakdown.Folder(@"C:\pagefile.sys"));
        Assert.Equal(@"C:\Windows", FolderBreakdown.Folder(@"C:\Windows\x.dll"));
        Assert.Equal(@"C:\", FolderBreakdown.Parent(@"C:\Windows"));
        Assert.Null(FolderBreakdown.Parent(@"C:\"));
        Assert.Null(FolderBreakdown.Parent(@"\"));
    }

    [Fact]
    public void EmptyFilesGiveNoRows()
    {
        Assert.Empty(FolderBreakdown.Build(new ProcessIo(1, "x", 0, 0, [])));
    }
}
