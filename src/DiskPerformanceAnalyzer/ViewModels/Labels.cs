namespace DiskPerformanceAnalyzer.ViewModels;

/// <summary>
/// The one vocabulary every label, legend and tooltip uses. Two metric families — requests
/// (blue) and data (green) — each split into read (lighter, ↓ like a download) and write
/// (darker, ↑ like an upload). Units are always spelled the same way and sit in parentheses.
/// </summary>
public static class Labels
{
    public const string ReadGlyph = "↓";  // ↓
    public const string WriteGlyph = "↑"; // ↑

    public const string Read = ReadGlyph + " Read";
    public const string Write = WriteGlyph + " Write";

    public const string Requests = "Requests";
    public const string Data = "Data";

    public const string RequestsUnit = "req/s";
    public const string DataUnit = "MB/s";

    public const string RequestsAxis = Requests + " (" + RequestsUnit + ")";
    public const string DataAxis = Data + " (" + DataUnit + ")";

    public const string ReadRequests = Read + " (" + RequestsUnit + ")";
    public const string WriteRequests = Write + " (" + RequestsUnit + ")";
    public const string ReadData = Read + " (" + DataUnit + ")";
    public const string WriteData = Write + " (" + DataUnit + ")";
}
