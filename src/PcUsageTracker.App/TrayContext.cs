using System.Diagnostics;
using PcUsageTracker.App.Assets;
using PcUsageTracker.App.Autostart;
using PcUsageTracker.App.Interop;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;
using Serilog;

namespace PcUsageTracker.App;

internal sealed class TrayContext : ApplicationContext
{
    const int TickIntervalMs = 1000;

    readonly NotifyIcon _notifyIcon;
    readonly SqliteStore _store;
    readonly SessionRecorder _recorder;
    readonly IForegroundProbe _probe;
    readonly IClock _clock;
    readonly System.Windows.Forms.Timer _timer;
    readonly SessionEventsWindow _events;
    readonly Aggregator _aggregator;
    readonly OwnedIcon _iconActive;
    readonly OwnedIcon _iconPaused;
    bool _lastPausedState;
    ReportForm? _reportForm;
    ToolStripMenuItem? _autostartMenuItem;

    public TrayContext(bool startedFromLogin)
    {
        _clock = new SystemClock();
        _probe = new Win32ForegroundProbe();

        var dataDir = GetDataFolder();
        Directory.CreateDirectory(dataDir);
        var dbPath = Path.Combine(dataDir, "history.db");
        _store = new SqliteStore(dbPath);

        var recovered = _store.RecoverOrphanedSessions(_clock.UtcNow);
        if (recovered > 0) Log.Information("Recovered {N} orphaned session(s) on startup", recovered);

        _recorder = new SessionRecorder(_store);
        _aggregator = new Aggregator(_store.Connection);

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

        EnsureAutostartFirstRun();

        Log.Information("Tray icon shown (startedFromLogin={FromLogin}, db={Db})", startedFromLogin, dbPath);
    }

    static string ExePath => Process.GetCurrentProcess().MainModule?.FileName
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

        _autostartMenuItem = new ToolStripMenuItem("Autostart on Windows login")
        {
            CheckOnClick = true,
            Checked = RunKeyRegistrar.IsRegistered(),
        };
        _autostartMenuItem.CheckedChanged += OnAutostartToggled;
        menu.Items.Add(_autostartMenuItem);

        menu.Items.Add("Open data folder", image: null, OnOpenDataFolder);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", image: null, OnQuit);
        return menu;
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

    void OnTick(object? sender, EventArgs e)
    {
        try
        {
            var snap = _probe.Sample();
            var now = _clock.UtcNow;
            _recorder.Tick(snap?.ProcessName, now);

            if (snap is { ProcessName: var name, ExePath: { } path } && !string.IsNullOrEmpty(path))
            {
                try { _store.UpsertProcessPath(name, path, now); }
                catch (Exception ex) { Log.Debug(ex, "UpsertProcessPath failed for {Name}", name); }
            }

            _notifyIcon.Text = Truncate(
                _recorder.IsPaused ? "PcUsageTracker (paused)" : $"Tracking: {_recorder.CurrentProcessName ?? "-"}",
                63);

            if (_recorder.IsPaused != _lastPausedState)
            {
                _notifyIcon.Icon = _recorder.IsPaused ? _iconPaused.Icon : _iconActive.Icon;
                _lastPausedState = _recorder.IsPaused;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Tick failed");
        }
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
            _reportForm = new ReportForm(_aggregator, _clock);
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
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            // 종료 직전 현재 세션을 닫는다 — 다음 실행 시 orphan recovery로 대체되지만 선제 정리.
            _recorder.Pause(_clock.UtcNow);
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _iconActive.Dispose();
            _iconPaused.Dispose();
            _reportForm?.Dispose();
            _events.Dispose();
            _store.Dispose();
        }
        base.Dispose(disposing);
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max);
}
