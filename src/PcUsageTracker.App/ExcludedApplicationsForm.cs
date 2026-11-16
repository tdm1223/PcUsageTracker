using PcUsageTracker.Core.Models;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.App;

internal sealed class ExcludedApplicationsForm : Form
{
    readonly SqliteStore _store;
    readonly Action _onChanged;
    readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
        RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = true, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
    };

    public ExcludedApplicationsForm(SqliteStore store, Action onChanged)
    {
        _store = store;
        _onChanged = onChanged;
        Text = UiText.T("Excluded applications", "추적 제외 목록");
        Size = new Size(650, 430);
        MinimumSize = new Size(480, 300);
        StartPosition = FormStartPosition.CenterParent;
        _grid.Columns.Add("Process", UiText.T("Process", "프로세스"));
        _grid.Columns.Add("Reason", UiText.T("Reason", "제외 사유"));
        _grid.Columns.Add("When", UiText.T("Excluded at", "제외 날짜"));
        var restore = new Button { AutoSize = true, Text = UiText.T("Resume tracking", "추적 다시 시작") };
        restore.Click += (_, _) => RestoreSelected();
        var close = new Button { AutoSize = true, Text = UiText.T("Close", "닫기"), DialogResult = DialogResult.OK };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6) };
        buttons.Controls.Add(restore);
        buttons.Controls.Add(close);
        Controls.Add(_grid);
        Controls.Add(buttons);
        Reload();
    }

    void Reload()
    {
        _grid.Rows.Clear();
        foreach (var entry in _store.ListExclusionEntries())
        {
            var reason = entry.Reason == "system-ui" ? UiText.T("Default system UI", "기본 시스템 UI") :
                entry.Reason == "user-hidden" ? UiText.T("Excluded by user", "사용자가 제외") : entry.Reason ?? "—";
            var index = _grid.Rows.Add(entry.ProcessName, reason, entry.ExcludedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            _grid.Rows[index].Tag = entry;
        }
        _grid.ClearSelection();
    }

    void RestoreSelected()
    {
        var selected = _grid.SelectedRows.Cast<DataGridViewRow>().Select(row => row.Tag)
            .OfType<ExcludedProcess>().Select(item => item.ProcessName).ToArray();
        if (selected.Length == 0) return;
        try
        {
            _store.RemoveExclusions(selected);
            _onChanged();
            Reload();
        }
        catch (Exception ex)
        {
            _onChanged();
            Reload();
            MessageBox.Show(this, ex.Message, UiText.T("Could not update exclusions", "제외 목록을 변경하지 못했습니다"),
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
