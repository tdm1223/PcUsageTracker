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
    const string LanguageSettingKey = "ui_language";

    readonly NotifyIcon _notifyIcon;
    readonly SqliteStore _store;
    readonly SessionRecorder _recorder;
    readonly IForegroundProbe _probe;
    readonly IIdleProbe _idleProbe;
    readonly IdleDetector _idleDetector;
    readonly IClock _clock;
    readonly System.Windows.Forms.Timer _timer;
    readonly System.Windows.Forms.Timer _automaticUpdateTimer;
    readonly System.Windows.Forms.Timer _automaticBackupTimer;
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
    bool _disposed;
    bool _backupInProgress;
    string? _lastBackupError;
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
        LoadLanguage();
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

        _automaticBackupTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _automaticBackupTimer.Tick += OnAutomaticBackupTick;
        _automaticBackupTimer.Start();

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
        menu.Items.Add("Back up all data...", image: null, OnBackup);
        menu.Items.Add("Restore full backup...", image: null, OnRestore);
        menu.Items.Add(BuildAutomaticBackupMenu());
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Idle threshold...", image: null, OnIdleSettings);
        menu.Items.Add("Applications && categories...", image: null, OnApplicationRules);
        menu.Items.Add(UiText.T("Excluded applications...", "추적 제외 목록..."), image: null, (_, _) =>
        {
            using var dialog = new ExcludedApplicationsForm(_store, RefreshExclusions);
            dialog.ShowDialog();
        });
        menu.Items.Add(UiText.T("Application groups...", "프로세스 그룹..."), image: null, (_, _) =>
        {
            using var dialog = new ApplicationGroupsForm(_store);
            dialog.ShowDialog();
            if (_reportForm is { IsDisposed: false }) _reportForm.RequestReload();
        });
        menu.Items.Add(new ToolStripSeparator());

        _autostartMenuItem = new ToolStripMenuItem("Autostart on Windows login")
        {
            CheckOnClick = true,
            Checked = RunKeyRegistrar.IsRegistered(),
        };
        _autostartMenuItem.CheckedChanged += OnAutostartToggled;
        menu.Items.Add(_autostartMenuItem);

        _checkForUpdatesMenuItem = new ToolStripMenuItem("Check for updates...");
        _checkForUpdatesMenuItem.Enabled = !_updateCheckInProgress;
        _checkForUpdatesMenuItem.Click += OnCheckForUpdates;
        menu.Items.Add(_checkForUpdatesMenuItem);
        menu.Items.Add("Open data folder", image: null, OnOpenDataFolder);
        menu.Items.Add(BuildLanguageMenu());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", image: null, OnQuit);
        UiText.ApplyMenu(menu.Items);
        return menu;
    }

    void LoadLanguage()
    {
        var saved = _store.GetSetting(LanguageSettingKey);
        UiText.Korean = saved is null
            ? System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko"
            : string.Equals(saved, "ko", StringComparison.OrdinalIgnoreCase);
    }

    ToolStripMenuItem BuildLanguageMenu()
    {
        var menu = new ToolStripMenuItem(UiText.T("Language", "언어"));
        foreach (var (code, label) in new[] { ("ko", "한국어"), ("en", "English") })
        {
            var item = new ToolStripMenuItem(label) { Checked = UiText.Korean == (code == "ko") };
            item.Click += (_, _) =>
            {
                try
                {
                    _store.SetSetting(LanguageSettingKey, code);
                    UiText.Korean = code == "ko";
                    var context = SynchronizationContext.Current;
                    if (context is null) RebuildLanguageUi();
                    else context.Post(_ => { if (!_disposed) RebuildLanguageUi(); }, null);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, UiText.T("Language", "언어"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            menu.DropDownItems.Add(item);
        }
        return menu;
    }

    void RebuildLanguageUi()
    {
        var previousMenu = _notifyIcon.ContextMenuStrip;
        _notifyIcon.ContextMenuStrip = BuildMenu();
        previousMenu?.Dispose();
        if (_reportForm is null || _reportForm.IsDisposed) return;
        var view = _reportForm.CaptureView();
        var wasVisible = _reportForm.Visible;
        _reportForm.Dispose();
        _reportForm = new ReportForm(_clock, _store, RefreshExclusions);
        _reportForm.RestoreView(view.Day, view.TabIndex);
        if (wasVisible) _reportForm.ToggleVisible();
    }

    ToolStripMenuItem BuildAutomaticBackupMenu()
    {
        var menu = new ToolStripMenuItem(UiText.T("Automatic backup", "자동 백업"));
        var current = AutomaticBackup.ParseFrequency(_store.GetSetting(AutomaticBackup.FrequencyKey));
        foreach (var frequency in Enum.GetValues<BackupFrequency>())
        {
            var label = frequency switch
            {
                BackupFrequency.Off => UiText.T("Off", "사용 안 함"),
                BackupFrequency.Daily => UiText.T("Daily", "매일"),
                BackupFrequency.Weekly => UiText.T("Weekly", "매주"),
                _ => UiText.T("Monthly", "매월"),
            };
            var item = new ToolStripMenuItem(label) { Checked = frequency == current, Tag = frequency };
            item.Click += (_, _) =>
            {
                try
                {
                    _store.SetSetting(AutomaticBackup.FrequencyKey, frequency.ToString());
                    foreach (var choice in menu.DropDownItems.OfType<ToolStripMenuItem>())
                        if (choice.Tag is BackupFrequency selected) choice.Checked = selected == frequency;
                    _automaticBackupTimer.Interval = 1000;
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Could not set automatic backup frequency");
                    MessageBox.Show(ex.Message, UiText.T("Automatic backup", "자동 백업"), MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            };
            menu.DropDownItems.Add(item);
        }
        menu.DropDownItems.Add(new ToolStripSeparator());
        var status = new ToolStripMenuItem { Enabled = false };
        menu.DropDownItems.Add(status);
        menu.DropDownOpening += (_, _) =>
        {
            var last = AutomaticBackup.ParseLastSuccess(_store.GetSetting(AutomaticBackup.LastSuccessKey));
            status.Text = _backupInProgress ? UiText.T("Backup in progress...", "백업 진행 중...") :
                _lastBackupError is not null ? UiText.T("Last backup failed; see logs", "마지막 백업 실패 · 로그 확인") :
                last is null ? UiText.T("No automatic backup yet", "자동 백업 기록 없음") :
                $"{UiText.T("Last backup", "최근 백업")}: {last.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
        };
        menu.DropDownItems.Add(UiText.T("Open backup folder", "백업 폴더 열기"), null, (_, _) =>
        {
            var directory = Path.Combine(GetDataFolder(), "backups");
            Directory.CreateDirectory(directory);
            Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
        });
        return menu;
    }

    async void OnAutomaticBackupTick(object? sender, EventArgs e)
    {
        _automaticBackupTimer.Interval = 60 * 60 * 1000;
        if (_backupInProgress || _disposed) return;
        try
        {
            var frequency = AutomaticBackup.ParseFrequency(_store.GetSetting(AutomaticBackup.FrequencyKey));
            var now = _clock.UtcNow;
            if (!AutomaticBackup.IsDue(frequency, AutomaticBackup.ParseLastSuccess(_store.GetSetting(AutomaticBackup.LastSuccessKey)), now)) return;
            _backupInProgress = true;
            var file = await Task.Run(() =>
            {
                using var connection = _store.OpenReadOnlyConnection();
                return AutomaticBackup.Create(connection, Path.Combine(GetDataFolder(), "icons"),
                    Path.Combine(GetDataFolder(), "backups"), now);
            });
            if (!_disposed) _store.SetSetting(AutomaticBackup.LastSuccessKey, now.ToString("O"));
            _lastBackupError = null;
            Log.Information("Automatic backup saved to {Path}", file);
        }
        catch (Exception ex) { _lastBackupError = ex.Message; Log.Warning(ex, "Automatic backup failed; will retry later"); }
        finally { _backupInProgress = false; }
    }

    void OnExportExcel(object? sender, EventArgs e)
    {
        using var dlg = new SaveFileDialog
        {
            Title = UiText.T("Export usage history to Excel", "사용 기록을 엑셀로 내보내기"),
            Filter = UiText.T("Excel workbook (*.xlsx)|*.xlsx", "엑셀 파일 (*.xlsx)|*.xlsx"),
            FileName = $"pc-usage-{DateTimeOffset.Now:yyyyMMdd}.xlsx",
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        try
        {
            var written = ExcelStorage.Export(_store, dlg.FileName);
            Log.Information("Excel export complete: {N} sessions → {Path}", written, dlg.FileName);
            MessageBox.Show(
                UiText.T($"{written} session(s) exported.\n\n{dlg.FileName}", $"사용 기록 {written}개를 내보냈습니다.\n\n{dlg.FileName}"),
                UiText.T("Export complete", "내보내기 완료"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel export failed for {Path}", dlg.FileName);
            MessageBox.Show(UiText.T($"Export failed:\n\n{ex.Message}", $"내보내기에 실패했습니다:\n\n{ex.Message}"), UiText.T("Error", "오류"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OnBackup(object? sender, EventArgs e)
    {
        using var dialog = new SaveFileDialog
        {
            Title = UiText.T("Back up all data", "전체 데이터 백업"), Filter = UiText.T("PcUsageTracker backup (*.pcutbackup)|*.pcutbackup", "PcUsageTracker 백업 (*.pcutbackup)|*.pcutbackup"),
            FileName = $"pc-usage-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.pcutbackup",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        try
        {
            FullBackup.Export(_store, Path.Combine(GetDataFolder(), "icons"), dialog.FileName);
            MessageBox.Show(UiText.T($"Backup saved:\n\n{dialog.FileName}", $"백업을 저장했습니다:\n\n{dialog.FileName}"), UiText.T("Backup complete", "백업 완료"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Full backup failed");
            MessageBox.Show(UiText.T($"Backup failed:\n\n{ex.Message}", $"백업에 실패했습니다:\n\n{ex.Message}"), UiText.T("Backup error", "백업 오류"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    void OnRestore(object? sender, EventArgs e)
    {
        using var dialog = new OpenFileDialog
        {
            Title = UiText.T("Restore full backup", "전체 백업 복원"), Filter = UiText.T("PcUsageTracker backup (*.pcutbackup)|*.pcutbackup", "PcUsageTracker 백업 (*.pcutbackup)|*.pcutbackup"),
            CheckFileExists = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        if (MessageBox.Show(UiText.T("Restore this backup? Current history, categories, exclusions, and settings will be replaced. " +
                            "A backup of the current data will be saved first.",
                            "이 백업을 복원하시겠습니까? 현재 사용 기록, 분류, 제외 목록 및 설정이 교체됩니다. 기존 데이터는 먼저 백업됩니다."), UiText.T("Confirm restore", "복원 확인"),
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var paused = false;
        string? success = null;
        Exception? failure = null;
        try
        {
            _reportForm?.Dispose();
            _reportForm = null;
            if (!_recorder.IsPaused)
            {
                _recorder.Pause(_clock.UtcNow);
                paused = true;
            }
            var iconDir = Path.Combine(GetDataFolder(), "icons");
            var safetyDir = Path.Combine(GetDataFolder(), "backups");
            var safetyPath = Path.Combine(safetyDir, $"before-restore-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pcutbackup");
            FullBackup.Export(_store, iconDir, safetyPath);
            var iconFailures = FullBackup.Restore(_store, iconDir, dialog.FileName, _clock.UtcNow);
            RefreshExclusions();
            _idleDetector.SetThreshold(TimeSpan.FromSeconds(LoadIdleThresholdSec()));
            LoadLanguage();
            RebuildLanguageUi();
            _automaticBackupTimer.Interval = 1000;
            var iconNote = iconFailures == 0 ? "" : UiText.T($"\n{iconFailures} cached icons could not be restored.", $"\n캐시된 아이콘 {iconFailures}개를 복원하지 못했습니다.");
            success = UiText.T($"Data restored. Previous data was backed up to:\n{safetyPath}{iconNote}", $"데이터를 복원했습니다. 이전 데이터 백업:\n{safetyPath}{iconNote}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Full restore failed");
            failure = ex;
        }
        finally
        {
            if (paused)
            {
                _recorder.Resume();
                OnTick(this, EventArgs.Empty);
            }
        }
        if (failure is not null)
            MessageBox.Show(UiText.T($"Restore failed:\n\n{failure.Message}", $"복원에 실패했습니다:\n\n{failure.Message}"), UiText.T("Restore error", "복원 오류"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        else
            MessageBox.Show(success!, UiText.T("Restore complete", "복원 완료"));
    }

    void OnImportExcel(object? sender, EventArgs e)
    {
        using var dlg = new OpenFileDialog
        {
            Title = UiText.T("Import usage history from Excel", "엑셀에서 사용 기록 가져오기"),
            Filter = UiText.T("Excel workbook (*.xlsx)|*.xlsx", "엑셀 파일 (*.xlsx)|*.xlsx"),
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != DialogResult.OK) return;

        var mode = AskImportMode();
        if (mode is null) return;

        if (mode == ImportMode.Replace)
        {
            var confirm = MessageBox.Show(
                UiText.T("This will delete all existing usage history and replace it with the imported file.\n\nContinue?", "기존 사용 기록을 모두 삭제하고 가져온 파일로 교체합니다.\n\n계속하시겠습니까?"),
                UiText.T("Confirm replace", "기록 교체 확인"), MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (confirm != DialogResult.Yes) return;
        }

        try
        {
            var imported = ImportWithRecorderSuspended(dlg.FileName, mode.Value);
            Log.Information("Excel import complete: {N} sessions ({Mode}) ← {Path}", imported, mode, dlg.FileName);

            if (_reportForm is { IsDisposed: false })
                _reportForm.RequestReload();

            MessageBox.Show(
                UiText.T($"{imported} session(s) imported ({mode}).", $"사용 기록 {imported}개를 가져왔습니다 ({(mode == ImportMode.Append ? "추가" : "교체")})."),
                UiText.T("Import complete", "가져오기 완료"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Excel import failed for {Path}", dlg.FileName);
            MessageBox.Show(UiText.T($"Import failed:\n\n{ex.Message}", $"가져오기에 실패했습니다:\n\n{ex.Message}"), UiText.T("Error", "오류"),
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
            UiText.T("Import mode:\n\n" +
            "  Yes — Append to existing data\n" +
            "  No — Replace all existing data\n" +
            "  Cancel — Abort", "가져오기 방식:\n\n  예 — 기존 기록에 추가\n  아니요 — 기존 기록 교체\n  취소 — 중단"),
            UiText.T("Choose import mode", "가져오기 방식 선택"),
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
            MessageBox.Show(UiText.T($"Failed to save idle threshold:\n\n{ex.Message}", $"자리 비움 기준을 저장하지 못했습니다:\n\n{ex.Message}"), UiText.T("Error", "오류"),
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
                MessageBox.Show(UiText.T("An update check is already in progress.", "업데이트를 이미 확인하고 있습니다."), UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
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
            _checkForUpdatesMenuItem.Text = UiText.Translate("Checking for updates...");
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
                        UiText.T($"PcUsageTracker {result.CurrentVersion} is up to date.", $"PcUsageTracker {result.CurrentVersion}은 최신 버전입니다."),
                        UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
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
                        UiText.T($"This copy cannot update itself safely. Install the release executable manually.\n\n{targetValidation.Error}",
                            $"이 실행 파일을 자동으로 안전하게 업데이트할 수 없습니다. 새 실행 파일을 직접 설치하세요.\n\n{targetValidation.Error}"),
                        UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                return;
            }

            var answer = MessageBox.Show(
                UiText.T($"PcUsageTracker {release.Version} is available (current: {result.CurrentVersion}).\n\n" +
                    "Download, verify, and install it now? The app will restart automatically.",
                    $"PcUsageTracker {release.Version} 업데이트가 있습니다 (현재: {result.CurrentVersion}).\n\n지금 다운로드하고 검증한 뒤 설치하시겠습니까? 앱이 자동으로 다시 시작됩니다."),
                UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information,
                MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;

            if (_checkForUpdatesMenuItem is not null)
                _checkForUpdatesMenuItem.Text = UiText.Translate("Downloading update...");
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
                    UiText.T($"The update helper did not accept the installation. The current app will keep running.\n\n{launch.Error}",
                        $"업데이트 설치를 시작하지 못했습니다. 현재 앱은 계속 실행됩니다.\n\n{launch.Error}"),
                    UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
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
                MessageBox.Show(UiText.T("The update was canceled.", "업데이트가 취소되었습니다."), UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "{Kind} update check failed", manual ? "Manual" : "Automatic");
            if (manual)
                MessageBox.Show(
                    UiText.T($"The update check failed.\n\n{ex.Message}", $"업데이트 확인에 실패했습니다.\n\n{ex.Message}"),
                    UiText.T("PcUsageTracker update", "PcUsageTracker 업데이트"),
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
        }
        finally
        {
            _updateCheckInProgress = false;
            if (_checkForUpdatesMenuItem is not null)
            {
                _checkForUpdatesMenuItem.Text = UiText.Translate("Check for updates...");
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
                _recorder.IsPaused ? UiText.T("PcUsageTracker (paused)", "PcUsageTracker (일시정지)")
                : isIdle ? UiText.T("PcUsageTracker (idle)", "PcUsageTracker (자리 비움)")
                : $"{UiText.T("Tracking", "추적 중")}: {_recorder.CurrentProcessName ?? "-"}",
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
            if (_disposed) return;
            _disposed = true;
            _timer.Stop();
            _timer.Dispose();
            _automaticUpdateTimer.Stop();
            _automaticUpdateTimer.Dispose();
            _automaticBackupTimer.Stop();
            _automaticBackupTimer.Dispose();
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
