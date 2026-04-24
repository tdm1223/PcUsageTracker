using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Sampling;
using Serilog;

namespace PcUsageTracker.App;

internal sealed class ReportForm : Form
{
    const int TodayWeekTopN = 5;
    const int AllTimeTopN = 20;
    const int RefreshMs = 5000;
    const int IconColWidth = 24;

    readonly Aggregator _agg;
    readonly IClock _clock;
    readonly DataGridView _todayGrid;
    readonly DataGridView _weekGrid;
    readonly DataGridView _allGrid;
    readonly System.Windows.Forms.Timer _refresh;
    readonly IconCache _iconCache = new();

    public ReportForm(Aggregator agg, IClock clock)
    {
        _agg = agg;
        _clock = clock;

        Text = "PcUsageTracker — Report";
        Size = new Size(880, 520);
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(640, 400);

        var tabs = new TabControl { Dock = DockStyle.Fill };

        var tab1 = new TabPage("Today + Week");
        var split = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
        };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));

        _todayGrid = BuildGrid();
        _weekGrid = BuildGrid();
        split.Controls.Add(WrapWithHeader("Today (top 5)", _todayGrid), 0, 0);
        split.Controls.Add(WrapWithHeader("This week (top 5)", _weekGrid), 1, 0);
        tab1.Controls.Add(split);

        var tab2 = new TabPage("All-time top 20");
        _allGrid = BuildGrid();
        tab2.Controls.Add(WrapWithHeader("All-time top 20", _allGrid));

        tabs.TabPages.Add(tab1);
        tabs.TabPages.Add(tab2);
        Controls.Add(tabs);

        _refresh = new System.Windows.Forms.Timer { Interval = RefreshMs };
        _refresh.Tick += (_, _) => ReloadAll();
        Shown += (_, _) => { ReloadAll(); _refresh.Start(); };
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

    static DataGridView BuildGrid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
            BorderStyle = BorderStyle.Fixed3D,
            RowTemplate = { Height = 22 },
        };
        var iconCol = new DataGridViewImageColumn
        {
            Name = "Icon",
            HeaderText = "",
            ImageLayout = DataGridViewImageCellLayout.Zoom,
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
            Name = "Bar",
            HeaderText = "",
            FillWeight = 35,
        });
        g.CellPainting += OnCellPainting;
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

    static void OnCellPainting(object? sender, DataGridViewCellPaintingEventArgs e)
    {
        if (e.RowIndex < 0 || e.ColumnIndex < 0) return;
        var grid = (DataGridView)sender!;
        if (grid.Columns[e.ColumnIndex].Name != "Bar") return;

        e.PaintBackground(e.ClipBounds, true);
        if (e.Value is double ratio && ratio > 0 && e.Graphics is { } g)
        {
            var pad = 3;
            var cw = e.CellBounds.Width - pad * 2;
            var barW = Math.Max(1, (int)(cw * Math.Clamp(ratio, 0.0, 1.0)));
            var rect = new Rectangle(e.CellBounds.X + pad, e.CellBounds.Y + pad + 2, barW, e.CellBounds.Height - pad * 2 - 4);
            using var brush = new SolidBrush(Color.SteelBlue);
            g.FillRectangle(brush, rect);
        }
        e.Handled = true;
    }

    void ReloadAll()
    {
        var now = _clock.UtcNow;
        var (todayFrom, todayTo) = Aggregator.TodayRange(now);
        var (weekFrom, weekTo) = Aggregator.ThisWeekRange(now);

        Populate(_todayGrid, _agg.TopN(todayFrom, todayTo, now, TodayWeekTopN));
        Populate(_weekGrid, _agg.TopN(weekFrom, weekTo, now, TodayWeekTopN));
        Populate(_allGrid, _agg.AllTime(now, AllTimeTopN));
    }

    void Populate(DataGridView grid, IReadOnlyList<ReportEntry> entries)
    {
        grid.Rows.Clear();
        if (entries.Count == 0) return;

        var max = entries.Max(r => r.TotalSeconds);
        foreach (var e in entries)
        {
            var ratio = max > 0 ? (double)e.TotalSeconds / max : 0.0;
            var icon = _iconCache.Get(e.ExePath);
            var idx = grid.Rows.Add(icon, e.ProcessName, FormatDuration(e.TotalSeconds), ratio);
            grid.Rows[idx].Cells["Bar"].ToolTipText = $"{ratio * 100:0.0}% of top";
            if (!string.IsNullOrEmpty(e.ExePath))
                grid.Rows[idx].Cells["Process"].ToolTipText = e.ExePath;
        }
    }

    static string FormatDuration(int seconds)
    {
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
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
            ReloadAll();
            _refresh.Start();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _refresh.Stop();
            _refresh.Dispose();
            _iconCache.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class IconCache : IDisposable
{
    readonly Dictionary<string, Image?> _map = new(StringComparer.OrdinalIgnoreCase);

    public Image? Get(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        if (_map.TryGetValue(exePath, out var cached)) return cached;

        Image? img = null;
        try
        {
            if (File.Exists(exePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                {
                    img = icon.ToBitmap();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ExtractAssociatedIcon failed for {Path}", exePath);
        }
        _map[exePath] = img;
        return img;
    }

    public void Dispose()
    {
        foreach (var img in _map.Values) img?.Dispose();
        _map.Clear();
    }
}
