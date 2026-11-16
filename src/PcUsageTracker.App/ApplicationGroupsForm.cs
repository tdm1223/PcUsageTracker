using PcUsageTracker.Core.Reporting;
using PcUsageTracker.Core.Storage;

namespace PcUsageTracker.App;

internal sealed class ApplicationGroupsForm : Form
{
    readonly SqliteStore _store;
    readonly ListBox _groups = new() { Dock = DockStyle.Fill };
    readonly CheckedListBox _applications = new() { Dock = DockStyle.Fill, CheckOnClick = true };
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    List<ApplicationGroup> _items = [];
    bool _loading;

    public ApplicationGroupsForm(SqliteStore store)
    {
        _store = store;
        Text = UiText.T("Application groups", "프로세스 그룹 관리");
        Size = new Size(760, 550);
        MinimumSize = new Size(560, 400);
        StartPosition = FormStartPosition.CenterParent;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 2, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(new Label { Text = UiText.T("Groups", "그룹"), AutoSize = true }, 0, 0);
        layout.Controls.Add(new Label { Text = UiText.T("Applications in the selected group", "그룹에 포함할 앱 선택"), AutoSize = true }, 1, 0);
        layout.Controls.Add(_groups, 0, 1);
        layout.Controls.Add(_applications, 1, 1);
        layout.Controls.Add(new Label { Text = UiText.T("Group name", "그룹 이름"), AutoSize = true }, 0, 2);
        layout.Controls.Add(_name, 1, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        var add = new Button { AutoSize = true, Text = UiText.T("New", "새 그룹") };
        var save = new Button { AutoSize = true, Text = UiText.T("Save group", "그룹 저장") };
        var delete = new Button { AutoSize = true, Text = UiText.T("Delete group", "그룹 삭제") };
        var close = new Button { AutoSize = true, Text = UiText.T("Close", "닫기"), DialogResult = DialogResult.OK };
        actions.Controls.AddRange([add, save, delete, close]);
        layout.Controls.Add(actions, 0, 3);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        _groups.SelectedIndexChanged += (_, _) => ShowSelected();
        add.Click += (_, _) => { _groups.ClearSelected(); _name.Clear(); SetChecked([]); _name.Focus(); };
        save.Click += (_, _) => SaveSelected();
        delete.Click += (_, _) => DeleteSelected();
        LoadGroups();
    }

    void LoadGroups(string? selected = null)
    {
        _loading = true;
        try
        {
            _items = ApplicationGroups.Load(_store.Connection).ToList();
            var known = _store.ListKnownApplications();
            var choices = known.Select(app => new Choice(app.ProcessName,
                    string.Equals(app.DisplayName, app.ProcessName, StringComparison.OrdinalIgnoreCase)
                        ? app.ProcessName : $"{app.DisplayName} ({app.ProcessName})"))
                .ToDictionary(choice => choice.Process, StringComparer.OrdinalIgnoreCase);
            foreach (var process in _items.SelectMany(item => item.Processes))
                choices.TryAdd(process, new Choice(process, process));
            _applications.Items.Clear();
            foreach (var choice in choices.Values.OrderBy(item => item.Label, StringComparer.CurrentCultureIgnoreCase))
                _applications.Items.Add(choice);
            _groups.Items.Clear();
            foreach (var group in _items) _groups.Items.Add(group.Name);
            if (selected is not null)
                _groups.SelectedIndex = _items.FindIndex(group => string.Equals(group.Name, selected, StringComparison.OrdinalIgnoreCase));
        }
        finally { _loading = false; }
        ShowSelected();
    }

    void ShowSelected()
    {
        if (_loading) return;
        var index = _groups.SelectedIndex;
        if (index < 0 || index >= _items.Count) { _name.Clear(); SetChecked([]); return; }
        _name.Text = _items[index].Name;
        SetChecked(_items[index].Processes);
    }

    void SetChecked(IEnumerable<string> processes)
    {
        var names = processes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _applications.Items.Count; i++)
            _applications.SetItemChecked(i, names.Contains(((Choice)_applications.Items[i]).Process));
    }

    void SaveSelected()
    {
        var next = _items.ToList();
        if (_groups.SelectedIndex >= 0) next.RemoveAt(_groups.SelectedIndex);
        var name = _name.Text.Trim();
        var processes = _applications.CheckedItems.Cast<Choice>().Select(item => item.Process).ToArray();
        string? error = null;
        if (name.Length == 0) error = UiText.T("Enter a group name.", "그룹 이름을 입력하세요.");
        else if (name.Length > 100) error = UiText.T("Group names must be at most 100 characters.", "그룹 이름은 100자 이내로 입력하세요.");
        else if (processes.Length == 0) error = UiText.T("Select at least one application.", "앱을 하나 이상 선택하세요.");
        else if (next.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            error = UiText.T("That group name is already in use.", "같은 이름의 그룹이 이미 있습니다.");
        else
        {
            var used = next.SelectMany(item => item.Processes).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var duplicate = processes.FirstOrDefault(used.Contains);
            if (duplicate is not null)
                error = UiText.T($"'{duplicate}' is already assigned to another group.", $"'{duplicate}' 앱이 이미 다른 그룹에 포함되어 있습니다.");
        }
        if (error is not null)
        {
            MessageBox.Show(this, error, UiText.T("Could not save group", "그룹을 저장하지 못했습니다"), MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        next.Add(new ApplicationGroup(name, processes));
        try { ApplicationGroups.Save(_store, next); LoadGroups(_name.Text.Trim()); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText.T("Could not save group", "그룹을 저장하지 못했습니다"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    void DeleteSelected()
    {
        var index = _groups.SelectedIndex;
        if (index < 0) return;
        var next = _items.ToList();
        next.RemoveAt(index);
        try { ApplicationGroups.Save(_store, next); LoadGroups(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, UiText.T("Could not delete group", "그룹을 삭제하지 못했습니다"), MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    sealed record Choice(string Process, string Label)
    {
        public override string ToString() => Label;
    }
}
