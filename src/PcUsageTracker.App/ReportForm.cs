using System.Runtime.InteropServices;
using System.Threading.Channels;
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
    const int HistoricalDashboardCacheLimit = 32;
    const string IdleDisplayName = "(Idle)";

    readonly IClock _clock;
    readonly SqliteStore _store;
    readonly Action _onExclusionChanged;
    readonly TabControl _tabs;
    readonly TabPage _dashboardTab;
    readonly TabPage _overviewTab;
    readonly TabPage _allTimeTab;
    readonly DateTimePicker _dashboardDatePicker;
    readonly Button _dashboardNextButton;
    readonly Label _totalValue;
    readonly Label _activeValue;
    readonly Label _idleValue;
    readonly Label _topApplicationValue;
    readonly TimelineControl _timeline;
    readonly DailyUsageChartControl _dailyChart;
    readonly ComboBox _dailyRange;
    readonly DataGridView _dashboardGrid;
    readonly DataGridView _todayGrid;
    readonly DataGridView _weekGrid;
    readonly DataGridView _monthGrid;
    readonly DataGridView _allGrid;
    readonly System.Windows.Forms.Timer _refresh;
    readonly System.Windows.Forms.Timer _resizeDebounce;
    readonly AsyncIconCache _iconCache = new();
    readonly SemaphoreSlim _refreshGate = new(1, 1);
    readonly Dictionary<DashboardCacheKey, DashboardReportData> _historicalDashboardCache = [];
    readonly Dictionary<DataGridView, Dictionary<string, List<DataGridViewRow>>> _rowsByProcess = [];
    readonly object _pendingIconSync = new();
    readonly Dictionary<string, IconResolvedEventArgs> _pendingIconUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    readonly Font _idleFont;
    CancellationTokenSource? _refreshCts;
    ReportViewKey? _activeRefreshView;
    ReportRefreshRequest? _coalescedRefresh;
    long _refreshVersion;
    DateOnly _lastKnownToday;
    ReportViewKey? _lastAppliedView;
    DateTimeOffset _lastAppliedAt;
    bool _isResizing;
    volatile bool _disposing;
    bool _suppressDashboardDateChange;
    bool _iconDrainScheduled;

    public ReportForm(IClock clock, SqliteStore store, Action onExclusionChanged)
    {
        _clock = clock;
        _store = store;
        _onExclusionChanged = onExclusionChanged;

        Text = "PcUsageTracker — Report";
        Size = new Size(1160, 720);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(720, 520);
        AutoScaleMode = AutoScaleMode.Font;
        DoubleBuffered = true;
        _idleFont = new Font(Font, FontStyle.Italic);

        _tabs = new TabControl { Dock = DockStyle.Fill };

        _dashboardTab = new TabPage("Dashboard");
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(_clock.UtcNow, TimeZoneInfo.Local).DateTime);
        _lastKnownToday = localToday;
        _dashboardDatePicker = new DateTimePicker
        {
            Format = DateTimePickerFormat.Custom,
            CustomFormat = "yyyy-MM-dd (ddd)",
            Width = 150,
            Value = localToday.ToDateTime(TimeOnly.MinValue),
            MaxDate = localToday.ToDateTime(TimeOnly.MinValue),
        };
        var previousButton = new Button { Text = "‹", Width = 36, Height = 28, AccessibleName = "Previous day" };
        _dashboardNextButton = new Button { Text = "›", Width = 36, Height = 28, AccessibleName = "Next day" };
        var todayButton = new Button { Text = "Today", AutoSize = true, Height = 28 };
        var applicationsButton = new Button
        {
            Text = "Applications && categories...",
            AutoSize = true,
            Height = 28,
            Margin = new Padding(16, 3, 3, 3),
        };
        var dashboardToolbar = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(6, 5, 6, 3),
        };
        dashboardToolbar.Controls.Add(previousButton);
        dashboardToolbar.Controls.Add(_dashboardDatePicker);
        dashboardToolbar.Controls.Add(_dashboardNextButton);
        dashboardToolbar.Controls.Add(todayButton);
        dashboardToolbar.Controls.Add(applicationsButton);

        var cards = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(6, 3, 6, 5),
        };
        for (var i = 0; i < 4; i++) cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        cards.Controls.Add(BuildMetricCard("Tracked", out _totalValue), 0, 0);
        cards.Controls.Add(BuildMetricCard("Active", out _activeValue), 1, 0);
        cards.Controls.Add(BuildMetricCard("Idle", out _idleValue), 2, 0);
        cards.Controls.Add(BuildMetricCard("Top application", out _topApplicationValue), 3, 0);

        _timeline = new TimelineControl { Dock = DockStyle.Fill };
        _dailyChart = new DailyUsageChartControl { Dock = DockStyle.Fill };
        _dailyRange = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Width = 84,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        _dailyRange.Items.AddRange(["7 days", "30 days"]);
        _dailyRange.SelectedIndex = 0;
        _dashboardGrid = BuildGrid(this);

        var lower = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 3),
        };
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        lower.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
        lower.Controls.Add(WrapDailyChart(_dailyChart, _dailyRange), 0, 0);
        lower.Controls.Add(WrapWithHeader("Applications", _dashboardGrid), 1, 0);

        var dashboardLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
        };
        dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 80));
        dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        dashboardLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        dashboardLayout.Controls.Add(dashboardToolbar, 0, 0);
        dashboardLayout.Controls.Add(cards, 0, 1);
        dashboardLayout.Controls.Add(BuildSectionHeader("24-hour timeline"), 0, 2);
        dashboardLayout.Controls.Add(_timeline, 0, 3);
        dashboardLayout.Controls.Add(lower, 0, 4);
        _dashboardTab.Controls.Add(dashboardLayout);

        _overviewTab = new TabPage("Today + Week + Month");
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
        _overviewTab.Controls.Add(split);

        _allTimeTab = new TabPage("All-time");
        _allGrid = BuildGrid(this);
        _allTimeTab.Controls.Add(WrapWithHeader("All-time", _allGrid));

        _tabs.TabPages.Add(_dashboardTab);
        _tabs.TabPages.Add(_overviewTab);
        _tabs.TabPages.Add(_allTimeTab);
        Controls.Add(_tabs);
        _iconCache.IconResolved += OnIconResolved;

        previousButton.Click += (_, _) => ChangeDashboardDate(-1);
        _dashboardNextButton.Click += (_, _) => ChangeDashboardDate(1);
        todayButton.Click += (_, _) => SetDashboardDate(LocalToday(_clock.UtcNow));
        applicationsButton.Click += (_, _) => OpenApplicationsEditor();
        _dashboardDatePicker.ValueChanged += (_, _) =>
        {
            UpdateDashboardDateNavigation(_clock.UtcNow);
            if (!_suppressDashboardDateChange && Visible &&
                _tabs.SelectedTab == _dashboardTab && !_isResizing)
                QueueVisibleRefresh();
        };
        _dailyRange.SelectedIndexChanged += (_, _) =>
        {
            if (Visible && _tabs.SelectedTab == _dashboardTab && !_isResizing)
                QueueVisibleRefresh();
        };

        _refresh = new System.Windows.Forms.Timer { Interval = RefreshMs };
        _refresh.Tick += (_, _) => OnRefreshTick();
        _resizeDebounce = new System.Windows.Forms.Timer { Interval = 200 };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            if (Visible && !_isResizing && IsVisibleDataStale(_clock.UtcNow)) QueueVisibleRefresh();
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (Visible && !_isResizing) QueueVisibleRefresh();
        };
        ResizeBegin += (_, _) =>
        {
            _isResizing = true;
            _refresh.Stop();
            CancelPendingRefresh();
        };
        ResizeEnd += (_, _) =>
        {
            _isResizing = false;
            if (!Visible) return;
            _refresh.Start();
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
        Shown += (_, _) => _refresh.Start();
        FormClosing += (s, e) =>
        {
            // 트레이 상주 앱 — 닫기 버튼은 숨기기로만
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                _refresh.Stop();
                CancelPendingRefresh();
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
            FillWeight = 42,
        });
        g.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = "Category",
            HeaderText = "Category",
            FillWeight = 25,
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
        var editItem = new ToolStripMenuItem("Edit application...");
        editItem.Click += (_, _) =>
        {
            if (g.SelectedRows.Count == 0 || g.SelectedRows[0].Tag is not ReportEntry entry) return;
            owner.OpenApplicationEditor(entry.ProcessName);
        };
        var deleteItem = new ToolStripMenuItem("Delete history && stop tracking");
        deleteItem.Click += (_, _) =>
        {
            if (g.SelectedRows.Count == 0 || g.SelectedRows[0].Tag is not ReportEntry entry) return;
            owner.OnDeleteAndExclude(entry.ProcessName);
        };
        menu.Items.Add(editItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(deleteItem);
        menu.Opening += (_, _) =>
        {
            var editable = g.SelectedRows.Count > 0 &&
                           g.SelectedRows[0].Tag is ReportEntry entry &&
                           !string.Equals(entry.ProcessName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase);
            editItem.Enabled = editable;
            deleteItem.Enabled = editable;
        };
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

    static Control BuildMetricCard(string title, out Label valueLabel)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(4),
            Padding = new Padding(10, 6, 10, 6),
            BackColor = Color.FromArgb(244, 246, 249),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
        panel.Controls.Add(new Label
        {
            Text = title,
            Dock = DockStyle.Fill,
            ForeColor = SystemColors.GrayText,
            TextAlign = ContentAlignment.BottomLeft,
            AutoEllipsis = true,
        }, 0, 0);
        valueLabel = new Label
        {
            Text = "—",
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 3,
                FontStyle.Bold),
            TextAlign = ContentAlignment.TopLeft,
            AutoEllipsis = true,
        };
        panel.Controls.Add(valueLabel, 0, 1);
        return panel;
    }

    static Control BuildSectionHeader(string header) => new Label
    {
        Text = header,
        Dock = DockStyle.Fill,
        Padding = new Padding(10, 2, 0, 0),
        Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
        TextAlign = ContentAlignment.MiddleLeft,
    };

    static Control WrapDailyChart(Control chart, ComboBox range)
    {
        var panel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(6),
        };
        panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(new Label
        {
            Text = "Daily usage",
            Dock = DockStyle.Fill,
            Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
        }, 0, 0);
        header.Controls.Add(range, 1, 0);
        panel.Controls.Add(header, 0, 0);
        panel.Controls.Add(chart, 0, 1);
        return panel;
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

    void OnRefreshTick()
    {
        var now = _clock.UtcNow;
        RefreshTodayBoundary(now);
        if (_tabs.SelectedTab == _dashboardTab && SelectedDashboardDate() != _lastKnownToday)
            return;
        QueueVisibleRefresh(now);
    }

    void QueueVisibleRefresh(bool invalidateHistorical = false) =>
        QueueVisibleRefresh(_clock.UtcNow, invalidateHistorical);

    void QueueVisibleRefresh(DateTimeOffset now, bool invalidateHistorical = false)
    {
        if (_disposing || IsDisposed) return;
        if (invalidateHistorical)
        {
            _historicalDashboardCache.Clear();
            _lastAppliedView = null;
        }
        if (!Visible || _isResizing)
        {
            if (invalidateHistorical) CancelPendingRefresh();
            return;
        }

        RefreshTodayBoundary(now);
        var request = CaptureRefreshRequest(now);
        if (request.Kind == ReportViewKind.Dashboard && request.Day != _lastKnownToday &&
            _historicalDashboardCache.TryGetValue(
                new DashboardCacheKey(request.Day, request.DayCount), out var cached))
        {
            CancelPendingRefresh();
            ApplyReportData(request, cached);
            return;
        }

        var disposition = ReportRefreshPolicy.Decide(
            _activeRefreshView is not null,
            _activeRefreshView == request.ViewKey,
            invalidateHistorical);
        if (disposition == RefreshRequestDisposition.Coalesce)
        {
            _coalescedRefresh = request;
            return;
        }
        if (disposition == RefreshRequestDisposition.Supersede) CancelPendingRefresh();
        StartRefresh(request);
    }

    void StartRefresh(ReportRefreshRequest request)
    {
        var cancellation = new CancellationTokenSource();
        _refreshCts = cancellation;
        _activeRefreshView = request.ViewKey;
        _coalescedRefresh = null;
        var version = Interlocked.Increment(ref _refreshVersion);
        _ = LoadAndApplyAsync(request, version, cancellation.Token);
    }

    async Task LoadAndApplyAsync(ReportRefreshRequest request, long version, CancellationToken cancellationToken)
    {
        var enteredGate = false;
        ReportData? data = null;
        Exception? error = null;
        try
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            cancellationToken.ThrowIfCancellationRequested();
            data = await Task.Run(() => LoadReportData(request), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            // A newer date/tab request superseded this result.
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            if (enteredGate) _refreshGate.Release();
            PostToUi(() => CompleteRefresh(request, version, cancellationToken, data, error));
        }
    }

    void CompleteRefresh(
        ReportRefreshRequest request,
        long version,
        CancellationToken cancellationToken,
        ReportData? data,
        Exception? error)
    {
        if (_disposing || version != Interlocked.Read(ref _refreshVersion)) return;

        _activeRefreshView = null;
        var completedCancellation = Interlocked.Exchange(ref _refreshCts, null);
        completedCancellation?.Dispose();
        if (error is not null)
            Log.Warning(error, "Background report refresh failed for {Kind}", request.Kind);
        else if (!cancellationToken.IsCancellationRequested && data is not null &&
                 RequestMatchesVisibleState(request))
            ApplyReportData(request, data);

        var followUp = _coalescedRefresh;
        _coalescedRefresh = null;
        if (followUp is { } latest && Visible && !_isResizing &&
            latest.ViewKey == CaptureRefreshRequest(_clock.UtcNow).ViewKey)
            StartRefresh(latest);
    }

    ReportData LoadReportData(ReportRefreshRequest request)
    {
        using var connection = _store.OpenReadOnlyConnection();
        var aggregator = new Aggregator(connection);
        if (request.Kind == ReportViewKind.AllTime)
            return new AllTimeReportData(aggregator.AllTime(request.Now));

        if (request.Kind == ReportViewKind.Overview)
        {
            var (todayFrom, todayTo) = Aggregator.TodayRange(request.Now);
            var (weekFrom, weekTo) = Aggregator.ThisWeekRange(request.Now);
            var (monthFrom, monthTo) = Aggregator.ThisMonthRange(request.Now);
            return new OverviewReportData(
                aggregator.TopN(todayFrom, todayTo, request.Now),
                aggregator.TopN(weekFrom, weekTo, request.Now),
                aggregator.TopN(monthFrom, monthTo, request.Now));
        }

        var zone = TimeZoneInfo.Local;
        var applications = aggregator.ApplicationTotals(request.Day, request.Now, zone);
        return new DashboardReportData(
            DashboardSummary.FromApplications(applications),
            applications,
            aggregator.Timeline(request.Day, request.Now, zone),
            aggregator.DailyTotals(request.Day, request.DayCount, request.Now, zone));
    }

    void ApplyReportData(ReportRefreshRequest request, ReportData data)
    {
        switch (data)
        {
            case DashboardReportData dashboard:
                SetLabelText(_totalValue, FormatDuration(dashboard.Summary.TotalSeconds));
                SetLabelText(_activeValue, FormatDuration(dashboard.Summary.ActiveSeconds));
                SetLabelText(_idleValue, FormatDuration(dashboard.Summary.IdleSeconds));
                SetLabelText(_topApplicationValue, dashboard.Summary.TopApplicationName is null
                    ? "—"
                    : $"{dashboard.Summary.TopApplicationName}  " +
                      FormatDuration(dashboard.Summary.TopApplicationSeconds));
                _timeline.SetData(request.Day, TimeZoneInfo.Local, dashboard.Timeline);
                _dailyChart.SetData(dashboard.Daily);
                Populate(_dashboardGrid, dashboard.Applications);
                UpdateDashboardDateNavigation(request.Now);
                if (request.Day != _lastKnownToday)
                {
                    var cacheKey = new DashboardCacheKey(request.Day, request.DayCount);
                    if (!_historicalDashboardCache.ContainsKey(cacheKey) &&
                        _historicalDashboardCache.Count >= HistoricalDashboardCacheLimit)
                        _historicalDashboardCache.Remove(_historicalDashboardCache.Keys.First());
                    _historicalDashboardCache[cacheKey] = dashboard;
                }
                break;
            case OverviewReportData overview:
                Populate(_todayGrid, overview.Today);
                Populate(_weekGrid, overview.Week);
                Populate(_monthGrid, overview.Month);
                break;
            case AllTimeReportData allTime:
                Populate(_allGrid, allTime.Entries);
                break;
        }

        _lastAppliedView = request.ViewKey;
        _lastAppliedAt = request.Now;
    }

    ReportRefreshRequest CaptureRefreshRequest(DateTimeOffset now)
    {
        if (_tabs.SelectedTab == _dashboardTab)
            return new ReportRefreshRequest(
                ReportViewKind.Dashboard,
                now,
                SelectedDashboardDate(),
                _dailyRange.SelectedIndex == 1 ? 30 : 7);
        return new ReportRefreshRequest(
            _tabs.SelectedTab == _allTimeTab ? ReportViewKind.AllTime : ReportViewKind.Overview,
            now,
            default,
            0);
    }

    bool RequestMatchesVisibleState(ReportRefreshRequest request) =>
        request.ViewKey == CaptureRefreshRequest(_clock.UtcNow).ViewKey;

    bool IsVisibleDataStale(DateTimeOffset now)
    {
        var request = CaptureRefreshRequest(now);
        if (_lastAppliedView != request.ViewKey) return true;
        if (request.Kind == ReportViewKind.Dashboard && request.Day != _lastKnownToday) return false;
        return now - _lastAppliedAt >= TimeSpan.FromMilliseconds(RefreshMs);
    }

    void CancelPendingRefresh()
    {
        Interlocked.Increment(ref _refreshVersion);
        _activeRefreshView = null;
        _coalescedRefresh = null;
        var cancellation = Interlocked.Exchange(ref _refreshCts, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    void PostToUi(Action action)
    {
        if (_disposing) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    static void SetLabelText(Label label, string value)
    {
        if (!string.Equals(label.Text, value, StringComparison.Ordinal)) label.Text = value;
    }

    void ChangeDashboardDate(int days)
    {
        var selected = SelectedDashboardDate();
        DateOnly next;
        try { next = selected.AddDays(days); }
        catch (ArgumentOutOfRangeException) { return; }

        var today = LocalToday(_clock.UtcNow);
        if (next > today) next = today;
        SetDashboardDate(next);
    }

    void SetDashboardDate(DateOnly day)
    {
        var now = _clock.UtcNow;
        RefreshTodayBoundary(now);
        var today = _lastKnownToday;
        var earliest = DateOnly.FromDateTime(_dashboardDatePicker.MinDate);
        if (day < earliest) day = earliest;
        if (day > today) day = today;
        var value = day.ToDateTime(TimeOnly.MinValue);
        if (value > _dashboardDatePicker.MaxDate) value = _dashboardDatePicker.MaxDate;
        if (_dashboardDatePicker.Value.Date == value) QueueVisibleRefresh();
        else _dashboardDatePicker.Value = value;
    }

    void RefreshTodayBoundary(DateTimeOffset now)
    {
        var today = LocalToday(now);
        if (today == _lastKnownToday)
        {
            UpdateDashboardDateNavigation(now);
            return;
        }

        var nextSelection = DashboardDateNavigation.SelectionAfterTodayChanged(
            _lastKnownToday,
            SelectedDashboardDate(),
            today);
        _lastKnownToday = today;
        var todayValue = today.ToDateTime(TimeOnly.MinValue);
        _suppressDashboardDateChange = true;
        try
        {
            _dashboardDatePicker.MaxDate = todayValue;
            _dashboardDatePicker.Value = nextSelection.ToDateTime(TimeOnly.MinValue);
        }
        finally
        {
            _suppressDashboardDateChange = false;
        }
        UpdateDashboardDateNavigation(now);
    }

    void UpdateDashboardDateNavigation(DateTimeOffset now)
    {
        _dashboardNextButton.Enabled = SelectedDashboardDate() < LocalToday(now);
    }

    DateOnly SelectedDashboardDate() => DateOnly.FromDateTime(_dashboardDatePicker.Value.Date);

    static DateOnly LocalToday(DateTimeOffset now) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, TimeZoneInfo.Local).DateTime);

    public void RequestReload() => QueueVisibleRefresh(invalidateHistorical: true);

    void Populate(DataGridView grid, IReadOnlyList<ReportEntry> entries)
    {
        var rebuildRows = !RowsMatchEntries(grid, entries);
        var selectedProcesses = rebuildRows
            ? grid.SelectedRows
                .Cast<DataGridViewRow>()
                .Select(GetProcessName)
                .Where(name => name is not null)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
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
                        GetProcessName(row), firstDisplayedProcess, StringComparison.OrdinalIgnoreCase));
                if (scrollRow is not null) grid.FirstDisplayedScrollingRowIndex = scrollRow.Index;
            }
        }
        finally
        {
            IndexRows(grid);
            grid.ResumeLayout();
        }
    }

    void IndexRows(DataGridView grid)
    {
        var index = new Dictionary<string, List<DataGridViewRow>>(StringComparer.OrdinalIgnoreCase);
        foreach (DataGridViewRow row in grid.Rows)
        {
            var processName = GetProcessName(row);
            if (processName is null) continue;
            if (!index.TryGetValue(processName, out var rows))
                index.Add(processName, rows = []);
            rows.Add(row);
        }
        _rowsByProcess[grid] = index;
    }

    static bool RowsMatchEntries(DataGridView grid, IReadOnlyList<ReportEntry> entries)
    {
        if (grid.Rows.Count != entries.Count) return false;
        for (var index = 0; index < entries.Count; index++)
        {
            if (!string.Equals(GetProcessName(grid.Rows[index]), entries[index].ProcessName,
                    StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    void UpdateRow(DataGridViewRow row, ReportEntry entry, long total)
    {
        var isIdle = string.Equals(entry.ProcessName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase);
        var iconLookup = isIdle ? default : _iconCache.GetOrQueue(entry.ProcessName, entry.ExePath);
        var rowEntry = iconLookup.ResolvedPath is { } resolvedPath
            ? entry with { ExePath = resolvedPath }
            : entry;

        SetCellValue(row.Cells["Icon"], iconLookup.Image ?? _iconCache.EmptyImage);
        SetCellValue(row.Cells["Process"], isIdle ? IdleDisplayName : entry.DisplayName);
        SetCellValue(row.Cells["Category"], isIdle ? string.Empty : entry.CategoryName ?? "Other");
        SetCellValue(row.Cells["Duration"], FormatDuration(entry.TotalSeconds));
        var pct = total > 0 ? (double)entry.TotalSeconds / total * 100.0 : 0.0;
        SetCellValue(row.Cells["Share"], $"{pct:0.0}%");
        row.Tag = rowEntry;

        var processStyle = row.Cells["Process"].Style;
        processStyle.Font = isIdle ? _idleFont : Font;
        processStyle.ForeColor = isIdle ? SystemColors.GrayText : SystemColors.ControlText;

        var categoryStyle = row.Cells["Category"].Style;
        if (entry.ColorRgb is { } rgb)
        {
            var color = ColorFromRgb(rgb);
            categoryStyle.BackColor = color;
            categoryStyle.ForeColor = ContrastColor(color);
            categoryStyle.SelectionBackColor = color;
            categoryStyle.SelectionForeColor = ContrastColor(color);
        }
        else
        {
            categoryStyle.BackColor = SystemColors.Window;
            categoryStyle.ForeColor = SystemColors.ControlText;
            categoryStyle.SelectionBackColor = SystemColors.Highlight;
            categoryStyle.SelectionForeColor = SystemColors.HighlightText;
        }

        var tooltip = isIdle
            ? string.Empty
            : $"Process: {entry.ProcessName}\nCategory: {entry.CategoryName ?? "Other"}\n{rowEntry.ExePath ?? string.Empty}";
        if (!string.Equals(row.Cells["Process"].ToolTipText, tooltip, StringComparison.Ordinal))
            row.Cells["Process"].ToolTipText = tooltip;
    }

    static void SetCellValue(DataGridViewCell cell, object value)
    {
        if (!Equals(cell.Value, value)) cell.Value = value;
    }

    static string? GetProcessName(DataGridViewRow row) =>
        row.Tag is ReportEntry entry ? entry.ProcessName : null;

    void OpenApplicationEditor(string processName)
    {
        if (string.Equals(processName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase)) return;
        using var dialog = new ApplicationRulesForm(_store, processName);
        dialog.ShowDialog(this);
        QueueVisibleRefresh(invalidateHistorical: true);
    }

    void OpenApplicationsEditor()
    {
        using var dialog = new ApplicationRulesForm(_store);
        dialog.ShowDialog(this);
        QueueVisibleRefresh(invalidateHistorical: true);
    }

    void OnIconResolved(object? sender, IconResolvedEventArgs e)
    {
        if (_disposing) return;
        if (e.Lookup.ResolvedPath is { Length: > 0 } repairedPath &&
            !string.Equals(e.RequestedPath, repairedPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                _store.UpsertProcessPathIndependent(e.ProcessName, repairedPath, DateTimeOffset.UtcNow);
                Log.Information("Repaired executable path for {Name}: {Path}", e.ProcessName, repairedPath);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Failed to repair executable path for {Name}", e.ProcessName);
            }
        }

        var scheduleDrain = false;
        lock (_pendingIconSync)
        {
            if (_disposing) return;
            var key = $"{e.ProcessName}\0{e.RequestedPath}";
            _pendingIconUpdates[key] = e;
            if (!_iconDrainScheduled)
            {
                _iconDrainScheduled = true;
                scheduleDrain = true;
            }
        }
        if (scheduleDrain) PostToUi(DrainIconUpdates);
    }

    void DrainIconUpdates()
    {
        IconResolvedEventArgs[] updates;
        lock (_pendingIconSync)
        {
            updates = _pendingIconUpdates.Values.ToArray();
            _pendingIconUpdates.Clear();
            _iconDrainScheduled = false;
        }

        if (_disposing) return;
        foreach (var update in updates)
        {
            foreach (var gridIndex in _rowsByProcess.Values)
            {
                if (!gridIndex.TryGetValue(update.ProcessName, out var rows)) continue;
                foreach (var row in rows)
                {
                    if (row.DataGridView is null || row.Tag is not ReportEntry entry ||
                        (!string.Equals(entry.ExePath, update.RequestedPath, StringComparison.OrdinalIgnoreCase) &&
                         !string.Equals(entry.ExePath, update.Lookup.ResolvedPath,
                             StringComparison.OrdinalIgnoreCase)))
                        continue;

                    SetCellValue(row.Cells["Icon"], update.Lookup.Image ?? _iconCache.EmptyImage);
                    if (update.Lookup.ResolvedPath is not { Length: > 0 } resolvedPath) continue;
                    row.Tag = entry with { ExePath = resolvedPath };
                    row.Cells["Process"].ToolTipText =
                        $"Process: {entry.ProcessName}\nCategory: {entry.CategoryName ?? "Other"}\n{resolvedPath}";
                }
            }
        }
    }

    static Color ColorFromRgb(int rgb) =>
        Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);

    static Color ContrastColor(Color color) =>
        color.R * 299 + color.G * 587 + color.B * 114 >= 150_000 ? Color.Black : Color.White;

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

    static string FormatDuration(long seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    void OnDeleteAndExclude(string processName)
    {
        // idle sentinel은 OS 프로세스가 아니므로 추적 제외 의미 없음 — 그리드 표시값('(Idle)')이 넘어오면 조용히 무시.
        if (string.Equals(processName, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase))
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
            QueueVisibleRefresh(invalidateHistorical: true);
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
            CancelPendingRefresh();
        }
        else
        {
            Show();
            Activate();
            QueueVisibleRefresh();
            _refresh.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _disposing = true;
            _refresh.Stop();
            _resizeDebounce.Stop();
            CancelPendingRefresh();
            _iconCache.IconResolved -= OnIconResolved;
            lock (_pendingIconSync)
            {
                _pendingIconUpdates.Clear();
                _iconDrainScheduled = false;
            }
            _refresh.Dispose();
            _resizeDebounce.Dispose();
            _idleFont.Dispose();
            _iconCache.Dispose();
        }
        base.Dispose(disposing);
    }

    enum ReportViewKind
    {
        Dashboard,
        Overview,
        AllTime,
    }

    readonly record struct ReportViewKey(ReportViewKind Kind, DateOnly Day, int DayCount);

    readonly record struct ReportRefreshRequest(
        ReportViewKind Kind,
        DateTimeOffset Now,
        DateOnly Day,
        int DayCount)
    {
        public ReportViewKey ViewKey => new(Kind, Day, DayCount);
    }

    readonly record struct DashboardCacheKey(DateOnly Day, int DayCount);

    abstract record ReportData;

    sealed record DashboardReportData(
        DashboardSummary Summary,
        IReadOnlyList<ReportEntry> Applications,
        IReadOnlyList<TimelineSegment> Timeline,
        IReadOnlyList<DailyUsagePoint> Daily) : ReportData;

    sealed record OverviewReportData(
        IReadOnlyList<ReportEntry> Today,
        IReadOnlyList<ReportEntry> Week,
        IReadOnlyList<ReportEntry> Month) : ReportData;

    sealed record AllTimeReportData(IReadOnlyList<ReportEntry> Entries) : ReportData;
}

internal sealed class BufferedDataGridView : DataGridView
{
    public BufferedDataGridView()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Rows.Count != 0) return;

        var emptyArea = ClientRectangle;
        emptyArea.Y += ColumnHeadersVisible ? ColumnHeadersHeight : 0;
        emptyArea.Height -= ColumnHeadersVisible ? ColumnHeadersHeight : 0;
        TextRenderer.DrawText(
            e.Graphics,
            "No activity recorded",
            Font,
            emptyArea,
            SystemColors.GrayText,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine);
    }
}

