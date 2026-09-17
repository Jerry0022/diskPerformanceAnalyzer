namespace DiskPerformanceAnalyzer.Monitoring;

/// <summary>
/// Computes disk "active time" the way Task Manager defines it: the share of a window in
/// which at least one I/O request was outstanding. Built from ETW DiskIO completion events
/// (completion timestamp + elapsed time), which stay correct on NVMe drives where the
/// PhysicalDisk "% Idle Time" counter is known to report 0.
/// </summary>
public static class BusyTime
{
    /// <summary>
    /// Returns the total length of the union of <paramref name="intervals"/> clamped to
    /// [<paramref name="windowStart"/>, <paramref name="windowEnd"/>]. Intervals are (start, end).
    /// </summary>
    public static double UnionLength(List<(double Start, double End)> intervals, double windowStart, double windowEnd)
    {
        if (intervals.Count == 0 || windowEnd <= windowStart)
        {
            return 0;
        }

        intervals.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        double total = 0;
        double curStart = double.NaN;
        double curEnd = double.NaN;
        foreach (var (rawStart, rawEnd) in intervals)
        {
            var start = Math.Max(rawStart, windowStart);
            var end = Math.Min(rawEnd, windowEnd);
            if (end <= start)
            {
                continue;
            }

            if (double.IsNaN(curStart))
            {
                (curStart, curEnd) = (start, end);
            }
            else if (start <= curEnd)
            {
                curEnd = Math.Max(curEnd, end);
            }
            else
            {
                total += curEnd - curStart;
                (curStart, curEnd) = (start, end);
            }
        }

        if (!double.IsNaN(curStart))
        {
            total += curEnd - curStart;
        }

        return total;
    }

    /// <summary>Percentage (0–100) of the window covered by the union of the intervals.</summary>
    public static double ActivePercent(List<(double Start, double End)> intervals, double windowStart, double windowEnd)
    {
        var span = windowEnd - windowStart;
        if (span <= 0)
        {
            return 0;
        }

        return Math.Clamp(UnionLength(intervals, windowStart, windowEnd) / span * 100.0, 0.0, 100.0);
    }
}
