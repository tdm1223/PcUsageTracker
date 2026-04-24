# PcUsageTracker

Personal Windows PC usage tracker. Tray app that records which process is in the foreground, second by second, to a local SQLite database. No network, no account, no telemetry.

## Install

1. Copy `PcUsageTracker.exe` anywhere (e.g. `%USERPROFILE%\Apps\PcUsageTracker\`).
2. Double-click to launch. A tray icon appears.
3. On first run, the app registers itself to start on Windows login via `HKCU\...\Run`. Toggle via tray menu.

Uninstall: Quit from tray, delete the exe, and optionally remove `%APPDATA%\PcUsageTracker\`.

## Usage

- **Left-click tray icon** — toggle the report popup.
- **Right-click tray icon** — menu:
  - Open report
  - Autostart on Windows login (toggle)
  - Open data folder (`%APPDATA%\PcUsageTracker\`)
  - Quit

### Report window

- **Today + Week tab** — top 5 processes for today and this week (Monday start).
- **All-time top 20 tab** — cumulative top 20.
- Refreshes every 5 seconds while open.
- Each row shows the executable's icon (extracted from the exe file), the process name, duration, and a proportional bar.
- Hovering the process name shows the full exe path.

## Data

- `%APPDATA%\PcUsageTracker\history.db` — SQLite (WAL).
- `%APPDATA%\PcUsageTracker\logs\` — rolling daily logs.
- Retention: unlimited. Open the DB with any SQLite viewer (DB Browser for SQLite, VS Code SQLite extension, etc.).

### Schema (v2)

```sql
CREATE TABLE sessions (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  process_name  TEXT    NOT NULL,
  start_at      INTEGER NOT NULL,  -- unix epoch seconds
  end_at        INTEGER,           -- NULL while open
  duration_sec  INTEGER
);

-- UI metadata: exe path → icon extraction for the report window
CREATE TABLE processes (
  name          TEXT PRIMARY KEY,
  exe_path      TEXT,
  last_seen_at  INTEGER NOT NULL
);
```

Migrations are idempotent. Existing v1 DBs are auto-upgraded on first launch.

## Behavior

- Samples the foreground process once per second.
- A row is written on process switch (close previous, open new).
- Screen lock (`Win+L`) pauses recording. Unlock resumes on next tick.
- System suspend / resume pauses and resumes similarly.
- On abnormal exit, any `end_at IS NULL` row is force-closed on next startup (capped at 24 hours from `start_at`).

## Build from source

Requires .NET 8 SDK.

```
dotnet test
dotnet publish src/PcUsageTracker.App -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

Output: `publish/PcUsageTracker.App.exe` (~157 MB, self-contained — includes the .NET 8 WindowsDesktop runtime).

If size matters, build as framework-dependent instead:
```
dotnet publish src/PcUsageTracker.App -c Release -r win-x64 --self-contained false \
    -p:PublishSingleFile=true -o publish-fd
```
That produces a ~2 MB exe but requires the .NET 8 Desktop Runtime on the target machine.

### Inspecting the DB

```
dotnet run --project tools/DbInspector
```

Or pass a custom path: `dotnet run --project tools/DbInspector -- C:\path\to\history.db`.

## Release

Tagged releases are published to GitHub Releases automatically via `.github/workflows/release.yml`.

```
git tag v0.1.0
git push origin v0.1.0
```

On tag push, the workflow (Windows runner) runs:

1. `dotnet test tests/PcUsageTracker.Core.Tests -c Release`
2. `dotnet publish src/PcUsageTracker.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true`
3. Renames the output to `PcUsageTracker-<tag>-win-x64.exe`
4. Creates a GitHub Release for the tag with the exe attached and auto-generated release notes

Requires the repo to be hosted on GitHub with Actions enabled. No secrets needed — `GITHUB_TOKEN` is sufficient for Release creation via the workflow's `contents: write` permission.

## Runtime budget (measured)

Measured on the release single-file build (Windows 11, .NET 8.0.205):

| Snapshot | RAM (Working Set) | CPU (cumulative) |
|----------|-------------------|------------------|
| Start (3 s)  | 52.1 MB       | 0.06 s           |
| +12 h        | _pending_     | _pending_        |
| +24 h        | _pending_     | _pending_        |

Target: <100 MB RAM, <1% CPU average. The 12 h / 24 h snapshots need a real overnight run.

## License

Personal use.
