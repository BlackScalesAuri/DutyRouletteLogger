# Duty Roulette Logger

A Dalamud (FFXIV Quick Launcher) plugin that records every roullete run (entry, exit, duration, party, role, status) to a SQLite database, with a filterable, sortable history window that can be exported to CSV. non roulette runs are not logged

### Instance tracking (SQLite database)

- Database at `%APPDATA%\XIVLauncher\pluginConfigs\DutyRouletteLogger\DutyRouletteLogger.db`.
- `InstanceRuns` table (columns as specified: QueueType, InstanceName,
  EnterTimestamp/ExitTimestamp in UTC, RunDurationSeconds, PartySize,
  IsComplete, Role, Notes).
- Automatic capture via:
  - `IDutyState.DutyStarted` → opens the run (instance name via
    `ContentFinderCondition`, formatted through
    `ISeStringEvaluator.EvaluateFromAddon(9781, [cfcRow.RowId])` — the same
    template the native UI uses, avoiding odd artifacts that a raw
    `cfcRow.Name.ToString()` can produce for names with embedded
    formatting/macros; with a fallback to the raw name and then to the
    territory's name via `IDataManager`).
  - `IDutyState.DutyCompleted` → closes the run as completed.
  - `ICondition` (`ConditionFlag.BoundByDuty` turning false without
    `DutyCompleted` having fired first) → closes the run as abandoned.
  - `IDutyState.DutyWiped` / `DutyRecommenced` → noted in `Notes`, without
    closing the run (the party may restart).
  - `IPartyList.Length` → party size at the time of entry.
  - `IPlayerState.ClassJob` → role (Tank/Healer/DPS) via a static job
    abbreviation mapping.
  - `IPlayerState.IsLevelSynced` → level sync status ("Synced"/"Unsynced"),
    recorded in `Notes` and included in the `[INSTANCE] ENTER` log line.
  - `IClientState.CfPop` → notes in `Notes` whether the entry came from a
    Duty Finder "pop", and is also the moment Queue Type gets captured (see
    the section above) via `ContentsFinder.QueueInfo.QueuedContentRouletteId`.
- Database robustness (replacing the original "Data Protection" request,
  which is an ASP.NET Core concept that doesn't apply to a local SQLite
  file):
  - `journal_mode=WAL` + `busy_timeout` to reduce the risk of corruption and
    locks.
  - If the database fails to open, the plugin quarantines the suspect file
    (`DutyRouletteLogger.db.corrupt-<timestamp>`) and creates a fresh one.
  - If the database is unavailable, instance events only show up as a
    message in the Dalamud log (`/xllog`) — nothing is persisted until the
    database comes back — and the history window shows a warning instead of
    breaking.
- Schema migration via `PRAGMA user_version` (incremental, ready for future
  versions).

### "Run History" window (main window)

Like the Settings window, this is a standard ImGui window and can be
minimized via the little arrow next to the title.

- Filters: Queue Type (dropdown with the values already in use), Instance
  (text), Period (All/7 days/30 days), Status
  (All/Completed/Abandoned/In Progress).
- Clickable columns to sort (Queue Type, Instance, Date/Time, Duration,
  Party, Status) — clicking the same column again reverses the direction.
  Each column's width adjusts automatically to its content
  (`ImGuiTableFlags.SizingFixedFit`) and can still be resized manually
  afterward.
- Statistics panel (respecting the current filters): total runs, average
  duration per Queue Type, most played instance, most used queue.
- "Export CSV" button (exports exactly what's currently filtered on screen).
- "Actions" column with a "Delete" button per row (with confirmation) to
  remove that specific run from history.

### "Settings" window

- Logging: enable/disable and minimum level sent to `/xllog` (see "Log
  levels" above), plus quick test buttons (LogWarning/LogDebug/LogInfo/LogError).
- Automatic instance tracking: enable/disable, enable chat notifications
  when entering/leaving an instance.
- History retention: 7 / 30 / 90 days or "forever" (runs older than the
  limit are automatically deleted on plugin startup).

### Commands

- `/dutyroulettelog` — opens/closes the run history window.
- `/dutyroulettelog config` — opens/closes the settings window.
- `/dutyroulettelog stats` — prints quick stats to chat.
- `/dutyroulettelog export` — exports all runs to CSV and reports the path in
  chat.

The Settings window also opens via the plugin's gear icon in Dalamud's
plugin installer (`OpenConfigUi`); the history window opens via a regular
click on the plugin's name (`OpenMainUi`).
