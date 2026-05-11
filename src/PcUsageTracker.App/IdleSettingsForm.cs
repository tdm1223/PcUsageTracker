namespace PcUsageTracker.App;

/// <summary>
/// "Idle threshold" 설정 다이얼로그. NumericUpDown으로 1~60분을 받고 DialogResult로 확정.
/// </summary>
internal sealed class IdleSettingsForm : Form
{
    const int MinMinutes = 1;
    const int MaxMinutes = 60;

    readonly NumericUpDown _input;

    public IdleSettingsForm(int currentMinutes)
    {
        Text = "Idle threshold";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(320, 130);

        var label = new Label
        {
            Text = "Stop tracking after this many minutes of no input:",
            Location = new Point(12, 14),
            AutoSize = true,
        };

        _input = new NumericUpDown
        {
            Minimum = MinMinutes,
            Maximum = MaxMinutes,
            Value = Math.Clamp(currentMinutes, MinMinutes, MaxMinutes),
            Location = new Point(12, 44),
            Width = 80,
            TextAlign = HorizontalAlignment.Right,
        };
        var unitLabel = new Label
        {
            Text = "minutes (1–60)",
            Location = new Point(100, 47),
            AutoSize = true,
        };

        var ok = new Button
        {
            Text = "OK",
            DialogResult = DialogResult.OK,
            Location = new Point(140, 90),
            Width = 80,
        };
        var cancel = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            Location = new Point(228, 90),
            Width = 80,
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { label, _input, unitLabel, ok, cancel });
    }

    public int Minutes => (int)_input.Value;
}
