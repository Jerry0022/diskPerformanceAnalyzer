// Temporary stubs: real implementations live on the windows branch. The orchestrator deletes this file at merge.
namespace DiskPerformanceAnalyzer.Platform
{
    public static class ExplorerLauncher
    {
        public static void Reveal(string path) { }
        public static void OpenFolder(string path) { }
    }

    public static class ProcessImagePath
    {
        public static bool TryGet(int pid, out string? path) { path = null; return false; }
    }
}
