# Architecture — Disk Activity Analyzer

## Goal

Answer, live and interactively: *"How busy is disk X right now, and which
processes are causing it?"* — the same numbers Task Manager → Performance →
Disk and Resource Monitor → Disk show. Capacity (GB used/free) is explicitly
out of scope.

## Data sources (Windows)

| Metric | Source | Notes |
|--------|--------|-------|
| Disk % active time | `PerformanceCounter("PhysicalDisk", "% Disk Time", "<n> C:")` — or `% Idle Time` inverted, which is what Task Manager actually uses | Per physical disk instance; poll at 1 s |
| Read/write throughput per disk | `PhysicalDisk\Disk Read Bytes/sec`, `Disk Write Bytes/sec` | Secondary axis on the chart |
| Queue length | `PhysicalDisk\Current Disk Queue Length` | Saturation indicator |
| Per-process I/O attribution | ETW kernel session, `KernelTraceEventParser.Keywords.DiskIO \| FileIO \| DiskFileIO` via `Microsoft.Diagnostics.Tracing.TraceEvent` | `DiskIORead/Write` events carry `ProcessID`, `DiskNumber`, `TransferSize`, `FileName` — this is the root-cause link disk → process → file |
| Fallback without admin | `Process.GetProcesses()` + `GetProcessIoCounters` (P/Invoke) deltas | Process totals only, **no per-disk attribution** — show a banner explaining the limitation |

ETW needs an elevated process (`SeSystemProfilePrivilege`). Request elevation
via `app.manifest` (`requireAdministrator`) or offer a "Restart as admin"
action; never silently fail.

## Layers

```
src/DiskPerformanceAnalyzer/
├── Monitoring/           ← no Avalonia refs; unit-testable
│   ├── IDiskActivitySource     (interface: samples → IObservable/Channel)
│   ├── PerfCounterDiskSource   (% active, throughput, queue per disk)
│   ├── EtwProcessIoSource      (per-process/per-disk bytes, top files)
│   └── IoCountersFallbackSource
├── ViewModels/           ← CommunityToolkit.Mvvm, aggregates samples into ring buffers
├── Views/                ← Avalonia AXAML; chart via LiveCharts2 (Avalonia)
└── Services/
    └── ExplorerLauncher    (`explorer.exe /select,"<path>"`)
```

## UI contract

- Disk selector (physical disk instances, refreshed on hot-plug).
- Live line chart: % active (primary), read/write MB/s (secondary), 60 s window,
  hover tooltip, click on a spike freezes that sample.
- Process table for the selected disk / frozen sample: process name, PID,
  read/write bytes in window, top file paths. Sorted by total bytes.
- Row actions: *Open in Explorer* (executable path and/or hottest file),
  *Copy path*.

## Non-goals

Directory size scans, duplicate finders, SMART health, disk cleanup.
