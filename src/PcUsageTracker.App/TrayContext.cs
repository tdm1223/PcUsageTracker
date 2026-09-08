using System.Diagnostics;
using PcUsageTracker.App.Assets;
using PcUsageTracker.App.Autostart;
using PcUsageTracker.App.Interop;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;
using PcUsageTracker.Core.Updates;
using Serilog;

namespace PcUsageTracker.App;

internal sealed class TrayContext : ApplicationContext
{
    const int TickIntervalMs = 1000;
    const int DefaultIdleThresholdSec = 180;
    const string IdleThresholdSettingKey = "idle_threshold_sec";
    const string LastUpdateCheckSettingKey = "update_last_check_utc";

    readonly NotifyIcon _notifyIcon;
    readonly SqliteStore _store;
    readonly SessionRecorder _recorder;
    readonly IForegroundProbe _probe;
    readonly IIdleProbe _idleProbe;
    readonly IdleDetector _idleDetector;
    readonly IClock _clock;
    readonly System.Windows.Forms.Timer _timer;
    readonly System.Windows.Forms.Timer _automaticUpdateTimer;
    readonly SessionEventsWindow _events;
    readonly OwnedIcon _iconActive;
    readonly OwnedIcon _iconPaused;
    readonly HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);
    readonly HttpClient _updateHttpClient;
    readonly UpdateCoordinator _updateCoordinator;
    readonly UpdateApplier _updateApplier = new();
    readonly CancellationTokenSource _updateLifetime = new();
    bool _lastPausedState;
    bool _lastIdleState;
    bool _updateCheckInProgress;
    bool _exitingForUpdate;
    ReportForm? _reportForm;
    ToolStripMenuItem? _autostartMenuItem;
    ToolStripMenuItem? _checkForUpdatesMenuItem;

    public TrayContext(bool startedFromLogin)
    {
        _clock = new SystemClock();
        _probe = new Win32ForegroundProbe();
        _idleProbe = new Win32IdleProbe();

        var dataDir = GetDataFolder();
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "history.db");
        _store = new SqliteStore(dbPath);
        _updateHttpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _updateCoordinator = new UpdateCoordinator(
            new GitHubReleaseClient(_updateHttpClient),
            AppVersion.Current(),
            Path.Combine(dataDir, "updates"));
        _updateCoordinator.CleanupStaleStaging(_clock.UtcNow, Environment.ProcessPath);

        var recovered = _store.RecoverOrphanedSessions(_clock.UtcNow);
        if (recovered > 0) Log.Information("Recovered {N} orphaned session(s) on startup", recovered);

        _recorder = new SessionRecorder(_store);
        _idleDetector = new IdleDetector(TimeSpan.FromSeconds(LoadIdleThresholdSec()));
        RefreshExclusions();

        _events = new SessionEventsWindow();
        _events.Locked += OnLocked;
        _events.Unlocked += OnUnlocked;
        _events.Suspending += OnSuspending;
        _events.Resuming += OnResuming;

        _iconActive = IconFactory.CreateTrayIcon(paused: false);
        _iconPaused = IconFactory.CreateTrayIcon(paused: true);

        _notifyIcon = new NotifyIcon
        {
            Icon = _iconActive.Icon,
            Text = "PcUsageTracker",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;

        _timer = new System.Windows.Forms.Timer { Interval = TickIntervalMs };
        _timer.Tick += OnTick;
        _timer.Start();

        _automaticUpdateTimer = new System.Windows.Forms.Timer();
        _automaticUpdateTimer.Tick += OnAutomaticUpdateTick;
        ScheduleAutomaticUpdateCheck(initialDelay: true);

        EnsureAutostartFirstRun();
        RunKeyRegistrar.ReconcilePath(ExePath);

        Log.Information("Tray icon shown (startedFromLogin={FromLogin}, db={Db})", startedFromLogin, dbPath);
    }

    static string ExePath => Environment.ProcessPath
                             ?? Process.GetCurrentProcess().MainModule?.FileName
                             ?? throw new InvalidOperationException("Cannot resolve exe path");

    static string AutostartMarkerPath => Path.Combine(GetDataFolder(), ".autostart_initialized");

    void EnsureAutostartFirstRun()
    {
        if (File.Exists(AutostartMarkerPath)) return;
        if (RunKeyRegistrar.Register(ExePath))
        {
            try { File.WriteAllText(AutostartMarkerPath, DateTimeOffset.UtcNow.ToString("o")); }
            catch (Exception ex) { Log.Warning(ex, "Failed writing autostart marker"); }
            if (_autostartMenuItem is not null)
            {
                // Checked 변경이 OnAutostartToggled → Register 재호출을 유발하지 않도록 핸들러를 잠시 분리.
                _autostartMenuItem.CheckedChanged -= OnAutostartToggled;
                _autostartMenuItem.Checked = true;
                _autostartMenuItem.CheckedChanged += OnAutostartToggled;
            }
        }
    }

    internal static string GetDataFolder()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "PcUsageTracker");
    }

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open report", image: null, OnOpenReport);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Export to Excel...", image: null, OnExportExcel);
        menu.Items.Add("Import from Excel...", image: null, OnImportExcel);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Idle threshold...", image: null, OnIdleSettings);
        menu.Items.Add("Applications && categories...", image: null, OnApplicationRules);
        menu.Items.Add(new ToolStripSeparator());

        _autostartMenuItem = new ToolStripMenuItem("Autostart on Windows login")
        {
            CheckOnClick = true,
            Checked = RunKeyRegistrar.IsRegistered(),
        };
        _autostartMenuItem.CheckedChanged += OnAutostartToggled;
        menu.Items.Add(_autostartMenuItem);

        _checkForUpdatesMenuItem = new ToolStripMenuItem("Check for updates...");
        _checkForUpdatesMenuItem.Click += OnCheckForUpdates;
        menu.Items.Add(_checkForUpdatesMenuItem);
        menu.Items.Add("Open data folder", image: null, OnOpenDataFolder);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", image: null, OnQuit);
        return menu;
    }

    void OnExportExcel(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = "Export usage history to Excel",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            FileName = $"pc-usage-{DateTimeOffset.Now:yyyyMMdd}.xlsx",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var written = ExcelStorage.Export(_store, dlg.FileName);
            Log.Information("Excel export complete: {N} sessions → {Path}", written, dlg.FileName);
            MessageBox.Show(
                $"{written} session(s) exported.\n\n{dlg.FileName}",
                "Export complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel export failed for {Path}", dlg.FileName);
            MessageBox.Show($"Export failed:\n\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OnImportExcel(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Import usage history from Excel",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var mode = AskImportMode();
        if (mode is null) return;

        if (mode == ImportMode.Replace)
        {
            var confirm = MessageBox.Show(
                "This will delete all existing usage history and replace it with the imported file.\n\nContinue?",
                "Confirm replace", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
        }

        try
        {
            var imported = ImportWithRecorderSuspended(dlg.FileName, mode.Value);
            Log.Information("Excel import complete: {N} sessions ({Mode}) ← {Path}", imported, mode, dlg.FileName);

            if (_reportForm is { IsDisposed: false })
                _reportForm.RequestReload();

            MessageBox.Show(
                $"{imported} session(s) imported ({mode}).",
                "Import complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel import failed for {Path}", dlg.FileName);
            MessageBox.Show($"Import failed:\n\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    int ImportWithRecorderSuspended(string path, ImportMode mode)
    {
        // Import can replace the row currently owned by SessionRecorder. Close it first so the
        // recorder never retains an ID that the transaction deleted, then immediately observe
        // foreground state again after success or rollback. A lock/suspend pause remains intact.
        var pausedForImport = false;
        try
        {
            if (!_recorder.IsPaused)
            {
                _recorder.Pause(_clock.UtcNow);
                pausedForImport = true;
            }

            return ExcelStorage.Import(_store, path, mode);
        }
        finally
        {
            if (pausedForImport)
            {
                _recorder.Resume();
                OnTick(this, EventArgs.Empty);
            }
        }
    }

    static ImportMode? AskImportMode()
    {
        // Append / Replace / Cancel — 3-button MessageBox로 단순 처리.
        // YesNoCancel: Yes=Append (기본), No=Replace, Cancel=Cancel
        var result = MessageBox.Show(
            "Import mode:\n\n" +
            "  Yes — Append to existing data\n" +
            "  No — Replace all existing data\n" +
            "  Cancel — Abort",
            "Choose import mode",
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        return result switch
        {
            DialogResult.Yes => ImportMode.Append,
            DialogResult.No => ImportMode.Replace,
            _ => null,
        };
    }

    void OnIdleSettings(object? sender, EventArgs e)
    {
        var currentSec = (int)_idleDetector.Threshold.TotalSeconds;
        // 초→분 변환: 정수 분 표시. ceil로 60초 미만이어도 최소 1분 보이게.
        var currentMinutes = Math.Max(1, (currentSec + 59) / 60);

        using var dlg = new IdleSettingsForm(currentMinutes);
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var newSec = dlg.Minutes * 60;
        try
        {
            _store.SetSetting(IdleThresholdSettingKey, newSec.ToString());
            _idleDetector.SetThreshold(TimeSpan.FromSeconds(newSec));
            Log.Information("Idle threshold updated to {Min} minutes", dlg.Minutes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed updating idle threshold");
            MessageBox.Show($"Failed to save idle threshold:\n\n{ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OnApplicationRules(object? sender, EventArgs e)
    {
        using var dialog = new ApplicationRulesForm(_store);
        dialog.ShowDialog();
        if (_reportForm is { IsDisposed: false })
            _reportForm.RequestReload();
    }

    void OnAutostartToggled(object? sender, EventArgs e)
    {
        if (_autostartMenuItem is null) return;
        var ok = _autostartMenuItem.Checked
            ? RunKeyRegistrar.Register(ExePath)
            : RunKeyRegistrar.Unregister();
        if (!ok)
        {
            // 실패 시 체크 상태를 실제 레지스트리 상태와 동기화
            _autostartMenuItem.CheckedChanged -= OnAutostartToggled;
            _autostartMenuItem.Checked = RunKeyRegistrar.IsRegistered();
            _autostartMenuItem.CheckedChanged += OnAutostartToggled;
        }
    }

    async void OnCheckForUpdates(object? sender, EventArgs e) =>
        await CheckForUpdatesAsync(manual: true);

    async void OnAutomaticUpdateTick(object? sender, EventArgs e)
    {
        _automaticUpdateTimer.Stop();
        await CheckForUpdatesAsync(manual: false);
    }

    async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateCheckInProgress)
        {
            if (manual)
                MessageBox.Show("An update check is already in progress.", "PcUsageTracker update",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var now = _clock.UtcNow;
        if (!manual && !UpdateCoordinator.IsAutomaticCheckDue(
                _store.GetSetting(LastUpdateCheckSettingKey), now))
        {
            ScheduleAutomaticUpdateCheck(initialDelay: false);
            return;
        }

        _updateCheckInProgress = true;
        _automaticUpdateTimer.Stop();
        if (_checkForUpdatesMenuItem is not null)
        {
            _checkForUpdatesMenuItem.Enabled = false;
            _checkForUpdatesMenuItem.Text = "Checking for updates...";
        }
        try
        {
            // Persist attempts, including failures, so an offline machine is not contacted every minute.
            _store.SetSetting(
                LastUpdateCheckSettingKey,
                UpdateCoordinator.FormatStoredCheckTime(now));

            var result = await _updateCoordinator.CheckAsync(_updateLifetime.Token);
            if (!result.IsUpdateAvailable)
            {
                if (manual)
                    MessageBox.Show(
                        $"PcUsageTracker {result.CurrentVersion} is up to date.",
                        "PcUsageTracker update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                return;
            }

            var release = result.LatestRelease;
            var targetValidation = _updateApplier.ValidateCurrentTarget(ExePath, Environment.ProcessId);
            if (!targetValidation.IsValid)
            {
                Log.Warning("Self-update target rejected: {Error}", targetValidation.Error);
                if (manual)
                    MessageBox.Show(
                        $"This copy cannot update itself safely. Install the release executable manually.\n\n{targetValidation.Error}",
                        "PcUsageTracker update",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                return;
            }

            var answer = MessageBox.Show(
                $"PcUsageTracker {release.Version} is available (current: {result.CurrentVersion}).\n\n" +
                "Download, verify, and install it now? The app will restart automatically.",
                "PcUsageTracker update",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (_checkForUpdatesMenuItem is not null)
                _checkForUpdatesMenuItem.Text = "Downloading update...";
            var prepared = await _updateCoordinator.DownloadAsync(release, _updateLifetime.Token);
            var markerPath = Path.Combine(
                _updateCoordinator.StagingDirectory,
                $"ready-{Guid.NewGuid():N}.ready");
            var acceptanceMarkerPath = Path.Combine(
                _updateCoordinator.StagingDirectory,
                $"accept-{Guid.NewGuid():N}.accept");
            var launch = await _updateApplier.LaunchHelperAndWaitForAcceptanceAsync(
                prepared,
                ExePath,
                Environment.ProcessId,
                _updateCoordinator.StagingDirectory,
                acceptanceMarkerPath,
                markerPath,
                _updateLifetime.Token);
            if (!launch.Started || !launch.Accepted)
            {
                MessageBox.Show(
                    $"The update helper did not accept the installation. The current app will keep running.\n\n{launch.Error}",
                    "PcUsageTracker update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return;
            }

            _exitingForUpdate = true;
            Log.Information("Verified update {Version} staged; helper started (elevated={Elevated})",
                prepared.Version, launch.Elevated);
            ExitThread();
        }
        catch (OperationCanceledException)
        {
            if (manual && !_updateLifetime.IsCancellationRequested)
                MessageBox.Show("The update was canceled.", "PcUsageTracker update",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Kind} update check failed", manual ? "Manual" : "Automatic");
            if (manual)
                MessageBox.Show(
                    $"The update check failed.\n\n{ex.Message}",
                    "PcUsageTracker update",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (_checkForUpdatesMenuItem is not null)
            {
                _checkForUpdatesMenuItem.Text = "Check for updates...";
                _checkForUpdatesMenuItem.Enabled = true;
            }
            if (!_exitingForUpdate && !_updateLifetime.IsCancellationRequested)
                ScheduleAutomaticUpdateCheck(initialDelay: false);
        }
    }

    void ScheduleAutomaticUpdateCheck(bool initialDelay)
    {
        if (_updateLifetime.IsCancellationRequested) return;
        var now = _clock.UtcNow;
        var delay = UpdateCoordinator.DelayUntilAutomaticCheck(
            _store.GetSetting(LastUpdateCheckSettingKey), now);
        if (initialDelay && delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(30);
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);
        _automaticUpdateTimer.Stop();
        _automaticUpdateTimer.Interval = (int)Math.Clamp(delay.TotalMilliseconds, 1_000, int.MaxValue);
        _automaticUpdateTimer.Start();
    }

    /// <summary>excluded_processes 테이블을 in-memory 캐시로 다시 로드. UI에서 exclusion 변경 시 호출.</summary>
    public void RefreshExclusions()
    {
        _excluded.Clear();
        foreach (var name in _store.ListExclusions()) _excluded.Add(name);
    }

    void OnTick(object? sender, EventArgs e)
    {
        try
        {
            var snap = _probe.Sample();
            var now = _clock.UtcNow;

            // Lock/Suspend Pause()가 우선. Pause 상태에서는 Tick이 no-op이므로 idle 검사 무의미.
            bool isIdle = false;
            if (!_recorder.IsPaused)
            {
                isIdle = _idleDetector.Update(_idleProbe.IdleDuration);
                if (isIdle != _lastIdleState)
                {
                    Log.Information(isIdle
                        ? "User idle detected — pausing tracking"
                        : "User returned — resuming tracking");
                    _lastIdleState = isIdle;
                }
            }
            else if (_lastIdleState)
            {
                // Pause 진입 시 idle 상태도 리셋해서 다음 unlock 후 첫 tick에서 정상 전이 로그가 나오게 한다.
                _lastIdleState = false;
            }

            var sampleName = snap?.ProcessName;
            var effectiveName = sampleName is not null && _excluded.Contains(sampleName) ? null : sampleName;
            if (isIdle) effectiveName = IdleSentinel.Name;
            _recorder.Tick(effectiveName, now);

            if (effectiveName is not null
                && !string.Equals(effectiveName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase)
                && snap is { ExePath: { } path }
                && !string.IsNullOrEmpty(path))
            {
                try { _store.UpsertProcessPath(effectiveName, path, now); }
                catch (Exception ex) { Log.Debug(ex, "UpsertProcessPath failed for {Name}", effectiveName); }
            }

            _notifyIcon.Text = Truncate(
                _recorder.IsPaused ? "PcUsageTracker (paused)"
                : isIdle ? "PcUsageTracker (idle)"
                : $"Tracking: {_recorder.CurrentProcessName ?? "-"}",
                63);

            bool wantsPausedIcon = _recorder.IsPaused || isIdle;
            if (wantsPausedIcon != _lastPausedState)
            {
                _notifyIcon.Icon = wantsPausedIcon ? _iconPaused.Icon : _iconActive.Icon;
                _lastPausedState = wantsPausedIcon;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tick failed");
        }
    }

    int LoadIdleThresholdSec()
    {
        var raw = _store.GetSetting(IdleThresholdSettingKey);
        if (int.TryParse(raw, out var sec) && sec > 0) return sec;
        return DefaultIdleThresholdSec;
    }

    void OnLocked()
    {
        Log.Information("Session locked — pausing recorder");
        _recorder.Pause(_clock.UtcNow);
    }

    void OnUnlocked()
    {
        Log.Information("Session unlocked — resuming recorder");
        _recorder.Resume();
    }

    void OnSuspending()
    {
        Log.Information("System suspending — pausing recorder");
        _recorder.Pause(_clock.UtcNow);
    }

    void OnResuming()
    {
        Log.Information("System resuming — resuming recorder");
        _recorder.Resume();
    }

    void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            OnOpenReport(sender, EventArgs.Empty);
    }

    void OnOpenReport(object? sender, EventArgs e)
    {
        if (_reportForm is null || _reportForm.IsDisposed)
            _reportForm = new ReportForm(_clock, _store, RefreshExclusions);
        _reportForm.ToggleVisible();
    }

    void OnOpenDataFolder(object? sender, EventArgs e)
    {
        var folder = GetDataFolder();
        Directory.CreateDirectory(folder);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    void OnQuit(object? sender, EventArgs e)
    {
        Log.Information("Quit requested from tray menu");
        _updateLifetime.Cancel();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _automaticUpdateTimer.Stop();
            _automaticUpdateTimer.Dispose();
            _updateLifetime.Cancel();
            // 종료 직전 현재 세션을 닫는다 — 다음 실행 시 orphan recovery로 대체되지만 선제 정리.
            _recorder.Pause(_clock.UtcNow);
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _iconActive.Dispose();
            _iconPaused.Dispose();
            _updateHttpClient.Dispose();
            _updateLifetime.Dispose();
            _reportForm?.Dispose();
            _events.Dispose();
            _store.Dispose();
        }
        base.Dispose(disposing);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
}
