# Disk Performance Analyzer

Task-Manager-style disk *activity* monitor for Windows: see how busy each physical disk is —
**requests per second** and throughput — and which **processes and files** are causing it.
Not a capacity tool; it never counts gigabytes.

![Disk Performance Analyzer](docs/screenshot.png)

## Download

Grab the latest `DiskPerformanceAnalyzer-<version>-win-x64.exe` from the
[**Releases**](../../releases/latest) page and run it. No installer, no .NET runtime needed
(the exe is self-contained). Windows 10/11 x64.

The app asks for administrator rights on start: it reads disk I/O from an ETW kernel trace,
the same source Resource Monitor uses, and that requires elevation. There is no non-admin mode.

Windows SmartScreen may warn because the binary is not code-signed — "More info → Run anyway".
Every release ships a `SHA256SUMS.txt` so you can verify the download.

## What it shows

- **Per disk**: current IOPS, read/write request split, throughput, 60 s sparkline.
- **Chart (60 s)**: read/write MB/s as filled areas, read/write requests/s as lines.
  Click to freeze a second; pause icon top-right.
- **Process table**: who issued the requests in the selected window (5 s / 1 min / 5 min / since
  start), share bar with details on hover, top file per process. Folder icon opens the
  executable or file in Explorer, share icon copies the path. Hidden columns (PID, read, write,
  requests) toggle via the column icon.
- **One filter** for chart and table: type a process or path fragment, or select rows to isolate
  them in the chart.

The tool is minimally invasive: the ETW session is real-time only (no trace file), nothing is
logged to disk, and the status bar shows the app's own I/O so you can confirm it stays at zero.

## Build from source

```
dotnet build DiskPerformanceAnalyzer.sln
dotnet test  DiskPerformanceAnalyzer.sln
dotnet run --project src/DiskPerformanceAnalyzer
```

Requires the .NET 8 SDK. Single-file publish as done by CI:

```
dotnet publish src/DiskPerformanceAnalyzer -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

## Releasing

Push a tag and CI does the rest — builds, tests, publishes the exe and creates the GitHub Release
with generated notes:

```
git tag v0.2.0
git push origin v0.2.0
```

## Stack

.NET 8 · Avalonia 11 · LiveCharts2 · CommunityToolkit.Mvvm · Microsoft.Diagnostics.Tracing.TraceEvent (ETW) · xunit

## License

[MIT](LICENSE)