internal sealed class AsyncIconCache : IDisposable
{
    const int RetrySeconds = 60;
    const int WorkerCount = 2;
    const int QueueCapacity = 128;
    const uint FileAttributeNormal = 0x80;
    const uint ShgfiIcon = 0x000000100;
    const uint ShgfiSmallIcon = 0x000000001;
    const uint ShgfiUseFileAttributes = 0x000000010;

    readonly object _sync = new();
    readonly Dictionary<string, CachedIcon> _map = new(StringComparer.OrdinalIgnoreCase);
    readonly List<Image> _ownedImages = [];
    readonly Channel<IconRequest> _queue;
    readonly CancellationTokenSource _disposeCancellation = new();
    readonly Task[] _workers;
    int _disposed;

    public Image EmptyImage { get; } = SystemIcons.Application.ToBitmap();
    public event EventHandler<IconResolvedEventArgs>? IconResolved;

    public AsyncIconCache()
    {
        _queue = Channel.CreateBounded<IconRequest>(new BoundedChannelOptions(QueueCapacity)
        {
            SingleWriter = false,
            SingleReader = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _workers = Enumerable.Range(0, WorkerCount)
            .Select(_ => Task.Run(() => RunWorkerAsync(_disposeCancellation.Token)))
            .ToArray();
    }

    public IconLookup GetOrQueue(string processName, string? exePath)
    {
        var key = $"{processName}\0{exePath}";
        var now = DateTimeOffset.UtcNow;
        CachedIcon cached;
        lock (_sync)
        {
            if (_disposed != 0) return default;
            _map.TryGetValue(key, out cached);
            if (cached.Loading || cached.RetryAfterUtc > now) return cached.Lookup;
            _map[key] = cached with { Loading = true };
        }

        if (!_queue.Writer.TryWrite(new IconRequest(key, processName, exePath, cached.Lookup)))
        {
            lock (_sync)
                _map[key] = cached with { RetryAfterUtc = now.AddSeconds(5), Loading = false };
        }
        return cached.Lookup;
    }

    async Task RunWorkerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try { Resolve(request); }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Background icon resolution failed for {Name}", request.ProcessName);
                    lock (_sync)
                    {
                        if (_disposed == 0)
                            _map[request.Key] = new CachedIcon(
                                request.PreviousLookup,
                                DateTimeOffset.UtcNow.AddSeconds(RetrySeconds),
                                Loading: false);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    void Resolve(IconRequest request)
    {
        var resolvedPath = ResolveExecutablePath(request.ProcessName, request.RequestedPath);
        var image = resolvedPath is null ? null : TryExtractExecutableIcon(resolvedPath);
        if (image is null && request.PreviousLookup.Image is null)
            image = TryGetShellIcon(
                resolvedPath ?? request.RequestedPath ?? $"{request.ProcessName}.exe",
                useFileAttributes: true);

        var lookup = new IconLookup(image ?? request.PreviousLookup.Image, resolvedPath);
        lock (_sync)
        {
            if (_disposed != 0)
            {
                image?.Dispose();
                return;
            }

            if (image is not null) _ownedImages.Add(image);
            _map[request.Key] = new CachedIcon(
                lookup,
                image is not null && resolvedPath is not null
                    ? DateTimeOffset.MaxValue
                    : DateTimeOffset.UtcNow.AddSeconds(RetrySeconds),
                Loading: false);
        }

        IconResolved?.Invoke(this,
            new IconResolvedEventArgs(request.ProcessName, request.RequestedPath, lookup));
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
                                 .Take(32)
                                 .OrderByDescending(Directory.GetLastWriteTimeUtc))
                    {
                        var sameRelativePath = Path.Combine(packageDir, insidePackage);
                        if (File.Exists(sameRelativePath)) return sameRelativePath;

                        var renamedOrMoved = FindFileBounded(packageDir, fileName, maxDepth: 4, maxDirectories: 256);
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
                         .Take(64)
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

    static string? FindFileBounded(string root, string fileName, int maxDepth, int maxDirectories)
    {
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var visited = 0;
        while (pending.Count > 0 && visited++ < maxDirectories)
        {
            var (directory, depth) = pending.Dequeue();
            try
            {
                var match = Directory.EnumerateFiles(directory, fileName, SearchOption.TopDirectoryOnly)
                    .FirstOrDefault();
                if (match is not null) return match;
                if (depth >= maxDepth) continue;
                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.TopDirectoryOnly))
                {
                    if (visited + pending.Count >= maxDirectories) break;
                    pending.Enqueue((child, depth + 1));
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (IOException)
            {
            }
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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IconResolved = null;
        _disposeCancellation.Cancel();
        _queue.Writer.TryComplete();
        lock (_sync)
        {
            foreach (var image in _ownedImages) image.Dispose();
            EmptyImage.Dispose();
            _ownedImages.Clear();
            _map.Clear();
        }
        _ = Task.WhenAll(_workers).ContinueWith(
            _ => _disposeCancellation.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    readonly record struct CachedIcon(IconLookup Lookup, DateTimeOffset RetryAfterUtc, bool Loading);
    readonly record struct IconRequest(
        string Key,
        string ProcessName,
        string? RequestedPath,
        IconLookup PreviousLookup);

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

internal sealed class IconResolvedEventArgs : EventArgs
{
    public IconResolvedEventArgs(string processName, string? requestedPath, IconLookup lookup)
    {
        ProcessName = processName;
        RequestedPath = requestedPath;
        Lookup = lookup;
    }

    public string ProcessName { get; }
    public string? RequestedPath { get; }
    public IconLookup Lookup { get; }
}

internal readonly record struct IconLookup(Image? Image, string? ResolvedPath);
