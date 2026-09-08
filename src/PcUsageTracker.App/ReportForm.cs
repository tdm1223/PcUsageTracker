using System.Runtime.InteropServices;
using Microsoft.Win32;
using PcUsageTracker.App.Interop;
using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;
using Serilog;

namespace PcUsageTracker.App;

internal sealed class ReportForm : Form
{
    const int RefreshMs = 5000;
    const int IconColWidth = 24;
    const string IdleDisplayName = "(Idle)";

    readonly Aggregator _agg;
    readonly IClock _clock;
    readonly SqliteStore _store;
    readonly Action _onExclusionChanged;
    readonly TabControl _tabs;
    readonly DataGridView _todayGrid;
    readonly DataGridView _weekGrid;
    readonly DataGridView _monthGrid;
    readonly DataGridView _allGrid;
    readonly System.Windows.Forms.Timer _refresh;
    readonly IconCache _iconCache = new();
    readonly Font _idleFont;
    bool _isResizing;

    public ReportForm(Aggregator agg, IClock clock, SqliteStore store, Action onExclusionChanged)
    {
        _agg = agg;
        _clock = clock;
        _store = store;
        _onExclusionChanged = onExclusionChanged;

        Text = "PcUsageTracker — Report";
        Size = new Size(880, 520);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 400);
        DoubleBuffered = true;
        _idleFont = new Font(Font, FontStyle.Italic);

        _tabs = new TabControl { Dock = DockStyle.Fill };

