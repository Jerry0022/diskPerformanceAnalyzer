namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>Parses PhysicalDisk instance names such as "0 C:", "1 D: E:" or "2".</summary>
public static class PerfCounterInstanceParser
{
    /// <summary>
    /// Returns the disk number and drive letters of an instance name, or <c>null</c>
    /// for instances that are not a physical disk (e.g. "_Total").
    /// </summary>
    public static (int DiskNumber, IReadOnlyList<string> DriveLetters)? Parse(string? instanceName)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            return null;
        }

        var parts = instanceName.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out var diskNumber))
        {
            return null;
        }

        var letters = new List<string>(parts.Length - 1);
        for (var i = 1; i < parts.Length; i++)
        {
            letters.Add(parts[i]);
        }

        return (diskNumber, letters);
    }
}
