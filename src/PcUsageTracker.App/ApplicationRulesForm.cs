using Microsoft.Data.Sqlite;
using PcUsageTracker.Core.Models;
using PcUsageTracker.Core.Storage;
using Serilog;

namespace PcUsageTracker.App;

/// <summary>Edits category definitions and presentation rules without changing session identities.</summary>
internal sealed class ApplicationRulesForm : Form
{
    readonly SqliteStore _store;
    readonly DataGridView _applicationsGrid = BuildReadOnlyGrid();
    readonly DataGridView _categoriesGrid = BuildReadOnlyGrid();
    readonly Label _selectedProcessLabel = new() { AutoSize = true, Text = "Select an application above." };
    readonly TextBox _aliasText = new() { Dock = DockStyle.Fill };
    readonly ComboBox _categoryCombo = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    readonly CheckBox _useCustomColor = new() { Text = "Override", AutoSize = true };
    readonly Button _colorButton = new() { Text = "#94A3B8", AutoSize = true, Enabled = false };
    readonly Button _saveRuleButton = new() { Text = "Save changes", AutoSize = true, Enabled = false };
    readonly Button _clearRuleButton = new() { Text = "Clear customizations", AutoSize = true, Enabled = false };
    readonly Button _editCategoryButton = new() { Text = "Edit...", AutoSize = true };
    readonly Button _deleteCategoryButton = new() { Text = "Delete", AutoSize = true };
    bool _loading;
    int _selectedColor = DefaultApplicationCategories.OtherColor;
    string? _selectedProcessName;
    string? _loadedAlias;
    long? _loadedCategoryId;
    int? _loadedColorOverrideRgb;

