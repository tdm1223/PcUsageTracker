# PcUsageTracker

Personal Windows PC usage tracker. Tray app that records which process is in the foreground, second by second, to a local SQLite database. No account and no telemetry. The only network feature is an update check against this repository's public GitHub Releases API.

## Install

1. Copy `PcUsageTracker.exe` anywhere (e.g. `%USERPROFILE%\Apps\PcUsageTracker\`).
2. Double-click to launch. A tray icon appears.
3. On first run, the app registers itself to start on Windows login via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. Toggle this on/off any time via tray right-click → **Autostart on Windows login**.

Uninstall: Quit from tray, delete the exe, and optionally remove `%APPDATA%\PcUsageTracker\`. Untoggling Autostart removes the Run-key entry.

## Usage

- **Left-click tray icon** — toggle the report popup.
- **Right-click tray icon** — menu:
  - Open report
  - Applications & categories...
  - Check for updates...
  - Autostart on Windows login (toggle)
  - Open data folder (`%APPDATA%\PcUsageTracker\`)
  - Quit

### Report window

- **Dashboard tab** — choose any recorded date, jump one day backward/forward, or return to Today. It shows tracked/active/idle totals, the top application, a color-coded 24-hour timeline, selectable 7-day or 30-day active/idle bars, and the selected day's applications.
- **Today + Week + Month tab** — all recorded processes for today, this week (Monday start), and this month.
- **All-time tab** — cumulative usage for every recorded process.
- Today's visible report refreshes every 5 seconds and preserves selected rows. Reporting uses an independent read-only SQLite connection in the background, so collection stays responsive; timer ticks coalesce behind an in-progress query instead of repeatedly canceling it. A historical Dashboard date stays cached instead of repeatedly querying unchanged data, and Import/application-rule changes invalidate that cache even while the report is hidden.
- Each row shows the executable's icon, customizable display name and category color, duration, and share.
- Missing or outdated executable paths are re-detected from running processes, Windows app registrations, and versioned install folders; unresolved entries use a neutral fallback instead of a broken-image X.
- Hovering the process name shows the full exe path.
- **Double-click a row** to open the folder containing that program.
- **Right-click any row → "Edit application..."** to set an alias, category, or per-application color. The original process name remains the identity used by tracking and deletion.
- **Right-click any row → "Delete history & stop tracking"** to remove all past sessions for that process and prevent it from being recorded again. Confirmation prompt is shown.
- Use **Applications & categories...** on the Dashboard toolbar to manage all application rules without returning to the tray menu.
- Timeline and trend charts are keyboard-focusable; use the arrow, Home, and End keys to inspect individual spans or days. Their focused values and summaries are exposed to Windows accessibility tools. The timeline is pre-rendered for smooth resizing, and placement/ticks use the selected local day's exact UTC boundaries, explicitly labeling repeated or skipped daylight-saving hours.

### Applications and categories

Open **Applications & categories...** from the tray menu to manage presentation rules for every application found in history or executable metadata. The seeded categories are Coding, Game, Communication, Browsing, System, and Other; categories can be edited and additional categories can be created. **Other** is the permanent fallback for unassigned applications, so its color and order can be edited but it cannot be renamed or deleted.

Aliases and category assignments are joined to reports dynamically. Changing a rule updates both historical and future usage displays without rewriting the underlying sessions. Pending application edits are saved automatically when selection changes, category definitions change, or the editor closes. `__idle__` is reserved (case-insensitive), always shown as `(Idle)` in gray, and cannot be edited.

### Excluded processes

Some Windows shell hosts are noisy (foreground every time you press the Windows key). The following are excluded by default on first launch:

- `StartMenuExperienceHost` — Start Menu
- `ShellExperienceHost` — notification center, action center
- `SearchHost`, `SearchUI` — search popup
- `TextInputHost` — touch keyboard / IME
- `LockApp` — lock screen
- `ApplicationFrameHost` — UWP app frame wrapper
- `SystemSettings`

Excluded processes never get recorded. Add more via the report window's right-click menu. Exclusions live in the `excluded_processes` table — feel free to edit directly with a SQLite viewer to remove an exclusion.

## Data

- `%APPDATA%\PcUsageTracker\history.db` — SQLite (WAL).
- `%APPDATA%\PcUsageTracker\logs\` — rolling daily logs.
- `%APPDATA%\PcUsageTracker\updates\` — temporary verified update downloads and startup markers. Stale staging files are cleaned automatically.
- Retention: unlimited. Open the DB with any SQLite viewer (DB Browser for SQLite, VS Code SQLite extension, etc.).

### Schema (v6)

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

-- Tracking exclusions (system shells + user-hidden processes)
CREATE TABLE excluded_processes (
  name         TEXT PRIMARY KEY,
  reason       TEXT,         -- 'system-ui' | 'user-hidden' | NULL
  excluded_at  INTEGER NOT NULL
);

CREATE TABLE settings (
  key    TEXT PRIMARY KEY,
  value  TEXT NOT NULL
);

CREATE TABLE categories (
  id          INTEGER PRIMARY KEY,
  name        TEXT NOT NULL COLLATE NOCASE UNIQUE,
  color_rgb   INTEGER NOT NULL CHECK(color_rgb BETWEEN 0 AND 16777215),
  sort_order  INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE application_rules (
  process_name       TEXT NOT NULL COLLATE NOCASE PRIMARY KEY,
  alias              TEXT,
  category_id        INTEGER REFERENCES categories(id) ON DELETE SET NULL,
  color_override_rgb INTEGER CHECK(color_override_rgb BETWEEN 0 AND 16777215),
  updated_at         INTEGER NOT NULL
);

-- v6 reporting/import indexes
CREATE INDEX idx_sessions_end_start
  ON sessions(end_at, start_at) WHERE end_at IS NOT NULL;
CREATE INDEX idx_sessions_open_start
  ON sessions(start_at) WHERE end_at IS NULL;
CREATE INDEX idx_sessions_import_logical
  ON sessions(process_name COLLATE NOCASE, start_at);
CREATE INDEX idx_processes_name_nocase_seen
  ON processes(name COLLATE NOCASE, last_seen_at DESC);
```

Migrations are transactional and idempotent. Existing v1-v5 DBs are auto-upgraded on first launch without rewriting session history.

## Behavior

- Samples the foreground process once per second.
- A row is written on process switch (close previous, open new).
- Reporting clips sessions to the requested local calendar day and splits multi-day sessions at
  time-zone-aware midnight boundaries. Day totals therefore remain correct on 23-hour and
  25-hour daylight-saving transition days; open sessions are counted only through the current time.
- Idle time is reported separately from active application time. Category and alias rules are
  resolved when a report is queried, so edits also reclassify historical totals.
- Append import identifies a session by case-insensitive process name plus start time. Re-importing
  the row is idempotent, and a later closed copy completes an earlier open copy in place. Once a
  row is closed, an open or conflicting closed copy does not replace it. Other overlapping rows are retained:
  totals intentionally sum recorded per-session durations rather than computing a wall-clock union.
- Excel imports are transactional, including Replace's clear operation. A parsing, validation, or
  database failure leaves the previous history intact. Live tracking closes its current session
  before import and immediately observes the foreground application again afterward.
- Screen lock (`Win+L`) pauses recording. Unlock resumes on next tick.
- System suspend / resume pauses and resumes similarly.
- On abnormal exit, any `end_at IS NULL` row is force-closed on next startup (capped at 24 hours from `start_at`).

## Build from source

Requires .NET 8 SDK.

```
dotnet test
dotnet publish src/PcUsageTracker.App -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version=0.2.0 -o publish
```

Output: `publish/PcUsageTracker.App.exe` (~157 MB, self-contained — includes the .NET 8 WindowsDesktop runtime).

If size matters, build as framework-dependent instead:
```
dotnet publish src/PcUsageTracker.App -c Release -r win-x64 --self-contained false \
    -p:PublishSingleFile=true -p:Version=0.2.0 -o publish-fd
```
That produces a ~2 MB exe but requires the .NET 8 Desktop Runtime on the target machine.
Replace `0.2.0` with the version being built. The source default is `0.2.0` for ordinary development builds, while the release workflow always overrides it from the exact `vMAJOR.MINOR.PATCH` tag.

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

On a stable `vMAJOR.MINOR.PATCH` tag push, the workflow (Windows runner) runs:

1. Tests the full solution in Release configuration with the tag version embedded in the binaries.
2. Publishes the app with `-p:Version=<tag-without-v>` as a self-contained Windows x64 single file.
3. Renames the output to `PcUsageTracker-<tag>-win-x64.exe`
4. Creates a GitHub Release for the tag with the exe attached and auto-generated release notes

Requires the repo to be hosted on GitHub with Actions enabled. No secrets needed — `GITHUB_TOKEN` is sufficient for Release creation via the workflow's `contents: write` permission.

### Updates and trust

The app checks the public `tdm1223/PcUsageTracker` latest stable release after a short startup delay and no more than once per day; the last attempt is stored in the local `settings` table. Automatic network/rate-limit failures are logged quietly. Use tray right-click → **Check for updates...** for an immediate check with visible status and errors.

Installation is always confirmed first. The updater accepts only the exact `PcUsageTracker-vX.Y.Z-win-x64.exe` asset over HTTPS, enforces GitHub's declared size and a 512 MiB ceiling, and requires the SHA-256 `digest` returned by the GitHub Releases API. The digest is checked after download and again by the helper immediately before replacement. Before the running app exits, the helper must confirm the original user SID, PID-to-executable binding, staging containment, embedded version, digest, apphost identity, and replacement access through an acceptance marker; the helper proceeds only after the parent acknowledges that marker. A timeout, helper crash, UAC cancellation, different-account UAC credential, or rejected check leaves the current tracker running and directs the user to install manually. Running through the shared `dotnet.exe` host is never eligible for self-update.

After acceptance, the helper atomically replaces the currently running path, keeps `PcUsageTracker.App.previous.exe` beside it, waits for the new tray context to report ready, and rolls back if startup fails. Administrator approval is requested only when a non-mutating replacement-access preflight fails under the current token.

This integrity check proves that the downloaded bytes match the asset served by the repository's GitHub release metadata. Releases are not currently Authenticode/code-signed, so Windows may show an unknown-publisher warning and the updater cannot independently protect against compromise of the GitHub repository or release-publishing credentials.

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
