# diskPerformanceAnalyzer

Windows disk *activity* monitor (Task-Manager-style): % active time per physical
disk, live interactive chart, top I/O processes as root cause, "open in
Explorer". Not capacity/storage analysis — never count gigabytes.

## Stack
.NET 8 · Avalonia 12 (MVVM, CommunityToolkit.Mvvm) · xunit · ETW via
Microsoft.Diagnostics.Tracing.TraceEvent · PerformanceCounter for disk % active

## Commands
- Build: `dotnet build DiskPerformanceAnalyzer.sln`
- Run:   `dotnet run --project src/DiskPerformanceAnalyzer`
- Test:  `dotnet test DiskPerformanceAnalyzer.sln`
- Lint:  `dotnet format DiskPerformanceAnalyzer.sln --verify-no-changes`

## Conventions
- Data layer (ETW/PerfCounter) has no Avalonia dependency — testable without UI.
- App always runs elevated (requireAdministrator) — ETW kernel session needs it; no fallback mode.
- Project artifacts in English; chat with the user in German.

## References
- Project map: see `.claude/project-map.md`
- Architecture & data sources: see `.claude/deep-knowledge/architecture.md`
