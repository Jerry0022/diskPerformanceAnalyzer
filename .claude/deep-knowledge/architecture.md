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
| Requests per second (IOPS) per disk and per process | Count of ETW `DiskIORead`/`DiskIOWrite` events per (disk, PID) per second | Request count is often the real saturation signal: thousands of 4 KB reads load a disk long before MB/s look high. The table sorts by requests by default |
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
- Ring buffer keeps 300 one-second snapshots. The process table aggregates a
  **selectable window (5 s / 1 min / 5 min, default 1 min)** and can be toggled
  to **cumulative since start**; it sorts by **requests** (default) or bytes.
  Click-to-freeze on the chart pins one exact second. Live is secondary —
  the aggregated view is what identifies the culprit.

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
├── ViewModels/           ← CommunityToolkit.Mvvm; MainViewModel owns filter, sort, column toggles
└── Views/                ← Avalonia AXAML; LiveCharts2 chart, Avalonia DataGrid (headers wired in code-behind)
```

## UI contract (revised 2026-09-18)

- Left rail: one card per physical disk — current IOPS (big), read/write
  request split, throughput, 60 s IOPS sparkline. "% active" is not shown:
  the user reads load from requests and throughput directly.
- One **filter bar** drives chart and table: substring on process name or
  attributed file path. Selecting rows in the table isolates those processes
  in the chart (Escape / "Clear filter" releases).
- Chart (60 s): read/write **throughput as filled areas** (left axis) and
  read/write **requests/s as lines** (right axis) — one hue per family, read
  always the lighter shade (green = bytes, blue = requests). Chart values are
  summed from the same per-process ETW buckets as the table, so they agree.
  Click freezes a second; a grey pause icon sits in the chart's top-right.
- Table (Avalonia DataGrid): PROCESS (name + folder icon → executable folder,
  share icon → copy path), SHARE (bar of the request share; hover shows
  requests, read, write, total), TOP FILE (`C:\Users\…\folder\file` + folder
  and share icons). PID, READ, WRITE, REQUESTS are hidden columns toggled via
  the column icon above the table. Headers sort (click toggles asc/desc),
  columns resize and reorder. Window segments (5 s / 1 min / 5 min / Σ) sit
  above the table on the right.
- App icon: `Assets/app.ico`, generated by `scripts/gen-icon.py` (PIL).

## Minimal invasiveness (verified 2026-09-18)

The monitor must not add to the load it measures:

- ETW session is **real-time only** (`TraceEventSession` without `FileName`;
  `IsRealTime` is asserted at start) — events are consumed in memory, no .etl
  is written. Kernel buffers 16 MB, keywords `DiskIO | DiskFileIO` only.
- Nothing is logged to disk (`LogToTrace()` has no file listener).
- The status bar shows the app's own request count ("This app: 0 IOPS now,
  N requests since start"); after start-up page-ins it stays at zero.
- Measured: ETW pump ≈ 0.6 % of one core; UI ≈ 9 % (LiveCharts redraw at
  1 Hz, redirection-surface rendering instead of the WinUI compositor).
  Private bytes flat at ~200 MB over 3 min.

## Non-goals

Directory size scans, duplicate finders, SMART health, disk cleanup.
