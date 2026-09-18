# diskPerformanceAnalyzer

Windows disk *activity* monitor (Task-Manager-style): requests/s and throughput per physical
disk, one filter driving chart + table, top I/O processes as root cause, "open in
Explorer". Not capacity/storage analysis — never count gigabytes.

## Stack
.NET 8 · Avalonia 11.3 + DataGrid (MVVM, CommunityToolkit.Mvvm) · LiveCharts2 · xunit · ETW via
Microsoft.Diagnostics.Tracing.TraceEvent · PerformanceCounter for throughput/queue; % active derived from ETW

## Commands
- Build: `dotnet build DiskPerformanceAnalyzer.sln`
- Run:   `dotnet run --project src/DiskPerformanceAnalyzer`
- Test:  `dotnet test DiskPerformanceAnalyzer.sln`
- Lint:  `dotnet format DiskPerformanceAnalyzer.sln --verify-no-changes`
- Release: `git tag vX.Y.Z && git push origin vX.Y.Z` — GitHub Actions publishes the single-file exe + zip to a Release

## Conventions
- Data layer (ETW/PerfCounter) has no Avalonia dependency — testable without UI.
- App always runs elevated (requireAdministrator) — ETW kernel session needs it; no fallback mode.
- Project artifacts in English; chat with the user in German.

## References
- Project map: see `.claude/project-map.md`
- Architecture & data sources: see `.claude/deep-knowledge/architecture.md`
