# Architecture — Disk Activity Analyzer

## Goal

Answer, live and interactively: *"How busy is disk X right now, and which
processes are causing it?"* — the same numbers Task Manager → Performance →
Disk and Resource Monitor → Disk show. Capacity (GB used/free) is explicitly
out of scope.

## Data sources (Windows)

| Metric | Source | Notes |
|--------|--------|-------|
| Disk % active time | **ETW-derived**: union of all DiskIO in-flight intervals (`TimeStamp − ElapsedTimeMSec` … `TimeStamp`) per disk per second → `BusyTime.ActivePercent` | The PhysicalDisk `% Idle Time` counter reports 0 (and `% Disk Time` > 3000 %) on NVMe drives — verified 2026-09-17 on this machine — so the counter is NOT used for % active |
| Read/write throughput per disk | `PhysicalDisk\Disk Read Bytes/sec`, `Disk Write Bytes/sec` | Secondary axis on the chart |
| Queue length | `PhysicalDisk\Current Disk Queue Length` | Saturation indicator |
| Per-process I/O attribution | ETW kernel session, `KernelTraceEventParser.Keywords.DiskIO \| FileIO \| DiskFileIO` via `Microsoft.Diagnostics.Tracing.TraceEvent` | `DiskIORead/Write` events carry `ProcessID`, `DiskNumber`, `TransferSize`, `FileName` — this is the root-cause link disk → process → file |

ETW needs an elevated process (`SeSystemProfilePrivilege`). **Decision
(2026-09-17): always elevated** — `app.manifest` sets `requireAdministrator`,
exactly like Resource Monitor. There is no non-admin fallback: process→disk
attribution is the product, and IoCounters cannot provide it.

## Contract between Monitoring and UI (decided 2026-09-17)

- `IDiskMonitor` — `Start()` / `Dispose()`, raises `SnapshotReady(DiskSnapshot)`
  once per second on a background thread. The ViewModel marshals to the UI
  thread via `Dispatcher.UIThread`. No Rx, no channels.
- `DiskSnapshot` — timestamp + one `DiskSample` per physical disk
  (`DiskNumber`, `Name`, drive letters, `ActivePercent`, `ReadBytesPerSec`,
  `WriteBytesPerSec`, `QueueLength`) + per-disk `ProcessIo` buckets for that
  second (`Pid`, `ProcessName`, `ReadBytes`, `WriteBytes`, top file paths).
- Ring buffer keeps 60 one-second snapshots. The process table aggregates a
  **selectable window (1 s / 5 s / 60 s, default 5 s)** and can be toggled to
  **cumulative since start**. Click-to-freeze on the chart pins one exact second.

## Layers

```
src/DiskPerformanceAnalyzer/
├── Monitoring/           ← no Avalonia refs; unit-testable
│   ├── IDiskMonitor / DiskSnapshot / DiskSample / ProcessIo
│   ├── PerfCounterDiskSource   (% active, throughput, queue per disk)
│   ├── EtwProcessIoSource      (per-process/per-disk bytes, top files)
│   ├── DiskMonitor             (combines sources, 1 Hz snapshot event)
│   └── SnapshotRingBuffer      (60 s, window + cumulative aggregation)
├── Platform/             ← Win32 helpers, no Avalonia refs
│   ├── ExplorerLauncher        (`explorer.exe /select,"<path>"`)
│   ├── ProcessImagePath        (QueryFullProcessImageName)
│   └── DiskDriveLetters        (disk number → drive letters)
├── ViewModels/           ← CommunityToolkit.Mvvm
└── Views/                ← Avalonia AXAML; chart via LiveCharts2 (Avalonia)
```

## UI contract

- Disk selector (physical disk instances, refreshed on hot-plug).
- Live line chart: % active (primary), read/write MB/s (secondary), 60 s window,
  hover tooltip, click on a spike freezes that sample.
- Process table for the selected disk / frozen sample: process name, PID,
  read/write bytes in window, top file paths. Sorted by total bytes.
- Row actions: *Open in Explorer* (executable path and/or hottest file),
  *Copy path*.
- Window selector 1 s / 5 s / 60 s + *cumulative* toggle above the table.

## Non-goals

Directory size scans, duplicate finders, SMART health, disk cleanup.
