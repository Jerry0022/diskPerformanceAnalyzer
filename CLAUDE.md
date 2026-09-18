# diskPerformanceAnalyzer

Disk *activity* monitor for Windows and Linux (Task-Manager-style): requests/s and throughput
per physical disk, one filter driving chart + table, top I/O processes as root cause, "open in
Explorer". Not capacity/storage analysis — never count gigabytes.

## Stack
.NET 8 · Avalonia 11.3 + DataGrid (MVVM, CommunityToolkit.Mvvm) · LiveCharts2 · xunit
Windows: ETW via Microsoft.Diagnostics.Tracing.TraceEvent · PerformanceCounter for throughput/queue; % active derived from ETW
Linux: /proc/diskstats + /sys/block for disks · tracefs `block:block_rq_issue` for per-process attribution (root)

## Commands
- Build: `dotnet build DiskPerformanceAnalyzer.sln`
- Run:   `dotnet run --project src/DiskPerformanceAnalyzer`
- Test:  `dotnet test DiskPerformanceAnalyzer.sln`
- Lint:  `dotnet format DiskPerformanceAnalyzer.sln --verify-no-changes`
- Probe (no UI): `dotnet run --project src/DiskPerformanceAnalyzer -- --probe 5` — prints disks + top processes; as root in WSL: `wsl -d Ubuntu-24.04 -u root -e ./publish/DiskPerformanceAnalyzer --probe 5`
- Release: `git tag vX.Y.Z && git push origin vX.Y.Z` — GitHub Actions publishes win-x64 exe/zip + linux-x64 binary/tar.gz to a Release

## Conventions
- Data layer (`Monitoring/`) has no Avalonia dependency — testable without UI. `DiskMonitor.Create()` picks the platform sources (`IDiskSampleSource` + `IProcessIoSource`).
- Windows always runs elevated (requireAdministrator) — ETW kernel session needs it. Linux asks for root via pkexec; declined → disk-only mode with `IDiskMonitor.Notice` shown in the rail.
- Windows-only code carries `[SupportedOSPlatform("windows")]` per member and is reached only behind `OperatingSystem.IsWindows()`; no assembly-level attribute.
- WSL/containers: the kernel trace reports root-namespace PIDs (`TracefsProcessIoSource._foreignPids`); GUI testing on Linux works in WSLg (`wsl -d Ubuntu-24.04`).
- Project artifacts in English; chat with the user in German.

## References
- Project map: see `.claude/project-map.md`
- Architecture & data sources: see `.claude/deep-knowledge/architecture.md`