        var tab1 = new TabPage("Today + Week + Month");
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));

        _todayGrid = BuildGrid(this);
        _weekGrid = BuildGrid(this);
        _monthGrid = BuildGrid(this);
        split.Controls.Add(WrapWithHeader("Today", _todayGrid), 0, 0);
        split.Controls.Add(WrapWithHeader("This week", _weekGrid), 1, 0);
        split.Controls.Add(WrapWithHeader("This month", _monthGrid), 2, 0);
        tab1.Controls.Add(split);

        var tab2 = new TabPage("All-time");
        _allGrid = BuildGrid(this);
        tab2.Controls.Add(WrapWithHeader("All-time", _allGrid));

        _tabs.TabPages.Add(tab1);
        _tabs.TabPages.Add(tab2);
        Controls.Add(_tabs);

        _refresh = new System.Windows.Forms.Timer { Interval = RefreshMs };
        _refresh.Tick += (_, _) => ReloadVisibleTab();
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (Visible && !_isResizing) ReloadVisibleTab();
        };
        ResizeBegin += (_, _) =>
        {
            _isResizing = true;
            _refresh.Stop();
        };
        ResizeEnd += (_, _) =>
        {
            _isResizing = false;
            if (!Visible) return;
            ReloadVisibleTab();
            _refresh.Start();
        };
        Shown += (_, _) => { ReloadVisibleTab(); _refresh.Start(); };
        FormClosing += (s, e) =>
        {
            // 트레이 상주 앱 — 닫기 버튼은 숨기기로만
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _refresh.Stop();
            }
        };
    }

    static DataGridView BuildGrid(ReportForm owner)
    {
        var g = new BufferedDataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            RowTemplate = { Height = 22 },
        };
        var iconCol = new DataGridViewImageColumn
        {
            Name = "Icon",
            HeaderText = "",
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            DefaultCellStyle = new DataGridViewCellStyle { NullValue = owner._iconCache.EmptyImage },
            AutoSizeMode = DataGridViewAutoSizeColumnMode.None,
            Width = IconColWidth,
            Resizable = DataGridViewTriState.False,
        };
        g.Columns.Add(iconCol);
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Process",
            HeaderText = "Process",
            FillWeight = 40,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Duration",
            HeaderText = "Duration",
            FillWeight = 25,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Share",
            HeaderText = "Share",
            FillWeight = 20,
            DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight },
        });

        // 우클릭 → 행 선택 + 컨텍스트 메뉴
        var menu = new ContextMenuStrip();
        var deleteItem = new ToolStripMenuItem("Delete history && stop tracking");
        deleteItem.Click += (_, _) =>
        {
            if (g.SelectedRows.Count == 0) return;
            var name = g.SelectedRows[0].Cells["Process"].Value as string;
            if (string.IsNullOrEmpty(name)) return;
            owner.OnDeleteAndExclude(name);
        };
        menu.Items.Add(deleteItem);
        g.ContextMenuStrip = menu;
        g.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right || e.RowIndex < 0) return;
            g.ClearSelection();
            g.Rows[e.RowIndex].Selected = true;
        };
        g.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0) return;
            owner.OpenContainingFolder(g.Rows[e.RowIndex]);
        };
        return g;
    }

    static Control WrapWithHeader(string header, Control inner)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var label = new Label
        {
            Text = header,
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        panel.Controls.Add(label, 0, 0);
        panel.Controls.Add(inner, 0, 1);
        return panel;
    }

    void ReloadVisibleTab()
    {
        var now = _clock.UtcNow;
        if (_tabs.SelectedIndex == 1)
        {
            Populate(_allGrid, _agg.AllTime(now));
            return;
        }

        var (todayFrom, todayTo) = Aggregator.TodayRange(now);
        var (weekFrom, weekTo) = Aggregator.ThisWeekRange(now);
        var (monthFrom, monthTo) = Aggregator.ThisMonthRange(now);

        Populate(_todayGrid, _agg.TopN(todayFrom, todayTo, now));
        Populate(_weekGrid, _agg.TopN(weekFrom, weekTo, now));
        Populate(_monthGrid, _agg.TopN(monthFrom, monthTo, now));
    }

    public void RequestReload() => ReloadVisibleTab();

    void Populate(DataGridView grid, IReadOnlyList<ReportEntry> entries)
    {
        var rebuildRows = !RowsMatchEntries(grid, entries);
        var selectedProcesses = rebuildRows
            ? grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(GetProcessName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.Ordinal)
            : null;
        var firstDisplayedProcess = rebuildRows && grid.FirstDisplayedScrollingRowIndex >= 0
            ? GetProcessName(grid.Rows[grid.FirstDisplayedScrollingRowIndex])
            : null;

        grid.SuspendLayout();
        try
        {
            long total = entries.Sum(r => (long)r.TotalSeconds);
            if (rebuildRows)
            {
                grid.Rows.Clear();
                foreach (var entry in entries)
                    grid.Rows.Add();
            }

            for (var index = 0; index < entries.Count; index++)
                UpdateRow(grid.Rows[index], entries[index], total);

            if (!rebuildRows) return;

            grid.ClearSelection();
            foreach (DataGridViewRow row in grid.Rows)
                row.Selected = selectedProcesses!.Contains(GetProcessName(row));

            if (firstDisplayedProcess is not null)
            {
                var scrollRow = grid.Rows.Cast<DataGridViewRow>()
                    .FirstOrDefault(row => string.Equals(
                        GetProcessName(row), firstDisplayedProcess, StringComparison.Ordinal));
                if (scrollRow is not null) grid.FirstDisplayedScrollingRowIndex = scrollRow.Index;
            }
        }
        finally
        {
            grid.ResumeLayout();
        }
    }

    static bool RowsMatchEntries(DataGridView grid, IReadOnlyList<ReportEntry> entries)
    {
        if (grid.Rows.Count != entries.Count) return false;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!string.Equals(GetProcessName(grid.Rows[index]), entries[index].ProcessName,
                    StringComparison.Ordinal))
                return false;
        }
        return true;
    }

    void UpdateRow(DataGridViewRow row, ReportEntry entry, long total)
    {
        var isIdle = string.Equals(entry.ProcessName, IdleSentinel.Name, StringComparison.Ordinal);
        var iconLookup = isIdle ? default : _iconCache.Get(entry.ProcessName, entry.ExePath);
        var rowEntry = iconLookup.ResolvedPath is { } resolvedPath
            ? entry with { ExePath = resolvedPath }
            : entry;
        if (!string.Equals(entry.ExePath, rowEntry.ExePath, StringComparison.OrdinalIgnoreCase))
            RepairStoredPath(entry.ProcessName, rowEntry.ExePath!);

        SetCellValue(row.Cells["Icon"], iconLookup.Image ?? _iconCache.EmptyImage);
        SetCellValue(row.Cells["Process"], isIdle ? IdleDisplayName : entry.ProcessName);
        SetCellValue(row.Cells["Duration"], FormatDuration(entry.TotalSeconds));
        var pct = total > 0 ? (double)entry.TotalSeconds / total * 100.0 : 0.0;
        SetCellValue(row.Cells["Share"], $"{pct:0.0}%");
        row.Tag = rowEntry;

        if (isIdle && row.Cells["Process"].Style.Font is null)
        {
            // '(Idle)'은 OS 프로세스가 아니라 입력 부재 시간이므로 실제 프로세스와 시각적으로 구별한다.
            row.Cells["Process"].Style = new DataGridViewCellStyle
            {
                Font = _idleFont,
                ForeColor = SystemColors.GrayText,
            };
        }

        var tooltip = isIdle ? string.Empty : rowEntry.ExePath ?? string.Empty;
        if (!string.Equals(row.Cells["Process"].ToolTipText, tooltip, StringComparison.Ordinal))
            row.Cells["Process"].ToolTipText = tooltip;
    }

    static void SetCellValue(DataGridViewCell cell, object value)
    {
        if (!Equals(cell.Value, value)) cell.Value = value;
    }

    static string? GetProcessName(DataGridViewRow row) =>
        row.Tag is ReportEntry entry ? entry.ProcessName : null;

    void RepairStoredPath(string processName, string exePath)
    {
        try
        {
            _store.UpsertProcessPath(processName, exePath, _clock.UtcNow);
            Log.Information("Repaired executable path for {Name}: {Path}", processName, exePath);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed to repair executable path for {Name}", processName);
        }
    }

    void OpenContainingFolder(DataGridViewRow row)
    {
        if (row.Tag is not ReportEntry { ExePath: { Length: > 0 } exePath }) return;

        try
        {
            var directory = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                MessageBox.Show(this, "The program folder could not be found.", "Folder unavailable",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Open containing folder failed for {Path}", exePath);
            MessageBox.Show(this, $"Could not open the program folder: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    static string FormatDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    void OnDeleteAndExclude(string processName)
    {
        // idle sentinel은 OS 프로세스가 아니므로 추적 제외 의미 없음 — 그리드 표시값('(Idle)')이 넘어오면 조용히 무시.
        if (string.Equals(processName, IdleDisplayName, StringComparison.Ordinal))
            return;

        var msg = $"Delete all history for '{processName}' and stop tracking it from now on?\n\n" +
                  $"This cannot be undone.";
        var result = MessageBox.Show(this, msg, "Confirm delete & exclude",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;

        try
        {
            var deleted = _store.DeleteSessionsForProcess(processName);
            _store.AddExclusion(processName, "user-hidden", _clock.UtcNow);
            _onExclusionChanged();
            ReloadVisibleTab();
            Log.Information("Deleted {N} sessions for {Name} and added exclusion", deleted, processName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Delete & exclude failed for {Name}", processName);
            MessageBox.Show(this, $"Operation failed: {ex.Message}", "Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public void ToggleVisible()
    {
        if (Visible)
        {
            Hide();
            _refresh.Stop();
        }
        else
        {
            Show();
            Activate();
            ReloadVisibleTab();
            _refresh.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Stop();
            _refresh.Dispose();
            _idleFont.Dispose();
            _iconCache.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }
}

internal sealed class IconCache : IDisposable
{
    const int RetrySeconds = 60;
    const uint FileAttributeNormal = 0x80;
    const uint ShgfiIcon = 0x000000100;
    const uint ShgfiSmallIcon = 0x000000001;
    const uint ShgfiUseFileAttributes = 0x000000010;

    readonly Dictionary<string, CachedIcon> _map = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Image> _ownedImages = [];

    public Image EmptyImage { get; } = new Bitmap(1, 1);

    public IconLookup Get(string processName, string? exePath)
    {
        var key = $"{processName}\0{exePath}";
        var now = DateTimeOffset.UtcNow;
        if (_map.TryGetValue(key, out var cached) && cached.RetryAfterUtc > now)
            return cached.Lookup;

        var resolvedPath = ResolveExecutablePath(processName, exePath);
        var image = resolvedPath is null ? null : TryExtractExecutableIcon(resolvedPath);
        if (image is not null)
        {
            _ownedImages.Add(image);
            var lookup = new IconLookup(image, resolvedPath);
            _map[key] = new CachedIcon(lookup, DateTimeOffset.MaxValue);
            return lookup;
        }

        // 실제 파일을 찾지 못해도 .exe 파일 형식의 Shell 아이콘을 사용해 빨간 X 표시를 피한다.
        // 실패 항목은 주기적으로 다시 탐지하므로, 나중에 프로그램이 실행되면 실제 아이콘으로 교체된다.
        var fallback = cached.Lookup.Image;
        if (fallback is null)
        {
            fallback = TryGetShellIcon(resolvedPath ?? exePath ?? $"{processName}.exe", useFileAttributes: true);
            if (fallback is not null) _ownedImages.Add(fallback);
        }

        var fallbackLookup = new IconLookup(fallback, resolvedPath);
        _map[key] = new CachedIcon(fallbackLookup, now.AddSeconds(RetrySeconds));
        return fallbackLookup;
    }

    static string? ResolveExecutablePath(string processName, string? storedPath)
    {
        if (!string.IsNullOrWhiteSpace(storedPath) && File.Exists(storedPath))
            return storedPath;

        return TryGetRunningProcessPath(processName)
            ?? TryGetAppPath(processName)
            ?? TryFindUpdatedInstallPath(storedPath)
            ?? TryFindOnPath(processName);
    }

    static string? TryGetRunningProcessPath(string processName)
    {
        var lookupName = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrWhiteSpace(lookupName)) return null;

        System.Diagnostics.Process[] processes;
        try
        {
            processes = System.Diagnostics.Process.GetProcessesByName(lookupName);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not enumerate processes named {Name}", lookupName);
            return null;
        }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = Win32ForegroundProbe.TryGetExePath((uint)process.Id);
                    if (!string.IsNullOrWhiteSpace(path)) return path;
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Could not resolve executable for {Name} pid={Pid}", lookupName, process.Id);
                }
            }
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }

        return null;
    }

    static string? TryGetAppPath(string processName)
    {
        var exeName = Path.GetFileName(processName);
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";

        string[] keys =
        [
            $@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
            $@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
            $@"HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths\{exeName}",
        ];

        foreach (var key in keys)
        {
            try
            {
                if (Registry.GetValue(key, null, null) is not string value) continue;
                var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (File.Exists(path)) return path;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not read App Paths entry {Key}", key);
            }
        }

        return null;
    }

    static string? TryFindUpdatedInstallPath(string? storedPath)
    {
        if (string.IsNullOrWhiteSpace(storedPath)) return null;

        try
        {
            var fullPath = Path.GetFullPath(storedPath);
            var windowsApps = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps");
            var rootPrefix = windowsApps.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var relative = fullPath[rootPrefix.Length..];
                var separator = relative.IndexOf(Path.DirectorySeparatorChar);
                if (separator > 0)
                {
                    var oldPackage = relative[..separator];
                    var nameSeparator = oldPackage.IndexOf('_');
                    var packageName = nameSeparator > 0 ? oldPackage[..nameSeparator] : oldPackage;
                    var insidePackage = relative[(separator + 1)..];
                    var fileName = Path.GetFileName(fullPath);

                    foreach (var packageDir in Directory.EnumerateDirectories(windowsApps, $"{packageName}_*")
                                 .OrderByDescending(Directory.GetLastWriteTimeUtc))
                    {
                        var sameRelativePath = Path.Combine(packageDir, insidePackage);
                        if (File.Exists(sameRelativePath)) return sameRelativePath;

                        var renamedOrMoved = Directory.EnumerateFiles(packageDir, fileName, SearchOption.AllDirectories)
                            .FirstOrDefault();
                        if (renamedOrMoved is not null) return renamedOrMoved;
                    }
                }
            }

            // Slack/Discord의 app-x.y.z, WebView의 숫자 버전 폴더처럼 업데이트 시
            // 중간 디렉터리 이름만 바뀌는 일반적인 설치 구조를 복구한다.
            var oldDirectory = Directory.GetParent(fullPath);
            var installRoot = oldDirectory?.Parent;
            if (oldDirectory is null || installRoot is null || !installRoot.Exists) return null;

            var oldDirectoryName = oldDirectory.Name;
            var looksVersioned = oldDirectoryName.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
                || (oldDirectoryName.Length > 0 && char.IsDigit(oldDirectoryName[0]));
            if (!looksVersioned) return null;

            var relativeToVersion = Path.GetRelativePath(oldDirectory.FullName, fullPath);
            foreach (var candidateDir in installRoot.EnumerateDirectories()
                         .OrderByDescending(directory => directory.LastWriteTimeUtc))
            {
                var candidate = Path.Combine(candidateDir.FullName, relativeToVersion);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not search updated install path for {Path}", storedPath);
        }

        return null;
    }

    static string? TryFindOnPath(string processName)
    {
        var exeName = Path.GetFileName(processName);
        if (!exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exeName += ".exe";

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 잘못된 PATH 항목은 건너뛴다.
            }
        }

        return null;
    }

    static Image? TryExtractExecutableIcon(string exePath)
    {
        try
        {
            using var icon = Icon.ExtractAssociatedIcon(exePath);
            if (icon is not null)
            {
                return icon.ToBitmap();
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ExtractAssociatedIcon failed for {Path}", exePath);
        }

        return TryGetShellIcon(exePath, useFileAttributes: false);
    }

    static Image? TryGetShellIcon(string path, bool useFileAttributes)
    {
        var flags = ShgfiIcon | ShgfiSmallIcon;
        if (useFileAttributes) flags |= ShgfiUseFileAttributes;

        var result = SHGetFileInfo(path, FileAttributeNormal, out var info,
            (uint)Marshal.SizeOf<ShellFileInfo>(), flags);
        if (result == IntPtr.Zero || info.IconHandle == IntPtr.Zero) return null;

        try
        {
            using var icon = (Icon)Icon.FromHandle(info.IconHandle).Clone();
            return icon.ToBitmap();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Shell icon lookup failed for {Path}", path);
            return null;
        }
        finally
        {
            DestroyIcon(info.IconHandle);
        }
    }

    public void Dispose()
    {
        foreach (var image in _ownedImages) image.Dispose();
        EmptyImage.Dispose();
        _ownedImages.Clear();
        _map.Clear();
    }

    readonly record struct CachedIcon(IconLookup Lookup, DateTimeOffset RetryAfterUtc);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        out ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DestroyIcon(IntPtr iconHandle);
}

internal readonly record struct IconLookup(Image? Image, string? ResolvedPath);
