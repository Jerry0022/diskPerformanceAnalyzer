using System;
using System.Diagnostics;
using System.IO;

namespace DiskPerformanceAnalyzer.Platform;

/// <summary>
/// Launches Windows Explorer to reveal or open files/directories. Never throws.
/// </summary>
public static class ExplorerLauncher
{
    /// <summary>
    /// Reveals <paramref name="path"/> in Explorer with the item selected. Falls back to
    /// opening the parent directory if the path itself does not exist, and is a no-op if
    /// neither the path nor its parent exists.
    /// </summary>
    public static void Reveal(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                Start("explorer.exe", BuildSelectArguments(path));
                return;
            }

            var parent = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
            {
                Start("explorer.exe", BuildOpenArguments(parent));
            }
        }
        catch
        {
            // Never throw from a UI-adjacent convenience helper.
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> (treated as a directory) in Explorer. No-op if it fails.
    /// </summary>
    public static void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            Start("explorer.exe", BuildOpenArguments(path));
        }
        catch
        {
            // Never throw from a UI-adjacent convenience helper.
        }
    }

    /// <summary>
    /// Builds the "/select,&lt;path&gt;" argument string for explorer.exe, quoting the path
    /// and normalizing a trailing directory separator (Explorer mishandles "C:\foo\").
    /// </summary>
    internal static string BuildSelectArguments(string path)
    {
        var normalized = path.TrimEnd('\\', '/');
        if (normalized.Length == 0)
        {
            normalized = path;
        }

        return $"/select,\"{normalized}\"";
    }

    /// <summary>
    /// Builds a quoted argument string for explorer.exe pointing at a directory.
    /// </summary>
    internal static string BuildOpenArguments(string path)
    {
        return $"\"{path}\"";
    }

    private static void Start(string fileName, string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
        });
    }
}
