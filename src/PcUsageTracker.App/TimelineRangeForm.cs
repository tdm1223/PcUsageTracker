using PcUsageTracker.Core.Reporting;

namespace PcUsageTracker.App;

internal sealed class TimelineRangeForm : Form
{
    public TimelineRangeForm(DateTimeOffset from, DateTimeOffset to, IReadOnlyList<TimelineRangeRow> rows)
    {
        Text = UiText.T("Selected time range", "선택한 시간 구간");
        Size = new Size(680, 420);
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent;
        var total = rows.Sum(row => row.TotalSeconds);
        var localFrom = TimeZoneInfo.ConvertTime(from, TimeZoneInfo.Local);
        var localTo = TimeZoneInfo.ConvertTime(to, TimeZoneInfo.Local);
        var header = new Label
        {
            Dock = DockStyle.Top, Height = 58, Padding = new Padding(10),
            Text = $"{localFrom:yyyy-MM-dd HH:mm:ss} – {localTo:yyyy-MM-dd HH:mm:ss}\n" +
                   $"{UiText.T("Recorded total", "기록 합계")}: {FormatDuration(total)}",
        };
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = SystemColors.Window,
        };
        grid.Columns.Add("Application", UiText.T("Application", "앱"));
        grid.Columns.Add("Category", UiText.T("Category", "분류"));
        grid.Columns.Add("Duration", UiText.T("Duration", "사용 시간"));
        grid.Columns.Add("Share", UiText.T("Share", "비율"));
        foreach (var row in rows)
            grid.Rows.Add(row.IsIdle ? UiText.T(row.DisplayName, "(자리 비움)") : row.DisplayName, row.IsIdle ? UiText.T("Idle", "자리 비움") : UiText.Category(row.CategoryName ?? "Other"),
                FormatDuration(row.TotalSeconds), $"{(total > 0 ? 100d * row.TotalSeconds / total : 0):0.0}%");
        grid.ClearSelection();
        Controls.Add(grid);
        Controls.Add(header);
    }

    internal static string FormatDuration(long seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? $"{(long)time.TotalHours}:{time.Minutes:D2}:{time.Seconds:D2}" : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }
}
