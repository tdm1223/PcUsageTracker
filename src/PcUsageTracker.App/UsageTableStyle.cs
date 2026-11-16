namespace PcUsageTracker.App;

/// <summary>Shared visual treatment for the detailed statistics tables.</summary>
internal static class UsageTableStyle
{
    public static readonly Font BodyFont = new(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 1f);
    public static readonly Font StrongFont = new(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 1f, FontStyle.Bold);
    public static readonly Font ValueFont = new(SystemFonts.DefaultFont.FontFamily, SystemFonts.DefaultFont.Size + 4f, FontStyle.Bold);
    public static readonly Color SummaryBack = Color.FromArgb(235, 242, 251);
    public static readonly Color IncreaseColor = Color.FromArgb(24, 79, 151);
    public static readonly Color DecreaseColor = Color.FromArgb(93, 71, 133);

    public static void Apply(DataGridView grid, params string[] numericColumns)
    {
        grid.Font = BodyFont;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = SystemInformation.HighContrast ? SystemColors.ControlText : Color.FromArgb(226, 231, 237);
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = Math.Max(32, (int)Math.Round(34 * grid.DeviceDpi / 96d));
        grid.RowTemplate.Height = Math.Max(27, (int)Math.Round(30 * grid.DeviceDpi / 96d));
        grid.ColumnHeadersDefaultCellStyle.Font = StrongFont;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(232, 237, 244);
        grid.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.DefaultCellStyle.ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(34, 43, 55);
        grid.DefaultCellStyle.Padding = new Padding(6, 0, 6, 0);
        grid.DefaultCellStyle.SelectionBackColor = SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(218, 231, 248);
        grid.DefaultCellStyle.SelectionForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Color.FromArgb(34, 43, 55);
        grid.AlternatingRowsDefaultCellStyle.BackColor = SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(248, 250, 252);
        foreach (var name in numericColumns)
        {
            grid.Columns[name].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns[name].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
        }
    }

    public static string Duration(long seconds)
    {
        var value = TimeSpan.FromSeconds(seconds);
        return $"{(long)value.TotalHours}:{value.Minutes:D2}:{value.Seconds:D2}";
    }
}