    public ApplicationRulesForm(SqliteStore store, string? initialProcessName = null)
    {
        _store = store;
        _applicationsGrid.MultiSelect = true;
        Text = "Applications & categories";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(920, 640);
        MinimumSize = new Size(720, 480);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildApplicationsTab());
        tabs.TabPages.Add(BuildCategoriesTab());

        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 44,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(6),
        };
        bottom.Controls.Add(close);
        Controls.Add(tabs);
        Controls.Add(bottom);

        _applicationsGrid.SelectionChanged += (_, _) => OnApplicationSelectionChanged();
        _applicationsGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) _aliasText.Focus();
        };
        _useCustomColor.CheckedChanged += (_, _) => _colorButton.Enabled = _useCustomColor.Checked;
        _categoryCombo.SelectionChangeCommitted += (_, _) => SaveCategoryImmediately();
        _colorButton.Click += (_, _) => ChooseColor(_colorButton, ref _selectedColor);
        _saveRuleButton.Click += (_, _) => SaveSelectedRule();
        _clearRuleButton.Click += (_, _) => ClearSelectedRule();

        Shown += (_, _) => ReloadAll(initialProcessName);
        FormClosing += (_, e) =>
        {
            if (!TrySavePendingRule(reloadApplications: false)) e.Cancel = true;
        };
    }

    TabPage BuildApplicationsTab()
    {
        var page = new TabPage("Applications");
        _applicationsGrid.Columns.Add("Process", "Process identity");
        _applicationsGrid.Columns.Add("Alias", "Display name");
        _applicationsGrid.Columns.Add("Category", "Category");
        _applicationsGrid.Columns.Add("Color", "Color");
        _applicationsGrid.Columns.Add("Path", "Executable path");
        _applicationsGrid.Columns[0].FillWeight = 25;
        _applicationsGrid.Columns[1].FillWeight = 25;
        _applicationsGrid.Columns[2].FillWeight = 18;
        _applicationsGrid.Columns[3].FillWeight = 12;
        _applicationsGrid.Columns[4].FillWeight = 45;

        var editor = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 4,
            RowCount = 3,
            Padding = new Padding(8),
        };
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        editor.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        editor.Controls.Add(_selectedProcessLabel, 0, 0);
        editor.SetColumnSpan(_selectedProcessLabel, 4);
        editor.Controls.Add(new Label { Text = "Alias", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        editor.Controls.Add(_aliasText, 1, 1);
        editor.Controls.Add(new Label { Text = "Category", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        editor.Controls.Add(_categoryCombo, 3, 1);

        var colorFlow = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        colorFlow.Controls.Add(_useCustomColor);
        colorFlow.Controls.Add(_colorButton);
        editor.Controls.Add(new Label { Text = "Color", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        editor.Controls.Add(colorFlow, 1, 2);

        var actions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        actions.Controls.Add(_saveRuleButton);
        actions.Controls.Add(_clearRuleButton);
        actions.Controls.Add(new Label
        {
            Text = "Category choices save immediately. Ctrl/Shift selects multiple rows and applies to all.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Margin = new Padding(8, 7, 0, 0),
        });
        editor.Controls.Add(actions, 2, 2);
        editor.SetColumnSpan(actions, 2);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
        layout.Controls.Add(_applicationsGrid, 0, 0);
        layout.Controls.Add(editor, 0, 1);
        page.Controls.Add(layout);
        return page;
    }

    TabPage BuildCategoriesTab()
    {
        var page = new TabPage("Categories");
        _categoriesGrid.Columns.Add("Name", "Name");
        _categoriesGrid.Columns.Add("Color", "Color");
        _categoriesGrid.Columns.Add("SortOrder", "Sort order");
        _categoriesGrid.Columns[0].FillWeight = 60;
        _categoriesGrid.Columns[1].FillWeight = 25;
        _categoriesGrid.Columns[2].FillWeight = 20;

        var add = new Button { Text = "Add...", AutoSize = true };
        add.Click += (_, _) => AddCategory();
        _editCategoryButton.Click += (_, _) => EditSelectedCategory();
        _deleteCategoryButton.Click += (_, _) => DeleteSelectedCategory();
        _categoriesGrid.SelectionChanged += (_, _) => UpdateCategoryButtons();
        _categoriesGrid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex >= 0) EditSelectedCategory();
        };

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 42,
            Padding = new Padding(6),
        };
        actions.Controls.Add(add);
        actions.Controls.Add(_editCategoryButton);
        actions.Controls.Add(_deleteCategoryButton);
        page.Controls.Add(_categoriesGrid);
        page.Controls.Add(actions);
        return page;
    }

    static DataGridView BuildReadOnlyGrid() => new BufferedDataGridView
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        RowHeadersVisible = false,
        MultiSelect = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = SystemColors.Window,
    };

    void ReloadAll(string? selectProcess = null)
    {
        _loading = true;
        try
        {
            var categories = _store.ListCategories();
            _categoryCombo.Items.Clear();
            _categoryCombo.Items.Add(new CategoryChoice(null, "Other / unassigned"));
            foreach (var category in categories)
                _categoryCombo.Items.Add(new CategoryChoice(category.Id, category.Name));

            _categoriesGrid.Rows.Clear();
            foreach (var category in categories)
            {
                var index = _categoriesGrid.Rows.Add(category.Name, ToHex(category.ColorRgb), category.SortOrder);
                var row = _categoriesGrid.Rows[index];
                row.Tag = category;
                ApplyColorCell(row.Cells["Color"], category.ColorRgb);
            }

            ReloadApplications(selectProcess ?? _selectedProcessName);
        }
        finally
        {
            _loading = false;
            LoadSelectedApplication();
            UpdateCategoryButtons();
        }
    }

    void ReloadApplications(string? selectProcess) => ReloadApplications(
        selectProcess is null ? null : new[] { selectProcess });

    void ReloadApplications(IReadOnlyCollection<string>? selectProcesses)
    {
        var wanted = selectProcesses is null
            ? null
            : new HashSet<string>(selectProcesses, StringComparer.OrdinalIgnoreCase);
        _applicationsGrid.Rows.Clear();
        foreach (var app in _store.ListKnownApplications())
        {
            var index = _applicationsGrid.Rows.Add(
                app.ProcessName, app.DisplayName, app.CategoryName, ToHex(app.ResolvedColorRgb), app.ExePath ?? string.Empty);
            var row = _applicationsGrid.Rows[index];
            row.Tag = app;
            ApplyColorCell(row.Cells["Color"], app.ResolvedColorRgb);
        }

        _applicationsGrid.ClearSelection();
        var selected = wanted is null
            ? []
            : _applicationsGrid.Rows.Cast<DataGridViewRow>()
                .Where(row => row.Tag is KnownApplication app && wanted.Contains(app.ProcessName))
                .ToArray();
        if (selected.Length > 0)
        {
            _applicationsGrid.CurrentCell = selected[0].Cells[0];
            foreach (var row in selected) row.Selected = true;
        }
        else if (_applicationsGrid.Rows.Count > 0)
        {
            _applicationsGrid.Rows[0].Selected = true;
            _applicationsGrid.CurrentCell = _applicationsGrid.Rows[0].Cells[0];
        }
    }

    void LoadSelectedApplication()
    {
        if (_loading) return;
        var selected = _applicationsGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<KnownApplication>()
            .ToArray();
        var app = selected.Length == 1 ? selected[0] : (KnownApplication?)null;
        _selectedProcessName = app?.ProcessName;
        var enabled = app is not null;
        _aliasText.Enabled = enabled;
        _categoryCombo.Enabled = enabled || selected.Length > 1;
        _useCustomColor.Enabled = enabled;
        _saveRuleButton.Enabled = enabled;
        _clearRuleButton.Enabled = enabled;

        if (selected.Length > 1)
        {
            _selectedProcessLabel.Text = $"{selected.Length} applications selected";
            _aliasText.Text = string.Empty;
            _categoryCombo.SelectedIndex = -1;
            _useCustomColor.Checked = false;
            _colorButton.Enabled = false;
            _loadedAlias = null;
            _loadedCategoryId = null;
            _loadedColorOverrideRgb = null;
            return;
        }

        if (app is null)
        {
            _selectedProcessLabel.Text = "Select an application above.";
            _aliasText.Text = string.Empty;
            _categoryCombo.SelectedIndex = -1;
            _useCustomColor.Checked = false;
            _colorButton.Enabled = false;
            _loadedAlias = null;
            _loadedCategoryId = null;
            _loadedColorOverrideRgb = null;
            return;
        }

        var value = app.Value;
        _selectedProcessLabel.Text = $"Process identity: {value.ProcessName}";
        _aliasText.Text = value.Alias ?? string.Empty;
        SelectCategory(value.CategoryId);
        _selectedColor = value.ColorOverrideRgb ?? value.ResolvedColorRgb;
        _useCustomColor.Checked = value.ColorOverrideRgb is not null;
        UpdateColorButton(_colorButton, _selectedColor);
        _loadedAlias = NormalizeAlias(value.Alias);
        _loadedCategoryId = value.CategoryId;
        _loadedColorOverrideRgb = value.ColorOverrideRgb;
    }

    void OnApplicationSelectionChanged()
    {
        if (_loading) return;
        var selected = _applicationsGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<KnownApplication>()
            .ToArray();
        var nextProcess = selected.Length == 1 ? selected[0].ProcessName : null;
        var selectionChanged = selected.Length != 1 ||
            !string.Equals(nextProcess, _selectedProcessName, StringComparison.OrdinalIgnoreCase);
        if (selectionChanged && !TrySavePendingRule(reloadApplications: false))
        {
            RestoreApplicationSelection(_selectedProcessName);
            return;
        }
        LoadSelectedApplication();
    }

    bool TrySavePendingRule(string? selectAfterSave = null, bool reloadApplications = true)
    {
        if (_selectedProcessName is null || !HasUnsavedRuleChanges()) return true;
        try
        {
            PersistSelectedRule();
            CaptureLoadedRuleValues();
            if (reloadApplications)
            {
                var selection = selectAfterSave ?? _selectedProcessName;
                _loading = true;
                try { ReloadApplications(selection); }
                finally { _loading = false; }
                LoadSelectedApplication();
            }
            else
            {
                RefreshApplicationRow(_selectedProcessName);
            }
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not auto-save the application rule. Your edits are still shown.", ex);
            return false;
        }
    }

    bool HasUnsavedRuleChanges()
    {
        var alias = NormalizeAlias(_aliasText.Text);
        var categoryId = (_categoryCombo.SelectedItem as CategoryChoice)?.Id;
        var color = _useCustomColor.Checked ? _selectedColor : (int?)null;
        return !string.Equals(alias, _loadedAlias, StringComparison.Ordinal) ||
               categoryId != _loadedCategoryId ||
               color != _loadedColorOverrideRgb;
    }

    void PersistSelectedRule()
    {
        if (_selectedProcessName is null) return;
        var alias = NormalizeAlias(_aliasText.Text);
        var categoryId = (_categoryCombo.SelectedItem as CategoryChoice)?.Id;
        var color = _useCustomColor.Checked ? _selectedColor : (int?)null;
        if (alias is null && categoryId is null && color is null)
            _store.DeleteApplicationRule(_selectedProcessName);
        else
            _store.UpsertApplicationRule(_selectedProcessName, alias, categoryId, color, DateTimeOffset.UtcNow);
    }

    void CaptureLoadedRuleValues()
    {
        _loadedAlias = NormalizeAlias(_aliasText.Text);
        _loadedCategoryId = (_categoryCombo.SelectedItem as CategoryChoice)?.Id;
        _loadedColorOverrideRgb = _useCustomColor.Checked ? _selectedColor : null;
    }

    void RefreshApplicationRow(string processName)
    {
        _loading = true;
        try
        {
            var updated = _store.ListKnownApplications().FirstOrDefault(app =>
                string.Equals(app.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            var row = _applicationsGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(candidate =>
                candidate.Tag is KnownApplication app &&
                string.Equals(app.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (row is null) return;
            if (string.IsNullOrEmpty(updated.ProcessName))
            {
                _applicationsGrid.Rows.Remove(row);
                return;
            }

            row.Tag = updated;
            row.Cells["Process"].Value = updated.ProcessName;
            row.Cells["Alias"].Value = updated.DisplayName;
            row.Cells["Category"].Value = updated.CategoryName;
            row.Cells["Color"].Value = ToHex(updated.ResolvedColorRgb);
            row.Cells["Path"].Value = updated.ExePath ?? string.Empty;
            ApplyColorCell(row.Cells["Color"], updated.ResolvedColorRgb);
            _applicationsGrid.Sort(_applicationsGrid.Columns["Alias"],
                System.ComponentModel.ListSortDirection.Ascending);
        }
        finally
        {
            _loading = false;
        }
    }

    void RestoreApplicationSelection(string? processName)
    {
        if (processName is null) return;
        _loading = true;
        try
        {
            var row = _applicationsGrid.Rows.Cast<DataGridViewRow>().FirstOrDefault(candidate =>
                candidate.Tag is KnownApplication app &&
                string.Equals(app.ProcessName, processName, StringComparison.OrdinalIgnoreCase));
            if (row is null) return;
            _applicationsGrid.ClearSelection();
            row.Selected = true;
            _applicationsGrid.CurrentCell = row.Cells[0];
        }
        finally
        {
            _loading = false;
        }
    }

    static string? NormalizeAlias(string? alias) => string.IsNullOrWhiteSpace(alias) ? null : alias.Trim();

    void SelectCategory(long? categoryId)
    {
        for (var i = 0; i < _categoryCombo.Items.Count; i++)
        {
            if (_categoryCombo.Items[i] is CategoryChoice choice && choice.Id == categoryId)
            {
                _categoryCombo.SelectedIndex = i;
                return;
            }
        }
        _categoryCombo.SelectedIndex = 0;
    }

    void SaveSelectedRule()
    {
        if (_selectedProcessName is null) return;
        try
        {
            PersistSelectedRule();
            CaptureLoadedRuleValues();
            ReloadAll(_selectedProcessName);
        }
        catch (Exception ex)
        {
            ShowError("Could not save the application rule.", ex);
        }
    }

    void ClearSelectedRule()
    {
        if (_selectedProcessName is null) return;
        try
        {
            _store.DeleteApplicationRule(_selectedProcessName);
            ReloadAll(_selectedProcessName);
        }
        catch (Exception ex)
        {
            ShowError("Could not clear the application rule.", ex);
        }
    }

    void ApplyCategoryToSelection()
    {
        var processNames = _applicationsGrid.SelectedRows.Cast<DataGridViewRow>()
            .Select(row => row.Tag)
            .OfType<KnownApplication>()
            .Select(app => app.ProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (processNames.Length < 2) return;
        if (_categoryCombo.SelectedItem is not CategoryChoice category)
        {
            MessageBox.Show(this, "Choose a category to apply to the selected applications.",
                "Bulk category", MessageBoxButtons.OK, MessageBoxIcon.Information);
            _categoryCombo.DroppedDown = true;
            return;
        }

        try
        {
            _store.SetApplicationCategories(processNames, category.Id, DateTimeOffset.UtcNow);
            _loading = true;
            try { ReloadApplications(processNames); }
            finally { _loading = false; }
            LoadSelectedApplication();
        }
        catch (Exception ex)
        {
            ShowError("Could not update the selected application categories.", ex);
        }
    }

    void SaveCategoryImmediately()
    {
        if (_loading || _categoryCombo.SelectedItem is not CategoryChoice) return;
        if (_applicationsGrid.SelectedRows.Count > 1)
        {
            ApplyCategoryToSelection();
            return;
        }

        if (_selectedProcessName is not null)
            TrySavePendingRule();
    }

    void AddCategory()
    {
        if (!TrySavePendingRule()) return;
        using var editor = new CategoryEditorForm(null);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _store.CreateCategory(editor.CategoryName, editor.ColorRgb, editor.SortOrder);
            ReloadAll();
        }
        catch (Exception ex)
        {
            ShowCategoryError(ex);
        }
    }

    void EditSelectedCategory()
    {
        if (!TrySavePendingRule()) return;
        if (_categoriesGrid.SelectedRows.Count == 0 ||
            _categoriesGrid.SelectedRows[0].Tag is not ApplicationCategory category) return;
        using var editor = new CategoryEditorForm(category);
        if (editor.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            _store.UpdateCategory(category.Id, editor.CategoryName, editor.ColorRgb, editor.SortOrder);
            ReloadAll();
        }
        catch (Exception ex)
        {
            ShowCategoryError(ex);
        }
    }

    void DeleteSelectedCategory()
    {
        if (!TrySavePendingRule()) return;
        if (_categoriesGrid.SelectedRows.Count == 0 ||
            _categoriesGrid.SelectedRows[0].Tag is not ApplicationCategory category) return;
        if (category.Id == DefaultApplicationCategories.OtherId) return;
        var result = MessageBox.Show(this,
            $"Delete category '{category.Name}'?\n\nApplications in it will become unassigned; their history is not changed.",
            "Delete category", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2);
        if (result != DialogResult.Yes) return;
        try
        {
            _store.DeleteCategory(category.Id);
            ReloadAll();
        }
        catch (Exception ex)
        {
            ShowError("Could not delete the category.", ex);
        }
    }

    void UpdateCategoryButtons()
    {
        var hasCategory = _categoriesGrid.SelectedRows.Count > 0 &&
                          _categoriesGrid.SelectedRows[0].Tag is ApplicationCategory;
        var canDelete = hasCategory &&
                        ((ApplicationCategory)_categoriesGrid.SelectedRows[0].Tag!).Id !=
                        DefaultApplicationCategories.OtherId;
        _editCategoryButton.Enabled = hasCategory;
        _deleteCategoryButton.Enabled = canDelete;
    }

    void ShowCategoryError(Exception ex)
    {
        var message = ex is SqliteException { SqliteErrorCode: 19 }
            ? "A category with that name already exists, or the entered values are invalid."
            : "Could not save the category.";
        ShowError(message, ex);
    }

    void ShowError(string message, Exception ex)
    {
        Log.Error(ex, "Application/category editor operation failed");
        MessageBox.Show(this, $"{message}\n\n{ex.Message}", "Error",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    internal static void ChooseColor(Button button, ref int target)
    {
        using var dialog = new ColorDialog { Color = FromRgb(target), FullOpen = true };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        target = ToRgb(dialog.Color);
        UpdateColorButton(button, target);
    }

    static void ApplyColorCell(DataGridViewCell cell, int rgb)
    {
        var color = FromRgb(rgb);
        cell.Style.BackColor = color;
        cell.Style.ForeColor = ContrastColor(color);
        cell.Style.SelectionBackColor = color;
        cell.Style.SelectionForeColor = ContrastColor(color);
    }

    internal static void UpdateColorButton(Button button, int rgb)
    {
        var color = FromRgb(rgb);
        button.Text = ToHex(rgb);
        button.BackColor = color;
        button.ForeColor = ContrastColor(color);
        button.UseVisualStyleBackColor = false;
    }

    static Color FromRgb(int rgb) => Color.FromArgb((rgb >> 16) & 0xff, (rgb >> 8) & 0xff, rgb & 0xff);
    static int ToRgb(Color color) => (color.R << 16) | (color.G << 8) | color.B;
    static string ToHex(int rgb) => $"#{rgb:X6}";
    static Color ContrastColor(Color color) =>
        color.R * 299 + color.G * 587 + color.B * 114 >= 150_000 ? Color.Black : Color.White;

    sealed record CategoryChoice(long? Id, string Name)
    {
        public override string ToString() => Name;
    }
}

internal sealed class CategoryEditorForm : Form
{
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    readonly Button _color = new() { AutoSize = true };
    readonly NumericUpDown _sortOrder = new() { Minimum = -10000, Maximum = 10000, Width = 100 };
    int _colorRgb;

    public string CategoryName => _name.Text.Trim();
    public int ColorRgb => _colorRgb;
    public int SortOrder => Decimal.ToInt32(_sortOrder.Value);

    public CategoryEditorForm(ApplicationCategory? category)
    {
        Text = category is null ? "Add category" : "Edit category";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(440, 195);

        _name.Text = category?.Name ?? string.Empty;
        var isInvariantOther = category?.Id == DefaultApplicationCategories.OtherId;
        _name.ReadOnly = isInvariantOther;
        _colorRgb = category?.ColorRgb ?? DefaultApplicationCategories.OtherColor;
        _sortOrder.Value = category?.SortOrder ?? 0;
        ApplicationRulesForm.UpdateColorButton(_color, _colorRgb);
        _color.Click += (_, _) => ApplicationRulesForm.ChooseColor(_color, ref _colorRgb);

        var fields = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 140,
            ColumnCount = 2,
            RowCount = 4,
            Padding = new Padding(10),
        };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        fields.Controls.Add(new Label { Text = "Name", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        fields.Controls.Add(_name, 1, 0);
        var nameHint = new Label
        {
            Text = "Other is the permanent fallback, so its name cannot be changed.",
            AutoSize = true,
            ForeColor = SystemColors.GrayText,
            Visible = isInvariantOther,
        };
        fields.Controls.Add(nameHint, 1, 1);
        fields.Controls.Add(new Label { Text = "Color", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        fields.Controls.Add(_color, 1, 2);
        fields.Controls.Add(new Label { Text = "Sort order", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        fields.Controls.Add(_sortOrder, 1, 3);

        var save = new Button { Text = "Save", AutoSize = true };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
        save.Click += (_, _) =>
        {
            if (CategoryName.Length == 0)
            {
                MessageBox.Show(this, "Enter a category name.", "Category name",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _name.Focus();
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        };
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom,
            Height = 45,
            Padding = new Padding(6),
            FlowDirection = FlowDirection.RightToLeft,
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        Controls.Add(fields);
        Controls.Add(buttons);
        AcceptButton = save;
        CancelButton = cancel;
    }
}
