using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.App;

internal sealed class PeriodStatisticsControl : UserControl
{
    readonly SqliteStore _store;
    readonly IClock _clock;
    readonly AsyncIconCache _iconCache;
    readonly bool _ownsIconCache;
    readonly Image _groupIcon = CreateGroupIcon();
    readonly DateTimePicker _from = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    readonly DateTimePicker _to = new() { Format = DateTimePickerFormat.Short, Width = 120 };
    readonly CheckBox _grouped = new() { AutoSize = true, Checked = true, Text = UiText.T("Combine groups", "그룹 합산") };
    readonly Label _summary = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _dayCountValue = new();
    readonly Label _trackedValue = new();
    readonly Label _averageValue = new();
    readonly DataGridView _applications = CreateGrid(showIcons: true);
    readonly DataGridView _categories = CreateGrid();
    int _requestVersion;
    bool _loaded;

    public PeriodStatisticsControl(SqliteStore store, IClock clock, AsyncIconCache? iconCache = null)
    {
        _store = store;
        _clock = clock;
        _ownsIconCache = iconCache is null;
        _iconCache = iconCache ?? new AsyncIconCache();
        _applications.Columns["Icon"].DefaultCellStyle.NullValue = _iconCache.EmptyImage;
        _iconCache.IconResolved += OnIconResolved;
        Dock = DockStyle.Fill;
        var today = TimeZoneInfo.ConvertTime(clock.UtcNow, TimeZoneInfo.Local).Date;
        _from.MaxDate = _to.MaxDate = today;
        _to.Value = today;
        _from.Value = today.AddDays(-6);
        var refresh = new Button { Text = UiText.T("Show", "조회"), AutoSize = true };
        var groups = new Button { Text = UiText.T("Manage groups...", "그룹 관리..."), AutoSize = true };
        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, Padding = new Padding(5) };
        top.Controls.AddRange([new Label { Text = UiText.T("From", "시작일"), AutoSize = true, Margin = new Padding(3, 7, 3, 0) },
            _from, new Label { Text = UiText.T("To", "종료일"), AutoSize = true, Margin = new Padding(3, 7, 3, 0) },
            _to, refresh, _grouped, groups]);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var appPage = new TabPage(UiText.T("Applications", "앱별")); appPage.Controls.Add(_applications);
        var categoryPage = new TabPage(UiText.T("Categories", "분류별")); categoryPage.Controls.Add(_categories);
        tabs.TabPages.Add(appPage); tabs.TabPages.Add(categoryPage);
        var metrics = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Padding = new Padding(5, 0, 5, 0) };
        for (var i = 0; i < 3; i++) metrics.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / 3));
        metrics.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        metrics.Controls.Add(CreateMetric(UiText.T("Days", "일수"), _dayCountValue), 0, 0);
        metrics.Controls.Add(CreateMetric(UiText.T("Tracked", "기록 시간"), _trackedValue), 1, 0);
        metrics.Controls.Add(CreateMetric(UiText.T("Daily average", "일평균"), _averageValue), 2, 0);
        _summary.Padding = new Padding(8, 0, 0, 0);
        _summary.ForeColor = SystemColors.GrayText;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(top, 0, 0); layout.Controls.Add(metrics, 0, 1); layout.Controls.Add(_summary, 0, 2); layout.Controls.Add(tabs, 0, 3);
        Controls.Add(layout);
        refresh.Click += (_, _) => LoadPeriod();
        _grouped.CheckedChanged += (_, _) => { if (_loaded) LoadPeriod(); };
        groups.Click += (_, _) =>
        {
            using var dialog = new ApplicationGroupsForm(_store);
            dialog.ShowDialog(FindForm());
            if (_loaded) LoadPeriod();
        };
    }

    public void EnsureLoaded() { if (!_loaded) LoadPeriod(); }
    public void InvalidateData() { _loaded = false; _requestVersion++; }

    static Control CreateMetric(string title, Label value)
    {
        var card = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(3), Padding = new Padding(10, 5, 10, 5),
            BackColor = SystemInformation.HighContrast ? SystemColors.Control : UsageTableStyle.SummaryBack };
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
        card.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
        card.Controls.Add(new Label { Text = title, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.ControlText }, 0, 0);
        value.Text = "—"; value.Dock = DockStyle.Fill; value.TextAlign = ContentAlignment.MiddleLeft;
        value.Font = UsageTableStyle.ValueFont; value.AutoEllipsis = true;
        card.Controls.Add(value, 0, 1);
        return card;
    }

    void ClearResults()
    {
        _dayCountValue.Text = _trackedValue.Text = _averageValue.Text = "—";
        _applications.SuspendLayout();
        _categories.SuspendLayout();
        try { _applications.Rows.Clear(); _categories.Rows.Clear(); }
        finally { _applications.ResumeLayout(); _categories.ResumeLayout(); }
    }

    async void LoadPeriod()
    {
        var today = TimeZoneInfo.ConvertTime(_clock.UtcNow, TimeZoneInfo.Local).Date;
        _from.MaxDate = _to.MaxDate = today;
        var fromDay = DateOnly.FromDateTime(_from.Value);
        var toDay = DateOnly.FromDateTime(_to.Value);
        var version = ++_requestVersion;
        if (fromDay > toDay)
        {
            _loaded = false;
            ClearResults();
            _summary.Text = UiText.T("Start date must not be after end date.", "시작일은 종료일보다 늦을 수 없습니다.");
            return;
        }
        var dayCount = toDay.DayNumber - fromDay.DayNumber + 1;
        var grouped = _grouped.Checked;
        var now = _clock.UtcNow;
        _loaded = true;
        ClearResults();
        _summary.Text = UiText.T("Loading...", "불러오는 중...");
        try
        {
            var data = await Task.Run(() =>
            {
                using var connection = _store.OpenReadOnlyConnection();
                var aggregator = new Aggregator(connection);
                var (from, _) = Aggregator.DayRange(fromDay, TimeZoneInfo.Local);
                var (_, to) = Aggregator.DayRange(toDay, TimeZoneInfo.Local);
                var entries = aggregator.TopN(from, to < now ? to : now, now);
                var groupRules = grouped ? ApplicationGroups.Load(connection) : Array.Empty<ApplicationGroup>();
                return (Entries: entries, Groups: ApplicationGroups.Summarize(entries, groupRules));
            });
            if (IsDisposed || version != _requestVersion) return;
            var total = data.Entries.Sum(item => (long)item.TotalSeconds);
            _dayCountValue.Text = $"{dayCount}{UiText.T(" days", "일")}";
            _trackedValue.Text = UsageTableStyle.Duration(total);
            _averageValue.Text = UsageTableStyle.Duration(total / dayCount);
            _summary.Text = UiText.T("Hours:minutes:seconds · Average includes days without records · Today is counted up to now",
                "시:분:초 · 기록 없는 날도 일평균에 포함 · 오늘은 현재까지 집계");
            _applications.Rows.Clear();
            var entriesByProcess = data.Entries.ToDictionary(entry => entry.ProcessName, StringComparer.OrdinalIgnoreCase);
            foreach (var row in data.Groups)
            {
                var name = row.IsGroup ? $"{row.Name} {UiText.T("(group)", "(그룹)")}" : row.Name;
                var process = row.IsGroup ? null : row.Processes[0];
                string? path = process is not null && entriesByProcess.TryGetValue(process, out var entry) ? entry.ExePath : null;
                var icon = row.IsGroup ? _groupIcon : process is null || string.Equals(process, IdleSentinel.Name, StringComparison.OrdinalIgnoreCase)
                    ? _iconCache.EmptyImage : _iconCache.GetOrQueue(process, path).Image ?? _iconCache.EmptyImage;
                var index = _applications.Rows.Add(icon, name, UiText.Category(row.Category ?? (row.IsGroup ? UiText.T("Mixed", "혼합") : "—")),
                    UsageTableStyle.Duration(row.Seconds), UsageTableStyle.Duration(row.Seconds / dayCount),
                    $"{(total > 0 ? row.Seconds * 100d / total : 0):0.0}%");
                _applications.Rows[index].Tag = process is null ? null : new IconKey(process, path);
                if (row.IsGroup) _applications.Rows[index].Cells["Name"].ToolTipText = string.Join(", ", row.Processes);
            }
            _categories.Rows.Clear();
            foreach (var row in UsageComparison.Create(data.Entries, []).Where(row => row.Name == "Idle" || row.Name.StartsWith("Category: ", StringComparison.Ordinal)))
                _categories.Rows.Add(row.Name == "Idle" ? UiText.T("Idle", "자리 비움") : UiText.Category(row.Name[10..]), "—",
                    UsageTableStyle.Duration(row.CurrentSeconds), UsageTableStyle.Duration(row.CurrentSeconds / dayCount),
                    $"{(total > 0 ? row.CurrentSeconds * 100d / total : 0):0.0}%");
            _applications.ClearSelection(); _categories.ClearSelection();
        }
        catch (Exception ex)
        {
            if (!IsDisposed && version == _requestVersion)
                _summary.Text = $"{UiText.T("Could not load statistics", "통계를 불러오지 못했습니다")}: {ex.Message}";
        }
    }

    void OnIconResolved(object? sender, IconResolvedEventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || Disposing) return;
                foreach (DataGridViewRow row in _applications.Rows)
                    if (row.Tag is IconKey key && string.Equals(key.Process, e.ProcessName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(key.Path, e.RequestedPath, StringComparison.OrdinalIgnoreCase))
                        row.Cells["Icon"].Value = e.Lookup.Image ?? _iconCache.EmptyImage;
            });
        }
        catch (InvalidOperationException) { /* The control was closed while the icon was loading. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _iconCache.IconResolved -= OnIconResolved;
            base.Dispose(true);
            _groupIcon.Dispose();
            if (_ownsIconCache) _iconCache.Dispose();
            return;
        }
        base.Dispose(false);
    }

    static Image CreateGroupIcon()
    {
        var image = new Bitmap(20, 20);
        using var graphics = Graphics.FromImage(image);
        using var brush = new SolidBrush(SystemColors.Highlight);
        graphics.FillRectangle(brush, 2, 2, 7, 7);
        graphics.FillRectangle(brush, 11, 2, 7, 7);
        graphics.FillRectangle(brush, 2, 11, 7, 7);
        graphics.FillRectangle(brush, 11, 11, 7, 7);
        return image;
    }

    sealed record IconKey(string Process, string? Path);

    static DataGridView CreateGrid(bool showIcons = false)
    {
        var grid = new BufferedDataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
        };
        if (showIcons)
            grid.Columns.Add(new DataGridViewImageColumn { Name = "Icon", HeaderText = "", ImageLayout = DataGridViewImageCellLayout.Zoom,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.None, Width = 30, Resizable = DataGridViewTriState.False });
        grid.Columns.Add("Name", UiText.T("Name", "이름"));
        grid.Columns.Add("Category", UiText.T("Category", "분류"));
        grid.Columns.Add("Duration", UiText.T("Duration", "사용 시간"));
        grid.Columns.Add("Average", UiText.T("Daily average", "일평균"));
        grid.Columns.Add("Share", UiText.T("Share", "비율"));
        UsageTableStyle.Apply(grid, "Duration", "Average", "Share");
        grid.Columns["Name"].FillWeight = 135;
        grid.Columns["Share"].FillWeight = 70;
        return grid;
    }
}
