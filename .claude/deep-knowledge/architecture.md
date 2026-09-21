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
exactly like Resource Monitor. There is no non-admin fallback on Windows:
process→disk attribution is the product, and IoCounters cannot provide it.

## Data sources (Linux, added 2026-09-18, v0.3)

| Metric | Source | Notes |
|--------|--------|-------|
| Disks | `/sys/block/*` whole devices (skip loop/ram/zram/fd/nbd); partitions via `<dev>/<part>/partition`; dm-*/md* resolved to their first slave | `LinuxBlockDevices`, refreshed every 10 s; index-stable per instance |
| Mount points | `/proc/self/mountinfo` field 3 (`major:minor`) → field 5, octal-unescaped | Longest-prefix lookup maps a path to its disk; "drive letters" on the card are mount points |
| % active, bytes/s, requests/s, queue | `/proc/diskstats` deltas: `io_ticks`, sectors×512, completed reads/writes, `in_flight` | `ProcDiskStatsSource` — the numbers iostat shows |
| Per-process attribution | tracefs `events/block/block_rq_issue` read from `trace_pipe` in a private instance (`instances/diskPerformanceAnalyzer`) | `TracefsProcessIoSource`: line `comm-tid [cpu] … block_rq_issue: 8,2 WS 4096 () sector + n [comm]` → disk via device number, thread → process via `/proc/tid/status` Tgid (at record time — worker threads die fast), name via `/proc/pid/comm`. Root required; tracefs is mounted on demand |
| Files per process | `/proc/pid/fd` targets on the same disk, no counts | The block layer does not know the file; eBPF would, but that is not worth the dependency |
| Busy time from the trace | none — `DrainActivePercent()` is empty, `io_ticks` is used | `block_rq_complete` would allow it; not needed |

Elevation on Linux: `Program.Main` relaunches itself through `pkexec env
DISPLAY=… WAYLAND_DISPLAY=… XDG_RUNTIME_DIR=… <exe> --no-elevate`; exit code
126/127 (declined / no agent) falls back to running unprivileged, where
`DiskMonitor.Notice` explains the missing process table. `--probe [seconds]`
runs the data layer headless (used by CI on the Ubuntu runner and for WSL).

Namespaces: the tracepoint reports root-namespace PIDs. Inside WSL distros
or containers they do not match `/proc`; the source detects this via its own
reader thread's comm (`dpa-trace-read`) and then uses the trace's comm as the
process name and lists no files.

## Contract between Monitoring and UI (decided 2026-09-17)

- `IDiskMonitor` — `Start()` / `Dispose()`, raises `SnapshotReady(DiskSnapshot)`
  once per second on a background thread. The ViewModel marshals to the UI
  thread via `Dispatcher.UIThread`. No Rx, no channels. `Notice` (after
  `Start`) says why process attribution is missing, or null.
