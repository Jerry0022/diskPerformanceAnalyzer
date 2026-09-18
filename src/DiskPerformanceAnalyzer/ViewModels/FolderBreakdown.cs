using DiskPerformanceAnalyzer.Monitoring;

namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>One line of a process's folder breakdown.</summary>
/// <param name="Path">Folder path, or empty for the unnamed/unattributed bucket.</param>
/// <param name="FileCount">Distinct files under this line.</param>
public sealed record FolderShare(string Path, long Bytes, long Ops, int FileCount)
{
    public bool IsUnnamed => Path.Length == 0;
}

/// <summary>
/// Rolls a process's per-file I/O up to folders so that "System is busy" becomes "System is busy
/// in C:\Users\Me\AppData\Local\Cache". The folder tree is refined top-down: starting at the
/// drive roots, the line with the most requests is split into its busiest sub-folder plus the
/// remainder, as long as the result still fits in <c>maxRows</c> lines. Busy folders therefore
/// end up as deep and specific as the data allows while the long tail stays rolled up — the most
/// differentiated folder level that fits.
/// </summary>
public static class FolderBreakdown
{
    public const int DefaultMaxRows = 7;

    private const string Root = "\\";
    private static readonly char[] Separators = ['\\', '/'];

    public static IReadOnlyList<FolderShare> Build(ProcessIo io, int maxRows = DefaultMaxRows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxRows, 1);

        var nodes = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<Node>();
        long unnamedBytes = 0, unnamedOps = 0;
        foreach (var file in io.Files)
        {
            if (file.IsUnnamed)
            {
                unnamedBytes += file.Bytes;
                unnamedOps += file.Ops;
                continue;
            }

            var node = GetOrAdd(nodes, roots, Folder(file.Path));
            node.DirectFiles++;
            for (var n = node; n is not null; n = n.Parent)
            {
                n.Bytes += file.Bytes;
                n.Ops += file.Ops;
                n.Files++;
            }
        }

        // Whatever the source could not attribute to a file counts as unnamed as well.
        var attributedBytes = roots.Sum(r => r.Bytes) + unnamedBytes;
        var attributedOps = roots.Sum(r => r.Ops) + unnamedOps;
        unnamedBytes += Math.Max(0, io.TotalBytes - attributedBytes);
        unnamedOps += Math.Max(0, io.TotalOps - attributedOps);
        var hasUnnamed = unnamedBytes > 0 || unnamedOps > 0;
        var budget = hasUnnamed ? maxRows - 1 : maxRows;

        var rows = roots.Select(r => new Row(r)).ToList();
        while (true)
        {
            // Refine the busiest line that still has sub-folders to pull out. Pulling a child out
            // adds a line unless the parent line becomes empty; only the latter is allowed once
            // the budget is used up.
            var candidate = rows
                .Where(r => r.HasMoreChildren && (rows.Count < budget || r.VanishesAfterExtract))
                .OrderByDescending(r => r.RemainingOps)
                .ThenByDescending(r => r.RemainingBytes)
                .FirstOrDefault();
            if (candidate is null)
            {
                break;
            }

            var child = candidate.ExtractNextChild();
            rows.Add(new Row(child));
            if (candidate.IsEmpty)
            {
                rows.Remove(candidate);
            }
        }

        var result = rows
            .OrderByDescending(r => r.RemainingOps)
            .ThenByDescending(r => r.RemainingBytes)
            .Take(budget)
            .Select(r => new FolderShare(r.Node.Path, r.RemainingBytes, r.RemainingOps, r.RemainingFiles))
            .ToList();
        if (hasUnnamed)
        {
            result.Add(new FolderShare(string.Empty, unnamedBytes, unnamedOps, 0));
            result = result.OrderByDescending(r => r.Ops).ThenByDescending(r => r.Bytes).ToList();
        }

        return result;
    }

    private static Node GetOrAdd(Dictionary<string, Node> nodes, List<Node> roots, string path)
    {
        if (nodes.TryGetValue(path, out var node))
        {
            return node;
        }

        var parentPath = Parent(path);
        var parent = parentPath is null ? null : GetOrAdd(nodes, roots, parentPath);
        node = new Node(path, parent);
        nodes[path] = node;
        if (parent is null)
        {
            roots.Add(node);
        }
        else
        {
            parent.Children.Add(node);
        }

        return node;
    }

    /// <summary>Folder containing <paramref name="path"/>: <c>C:\a\b.txt</c> gives <c>C:\a</c>, <c>C:\b.txt</c> gives <c>C:\</c>.</summary>
    internal static string Folder(string path)
    {
        var cut = path.LastIndexOfAny(Separators);
        if (cut < 0)
        {
            return Root;
        }

        var folder = path[..cut];
        return folder.Length == 0 ? Root : IsDrive(folder) ? folder + '\\' : folder;
    }

    /// <summary>Parent folder, or null at a drive root or the bare root.</summary>
    internal static string? Parent(string folder)
    {
        if (folder == Root || IsDriveRoot(folder))
        {
            return null;
        }

        var cut = folder.LastIndexOfAny(Separators);
        if (cut < 0)
        {
            return null;
        }

        var parent = folder[..cut];
        return parent.Length == 0 ? Root : IsDrive(parent) ? parent + '\\' : parent;
    }

    private static bool IsDrive(string s) => s.Length == 2 && s[1] == ':';
    private static bool IsDriveRoot(string s) => s.Length == 3 && s[1] == ':' && (s[2] == '\\' || s[2] == '/');

    private sealed class Node(string path, Node? parent)
    {
        public string Path { get; } = path;
        public Node? Parent { get; } = parent;
        public List<Node> Children { get; } = new();
        public long Bytes;
        public long Ops;
        public int Files;
        public int DirectFiles;
    }

    /// <summary>A folder line: the node minus the children already pulled out into their own lines.</summary>
    private sealed class Row
    {
        private readonly List<Node> _children;
        private int _next;

        public Row(Node node)
        {
            Node = node;
            _children = node.Children.OrderByDescending(c => c.Ops).ThenByDescending(c => c.Bytes).ToList();
            RemainingBytes = node.Bytes;
            RemainingOps = node.Ops;
            RemainingFiles = node.Files;
        }

        public Node Node { get; }
        public long RemainingBytes { get; private set; }
        public long RemainingOps { get; private set; }
        public int RemainingFiles { get; private set; }

        public bool HasMoreChildren => _next < _children.Count;
        public bool IsEmpty => RemainingFiles == 0 && RemainingOps == 0 && RemainingBytes == 0;

        public bool VanishesAfterExtract =>
            HasMoreChildren
            && RemainingFiles - _children[_next].Files == 0
            && RemainingOps - _children[_next].Ops == 0
            && RemainingBytes - _children[_next].Bytes == 0;

        public Node ExtractNextChild()
        {
            var child = _children[_next++];
            RemainingBytes -= child.Bytes;
            RemainingOps -= child.Ops;
            RemainingFiles -= child.Files;
            return child;
        }
    }
}