- `DiskMonitor(IDiskSampleSource, IProcessIoSource?)` — `Create()` picks
  PerfCounter+ETW on Windows, `/proc/diskstats`+tracefs on Linux. With a
  process source, per-disk bytes/requests come from the trace so chart and
  table agree; without one the disk source's own numbers are used.
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
│   ├── IDiskSampleSource / IProcessIoSource  (platform seams)
│   ├── PerfCounterDiskSource   (Windows: throughput, queue per disk)
│   ├── EtwProcessIoSource      (Windows: per-process/per-disk bytes, requests, files, busy time)
│   ├── Linux/LinuxBlockDevices (sysfs disks, partitions, dm/md, mount points)
│   ├── Linux/ProcDiskStatsSource (Linux: % active, throughput, requests, queue)
│   ├── Linux/TracefsProcessIoSource (Linux: block_rq_issue → process, open files)
│   ├── DiskMonitor             (Create() = platform factory; combines sources, 1 Hz snapshot event)
│   └── SnapshotRingBuffer      (300 s, window + cumulative aggregation)
├── Platform/             ← OS helpers, no Avalonia refs
│   ├── ExplorerLauncher        (Windows `explorer.exe /select,"<path>"`, Linux `xdg-open <folder>`)
│   ├── ProcessImagePath        (QueryFullProcessImageName / `/proc/pid/exe`)
│   ├── Elevation               (Administrators / root)
│   ├── DiskDriveLetters        (Windows: disk number → drive letters)
│   ├── AppInstall              (Windows: copy to %LocalAppData%\Programs, clean reinstall, deferred uninstall)
│   ├── StartMenuShortcut       (Windows: .lnk via IShellLink, on an STA thread)
│   ├── StartupTask             (Windows: schtasks /XML logon task, RunLevel HighestAvailable, no time limit)
│   ├── SingleInstance          (Windows: named mutex + activate event — one copy per session)
│   └── InteractiveUser         (Windows: process user ≠ session user → over-the-shoulder UAC)
├── Probe.cs              ← `--probe`: headless data-layer run (CI, SSH, WSL)
├── ViewModels/           ← CommunityToolkit.Mvvm; MainViewModel owns filter, sort, column toggles
└── Views/                ← Avalonia AXAML; LiveCharts2 chart, Avalonia DataGrid (headers wired in code-behind)
```

## UI contract (revised 2026-09-18, v0.2)

- **Vocabulary** (`ViewModels/Labels.cs`): two metric families — *Requests* (blue,
  unit `req/s`) and *Data* (green, unit `MB/s`) — each split into `↓ Read` (lighter)
  and `↑ Write` (darker). Every label, legend, tooltip and column uses exactly these
  words and glyphs; units sit in parentheses ("Read (req/s)").
- **Numbers** (`Formatting`): no decimals. Values are rounded to two significant
  digits ("1,234" → "1,200", "12.3 MB" → "12 MB"); units switch only when the
  scaled value reaches 10,000 so two digits survive. The data axis uses one unit
  for all its ticks, chosen from the visible maximum.
- Left rail: one card per physical disk — requests/s (big), `↓ ↑` request split,
  `↓ ↑` data split, 60 s sparkline. Fixed column widths + tabular figures
  (`FontFeatures tnum`) so live values never push their neighbours around.
- One **filter bar** drives chart and table: substring on process name or
  attributed file path. Clicking a row isolates that process in the chart;
  clicking the single selected row again (or Escape / "Clear filter") releases.
- Chart (60 s): data as filled areas (left axis), requests/s as lines (right
  axis). Click freezes a second; a grey pause icon sits in the top-right.
- Table (Avalonia DataGrid): PROCESS (chevron → folder breakdown, name, folder
  and share icons), SHARE — **two bars per row**, requests (blue) and data
  (green), the header chips `REQUESTS` / `DATA` choose the sort key and the
  primary (undimmed) bar; hover shows all numbers — and TOP FILE. PID, READ,
  WRITE, REQUESTS are hidden columns behind the column icon. Headers sort,
  columns resize and reorder. Pressing an icon never changes the selection.
- **Folder breakdown** (`FolderBreakdown`): the expanded row shows where the
  process is busy — up to 7 lines: per-file I/O rolled up to folders, refined
  top-down (busiest folder split into its busiest sub-folder + remainder while
  the result fits), plus one "Unnamed I/O" line for paging / NTFS metadata /
  cache flushes and anything the kernel never named. Per-file counts come from
  `ProcessIo.Files` (ETW bucket keeps bytes + ops per FileKey, max 32 per
  second; ring buffer keeps 64 per process, pruned to the 24 hottest).
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

## Windows integration (added 2026-09-21, v0.4)

The settings gear (header, top right) installs the program to a fixed per-user
folder and, once installed, offers "Show in Start menu" and "Start with
Windows". Autostart is a Task Scheduler logon task rather than a Run key: the
app carries `requireAdministrator`, and UAC silently drops Run-key entries that
need elevation. The task runs with `HighestAvailable` and `ExecutionTimeLimit`
`PT0S` (the 72 h default would kill a monitor that stays open). Every toggle
runs off the UI thread and re-reads the system state afterwards, so a failed
change snaps the checkbox back and shows the reason. `SingleInstance` keeps one
copy per session — a second start would stop the first copy's ETW session by
name (`StopStaleSession`) — and hands over to the running window instead.

## Non-goals

Directory size scans, duplicate finders, SMART health, disk cleanup.
